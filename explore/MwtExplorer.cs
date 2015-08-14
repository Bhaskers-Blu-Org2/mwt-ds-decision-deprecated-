﻿using System;

namespace MultiWorldTesting
{
    /// <summary>
    /// The top level MwtExplorer class.  Using this makes sure that the
    /// right bits are recorded and good random actions are chosen.
    /// </summary>
    /// <typeparam name="TContext">The Context type.</typeparam>
    public class MwtExplorer<TContext>
    {
        private ulong appId;
        private IRecorder<TContext> recorder;
        private Func<TContext, uint> getNumberOfActionsFunc;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="appId">This should be unique to each experiment to avoid correlation bugs.</param>
        /// <param name="recorder">A user-specified class for recording the appropriate bits for use in evaluation and learning.</param>
        public MwtExplorer(string appId, IRecorder<TContext> recorder)
        {
            this.appId = MurMurHash3.ComputeIdHash(appId);
            this.recorder = recorder;
            this.getNumberOfActionsFunc = null;
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="appId">This should be unique to each experiment to avoid correlation bugs.</param>
        /// <param name="recorder">A user-specified class for recording the appropriate bits for use in evaluation and learning.</param>
        /// <param name="getNumberOfActionsFunc">The func delegate to retrieve number of actions in a given context.</param>
        public MwtExplorer(string appId, IRecorder<TContext> recorder, Func<TContext, uint> getNumberOfActionsFunc)
        {
            this.appId = MurMurHash3.ComputeIdHash(appId);
            this.recorder = recorder;
            this.getNumberOfActionsFunc = getNumberOfActionsFunc;
        }

        /// <summary>
        /// Choose_Action should be drop-in replacement for any existing policy function.
        /// </summary>
        /// <param name="explorer">An existing exploration algorithm (one of the above) which uses the default policy as a callback.</param>
        /// <param name="uniqueKey">A unique identifier for the experimental unit. This could be a user id, a session id, etc...</param>
        /// <param name="context">The context upon which a decision is made. See SimpleContext above for an example.</param>
        /// <returns>A list of unsigned 32-bit integers representing the 1-based chosen actions.</returns>
        public uint[] ChooseAction(IExplorer<TContext> explorer, string uniqueKey, TContext context)
        {
            ulong seed = MurMurHash3.ComputeIdHash(uniqueKey);

            DecisionTuple decisionTuple = explorer.ChooseAction(seed + this.appId, context, this.getNumberOfActionsFunc);

            if (decisionTuple.ShouldRecord)
            {
                this.recorder.Record(context, decisionTuple.Actions, decisionTuple.Probability, uniqueKey);
            }

            return decisionTuple.Actions;
        }
    };
}