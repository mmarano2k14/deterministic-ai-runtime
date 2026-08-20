namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery
{
    /// <summary>
    /// Defines canonical runtime execution recovery mode values.
    /// </summary>
    /// <remarks>
    /// These values preserve the existing physical recovery mode strings used in
    /// durable metadata and recovery coordination. They do not grant recovery authority.
    /// </remarks>
    public static class AiRuntimeRecoveryModes
    {
        /// <summary>
        /// Gets the recovery mode that resumes an already durable execution.
        /// </summary>
        public const string ResumeExistingExecution = "resume-existing-execution";

        /// <summary>
        /// Gets the recovery mode that requeues a local queued run.
        /// </summary>
        public const string RequeueLocalQueuedRun = "requeue-local-queued-run";
    }
}
