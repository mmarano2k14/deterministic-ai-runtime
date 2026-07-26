namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Defines why a runtime pool child process completed.
    /// </summary>
    public enum AiRuntimeProcessPoolChildExitKind
    {
        /// <summary>
        /// The child exited after an explicit pool-manager stop request.
        /// </summary>
        Requested = 0,

        /// <summary>
        /// The child exited without an explicit pool-manager stop request.
        /// </summary>
        Unexpected = 1,

        /// <summary>
        /// The child lifecycle adapter failed while observing or controlling the process.
        /// </summary>
        Faulted = 2
    }
}
