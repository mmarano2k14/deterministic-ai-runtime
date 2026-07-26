namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Defines the lifecycle status of one process-host Runtime Pool Manager incarnation.
    /// </summary>
    public enum AiRuntimeProcessPoolManagerStatus
    {
        /// <summary>
        /// The manager has been created but has not started child processes.
        /// </summary>
        Created = 0,

        /// <summary>
        /// The manager owns at least the configured minimum number of running child processes.
        /// </summary>
        Running = 1,

        /// <summary>
        /// The manager is stopping its child processes.
        /// </summary>
        Stopping = 2,

        /// <summary>
        /// The manager and every tracked child process have stopped.
        /// </summary>
        Stopped = 3,

        /// <summary>
        /// One or more child processes could not be stopped and remain tracked.
        /// </summary>
        Faulted = 4,

        /// <summary>
        /// The manager remains active but currently owns fewer than the configured minimum number
        /// of child processes.
        /// </summary>
        Degraded = 5
    }
}
