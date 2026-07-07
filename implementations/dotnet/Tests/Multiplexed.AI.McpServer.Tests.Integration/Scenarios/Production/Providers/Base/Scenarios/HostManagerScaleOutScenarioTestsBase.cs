using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.McpServer.Tests.Integration.Auth;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Scenarios
{
    /// <summary>
    /// Base class for host-manager scale-out scenario tests.
    /// </summary>
    public abstract class HostManagerScaleOutScenarioTestsBase
    {
        private readonly ITestOutputHelper output;
        private readonly IProcessHostScenarioRuntimeProfile profile;

        /// <summary>
        /// Initializes a new instance of the <see cref="HostManagerScaleOutScenarioTestsBase"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="profile">The host-manager scenario runtime profile.</param>
        protected HostManagerScaleOutScenarioTestsBase(
            ITestOutputHelper output,
            IProcessHostScenarioRuntimeProfile profile)
        {
            this.output = output ?? throw new ArgumentNullException(nameof(output));
            this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
        }

        /// <summary>
        /// Verifies that host-manager scale-out can fulfill runtime capacity without changing the runtime transport provider.
        /// </summary>
        /// <returns>A task that completes when the proof has finished.</returns>
        protected async Task HostManager_Should_Fulfill_ScaleOut_Without_Changing_Runtime_Transport()
        {
            var scenario =
                ProductionRuntimeScenarioFactory.CreateSingleTenantDedicatedRuntimeModeScenario();

            var tenant =
                scenario.Tenants.Single();

            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    $"{this.profile.ProviderLabel}-scaleout");

            var runtimeHostAssemblyPath =
                GenericMcpRuntimeHostAssemblyResolver.ResolveRuntimeHostAssemblyPath();

            var settings =
                this.profile.BuildSettings(
                    scenario,
                    controlPlaneId,
                    runtimeHostAssemblyPath);

            this.output.WriteLine(
                "[PROFILE FINAL DEBUG] ProfileType='{0}', ScenarioDebug='{1}', HostCreationMode='{2}', ClientMode='{3}', GrpcRequireReadiness='{4}', GrpcReadinessTimeoutSeconds='{5}', GrpcReadinessPollMs='{6}', KubernetesRequireRuntimeReadiness='{7}', RuntimeImage='{8}', ImagePullPolicy='{9}'",
                this.profile.GetType().FullName,
                settings.GetValueOrDefault("ScenarioDebug:Profile"),
                settings.GetValueOrDefault("AiGrpcRuntimeScaleOut:HostCreationMode"),
                settings.GetValueOrDefault("AiKubernetesRuntimeHost:ClientMode"),
                settings.GetValueOrDefault("AiGrpcRuntimeScaleOut:RequireReadiness"),
                settings.GetValueOrDefault("AiGrpcRuntimeScaleOut:ReadinessTimeoutSeconds"),
                settings.GetValueOrDefault("AiGrpcRuntimeScaleOut:ReadinessPollIntervalMilliseconds"),
                settings.GetValueOrDefault("AiKubernetesRuntimeHost:RequireRuntimeReadiness"),
                settings.GetValueOrDefault("AiKubernetesRuntimeHost:RuntimeImage"),
                settings.GetValueOrDefault("AiKubernetesRuntimeHost:ImagePullPolicy"));

            await using var host =
                new GenericMcpServerTestHost(settings);

            var scaleOutRequestStore =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var requestId =
                $"scaleout-{Guid.NewGuid():N}";

            var sharedRunId =
                $"shared-run-{Guid.NewGuid():N}";

            var pipelineName =
                $"{scenario.Name}-{tenant.TenantId}-{this.profile.ProviderLabel}-{Guid.NewGuid():N}";

            var executionContextSnapshot = McpRbacTestContextFactory.CreateDefaultContext();

            var request =
                new AiRuntimeScaleOutRequestRecord
                {
                    RequestId = requestId,
                    ControlPlaneId = controlPlaneId,
                    SharedRunId = sharedRunId,
                    ExecutionContextSnapshot = new ExecutionContextSnapshot
                    {
                        ContextKey = pipelineName,
                        Project = "integration-tests",
                        UserId = this.profile.RequestedBy,
                        TenantId = tenant.TenantId,
                        TenantGroupId = tenant.TenantGroupId,
                        CurrentNamespace = tenant.TenantId,
                        Namespaces = executionContextSnapshot.Namespaces,
                        InFlightCount = 0,
                        TtlSeconds = 300,
                        CreatedAtUtc = DateTime.UtcNow
                    },
                    TenantId = tenant.TenantId,
                    TenantGroupId = tenant.TenantGroupId,
                    PipelineKey = pipelineName,
                    IsolationMode = AiRuntimeInstanceIsolationMode.Dedicated,
                    PreferDedicatedCapacity = true,
                    AllowSharedFallback = false,
                    MaxRuntimeInstances = tenant.MaxRuntimeInstances,
                    RuntimeInstanceIdPrefix = tenant.RuntimeInstanceIdPrefix,
                    WorkerCountPerInstance = tenant.WorkerCountPerInstance,
                    MaxConcurrentRunsPerInstance = tenant.MaxConcurrentRunsPerInstance,
                    LocalQueueCapacity = tenant.LocalQueueCapacity,
                    Status = AiRuntimeScaleOutRequestStatus.Pending,
                    Reason = "integration-test-kubernetes-host-manager-scaleout",
                    VisibleInstanceCount = 0,
                    AvailableInstanceCount = 0,
                    CurrentInstanceCount = 0,
                    MaxInstanceCount = tenant.MaxRuntimeInstances,
                    RequestedTargetInstanceCount = 1,
                    ProviderHint = this.profile.ProviderName,
                    RequestedBy = this.profile.RequestedBy,
                    Source = this.profile.Source,
                    CorrelationId = requestId,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
                    Metadata =
                    {
                        ["provider.name"] = this.profile.ProviderName,
                        ["transport.name"] = this.profile.ProviderName,
                        ["host.provider"] = AiRuntimeHostProviderNames.Kubernetes,
                        ["host.creation.mode"] = "Kubernetes"
                    }
                };

            this.output.WriteLine(
                $"[{this.profile.LogPrefix} SCALE-OUT PROOF] Starting. Provider='{this.profile.ProviderName}', ProviderLabel='{this.profile.ProviderLabel}', HostProvider='{AiRuntimeHostProviderNames.Kubernetes}', ControlPlaneId='{controlPlaneId}', TenantId='{tenant.TenantId}', Pipeline='{pipelineName}', RequestId='{requestId}'.");

            await scaleOutRequestStore
                .CreateAsync(
                    request,
                    cancellationToken: default)
                .ConfigureAwait(false);

            var fulfilledRequest =
                await WaitForScaleOutRequestFulfilledAsync(
                    scaleOutRequestStore,
                    requestId,
                    timeout: scenario.ScaleOutTimeout)
                .ConfigureAwait(false);

            Assert.Equal(AiRuntimeScaleOutRequestStatus.Fulfilled, fulfilledRequest.Status);
            Assert.False(string.IsNullOrWhiteSpace(fulfilledRequest.FulfilledBy));
            Assert.False(string.IsNullOrWhiteSpace(fulfilledRequest.FulfilledRuntimeInstanceId));
            Assert.Equal(this.profile.ProviderName, fulfilledRequest.Metadata["provider.name"]);
            Assert.Equal(this.profile.ProviderName, fulfilledRequest.Metadata["transport.name"]);
            Assert.Equal(AiRuntimeHostProviderNames.Kubernetes, fulfilledRequest.Metadata["host.provider"]);

            this.output.WriteLine(
                $"[{this.profile.LogPrefix} SCALE-OUT PROOF] Fulfilled. RequestId='{fulfilledRequest.RequestId}', SharedRunId='{fulfilledRequest.SharedRunId}', RuntimeInstanceId='{fulfilledRequest.FulfilledRuntimeInstanceId}', FulfilledBy='{fulfilledRequest.FulfilledBy}', RuntimeProvider='{fulfilledRequest.Metadata["provider.name"]}', Transport='{fulfilledRequest.Metadata["transport.name"]}', HostProvider='{fulfilledRequest.Metadata["host.provider"]}', TenantId='{tenant.TenantId}'.");
        }

        /// <summary>
        /// Waits for a runtime scale-out request to become fulfilled.
        /// </summary>
        /// <param name="store">The runtime scale-out request store.</param>
        /// <param name="requestId">The request identifier.</param>
        /// <param name="timeout">The wait timeout.</param>
        /// <returns>The fulfilled request record.</returns>
        private static async Task<AiRuntimeScaleOutRequestRecord> WaitForScaleOutRequestFulfilledAsync(
            IAiRuntimeScaleOutRequestStore store,
            string requestId,
            TimeSpan timeout)
        {
            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            AiRuntimeScaleOutRequestRecord? lastRecord = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                lastRecord =
                    await store
                        .GetAsync(
                            requestId,
                            cancellationToken: default)
                        .ConfigureAwait(false);

                if (lastRecord is not null &&
                    lastRecord.Status == AiRuntimeScaleOutRequestStatus.Fulfilled)
                {
                    return lastRecord;
                }

                if (lastRecord is not null &&
                    lastRecord.Status == AiRuntimeScaleOutRequestStatus.Rejected)
                {
                    Assert.Fail(
                        $"Scale-out request was rejected. RequestId='{requestId}', RejectedBy='{lastRecord.RejectedBy}', Reason='{lastRecord.RejectionReason}'.");
                }

                if (lastRecord is not null &&
                    lastRecord.Status == AiRuntimeScaleOutRequestStatus.Expired)
                {
                    Assert.Fail(
                        $"Scale-out request expired. RequestId='{requestId}'.");
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                $"No fulfilled scale-out request was observed within '{timeout}'. RequestId='{requestId}', LastStatus='{lastRecord?.Status.ToString() ?? "missing"}', ObservedBy='{lastRecord?.ObservedBy}', FulfilledBy='{lastRecord?.FulfilledBy}', RejectedBy='{lastRecord?.RejectedBy}', RejectionReason='{lastRecord?.RejectionReason}'.");

            return null!;
        }
    }
}