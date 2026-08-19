using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.ProcessControl;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.KubernetesPool;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Runners;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Results;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Runners;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.Runners
{
    /// <summary>
    /// Runs focused production scenarios against one real HTTP Kubernetes Runtime Pool Pod.
    /// </summary>
    public sealed class HttpKubernetesRuntimePoolProductionScenarioRunner :
        IProductionRuntimeScenarioRunner
    {
        private readonly IKubernetesRuntimePoolScenarioRuntimeProfile profile;
        private readonly KubernetesRuntimePoolProductionInfrastructure infrastructure;
        private readonly KubernetesRuntimePoolChildFailureBoundary childFailureBoundary;
        private readonly ProcessHostProductionScenarioRunner inner;

        /// <summary>
        /// Initializes a one-Pod, one-runtime HTTP Kubernetes Runtime Pool scenario runner.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="childFailureBoundary">The physical boundary used when a Child DAG failure is configured.</param>
        /// <param name="maximumPodCount">The maximum Runtime Pool Pod capacity allowed during the focused proof.</param>
        public HttpKubernetesRuntimePoolProductionScenarioRunner(
            ITestOutputHelper output,
            KubernetesRuntimePoolChildFailureBoundary childFailureBoundary =
                KubernetesRuntimePoolChildFailureBoundary.RuntimeProcess,
            int maximumPodCount = 1)
        {
            ArgumentNullException.ThrowIfNull(output);

            this.profile =
                new HttpKubernetesRuntimePoolChildDagScenarioRuntimeProfile(
                    maximumPodCount);
            this.childFailureBoundary = childFailureBoundary;

            this.infrastructure =
                new KubernetesRuntimePoolProductionInfrastructure(
                    output,
                    this.profile.LogPrefix);

            this.inner =
                new ProcessHostProductionScenarioRunner(
                    this.profile.ProviderLabel,
                    this.profile.LogPrefix,
                    this.profile.ProviderName,
                    AiRuntimeHostCreationMode.KubernetesPool,
                    this.profile.BuildSettings,
                    output,
                    this.CleanupAsync,
                    this.CreateRuntimePoolChildFailureControl);
        }

        /// <inheritdoc />
        public string ProviderLabel => this.profile.ProviderLabel;

        /// <inheritdoc />
        public Task<ProductionRuntimeScenarioResult> RunAsync(
            ProductionRuntimeScenarioDefinition scenario,
            CancellationToken cancellationToken = default)
        {
            return this.inner.RunAsync(
                scenario,
                cancellationToken);
        }

        private IAiRuntimeHostProcessControl
            CreateRuntimePoolChildFailureControl(
                IServiceProvider services,
                string controlPlaneId,
                ProductionRuntimeScenarioDefinition scenario)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentNullException.ThrowIfNull(scenario);

            var poolId =
                RuntimePoolCrashRecoveryScenarioIdentity.CreatePoolId(
                    this.profile.PoolIdPrefix,
                    controlPlaneId);
            var registry =
                services.GetRequiredService<IAiRuntimeInstanceRegistry>();

            if (this.childFailureBoundary ==
                KubernetesRuntimePoolChildFailureBoundary.RuntimeProcess)
            {
                return this.infrastructure.CreateRuntimePoolChildProcessControl(
                    registry,
                    poolId,
                    this.profile.LogPrefix);
            }

            if (this.childFailureBoundary !=
                KubernetesRuntimePoolChildFailureBoundary.KubernetesPod)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(this.childFailureBoundary),
                    this.childFailureBoundary,
                    "Unsupported Kubernetes Runtime Pool Child DAG failure boundary.");
            }

            if (scenario.Tenants.Count != 1)
            {
                throw new InvalidOperationException(
                    "The focused Kubernetes Child DAG Pod failure proof requires exactly one tenant.");
            }

            var tenant = scenario.Tenants.Single();
            var maximumRuntimeCapacity =
                checked(
                    this.profile.Topology.MaximumPodCount *
                    this.profile.Topology.MaximumRuntimeCountPerPod);

            return this.infrastructure.CreateRuntimePoolPodFailureControl(
                registry,
                services.GetRequiredService<
                    IAiKubernetesRuntimePoolPodFailureRecoveryCoordinator>(),
                poolId,
                snapshot =>
                    KubernetesRuntimePoolProductionInfrastructure
                        .CreatePodRecoveryHostStartTemplate(
                            snapshot,
                            tenant,
                            controlPlaneId,
                            poolId,
                            this.profile.ProviderName,
                            maximumRuntimeCapacity,
                            "child-dag"),
                this.profile.LogPrefix);
        }

        private Task CleanupAsync(
            string controlPlaneId)
        {
            var poolId =
                RuntimePoolCrashRecoveryScenarioIdentity.CreatePoolId(
                    this.profile.PoolIdPrefix,
                    controlPlaneId);

            return this.infrastructure.CleanupControlPlanePodsAsync(
                controlPlaneId,
                poolId);
        }
    }

    /// <summary>
    /// Selects the physical Kubernetes Runtime Pool failure boundary used by focused Child DAG recovery proofs.
    /// </summary>
    public enum KubernetesRuntimePoolChildFailureBoundary
    {
        /// <summary>
        /// Kills only the RuntimeInstanceOnly process while preserving its Pod.
        /// </summary>
        RuntimeProcess = 0,

        /// <summary>
        /// Deletes the complete Kubernetes Runtime Pool Pod containing the targeted runtime.
        /// </summary>
        KubernetesPod = 1
    }
}
