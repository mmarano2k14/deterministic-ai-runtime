using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Readiness;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.McpServer.Tests.Integration.Auth;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;
using Multiplexed.AI.Stores;
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

            var scaleOutSectionName =
                string.Equals(this.profile.ProviderName, "grpc", StringComparison.OrdinalIgnoreCase)
                    ? "AiGrpcRuntimeScaleOut"
                    : "AiHttpRuntimeScaleOut";

            var expectedHostCreationMode =
                settings.GetValueOrDefault($"{scaleOutSectionName}:HostCreationMode") ?? string.Empty;

            var expectedHostProvider =
                string.Equals(expectedHostCreationMode, AiRuntimeHostCreationMode.Kubernetes.ToString(), StringComparison.OrdinalIgnoreCase)
                    ? AiRuntimeHostProviderNames.Kubernetes
                    : string.Empty;

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

            var executionContextSnapshot =
                McpRbacTestContextFactory.CreateDefaultContext();

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
                    Reason = "integration-test-host-manager-scaleout",
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
                        ["host.creation.mode"] = expectedHostCreationMode
                    }
                };

            if (!string.IsNullOrWhiteSpace(expectedHostProvider))
            {
                request.Metadata["host.provider"] = expectedHostProvider;
            }

            this.output.WriteLine(
                $"[{this.profile.LogPrefix} SCALE-OUT PROOF] Starting. Provider='{this.profile.ProviderName}', ProviderLabel='{this.profile.ProviderLabel}', HostProvider='{(string.IsNullOrWhiteSpace(expectedHostProvider) ? "(none)" : expectedHostProvider)}', HostCreationMode='{expectedHostCreationMode}', ControlPlaneId='{controlPlaneId}', TenantId='{tenant.TenantId}', Pipeline='{pipelineName}', RequestId='{requestId}'.");

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

            Assert.True(
                fulfilledRequest.Metadata.TryGetValue("provider.name", out var fulfilledProviderName),
                "Fulfilled scale-out metadata should contain provider.name.");

            Assert.Equal(
                this.profile.ProviderName,
                fulfilledProviderName);

            Assert.True(
                fulfilledRequest.Metadata.TryGetValue("transport.name", out var fulfilledTransportName),
                "Fulfilled scale-out metadata should contain transport.name.");

            Assert.Equal(
                this.profile.ProviderName,
                fulfilledTransportName);

            if (!string.IsNullOrWhiteSpace(expectedHostCreationMode))
            {
                Assert.True(
                    fulfilledRequest.Metadata.TryGetValue("host.creation.mode", out var fulfilledHostCreationMode) ||
                    fulfilledRequest.Metadata.TryGetValue("hostCreation.mode", out fulfilledHostCreationMode),
                    "Fulfilled scale-out metadata should contain host creation mode.");

                Assert.True(
                    string.Equals(expectedHostCreationMode, fulfilledHostCreationMode, StringComparison.OrdinalIgnoreCase),
                    $"Expected host creation mode '{expectedHostCreationMode}', but found '{fulfilledHostCreationMode}'.");
            }

            if (!string.IsNullOrWhiteSpace(expectedHostProvider))
            {
                Assert.True(
                    fulfilledRequest.Metadata.TryGetValue("host.provider", out var fulfilledHostProvider),
                    "Fulfilled scale-out metadata should contain host.provider when the profile expects a host provider.");

                Assert.True(
                    string.Equals(expectedHostProvider, fulfilledHostProvider, StringComparison.OrdinalIgnoreCase),
                    $"Expected host provider '{expectedHostProvider}', but found '{fulfilledHostProvider}'.");
            }

            var fulfilledHostProviderForLog =
                fulfilledRequest.Metadata.TryGetValue("host.provider", out var hostProviderForLog)
                    ? hostProviderForLog
                    : "(none)";

            var fulfilledHostCreationModeForLog =
                fulfilledRequest.Metadata.TryGetValue("host.creation.mode", out var hostCreationModeForLog)
                    ? hostCreationModeForLog
                    : fulfilledRequest.Metadata.TryGetValue("hostCreation.mode", out hostCreationModeForLog)
                        ? hostCreationModeForLog
                        : "(none)";

            this.output.WriteLine(
                $"[{this.profile.LogPrefix} SCALE-OUT PROOF] Fulfilled. RequestId='{fulfilledRequest.RequestId}', SharedRunId='{fulfilledRequest.SharedRunId}', RuntimeInstanceId='{fulfilledRequest.FulfilledRuntimeInstanceId}', FulfilledBy='{fulfilledRequest.FulfilledBy}', RuntimeProvider='{fulfilledProviderName}', Transport='{fulfilledTransportName}', HostProvider='{fulfilledHostProviderForLog}', HostCreationMode='{fulfilledHostCreationModeForLog}', TenantId='{tenant.TenantId}'.");
        }

        /// <summary>
        /// Verifies that host-manager scale-out fulfills runtime capacity and exposes a routable ready runtime instance.
        /// </summary>
        /// <returns>A task that completes when the proof has finished.</returns>
        protected async Task HostManager_Should_Fulfill_ScaleOut_And_Expose_Routable_Runtime_Instance()
        {
            var scenario =
                ProductionRuntimeScenarioFactory.CreateSingleTenantDedicatedRuntimeModeScenario();

            var tenant =
                scenario.Tenants.Single();

            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    $"{this.profile.ProviderLabel}-routable-scaleout");

            var runtimeHostAssemblyPath =
                GenericMcpRuntimeHostAssemblyResolver.ResolveRuntimeHostAssemblyPath();

            var settings =
                this.profile.BuildSettings(
                    scenario,
                    controlPlaneId,
                    runtimeHostAssemblyPath);

            var scaleOutSectionName =
                string.Equals(this.profile.ProviderName, "grpc", StringComparison.OrdinalIgnoreCase)
                    ? "AiGrpcRuntimeScaleOut"
                    : "AiHttpRuntimeScaleOut";

            var expectedHostCreationMode =
                settings.GetValueOrDefault($"{scaleOutSectionName}:HostCreationMode") ?? string.Empty;

            var expectedHostProvider =
                string.Equals(expectedHostCreationMode, AiRuntimeHostCreationMode.Kubernetes.ToString(), StringComparison.OrdinalIgnoreCase)
                    ? AiRuntimeHostProviderNames.Kubernetes
                    : string.Empty;

            this.output.WriteLine(
                "[PROFILE ROUTABLE DEBUG] ProfileType='{0}', ScenarioDebug='{1}', HostCreationMode='{2}', ClientMode='{3}', Provider='{4}', ProviderLabel='{5}', RuntimeImage='{6}', ImagePullPolicy='{7}'",
                this.profile.GetType().FullName,
                settings.GetValueOrDefault("ScenarioDebug:Profile"),
                expectedHostCreationMode,
                settings.GetValueOrDefault("AiKubernetesRuntimeHost:ClientMode"),
                this.profile.ProviderName,
                this.profile.ProviderLabel,
                settings.GetValueOrDefault("AiKubernetesRuntimeHost:RuntimeImage"),
                settings.GetValueOrDefault("AiKubernetesRuntimeHost:ImagePullPolicy"));

            await using var host =
                new GenericMcpServerTestHost(settings);

            var scaleOutRequestStore =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var readinessWaiter =
                host.Services.GetRequiredService<IAiRuntimeInstanceReadinessWaiter>();

            var requestId =
                $"scaleout-{Guid.NewGuid():N}";

            var sharedRunId =
                $"shared-run-{Guid.NewGuid():N}";

            var pipelineName =
                $"{scenario.Name}-{tenant.TenantId}-{this.profile.ProviderLabel}-routable-{Guid.NewGuid():N}";

            var executionContextSnapshot =
                McpRbacTestContextFactory.CreateDefaultContext();

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
                    Reason = "integration-test-host-manager-routable-runtime-scaleout",
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
                ["host.creation.mode"] = expectedHostCreationMode
                    }
                };

            if (!string.IsNullOrWhiteSpace(expectedHostProvider))
            {
                request.Metadata["host.provider"] = expectedHostProvider;
            }

            this.output.WriteLine(
                $"[{this.profile.LogPrefix} ROUTABLE SCALE-OUT PROOF] Starting. Provider='{this.profile.ProviderName}', ProviderLabel='{this.profile.ProviderLabel}', HostProvider='{(string.IsNullOrWhiteSpace(expectedHostProvider) ? "(none)" : expectedHostProvider)}', HostCreationMode='{expectedHostCreationMode}', ControlPlaneId='{controlPlaneId}', TenantId='{tenant.TenantId}', TenantGroupId='{tenant.TenantGroupId}', Pipeline='{pipelineName}', RequestId='{requestId}'.");

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

            Assert.True(
                fulfilledRequest.Metadata.TryGetValue("provider.name", out var fulfilledProviderName),
                "Fulfilled scale-out metadata should contain provider.name.");

            Assert.Equal(
                this.profile.ProviderName,
                fulfilledProviderName);

            Assert.True(
                fulfilledRequest.Metadata.TryGetValue("transport.name", out var fulfilledTransportName),
                "Fulfilled scale-out metadata should contain transport.name.");

            Assert.Equal(
                this.profile.ProviderName,
                fulfilledTransportName);

            if (!string.IsNullOrWhiteSpace(expectedHostCreationMode))
            {
                Assert.True(
                    fulfilledRequest.Metadata.TryGetValue("host.creation.mode", out var fulfilledHostCreationMode) ||
                    fulfilledRequest.Metadata.TryGetValue("hostCreation.mode", out fulfilledHostCreationMode),
                    "Fulfilled scale-out metadata should contain host creation mode.");

                Assert.True(
                    string.Equals(expectedHostCreationMode, fulfilledHostCreationMode, StringComparison.OrdinalIgnoreCase),
                    $"Expected host creation mode '{expectedHostCreationMode}', but found '{fulfilledHostCreationMode}'.");
            }

            if (!string.IsNullOrWhiteSpace(expectedHostProvider))
            {
                Assert.True(
                    fulfilledRequest.Metadata.TryGetValue("host.provider", out var fulfilledHostProvider),
                    "Fulfilled scale-out metadata should contain host.provider when the profile expects a host provider.");

                Assert.True(
                    string.Equals(expectedHostProvider, fulfilledHostProvider, StringComparison.OrdinalIgnoreCase),
                    $"Expected host provider '{expectedHostProvider}', but found '{fulfilledHostProvider}'.");
            }

            var requireTransportEndpoint =
                !string.Equals(expectedHostCreationMode, AiRuntimeHostCreationMode.Kubernetes.ToString(), StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(expectedHostCreationMode, AiRuntimeHostCreationMode.Fixture.ToString(), StringComparison.OrdinalIgnoreCase);

            var transportEndpoint =
                fulfilledRequest.Metadata.TryGetValue("transport.endpoint", out var fulfilledTransportEndpoint)
                    ? fulfilledTransportEndpoint
                    : fulfilledRequest.Metadata.TryGetValue("transportEndpoint", out fulfilledTransportEndpoint)
                        ? fulfilledTransportEndpoint
                        : string.Empty;

            this.output.WriteLine(
                $"[{this.profile.LogPrefix} ROUTABLE RUNTIME READINESS PROOF] Waiting. RuntimeInstanceId='{fulfilledRequest.FulfilledRuntimeInstanceId}', Provider='{this.profile.ProviderName}', Transport='{this.profile.ProviderName}', HostCreationMode='{expectedHostCreationMode}', RequireTransportEndpoint='{requireTransportEndpoint}', TransportEndpoint='{transportEndpoint}'.");

            var readinessResult =
                await readinessWaiter
                    .WaitUntilReadyAsync(
                        new AiRuntimeInstanceReadinessRequest
                        {
                            ControlPlaneId = controlPlaneId,
                            ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                            RuntimeInstanceId = fulfilledRequest.FulfilledRuntimeInstanceId,
                            ProviderName = this.profile.ProviderName,
                            TransportName = this.profile.ProviderName,
                            TransportEndpoint = transportEndpoint,
                            RequireTransportEndpoint = requireTransportEndpoint,
                            Timeout = scenario.ScaleOutTimeout,
                            PollInterval = TimeSpan.FromMilliseconds(250)
                        },
                        cancellationToken: default)
                    .ConfigureAwait(false);

            Assert.True(
                readinessResult.Success,
                $"Runtime instance should be routable and ready. RuntimeInstanceId='{fulfilledRequest.FulfilledRuntimeInstanceId}', FailureReason='{readinessResult.FailureReason}', TimedOut='{readinessResult.TimedOut}'.");

            Assert.False(
                readinessResult.TimedOut,
                $"Runtime readiness should not time out. RuntimeInstanceId='{fulfilledRequest.FulfilledRuntimeInstanceId}'.");

            Assert.Equal(
                fulfilledRequest.FulfilledRuntimeInstanceId,
                readinessResult.RuntimeInstanceId);

            var fulfilledHostProviderForLog =
                fulfilledRequest.Metadata.TryGetValue("host.provider", out var hostProviderForLog)
                    ? hostProviderForLog
                    : "(none)";

            var fulfilledHostCreationModeForLog =
                fulfilledRequest.Metadata.TryGetValue("host.creation.mode", out var hostCreationModeForLog)
                    ? hostCreationModeForLog
                    : fulfilledRequest.Metadata.TryGetValue("hostCreation.mode", out hostCreationModeForLog)
                        ? hostCreationModeForLog
                        : "(none)";

            this.output.WriteLine(
                $"[{this.profile.LogPrefix} ROUTABLE SCALE-OUT PROOF] Fulfilled and routable. RequestId='{fulfilledRequest.RequestId}', SharedRunId='{fulfilledRequest.SharedRunId}', RuntimeInstanceId='{fulfilledRequest.FulfilledRuntimeInstanceId}', FulfilledBy='{fulfilledRequest.FulfilledBy}', RuntimeProvider='{fulfilledProviderName}', Transport='{fulfilledTransportName}', HostProvider='{fulfilledHostProviderForLog}', HostCreationMode='{fulfilledHostCreationModeForLog}', TenantId='{tenant.TenantId}', ReadinessTransportEndpoint='{readinessResult.TransportEndpoint ?? "(null)"}'.");
        }

        /// <summary>
        /// Verifies that host-manager scale-out exposes a routable runtime instance and dispatches real work to it.
        /// </summary>
        /// <returns>A task that completes when the proof has finished.</returns>
        protected async Task HostManager_Should_Fulfill_ScaleOut_Expose_Routable_Runtime_And_Dispatch_Real_Work()
        {
            var scenario =
                ProductionRuntimeScenarioFactory.CreateSingleTenantDedicatedRuntimeModeScenario();

            var tenant =
                scenario.Tenants.Single();

            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    $"{this.profile.ProviderLabel}-dispatch-scaleout");

            var runtimeHostAssemblyPath =
                GenericMcpRuntimeHostAssemblyResolver.ResolveRuntimeHostAssemblyPath();

            var settings =
                this.profile.BuildSettings(
                    scenario,
                    controlPlaneId,
                    runtimeHostAssemblyPath);

            var scaleOutSectionName =
                string.Equals(this.profile.ProviderName, "grpc", StringComparison.OrdinalIgnoreCase)
                    ? "AiGrpcRuntimeScaleOut"
                    : "AiHttpRuntimeScaleOut";

            var expectedHostCreationMode =
                settings.GetValueOrDefault($"{scaleOutSectionName}:HostCreationMode") ?? string.Empty;

            var expectedHostProvider =
                string.Equals(expectedHostCreationMode, AiRuntimeHostCreationMode.Kubernetes.ToString(), StringComparison.OrdinalIgnoreCase)
                    ? AiRuntimeHostProviderNames.Kubernetes
                    : string.Empty;

            this.output.WriteLine(
                "[PROFILE DISPATCH DEBUG] ProfileType='{0}', ScenarioDebug='{1}', HostCreationMode='{2}', ClientMode='{3}', Provider='{4}', ProviderLabel='{5}', RuntimeImage='{6}', ImagePullPolicy='{7}'",
                this.profile.GetType().FullName,
                settings.GetValueOrDefault("ScenarioDebug:Profile"),
                expectedHostCreationMode,
                settings.GetValueOrDefault("AiKubernetesRuntimeHost:ClientMode"),
                this.profile.ProviderName,
                this.profile.ProviderLabel,
                settings.GetValueOrDefault("AiKubernetesRuntimeHost:RuntimeImage"),
                settings.GetValueOrDefault("AiKubernetesRuntimeHost:ImagePullPolicy"));

            await using var host =
                new GenericMcpServerTestHost(settings);

            var scaleOutRequestStore =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var sharedRunStore =
                host.Services.GetRequiredService<IAiSharedRunStore>();

            var runExecutionIndex =
                host.Services.GetRequiredService<IAiRuntimeRunExecutionIndex>();

            var dagStore =
                host.Services.GetRequiredService<IAiDagExecutionStore>();

            using var httpClient =
                host.CreateClient();

            var mcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        host,
                        httpClient,
                        this.profile.RequestedBy,
                        tenantId: tenant.TenantId,
                        tenantGroupId: tenant.TenantGroupId)
                    .ConfigureAwait(false);

            var pipelineName =
                $"{scenario.Name}-{tenant.TenantId}-{this.profile.ProviderLabel}-dispatch-{Guid.NewGuid():N}";

            this.output.WriteLine(
                $"[{this.profile.LogPrefix} DISPATCH SCALE-OUT PROOF] Starting. Provider='{this.profile.ProviderName}', ProviderLabel='{this.profile.ProviderLabel}', HostProvider='{(string.IsNullOrWhiteSpace(expectedHostProvider) ? "(none)" : expectedHostProvider)}', HostCreationMode='{expectedHostCreationMode}', ControlPlaneId='{controlPlaneId}', TenantId='{tenant.TenantId}', TenantGroupId='{tenant.TenantGroupId}', Pipeline='{pipelineName}'.");

            var dispatchedRun =
                await ProductionSharedRunTestHelpers
                    .SubmitAndDispatchOneRunAsync(
                        mcp,
                        scaleOutRequestStore,
                        tenant,
                        controlPlaneId,
                        pipelineName,
                        this.profile.RequestedBy,
                        this.profile.Source,
                        scenario.ScaleOutTimeout,
                        scenario.DispatchTimeout)
                    .ConfigureAwait(false);

            Assert.False(
                string.IsNullOrWhiteSpace(dispatchedRun.SharedRunId));

            Assert.False(
                string.IsNullOrWhiteSpace(dispatchedRun.AssignedRuntimeInstanceId));

            Assert.False(
                string.IsNullOrWhiteSpace(dispatchedRun.LocalRunId));

            var assignedRuntimeInstanceId =
                dispatchedRun.AssignedRuntimeInstanceId!;

            this.output.WriteLine(
                $"[{this.profile.LogPrefix} DISPATCH SCALE-OUT PROOF] Real shared run dispatched. SharedRunId='{dispatchedRun.SharedRunId}', RuntimeInstanceId='{assignedRuntimeInstanceId}', LocalRunId='{dispatchedRun.LocalRunId}', TenantId='{tenant.TenantId}'.");

            Assert.Contains(
                controlPlaneId,
                assignedRuntimeInstanceId,
                StringComparison.Ordinal);

            Assert.Contains(
                tenant.RuntimeInstanceIdPrefix,
                assignedRuntimeInstanceId,
                StringComparison.Ordinal);

            var resolvedExecution =
                await ProductionRecoveryWaitHelpers
                    .WaitForDurableDagExecutionAsync(
                        sharedRunStore,
                        runExecutionIndex,
                        dagStore,
                        dispatchedRun.SharedRunId,
                        TimeSpan.FromSeconds(30))
                    .ConfigureAwait(false);

            Assert.False(
                string.IsNullOrWhiteSpace(resolvedExecution.ExecutionId));

            Assert.Equal(
                dispatchedRun.SharedRunId,
                resolvedExecution.SharedRun.SharedRunId);

            Assert.Equal(
                assignedRuntimeInstanceId,
                resolvedExecution.SharedRun.AssignedRuntimeInstanceId);

            this.output.WriteLine(
                $"[{this.profile.LogPrefix} DISPATCH SCALE-OUT PROOF] Durable DAG execution resolved. SharedRunId='{resolvedExecution.SharedRun.SharedRunId}', RuntimeInstanceId='{resolvedExecution.SharedRun.AssignedRuntimeInstanceId}', LocalRunId='{resolvedExecution.SharedRun.LocalRunId}', ExecutionId='{resolvedExecution.ExecutionId}'.");

            await ProductionRecoveryWaitHelpers
                .WaitForDagCompletedStepCountAsync(
                    dagStore,
                    resolvedExecution.ExecutionId,
                    tenant.Run.StepCount,
                    scenario.CompletionTimeout)
                .ConfigureAwait(false);

            this.output.WriteLine(
                $"[{this.profile.LogPrefix} DISPATCH SCALE-OUT PROOF] Real DAG completed on scaled-out runtime. SharedRunId='{resolvedExecution.SharedRun.SharedRunId}', RuntimeInstanceId='{assignedRuntimeInstanceId}', ExecutionId='{resolvedExecution.ExecutionId}', CompletedSteps='{tenant.Run.StepCount}', Provider='{this.profile.ProviderName}', Transport='{this.profile.ProviderName}', HostProvider='{(string.IsNullOrWhiteSpace(expectedHostProvider) ? "(none)" : expectedHostProvider)}', HostCreationMode='{expectedHostCreationMode}', TenantId='{tenant.TenantId}'.");
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