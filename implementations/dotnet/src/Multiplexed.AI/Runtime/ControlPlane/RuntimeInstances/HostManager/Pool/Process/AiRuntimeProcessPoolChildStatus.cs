namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Defines the lifecycle status of one independently managed runtime child process.
    /// </summary>
    public enum AiRuntimeProcessPoolChildStatus
    {
        /// <summary>
        /// The child process is being created.
        /// </summary>
        Starting = 0,

        /// <summary>
        /// The child process has started and is managed by the runtime pool.
        /// </summary>
        Running = 1,

        /// <summary>
        /// The child process is being stopped.
        /// </summary>
        Stopping = 2,

        /// <summary>
        /// The child process has stopped.
        /// </summary>
        Stopped = 3,

        /// <summary>
        /// The child process lifecycle failed.
        /// </summary>
        Faulted = 4
    }
}
