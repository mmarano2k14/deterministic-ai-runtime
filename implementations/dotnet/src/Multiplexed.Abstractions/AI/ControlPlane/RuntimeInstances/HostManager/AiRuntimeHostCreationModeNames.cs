namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager
{
    /// <summary>
    /// Defines canonical serialized host creation mode names that must preserve their exact persisted values.
    /// </summary>
    public static class AiRuntimeHostCreationModeNames
    {
        /// <summary>The process runtime pool host creation mode name.</summary>
        public const string ProcessPool = "ProcessPool";
        /// <summary>The Kubernetes runtime pool host creation mode name.</summary>
        public const string KubernetesPool = "KubernetesPool";
    }
}
