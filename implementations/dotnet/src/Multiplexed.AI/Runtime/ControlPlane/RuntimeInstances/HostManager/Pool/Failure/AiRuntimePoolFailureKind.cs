namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure
{
    /// <summary>
    /// Defines why one runtime-pool failure observation was created.
    /// </summary>
    public enum AiRuntimePoolFailureKind
    {
        /// <summary>
        /// The operating-system child process exited without a requested pool shutdown.
        /// </summary>
        UnexpectedProcessExit = 0,

        /// <summary>
        /// The child lifecycle adapter failed while observing or controlling the process.
        /// </summary>
        LifecycleObserverFault = 1,

        /// <summary>
        /// A complete Kubernetes Runtime Pool Pod disappeared unexpectedly.
        /// </summary>
        UnexpectedPodDeletion = 2
    }
}
