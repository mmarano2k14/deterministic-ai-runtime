namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Defines the authoritative environment-variable contract passed to runtime pool child
    /// processes.
    /// </summary>
    public static class AiRuntimeProcessPoolChildEnvironment
    {
        /// <summary>
        /// Gets the environment-variable name carrying the logical runtime pool identifier.
        /// </summary>
        public const string PoolId = "MULTIPLEXED_AI_RUNTIME_POOL_ID";

        /// <summary>
        /// Gets the environment-variable name carrying the exact host-incarnation identifier.
        /// </summary>
        public const string HostId = "MULTIPLEXED_AI_RUNTIME_HOST_ID";

        /// <summary>
        /// Gets the environment-variable name carrying the independent runtime instance
        /// identifier.
        /// </summary>
        public const string RuntimeInstanceId =
            "MULTIPLEXED_AI_RUNTIME_INSTANCE_ID";

        /// <summary>
        /// Gets the environment-variable name carrying the child ordinal within the manager
        /// incarnation.
        /// </summary>
        public const string ProcessOrdinal =
            "MULTIPLEXED_AI_RUNTIME_PROCESS_ORDINAL";
    }
}
