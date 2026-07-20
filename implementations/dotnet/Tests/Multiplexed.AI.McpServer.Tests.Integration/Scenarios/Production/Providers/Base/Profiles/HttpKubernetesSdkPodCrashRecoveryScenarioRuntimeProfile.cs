using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.Runners;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles
{
    /// <summary>
    /// Provides the HTTP Kubernetes SDK runtime profile for production pod crash recovery scenarios.
    /// </summary>
    public sealed class HttpKubernetesSdkPodCrashRecoveryScenarioRuntimeProfile : IProcessHostScenarioRuntimeProfile
    {
        /// <inheritdoc />
        public string ProviderName => "http";

        /// <inheritdoc />
        public string ProviderLabel => "hk8s-crash";

        /// <inheritdoc />
        public string LogPrefix => "HTTP K8S SDK POD CRASH RECOVERY";

        /// <inheritdoc />
        public string RequestedBy => "http-kubernetes-sdk-pod-crash-recovery-test";

        /// <inheritdoc />
        public string Source => "integration-test";

        /// <inheritdoc />
        public AiRuntimeHostCreationMode HostCreationMode => AiRuntimeHostCreationMode.Kubernetes;

        /// <inheritdoc />
        public Dictionary<string, string?> BuildSettings(
            ProductionRuntimeScenarioDefinition scenario,
            string controlPlaneId,
            string runtimeHostAssemblyPath)
        {
            return HttpKubernetesSdkPodCrashRecoveryProductionScenarioSettingsBuilder.Build(
                scenario,
                controlPlaneId,
                runtimeHostAssemblyPath);
        }
    }
}
