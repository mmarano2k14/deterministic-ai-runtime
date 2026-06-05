namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport
{
    /// <summary>
    /// Defines runtime instance command transport operations.
    /// </summary>
    /// <remarks>
    /// These operations represent provider-level commands that can be transported
    /// to a runtime instance through Redis, HTTP, gRPC, Kubernetes, or another
    /// future transport.
    /// </remarks>
    public enum AiRuntimeInstanceCommandOperation
    {
        /// <summary>
        /// Unknown command operation.
        /// </summary>
        Unknown = 0,

        /// <summary>
        /// Dispatches a shared run to a runtime instance.
        /// </summary>
        DispatchRun = 1,

        /// <summary>
        /// Gets the status of a runtime run.
        /// </summary>
        GetRunStatus = 2,

        /// <summary>
        /// Gets the status of a runtime queue.
        /// </summary>
        GetQueueStatus = 3,

        /// <summary>
        /// Pauses a runtime queue.
        /// </summary>
        PauseQueue = 4,

        /// <summary>
        /// Resumes a runtime queue.
        /// </summary>
        ResumeQueue = 5,

        /// <summary>
        /// Cancels a runtime run.
        /// </summary>
        CancelRun = 6,

        /// <summary>
        /// Cancels a queued runtime run.
        /// </summary>
        CancelQueuedRun = 7
    }
}