namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Represents the result of requeueing shared runs after scale-out fulfillment.
    /// </summary>
    public sealed class AiScaleOutFulfilledRunRequeueResult
    {
        /// <summary>
        /// Gets a value indicating whether a linked shared run was found.
        /// </summary>
        public bool LinkedSharedRunFound { get; init; }

        /// <summary>
        /// Gets a value indicating whether the linked shared run was requeued or was already dispatchable.
        /// </summary>
        public bool RequeueSucceeded { get; init; }

        /// <summary>
        /// Gets the number of candidate shared runs.
        /// </summary>
        public int CandidateCount { get; init; }

        /// <summary>
        /// Gets the optional shared run identifier.
        /// </summary>
        public string? SharedRunId { get; init; }

        /// <summary>
        /// Gets the optional reason.
        /// </summary>
        public string? Reason { get; init; }

        /// <summary>
        /// Creates a result for a request without a linked shared run.
        /// </summary>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <returns>The result.</returns>
        public static AiScaleOutFulfilledRunRequeueResult NoLinkedSharedRun(
            string? sharedRunId)
        {
            return new AiScaleOutFulfilledRunRequeueResult
            {
                LinkedSharedRunFound = false,
                RequeueSucceeded = true,
                CandidateCount = 0,
                SharedRunId = sharedRunId,
                Reason = "No linked shared run was found. Treating scale-out as capacity-only fulfillment."
            };
        }

        /// <summary>
        /// Creates a successful result.
        /// </summary>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <param name="candidateCount">The candidate count.</param>
        /// <param name="reason">The reason.</param>
        /// <returns>The result.</returns>
        public static AiScaleOutFulfilledRunRequeueResult Succeeded(
            string sharedRunId,
            int candidateCount,
            string reason)
        {
            return new AiScaleOutFulfilledRunRequeueResult
            {
                LinkedSharedRunFound = true,
                RequeueSucceeded = true,
                CandidateCount = candidateCount,
                SharedRunId = sharedRunId,
                Reason = reason
            };
        }

        /// <summary>
        /// Creates a failed result.
        /// </summary>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <param name="candidateCount">The candidate count.</param>
        /// <param name="reason">The reason.</param>
        /// <returns>The result.</returns>
        public static AiScaleOutFulfilledRunRequeueResult Failed(
            string sharedRunId,
            int candidateCount,
            string reason)
        {
            return new AiScaleOutFulfilledRunRequeueResult
            {
                LinkedSharedRunFound = true,
                RequeueSucceeded = false,
                CandidateCount = candidateCount,
                SharedRunId = sharedRunId,
                Reason = reason
            };
        }
    }
}