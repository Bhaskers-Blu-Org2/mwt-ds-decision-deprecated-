﻿using MultiWorldTesting;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Newtonsoft.Json;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using System.Diagnostics;

namespace DecisionSample
{
    // TODO: rename Recorder to Logger?
    internal class DecisionServiceRecorder<TContext> : IRecorder<TContext>, IDisposable
    {
        public DecisionServiceRecorder(BatchingConfiguration batchConfig, 
            Func<TContext, string> contextSerializer, 
            int experimentalUnitDurationInSeconds,
            string authorizationToken) 
        {
            this.batchConfig = batchConfig;
            this.contextSerializer = contextSerializer;
            this.experimentalUnitDurationInSeconds = experimentalUnitDurationInSeconds;
            this.authorizationToken = authorizationToken;

            this.httpClient = new HttpClient();
            this.httpClient.BaseAddress = new Uri(this.ServiceAddress);
            this.httpClient.Timeout = TimeSpan.FromSeconds(this.ConnectionTimeOutInSeconds);
            this.httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(this.AuthenticationScheme, this.authorizationToken);

            this.eventSource = new TransformBlock<IEvent, string>(ev => JsonConvert.SerializeObject(ev), new ExecutionDataflowBlockOptions { 
                // TODO: Discuss whether we should expose another config setting for this BoundedCapacity
                BoundedCapacity = batchConfig.MaxUploadQueueCapacity
            });
            this.eventProcessor = new ActionBlock<IList<string>>((Func<IList<string>, Task>)this.BatchProcess, new ExecutionDataflowBlockOptions 
            { 
                // TODO: Finetune these numbers
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                BoundedCapacity = batchConfig.MaxUploadQueueCapacity,
            });

            this.eventUnsubscriber = this.eventSource.AsObservable()
                .Window(batchConfig.MaxDuration)
                .Select(w => w.Buffer(batchConfig.MaxEventCount, batchConfig.MaxBufferSizeInBytes, json => Encoding.Default.GetByteCount(json)))
                .SelectMany(buffer => buffer)
                .Subscribe(this.eventProcessor.AsObserver());
        }

        // TODO: add a TryRecord that doesn't block and returns whether the operation was successful
        public void Record(TContext context, uint action, float probability, string uniqueKey)
        {
            bool success = this.eventSource.Post(new Interaction
            { 
                ID = uniqueKey,
                Action = (int)action,
                Probability = probability,
                Context = this.contextSerializer(context)
            });

            if (!success)
            {
                Trace.TraceError("Cannot record interaction with key: {0}.", uniqueKey);
            }
        }

        public void ReportReward(float reward, string uniqueKey)
        {
            this.eventSource.AsObserver().OnNext(new Observation
            { 
                ID = uniqueKey,
                Value = JsonConvert.SerializeObject(reward)
            });
        }

        public bool TryReportReward(float reward, string uniqueKey)
        {
            return this.eventSource.Post(new Observation
            {
                ID = uniqueKey,
                Value = JsonConvert.SerializeObject(reward)
            });
        }

        public void ReportOutcome(string outcomeJson, string uniqueKey)
        {
            this.eventSource.AsObserver().OnNext(new Observation
            { 
                ID = uniqueKey,
                Value = outcomeJson
            });
        }

        public bool TryReportOutcome(string outcomeJson, string uniqueKey)
        {
            return this.eventSource.Post(new Observation
            {
                ID = uniqueKey,
                Value = outcomeJson
            });
        }

        // TODO: at the time of server communication, if the client is out of memory (or meets some predefined upper bound):
        // 1. It can block the execution flow.
        // 2. Or drop events.
        private async Task BatchProcess(IList<string> jsonEvents)
        {
            using (var jsonMemStream = new MemoryStream())
            using (var jsonWriter = new JsonTextWriter(new StreamWriter(jsonMemStream)))
            {
                var serializer = new JsonSerializer();
                serializer.Serialize(jsonWriter, new EventBatch
                {
                    Events = jsonEvents,
                    ExperimentalUnitDurationInSeconds = this.experimentalUnitDurationInSeconds
                });

                jsonWriter.Flush();
                jsonMemStream.Position = 0;

#if TEST
                await this.BatchLog("decision_service_test_output", jsonMemStream);
#else
                await this.BatchUpload(jsonMemStream);
#endif
            }
        }

        private async Task BatchUpload(MemoryStream jsonMemStream)
        {
            HttpResponseMessage response = await httpClient.PostAsync(this.ServicePostAddress, new StreamContent(jsonMemStream)).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Task<string> taskReadResponse = response.Content.ReadAsStringAsync();
                taskReadResponse.Wait();
                string responseMessage = taskReadResponse.Result;

                // TODO: throw exception with custom message?
            }
        }

        /// <summary>
        /// Blocks further incoming messages and finishes processing all data in buffer. This is a blocking call.
        /// </summary>
        public async Task FlushAsync()
        {
            this.eventSource.Complete();

            // TODO: 
            //this.eventProcessor.Completion.Wait();

            await this.eventProcessor.Completion;
        }

        // Internally, background tasks can get back latest model version as a return value from the HTTP communication with Ingress worker

        public void Dispose() 
        {
            this.httpClient.Dispose();
            this.eventUnsubscriber.Dispose();
        }

#if TEST
        private async Task BatchLog(string batchFile, MemoryStream jsonMemStream)
        {
            // TODO: use other mechanisms to flush data than writing to disk
            File.WriteAllText(batchFile, Encoding.UTF8.GetString(jsonMemStream.ToArray()));
        }
#endif

        #region Members
        private BatchingConfiguration batchConfig;
        private Func<TContext, string> contextSerializer;
        private TransformBlock<IEvent, string> eventSource;
        private ActionBlock<IList<string>> eventProcessor;
        private IDisposable eventUnsubscriber;
        private int experimentalUnitDurationInSeconds;
        private string authorizationToken;
        private HttpClient httpClient;
        #endregion

        #region Constants
        //private readonly string ServiceAddress = "http://decisionservice.cloudapp.net";
        private readonly string ServiceAddress = "http://localhost:1362";
        private readonly string ServicePostAddress = "/DecisionService.svc/PostExperimentalUnits";
        private readonly int ConnectionTimeOutInSeconds = 60;
        private readonly string AuthenticationScheme = "Bearer";
        #endregion
    }
}