namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles
{
    /// <summary>
    /// Identifies the physical failure boundary exercised by one phase of a Runtime Pool crash-recovery scenario.
    /// </summary>
    public enum RuntimePoolCrashFailureKind
    {
        /// <summary>
        /// Kills one exact runtime process while preserving healthy runtime siblings hosted by the same Pod.
        /// </summary>
        RuntimeProcess = 0,

        /// <summary>
        /// Deletes one exact Kubernetes Pod and therefore fails its complete runtime membership.
        /// </summary>
        KubernetesPod = 1
    }
}
