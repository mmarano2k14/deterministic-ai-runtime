using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Multiplexed.AI.Runtime.ControlPlane.Admission;
using Multiplexed.AI.Runtime.ControlPlane.Admission.Reservations;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Process;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http.ScaleOut;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue.Redis;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Development.Http
{
    /// <summary>
    /// Contains production-oriented HTTP runtime provider tests for process-based runtime host scale-out.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Validate that HTTP scale-out can launch a real RuntimeInstanceOnly process.
    /// - Validate that tenant runtime settings are loaded from the current hardcoded tenant settings provider.
    /// - Validate that the effective tenant settings flow end-to-end into Redis scale-out records,
    ///   host start metadata, runtime registration snapshots, and runtime capacity descriptors.
    ///
    /// CURRENT TENANT SETTINGS SOURCE:
    /// - HardcodedAiTenantRuntimeSettingsProvider.
    ///
    /// FUTURE TENANT SETTINGS SOURCE:
    /// - Mongo/config-backed tenant settings provider.
    ///
    /// IMPORTANT:
    /// - These tests intentionally use real Redis-backed stores.
    /// - These tests intentionally launch a real runtime host process.
    /// - These tests must not fake registry/capacity records.
    /// - These tests must not bypass the shared queue or the scale-out watcher.
    /// </remarks>
    public sealed class HttpRuntimeProviderProcessHostProductionScenarioTests
    {
        private const string RequestedBy = "mcp-http-process-production-test";

        private const string Source = "mcp-http-process-production-test";

        private const string DedicatedTenantId = "tenant-a";

        private const string DedicatedRuntimeInstanceIdPrefix = "tenant-a-runtime";

        private const string UnknownTenantId = "tenant-process-unknown";

        private const string SharedRuntimeInstanceIdPrefix = "runtime-instance";

        private const int DefaultResolvedLocalQueueCapacity = 100;

        private readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpRuntimeProviderProcessHostProductionScenarioTests"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public HttpRuntimeProviderProcessHostProductionScenarioTests(
            ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>
        /// Verifies that process-based HTTP scale-out uses the hardcoded dedicated tenant runtime settings end-to-end.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task ControlPlaneWithHttpRuntimeInstances_With_Process_HostCreation_Mode_Should_Use_Hardcoded_Dedicated_Tenant_Runtime_Settings_End_To_End()
        {
            var controlPlaneId = GenericMcpServerTestSettings.CreateControlPlaneId("http-process-dedicated-tenant-production");
            var runtimeHostAssemblyPath = GenericMcpRuntimeHostAssemblyResolver.ResolveRuntimeHostAssemblyPath();
            var controlPlaneSettings = GenericMcpServerTestSettings.CreateHttpProcessHostScaleOutOnlyControlPlaneSettings(controlPlaneId, runtimeHostAssemblyPath);

            this.output.WriteLine("Tenant runtime settings source: HardcodedAiTenantRuntimeSettingsProvider.");
            this.output.WriteLine($"Resolved runtime host assembly path: '{runtimeHostAssemblyPath}'.");
            this.output.WriteLine($"TEST SETTING AiHttpRuntimeScaleOut:Mode='{controlPlaneSettings["AiHttpRuntimeScaleOut:Mode"]}'.");
            this.output.WriteLine($"TEST SETTING AiHttpRuntimeScaleOut:HostCreationMode='{controlPlaneSettings["AiHttpRuntimeScaleOut:HostCreationMode"]}'.");
            this.output.WriteLine($"TEST SETTING AiRuntimeProcessHostCreation:RuntimeHostAssemblyPath='{controlPlaneSettings["AiRuntimeProcessHostCreation:RuntimeHostAssemblyPath"]}'.");

            await using var host = new GenericMcpServerTestHost(controlPlaneSettings);
            using var client = host.CreateClient();

            AssertRedisStoresPublisherWatcherHttpProviderAndProcessHostManager(host.Services, runtimeHostAssemblyPath);

            var tenantRuntimeSettingsProvider = host.Services.GetRequiredService<IAiTenantRuntimeSettingsProvider>();
            var tenantRuntimeSettings = tenantRuntimeSettingsProvider.GetSettings(DedicatedTenantId, null);

            this.output.WriteLine(
                $"Tenant runtime settings loaded from HardcodedAiTenantRuntimeSettingsProvider. " +
                $"TenantId='{tenantRuntimeSettings.TenantId}', " +
                $"TenantGroupId='{tenantRuntimeSettings.TenantGroupId ?? "null"}', " +
                $"IsolationMode='{tenantRuntimeSettings.IsolationMode}', " +
                $"PreferDedicatedCapacity='{tenantRuntimeSettings.PreferDedicatedCapacity}', " +
                $"AllowSharedFallback='{tenantRuntimeSettings.AllowSharedFallback}', " +
                $"MaxRuntimeInstances='{tenantRuntimeSettings.MaxRuntimeInstances}', " +
                $"WorkerCountPerInstance='{tenantRuntimeSettings.WorkerCountPerInstance}', " +
                $"MaxConcurrentRunsPerInstance='{tenantRuntimeSettings.MaxConcurrentRunsPerInstance}', " +
                $"LocalQueueCapacity='{tenantRuntimeSettings.LocalQueueCapacity?.ToString() ?? "null"}', " +
                $"RuntimeInstanceIdPrefix='{tenantRuntimeSettings.RuntimeInstanceIdPrefix}'.");

            Assert.Equal(DedicatedTenantId, tenantRuntimeSettings.TenantId);
            Assert.Equal(AiRuntimeInstanceIsolationMode.Dedicated, tenantRuntimeSettings.IsolationMode);
            Assert.True(tenantRuntimeSettings.PreferDedicatedCapacity);
            Assert.False(tenantRuntimeSettings.AllowSharedFallback);
            Assert.Equal(3, tenantRuntimeSettings.MaxRuntimeInstances);
            Assert.Equal(10, tenantRuntimeSettings.WorkerCountPerInstance);
            Assert.Equal(5, tenantRuntimeSettings.MaxConcurrentRunsPerInstance);
            Assert.True(tenantRuntimeSettings.LocalQueueCapacity.HasValue);
            Assert.Equal(500, tenantRuntimeSettings.LocalQueueCapacity.Value);
            Assert.Equal(DedicatedRuntimeInstanceIdPrefix, tenantRuntimeSettings.RuntimeInstanceIdPrefix);

            var expectedResolvedLocalQueueCapacity = tenantRuntimeSettings.LocalQueueCapacity ?? DefaultResolvedLocalQueueCapacity;

            var mcp = await McpRbacTestClientHelper
                .CreateConfiguredClientAsync(host, client, RequestedBy, tenantId: DedicatedTenantId)
                .ConfigureAwait(false);

            var sharedRunStore = host.Services.GetRequiredService<IAiSharedRunStore>();
            var scaleOutRequestStore = host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var pipelineName = $"mcp-http-process-dedicated-tenant-{Guid.NewGuid():N}";

            var result = await SubmitSingleRunAndWaitForFulfilledScaleOutAsync(
                    mcp,
                    sharedRunStore,
                    scaleOutRequestStore,
                    controlPlaneId,
                    pipelineName,
                    DedicatedTenantId,
                    TimeSpan.FromSeconds(45))
                .ConfigureAwait(false);

            var expectedTenantGroupId = result.SharedRun.ExecutionContextSnapshot.TenantGroupId;

            Assert.False(string.IsNullOrWhiteSpace(expectedTenantGroupId));
            Assert.NotNull(result.SharedRun.AdmissionDecision);
            Assert.Equal(AiRunAdmissionDecisionType.RequestScaleOut, result.SharedRun.AdmissionDecision.DecisionType);
            Assert.Equal(DedicatedTenantId, result.SharedRun.AdmissionDecision.TenantId);
            Assert.Equal(expectedTenantGroupId, result.SharedRun.AdmissionDecision.TenantGroupId);
            Assert.NotNull(result.SharedRun.AdmissionDecision.TenantRuntimeSettings);

            AssertTenantSettings(
                result.SharedRun.AdmissionDecision.TenantRuntimeSettings!,
                DedicatedTenantId,
                AiRuntimeInstanceIsolationMode.Dedicated,
                preferDedicatedCapacity: true,
                allowSharedFallback: false,
                maxRuntimeInstances: 3,
                workerCountPerInstance: 10,
                maxConcurrentRunsPerInstance: 5,
                localQueueCapacity: 500,
                runtimeInstanceIdPrefix: DedicatedRuntimeInstanceIdPrefix);

            AssertScaleOutRequestUsesTenantSettings(
                result.ScaleOutRequest,
                DedicatedTenantId,
                expectedTenantGroupId,
                AiRuntimeInstanceIsolationMode.Dedicated,
                preferDedicatedCapacity: true,
                allowSharedFallback: false,
                maxRuntimeInstances: 3,
                expectedScaleOutMaxInstanceCount: 3,
                workerCountPerInstance: 10,
                maxConcurrentRunsPerInstance: 5,
                localQueueCapacity: 500,
                runtimeInstanceIdPrefix: DedicatedRuntimeInstanceIdPrefix);

            var fulfilledRuntimeInstanceId = result.ScaleOutRequest.FulfilledRuntimeInstanceId!;

            Assert.Contains($":{DedicatedRuntimeInstanceIdPrefix}-1", fulfilledRuntimeInstanceId, StringComparison.Ordinal);
            Assert.DoesNotContain($":{SharedRuntimeInstanceIdPrefix}-1", fulfilledRuntimeInstanceId, StringComparison.Ordinal);

            var stores = CreateTenantVisibleRedisRuntimeStores(host.Services, DedicatedTenantId, expectedTenantGroupId, "http-process-dedicated-tenant-production");

            var registered = await stores.Registry
                .GetAsync(fulfilledRuntimeInstanceId)
                .ConfigureAwait(false);

            var capacity = await stores.CapacityStore
                .GetAsync(fulfilledRuntimeInstanceId)
                .ConfigureAwait(false);

            Assert.NotNull(registered);
            Assert.NotNull(capacity);

            AssertRuntimeRegistrationAndCapacityUseTenantSettings(
                registered!,
                capacity!,
                fulfilledRuntimeInstanceId,
                DedicatedTenantId,
                expectedTenantGroupId,
                AiRuntimeInstanceIsolationMode.Dedicated,
                preferDedicatedCapacity: true,
                allowSharedFallback: false,
                maxRuntimeInstances: 3,
                workerCountPerInstance: 10,
                maxConcurrentRunsPerInstance: 5,
                localQueueCapacity: expectedResolvedLocalQueueCapacity,
                runtimeInstanceIdPrefix: DedicatedRuntimeInstanceIdPrefix);

            this.output.WriteLine(
                $"PROCESS HOST DEDICATED TENANT SETTINGS END-TO-END VALIDATED. " +
                $"ControlPlaneId='{controlPlaneId}', SharedRunId='{result.SharedRunId}', " +
                $"RequestId='{result.ScaleOutRequest.RequestId}', TenantId='{DedicatedTenantId}', " +
                $"TenantGroupId='{expectedTenantGroupId}', RuntimeInstanceId='{fulfilledRuntimeInstanceId}', " +
                $"PipelineKey='{pipelineName}', RuntimeHostAssemblyPath='{runtimeHostAssemblyPath}'.");
        }

        /// <summary>
        /// Verifies that process-based HTTP scale-out uses the current hardcoded shared fallback settings
        /// when the tenant is not explicitly configured in the hardcoded tenant settings provider.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task ControlPlaneWithHttpRuntimeInstances_With_Process_HostCreation_Mode_Should_Use_Hardcoded_Shared_Fallback_Settings_When_Tenant_Is_Not_Configured()
        {
            var controlPlaneId = GenericMcpServerTestSettings.CreateControlPlaneId("http-process-shared-fallback-production");
            var runtimeHostAssemblyPath = GenericMcpRuntimeHostAssemblyResolver.ResolveRuntimeHostAssemblyPath();
            var controlPlaneSettings = GenericMcpServerTestSettings.CreateHttpProcessHostScaleOutOnlyControlPlaneSettings(controlPlaneId, runtimeHostAssemblyPath);

            this.output.WriteLine("Tenant runtime settings source: HardcodedAiTenantRuntimeSettingsProvider.");
            this.output.WriteLine($"Resolved runtime host assembly path: '{runtimeHostAssemblyPath}'.");

            await using var host = new GenericMcpServerTestHost(controlPlaneSettings);
            using var client = host.CreateClient();

            AssertRedisStoresPublisherWatcherHttpProviderAndProcessHostManager(host.Services, runtimeHostAssemblyPath);

            var tenantRuntimeSettingsProvider = host.Services.GetRequiredService<IAiTenantRuntimeSettingsProvider>();
            var tenantRuntimeSettings = tenantRuntimeSettingsProvider.GetSettings(UnknownTenantId, null);

            this.output.WriteLine(
                $"Tenant runtime settings loaded from HardcodedAiTenantRuntimeSettingsProvider shared fallback. " +
                $"TenantId='{tenantRuntimeSettings.TenantId}', " +
                $"TenantGroupId='{tenantRuntimeSettings.TenantGroupId ?? "null"}', " +
                $"IsolationMode='{tenantRuntimeSettings.IsolationMode}', " +
                $"PreferDedicatedCapacity='{tenantRuntimeSettings.PreferDedicatedCapacity}', " +
                $"AllowSharedFallback='{tenantRuntimeSettings.AllowSharedFallback}', " +
                $"MaxRuntimeInstances='{tenantRuntimeSettings.MaxRuntimeInstances}', " +
                $"WorkerCountPerInstance='{tenantRuntimeSettings.WorkerCountPerInstance}', " +
                $"MaxConcurrentRunsPerInstance='{tenantRuntimeSettings.MaxConcurrentRunsPerInstance}', " +
                $"LocalQueueCapacity='{tenantRuntimeSettings.LocalQueueCapacity?.ToString() ?? "null"}', " +
                $"RuntimeInstanceIdPrefix='{tenantRuntimeSettings.RuntimeInstanceIdPrefix}'.");

            Assert.Equal(UnknownTenantId, tenantRuntimeSettings.TenantId);
            Assert.Equal(AiRuntimeInstanceIsolationMode.Shared, tenantRuntimeSettings.IsolationMode);
            Assert.False(tenantRuntimeSettings.PreferDedicatedCapacity);
            Assert.True(tenantRuntimeSettings.AllowSharedFallback);
            Assert.Equal(1, tenantRuntimeSettings.MaxRuntimeInstances);
            Assert.Equal(10, tenantRuntimeSettings.WorkerCountPerInstance);
            Assert.Equal(3, tenantRuntimeSettings.MaxConcurrentRunsPerInstance);
            Assert.Null(tenantRuntimeSettings.LocalQueueCapacity);
            Assert.Equal(SharedRuntimeInstanceIdPrefix, tenantRuntimeSettings.RuntimeInstanceIdPrefix);

            var expectedResolvedLocalQueueCapacity = tenantRuntimeSettings.LocalQueueCapacity ?? DefaultResolvedLocalQueueCapacity;

            var mcp = await McpRbacTestClientHelper
                .CreateConfiguredClientAsync(host, client, RequestedBy, tenantId: UnknownTenantId)
                .ConfigureAwait(false);

            var sharedRunStore = host.Services.GetRequiredService<IAiSharedRunStore>();
            var scaleOutRequestStore = host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var pipelineName = $"mcp-http-process-shared-fallback-{Guid.NewGuid():N}";

            var result = await SubmitSingleRunAndWaitForFulfilledScaleOutAsync(
                    mcp,
                    sharedRunStore,
                    scaleOutRequestStore,
                    controlPlaneId,
                    pipelineName,
                    UnknownTenantId,
                    TimeSpan.FromSeconds(45))
                .ConfigureAwait(false);

            var expectedTenantGroupId = result.SharedRun.ExecutionContextSnapshot.TenantGroupId;

            Assert.False(string.IsNullOrWhiteSpace(expectedTenantGroupId));
            Assert.NotNull(result.SharedRun.AdmissionDecision);
            Assert.NotNull(result.SharedRun.AdmissionDecision.TenantRuntimeSettings);

            AssertTenantSettings(
                result.SharedRun.AdmissionDecision.TenantRuntimeSettings!,
                UnknownTenantId,
                tenantRuntimeSettings.IsolationMode,
                tenantRuntimeSettings.PreferDedicatedCapacity,
                tenantRuntimeSettings.AllowSharedFallback,
                tenantRuntimeSettings.MaxRuntimeInstances,
                tenantRuntimeSettings.WorkerCountPerInstance,
                tenantRuntimeSettings.MaxConcurrentRunsPerInstance,
                tenantRuntimeSettings.LocalQueueCapacity,
                tenantRuntimeSettings.RuntimeInstanceIdPrefix);

            AssertScaleOutRequestUsesTenantSettings(
                result.ScaleOutRequest,
                UnknownTenantId,
                expectedTenantGroupId,
                tenantRuntimeSettings.IsolationMode,
                tenantRuntimeSettings.PreferDedicatedCapacity,
                tenantRuntimeSettings.AllowSharedFallback,
                maxRuntimeInstances: tenantRuntimeSettings.MaxRuntimeInstances,
                expectedScaleOutMaxInstanceCount: 3,
                workerCountPerInstance: tenantRuntimeSettings.WorkerCountPerInstance,
                maxConcurrentRunsPerInstance: tenantRuntimeSettings.MaxConcurrentRunsPerInstance,
                localQueueCapacity: tenantRuntimeSettings.LocalQueueCapacity,
                runtimeInstanceIdPrefix: tenantRuntimeSettings.RuntimeInstanceIdPrefix);

            var fulfilledRuntimeInstanceId = result.ScaleOutRequest.FulfilledRuntimeInstanceId!;

            Assert.Contains($":{SharedRuntimeInstanceIdPrefix}-1", fulfilledRuntimeInstanceId, StringComparison.Ordinal);

            var stores = CreateTenantVisibleRedisRuntimeStores(host.Services, UnknownTenantId, expectedTenantGroupId, "http-process-shared-fallback-production");

            var registered = await stores.Registry
                .GetAsync(fulfilledRuntimeInstanceId)
                .ConfigureAwait(false);

            var capacity = await stores.CapacityStore
                .GetAsync(fulfilledRuntimeInstanceId)
                .ConfigureAwait(false);

            Assert.NotNull(registered);
            Assert.NotNull(capacity);

            AssertRuntimeRegistrationAndCapacityUseTenantSettings(
                registered!,
                capacity!,
                fulfilledRuntimeInstanceId,
                UnknownTenantId,
                expectedTenantGroupId,
                tenantRuntimeSettings.IsolationMode,
                tenantRuntimeSettings.PreferDedicatedCapacity,
                tenantRuntimeSettings.AllowSharedFallback,
                tenantRuntimeSettings.MaxRuntimeInstances,
                tenantRuntimeSettings.WorkerCountPerInstance,
                tenantRuntimeSettings.MaxConcurrentRunsPerInstance,
                expectedResolvedLocalQueueCapacity,
                tenantRuntimeSettings.RuntimeInstanceIdPrefix);

            this.output.WriteLine(
                $"PROCESS HOST SHARED FALLBACK TENANT SETTINGS END-TO-END VALIDATED. " +
                $"ControlPlaneId='{controlPlaneId}', SharedRunId='{result.SharedRunId}', " +
                $"RequestId='{result.ScaleOutRequest.RequestId}', TenantId='{UnknownTenantId}', " +
                $"TenantGroupId='{expectedTenantGroupId}', RuntimeInstanceId='{fulfilledRuntimeInstanceId}', " +
                $"PipelineKey='{pipelineName}', RuntimeHostAssemblyPath='{runtimeHostAssemblyPath}'.");
        }

        /// <summary>
        /// Verifies that a queued dedicated tenant run is dispatched to the real process-based runtime instance after scale-out completes.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task ControlPlaneWithHttpRuntimeInstances_With_Process_HostCreation_Mode_Should_Dispatch_Dedicated_Tenant_Run_After_Runtime_Process_Becomes_Ready()
        {
            var controlPlaneId = GenericMcpServerTestSettings.CreateControlPlaneId("http-process-dedicated-dispatch-production");
            var runtimeHostAssemblyPath = GenericMcpRuntimeHostAssemblyResolver.ResolveRuntimeHostAssemblyPath();
            var controlPlaneSettings = GenericMcpServerTestSettings.CreateHttpProcessHostScaleOutOnlyControlPlaneSettings(controlPlaneId, runtimeHostAssemblyPath);

            controlPlaneSettings["AiMcpHost:EnableSharedQueuePump"] = "true";
            controlPlaneSettings["AiSharedQueueBackgroundService:Enabled"] = "true";
            controlPlaneSettings["AiSharedQueuePump:Enabled"] = "true";
            controlPlaneSettings["AiSharedQueueBackgroundService:IntervalSeconds"] = "1";
            controlPlaneSettings["AiSharedQueueBackgroundService:MaxDispatchesPerCycle"] = "10";

            controlPlaneSettings["AiHttpRuntimeInstanceProvider:EnableCircuitBreaker"] = "false";
            controlPlaneSettings["AiHttpRuntimeInstanceProvider:CircuitBreakerFailureThreshold"] = "100";

            this.output.WriteLine("Tenant runtime settings source: HardcodedAiTenantRuntimeSettingsProvider.");
            this.output.WriteLine($"Resolved runtime host assembly path: '{runtimeHostAssemblyPath}'.");
            this.output.WriteLine($"TEST SETTING AiHttpRuntimeScaleOut:Mode='{controlPlaneSettings["AiHttpRuntimeScaleOut:Mode"]}'.");
            this.output.WriteLine($"TEST SETTING AiHttpRuntimeScaleOut:HostCreationMode='{controlPlaneSettings["AiHttpRuntimeScaleOut:HostCreationMode"]}'.");
            this.output.WriteLine($"TEST SETTING AiRuntimeProcessHostCreation:RuntimeHostAssemblyPath='{controlPlaneSettings["AiRuntimeProcessHostCreation:RuntimeHostAssemblyPath"]}'.");

            await using var host = new GenericMcpServerTestHost(controlPlaneSettings);
            using var client = host.CreateClient();

            AssertRedisStoresPublisherWatcherHttpProviderAndProcessHostManager(host.Services, runtimeHostAssemblyPath);

            var tenantRuntimeSettingsProvider = host.Services.GetRequiredService<IAiTenantRuntimeSettingsProvider>();
            var tenantRuntimeSettings = tenantRuntimeSettingsProvider.GetSettings(DedicatedTenantId, null);

            Assert.Equal(DedicatedTenantId, tenantRuntimeSettings.TenantId);
            Assert.Equal(AiRuntimeInstanceIsolationMode.Dedicated, tenantRuntimeSettings.IsolationMode);
            Assert.True(tenantRuntimeSettings.PreferDedicatedCapacity);
            Assert.False(tenantRuntimeSettings.AllowSharedFallback);
            Assert.Equal(DedicatedRuntimeInstanceIdPrefix, tenantRuntimeSettings.RuntimeInstanceIdPrefix);

            var mcp = await McpRbacTestClientHelper
                .CreateConfiguredClientAsync(host, client, RequestedBy, tenantId: DedicatedTenantId)
                .ConfigureAwait(false);

            var sharedRunStore = host.Services.GetRequiredService<IAiSharedRunStore>();
            var scaleOutRequestStore = host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var pipelineName = $"mcp-http-process-dedicated-dispatch-{Guid.NewGuid():N}";

            var result = await SubmitSingleRunAndWaitForFulfilledScaleOutAsync(
                    mcp,
                    sharedRunStore,
                    scaleOutRequestStore,
                    controlPlaneId,
                    pipelineName,
                    DedicatedTenantId,
                    TimeSpan.FromSeconds(45))
                .ConfigureAwait(false);

            Assert.NotNull(result.ScaleOutRequest.FulfilledRuntimeInstanceId);

            var fulfilledRuntimeInstanceId = result.ScaleOutRequest.FulfilledRuntimeInstanceId!;

            Assert.Contains($":{DedicatedRuntimeInstanceIdPrefix}-1", fulfilledRuntimeInstanceId, StringComparison.Ordinal);
            Assert.DoesNotContain($":{SharedRuntimeInstanceIdPrefix}-1", fulfilledRuntimeInstanceId, StringComparison.Ordinal);

            var expectedTenantGroupId = result.SharedRun.ExecutionContextSnapshot.TenantGroupId;

            Assert.False(string.IsNullOrWhiteSpace(expectedTenantGroupId));

            var stores = CreateTenantVisibleRedisRuntimeStores(
                host.Services,
                DedicatedTenantId,
                expectedTenantGroupId,
                "http-process-dedicated-dispatch-production");

            var registered = await stores.Registry.GetAsync(fulfilledRuntimeInstanceId).ConfigureAwait(false);
            var capacity = await stores.CapacityStore.GetAsync(fulfilledRuntimeInstanceId).ConfigureAwait(false);


            Assert.NotNull(registered);
            Assert.NotNull(capacity);
            Assert.Equal(AiRuntimeInstanceStatus.Ready, registered!.Status);
            Assert.True(registered.CanAcceptRun);
            Assert.Equal(AiRuntimeInstanceStatus.Ready, capacity!.Status);
            Assert.True(capacity.CanAcceptRun);

            this.output.WriteLine($"REGISTERED transport.endpoint='{registered!.Metadata.GetValueOrDefault("transport.endpoint")}'.");
            this.output.WriteLine($"REGISTERED command.transport.endpoint='{registered.Metadata.GetValueOrDefault(AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint)}'.");
            this.output.WriteLine($"CAPACITY transport.endpoint='{capacity!.Metadata.GetValueOrDefault("transport.endpoint")}'.");
            this.output.WriteLine($"CAPACITY command.transport.endpoint='{capacity.Metadata.GetValueOrDefault(AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint)}'.");

            Assert.StartsWith("http://localhost:", capacity.Metadata.GetValueOrDefault(AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint), StringComparison.OrdinalIgnoreCase);

            var dispatchedRuns = await McpTestWaitHelpers
                .WaitForDispatchedRunsAsync(
                    mcp,
                    pipelineName,
                    new HashSet<string>(StringComparer.Ordinal)
                    {
                result.SharedRunId
                    },
                    expectedCount: 1,
                    timeout: TimeSpan.FromSeconds(60))
                .ConfigureAwait(false);

            var dispatchedRun = Assert.Single(dispatchedRuns);

            Assert.Equal(result.SharedRunId, dispatchedRun.SharedRunId);
            Assert.Equal(fulfilledRuntimeInstanceId, dispatchedRun.AssignedRuntimeInstanceId);
            Assert.NotEqual(AiSharedRunStatus.ScaleOutRequested, dispatchedRun.Status);
            Assert.NotEqual(AiSharedRunStatus.QueuedGlobally, dispatchedRun.Status);

            this.output.WriteLine(
                $"PROCESS HOST DEDICATED DISPATCH END-TO-END VALIDATED. " +
                $"ControlPlaneId='{controlPlaneId}', SharedRunId='{result.SharedRunId}', " +
                $"RequestId='{result.ScaleOutRequest.RequestId}', TenantId='{DedicatedTenantId}', " +
                $"TenantGroupId='{expectedTenantGroupId}', RuntimeInstanceId='{fulfilledRuntimeInstanceId}', " +
                $"SharedRunStatus='{dispatchedRun.Status}', PipelineKey='{pipelineName}', " +
                $"RuntimeHostAssemblyPath='{runtimeHostAssemblyPath}'.");
        }

        /// <summary>
        /// Verifies that process-based HTTP scale-out rejects the request when the runtime host assembly path is invalid.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task ControlPlaneWithHttpRuntimeInstances_With_Process_HostCreation_Mode_Should_Reject_ScaleOut_When_Runtime_Assembly_Path_Is_Invalid()
        {
            var controlPlaneId = GenericMcpServerTestSettings.CreateControlPlaneId("http-process-invalid-assembly-production");
            var validRuntimeHostAssemblyPath = GenericMcpRuntimeHostAssemblyResolver.ResolveRuntimeHostAssemblyPath();
            var invalidRuntimeHostAssemblyPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"missing-runtime-host-{Guid.NewGuid():N}",
                "Multiplexed.AI.McpServer.Host.dll");

            var controlPlaneSettings = GenericMcpServerTestSettings.CreateHttpProcessHostScaleOutOnlyControlPlaneSettings(
                controlPlaneId,
                validRuntimeHostAssemblyPath);

            controlPlaneSettings["AiRuntimeProcessHostCreation:RuntimeHostAssemblyPath"] = invalidRuntimeHostAssemblyPath;

            this.output.WriteLine("Process provisioning failure scenario: invalid runtime host assembly path.");
            this.output.WriteLine($"Valid runtime host assembly path resolved by test resolver: '{validRuntimeHostAssemblyPath}'.");
            this.output.WriteLine($"Invalid runtime host assembly path used by test: '{invalidRuntimeHostAssemblyPath}'.");
            this.output.WriteLine($"TEST SETTING AiHttpRuntimeScaleOut:Mode='{controlPlaneSettings["AiHttpRuntimeScaleOut:Mode"]}'.");
            this.output.WriteLine($"TEST SETTING AiHttpRuntimeScaleOut:HostCreationMode='{controlPlaneSettings["AiHttpRuntimeScaleOut:HostCreationMode"]}'.");
            this.output.WriteLine($"TEST SETTING AiRuntimeProcessHostCreation:RuntimeHostAssemblyPath='{controlPlaneSettings["AiRuntimeProcessHostCreation:RuntimeHostAssemblyPath"]}'.");

            await using var host = new GenericMcpServerTestHost(controlPlaneSettings);
            using var client = host.CreateClient();

            AssertRedisStoresPublisherWatcherHttpProviderAndProcessHostManager(
                host.Services,
                invalidRuntimeHostAssemblyPath);

            var mcp = await McpRbacTestClientHelper
                .CreateConfiguredClientAsync(
                    host,
                    client,
                    RequestedBy,
                    tenantId: DedicatedTenantId)
                .ConfigureAwait(false);

            var sharedRunStore = host.Services.GetRequiredService<IAiSharedRunStore>();
            var scaleOutRequestStore = host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();
            var runtimeInstanceRegistry = host.Services.GetRequiredService<IAiRuntimeInstanceRegistry>();
            var runtimeInstanceCapacityStore = host.Services.GetRequiredService<IAiRuntimeInstanceCapacityStore>();

            var pipelineName = $"mcp-http-process-invalid-assembly-{Guid.NewGuid():N}";

            var submittedSharedRunIds = await SubmitRunsAsync(
                    mcp,
                    pipelineName,
                    count: 1,
                    stepCount: 3,
                    flakyStepInterval: 0,
                    tenantId: DedicatedTenantId)
                .ConfigureAwait(false);

            var sharedRunId = Assert.Single(submittedSharedRunIds);

            var sharedRun = await sharedRunStore
                .GetAsync(sharedRunId)
                .ConfigureAwait(false);

            Assert.NotNull(sharedRun);
            Assert.Equal(AiSharedRunStatus.ScaleOutRequested, sharedRun!.Status);
            Assert.Equal(controlPlaneId, sharedRun.ControlPlaneId);
            Assert.Equal(pipelineName, sharedRun.PipelineKey);
            Assert.Equal(DedicatedTenantId, sharedRun.ExecutionContextSnapshot.TenantId);
            Assert.NotNull(sharedRun.AdmissionDecision);
            Assert.Equal(AiRunAdmissionDecisionType.RequestScaleOut, sharedRun.AdmissionDecision.DecisionType);

            var expectedScaleOutRequestId = $"scale-out-{sharedRunId}";

            var rejectedScaleOutRequest = await WaitForScaleOutRequestStatusAsync(
                    scaleOutRequestStore,
                    expectedScaleOutRequestId,
                    AiRuntimeScaleOutRequestStatus.Rejected,
                    TimeSpan.FromSeconds(15))
                .ConfigureAwait(false);

            Assert.Equal(expectedScaleOutRequestId, rejectedScaleOutRequest.RequestId);
            Assert.Equal(sharedRunId, rejectedScaleOutRequest.SharedRunId);
            Assert.Equal(controlPlaneId, rejectedScaleOutRequest.ControlPlaneId);
            Assert.Equal(DedicatedTenantId, rejectedScaleOutRequest.TenantId);
            Assert.Equal(pipelineName, rejectedScaleOutRequest.PipelineKey);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Rejected, rejectedScaleOutRequest.Status);
            Assert.Equal("http", rejectedScaleOutRequest.ProviderHint);
            Assert.Equal("http", rejectedScaleOutRequest.Metadata["providerHint"]);
            Assert.Equal("mcp-scaleout-watcher", rejectedScaleOutRequest.RejectedBy);
            Assert.True(
                rejectedScaleOutRequest.RejectionReason?.StartsWith(
                    "process-runtime-host-assembly-not-found:",
                    StringComparison.Ordinal) == true,
                $"Unexpected rejection reason: '{rejectedScaleOutRequest.RejectionReason}'.");

            Assert.True(string.IsNullOrWhiteSpace(rejectedScaleOutRequest.FulfilledRuntimeInstanceId));
            Assert.True(string.IsNullOrWhiteSpace(rejectedScaleOutRequest.FulfilledBy));

            var expectedRuntimeInstanceId = $"{controlPlaneId}:{DedicatedRuntimeInstanceIdPrefix}-1";

            var registered = await runtimeInstanceRegistry
                .GetAsync(expectedRuntimeInstanceId)
                .ConfigureAwait(false);

            var capacity = await runtimeInstanceCapacityStore
                .GetAsync(expectedRuntimeInstanceId)
                .ConfigureAwait(false);

            Assert.Null(registered);
            Assert.Null(capacity);

            var finalSharedRun = await sharedRunStore
                .GetAsync(sharedRunId)
                .ConfigureAwait(false);

            Assert.NotNull(finalSharedRun);
            Assert.NotEqual(AiSharedRunStatus.Dispatched, finalSharedRun!.Status);
            Assert.NotEqual(AiSharedRunStatus.Completed, finalSharedRun.Status);
            Assert.True(string.IsNullOrWhiteSpace(finalSharedRun.AssignedRuntimeInstanceId));
            Assert.True(string.IsNullOrWhiteSpace(finalSharedRun.LocalRunId));
            Assert.True(string.IsNullOrWhiteSpace(finalSharedRun.ExecutionId));

            this.output.WriteLine(
                $"PROCESS HOST INVALID ASSEMBLY PROVISIONING FAILURE VALIDATED. " +
                $"ControlPlaneId='{controlPlaneId}', SharedRunId='{sharedRunId}', " +
                $"RequestId='{rejectedScaleOutRequest.RequestId}', TenantId='{DedicatedTenantId}', " +
                $"ExpectedRuntimeInstanceId='{expectedRuntimeInstanceId}', " +
                $"RejectedReason='{rejectedScaleOutRequest.RejectionReason}', " +
                $"InvalidRuntimeHostAssemblyPath='{invalidRuntimeHostAssemblyPath}'.");
        }

        /// <summary>
        /// Verifies that process-based HTTP scale-out rejects the request when the runtime host assembly path is invalid.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task ControlPlaneWithHttpRuntimeInstances_With_Process_HostCreation_Mode_Should_Not_Fulfill_ScaleOut_When_Runtime_Host_Assembly_Is_Missing()
        {
            var controlPlaneId = GenericMcpServerTestSettings.CreateControlPlaneId("http-process-invalid-assembly-production");
            var validRuntimeHostAssemblyPath = GenericMcpRuntimeHostAssemblyResolver.ResolveRuntimeHostAssemblyPath();
            var invalidRuntimeHostAssemblyPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"missing-runtime-host-{Guid.NewGuid():N}",
                "Multiplexed.AI.McpServer.Host.dll");

            var controlPlaneSettings = GenericMcpServerTestSettings.CreateHttpProcessHostScaleOutOnlyControlPlaneSettings(
                controlPlaneId,
                validRuntimeHostAssemblyPath);

            controlPlaneSettings["AiRuntimeProcessHostCreation:RuntimeHostAssemblyPath"] = invalidRuntimeHostAssemblyPath;

            this.output.WriteLine("Process provisioning failure scenario: invalid runtime host assembly path.");
            this.output.WriteLine($"Valid runtime host assembly path resolved by test resolver: '{validRuntimeHostAssemblyPath}'.");
            this.output.WriteLine($"Invalid runtime host assembly path used by test: '{invalidRuntimeHostAssemblyPath}'.");
            this.output.WriteLine($"TEST SETTING AiHttpRuntimeScaleOut:Mode='{controlPlaneSettings["AiHttpRuntimeScaleOut:Mode"]}'.");
            this.output.WriteLine($"TEST SETTING AiHttpRuntimeScaleOut:HostCreationMode='{controlPlaneSettings["AiHttpRuntimeScaleOut:HostCreationMode"]}'.");
            this.output.WriteLine($"TEST SETTING AiRuntimeProcessHostCreation:RuntimeHostAssemblyPath='{controlPlaneSettings["AiRuntimeProcessHostCreation:RuntimeHostAssemblyPath"]}'.");

            await using var host = new GenericMcpServerTestHost(controlPlaneSettings);
            using var client = host.CreateClient();

            AssertRedisStoresPublisherWatcherHttpProviderAndProcessHostManager(
                host.Services,
                invalidRuntimeHostAssemblyPath);

            var mcp = await McpRbacTestClientHelper
                .CreateConfiguredClientAsync(
                    host,
                    client,
                    RequestedBy,
                    tenantId: DedicatedTenantId)
                .ConfigureAwait(false);

            var sharedRunStore = host.Services.GetRequiredService<IAiSharedRunStore>();
            var scaleOutRequestStore = host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();
            var runtimeInstanceRegistry = host.Services.GetRequiredService<IAiRuntimeInstanceRegistry>();
            var runtimeInstanceCapacityStore = host.Services.GetRequiredService<IAiRuntimeInstanceCapacityStore>();

            var pipelineName = $"mcp-http-process-invalid-assembly-{Guid.NewGuid():N}";

            var submittedSharedRunIds = await SubmitRunsAsync(
                    mcp,
                    pipelineName,
                    count: 1,
                    stepCount: 3,
                    flakyStepInterval: 0,
                    tenantId: DedicatedTenantId)
                .ConfigureAwait(false);

            var sharedRunId = Assert.Single(submittedSharedRunIds);

            var sharedRun = await sharedRunStore
                .GetAsync(sharedRunId)
                .ConfigureAwait(false);

            Assert.NotNull(sharedRun);
            Assert.Equal(AiSharedRunStatus.ScaleOutRequested, sharedRun!.Status);
            Assert.Equal(controlPlaneId, sharedRun.ControlPlaneId);
            Assert.Equal(pipelineName, sharedRun.PipelineKey);
            Assert.Equal(DedicatedTenantId, sharedRun.ExecutionContextSnapshot.TenantId);
            Assert.NotNull(sharedRun.AdmissionDecision);
            Assert.Equal(AiRunAdmissionDecisionType.RequestScaleOut, sharedRun.AdmissionDecision.DecisionType);

            var expectedScaleOutRequestId = $"scale-out-{sharedRunId}";

            var rejectedScaleOutRequest = await WaitForScaleOutRequestStatusAsync(
                    scaleOutRequestStore,
                    expectedScaleOutRequestId,
                    AiRuntimeScaleOutRequestStatus.Rejected,
                    TimeSpan.FromSeconds(15))
                .ConfigureAwait(false);

            Assert.Equal(expectedScaleOutRequestId, rejectedScaleOutRequest.RequestId);
            Assert.Equal(sharedRunId, rejectedScaleOutRequest.SharedRunId);
            Assert.Equal(controlPlaneId, rejectedScaleOutRequest.ControlPlaneId);
            Assert.Equal(DedicatedTenantId, rejectedScaleOutRequest.TenantId);
            Assert.Equal(pipelineName, rejectedScaleOutRequest.PipelineKey);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Rejected, rejectedScaleOutRequest.Status);
            Assert.Equal("http", rejectedScaleOutRequest.ProviderHint);
            Assert.Equal("http", rejectedScaleOutRequest.Metadata["providerHint"]);
            Assert.Equal("mcp-scaleout-watcher", rejectedScaleOutRequest.RejectedBy);
            Assert.True(
                rejectedScaleOutRequest.RejectionReason?.StartsWith(
                    "process-runtime-host-assembly-not-found:",
                    StringComparison.Ordinal) == true,
                $"Unexpected rejection reason: '{rejectedScaleOutRequest.RejectionReason}'.");

            Assert.True(string.IsNullOrWhiteSpace(rejectedScaleOutRequest.FulfilledRuntimeInstanceId));
            Assert.True(string.IsNullOrWhiteSpace(rejectedScaleOutRequest.FulfilledBy));

            var expectedRuntimeInstanceId = $"{controlPlaneId}:{DedicatedRuntimeInstanceIdPrefix}-1";

            var registered = await runtimeInstanceRegistry
                .GetAsync(expectedRuntimeInstanceId)
                .ConfigureAwait(false);

            var capacity = await runtimeInstanceCapacityStore
                .GetAsync(expectedRuntimeInstanceId)
                .ConfigureAwait(false);

            Assert.Null(registered);
            Assert.Null(capacity);

            var finalSharedRun = await sharedRunStore
                .GetAsync(sharedRunId)
                .ConfigureAwait(false);

            Assert.NotNull(finalSharedRun);
            Assert.NotEqual(AiSharedRunStatus.Dispatched, finalSharedRun!.Status);
            Assert.NotEqual(AiSharedRunStatus.Completed, finalSharedRun.Status);
            Assert.True(string.IsNullOrWhiteSpace(finalSharedRun.AssignedRuntimeInstanceId));
            Assert.True(string.IsNullOrWhiteSpace(finalSharedRun.LocalRunId));
            Assert.True(string.IsNullOrWhiteSpace(finalSharedRun.ExecutionId));

            this.output.WriteLine(
                $"PROCESS HOST INVALID ASSEMBLY PROVISIONING FAILURE VALIDATED. " +
                $"ControlPlaneId='{controlPlaneId}', SharedRunId='{sharedRunId}', " +
                $"RequestId='{rejectedScaleOutRequest.RequestId}', TenantId='{DedicatedTenantId}', " +
                $"ExpectedRuntimeInstanceId='{expectedRuntimeInstanceId}', " +
                $"RejectedReason='{rejectedScaleOutRequest.RejectionReason}', " +
                $"InvalidRuntimeHostAssemblyPath='{invalidRuntimeHostAssemblyPath}'.");
        }

        /// <summary>
        /// Verifies that process-based HTTP scale-out does not fulfill the request when the runtime process starts,
        /// registers capacity, but does not expose the runtime command endpoint.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task ControlPlaneWithHttpRuntimeInstances_With_Process_HostCreation_Mode_Should_Not_Fulfill_ScaleOut_When_Runtime_Command_Endpoint_Is_Missing()
        {
            var controlPlaneId = GenericMcpServerTestSettings.CreateControlPlaneId("http-process-missing-command-endpoint-production");
            var runtimeHostAssemblyPath = GenericMcpRuntimeHostAssemblyResolver.ResolveRuntimeHostAssemblyPath();

            var controlPlaneSettings = GenericMcpServerTestSettings.CreateHttpProcessHostScaleOutOnlyControlPlaneSettings(
                controlPlaneId,
                runtimeHostAssemblyPath);

            controlPlaneSettings["AiRuntimeProcessHostCreation:EnvironmentVariables:Tests__DisableRuntimeCommandEndpoint"] = "true";

            this.output.WriteLine("Process readiness failure scenario: runtime command endpoint disabled.");
            this.output.WriteLine($"Runtime host assembly path: '{runtimeHostAssemblyPath}'.");
            this.output.WriteLine($"TEST SETTING AiHttpRuntimeScaleOut:Mode='{controlPlaneSettings["AiHttpRuntimeScaleOut:Mode"]}'.");
            this.output.WriteLine($"TEST SETTING AiHttpRuntimeScaleOut:HostCreationMode='{controlPlaneSettings["AiHttpRuntimeScaleOut:HostCreationMode"]}'.");
            this.output.WriteLine($"TEST SETTING AiRuntimeProcessHostCreation:RuntimeHostAssemblyPath='{controlPlaneSettings["AiRuntimeProcessHostCreation:RuntimeHostAssemblyPath"]}'.");
            this.output.WriteLine($"TEST SETTING Tests:DisableRuntimeCommandEndpoint via child env='{controlPlaneSettings["AiRuntimeProcessHostCreation:EnvironmentVariables:Tests__DisableRuntimeCommandEndpoint"]}'.");

            await using var host = new GenericMcpServerTestHost(controlPlaneSettings);
            using var client = host.CreateClient();

            AssertRedisStoresPublisherWatcherHttpProviderAndProcessHostManager(
                host.Services,
                runtimeHostAssemblyPath);

            var mcp = await McpRbacTestClientHelper
                .CreateConfiguredClientAsync(
                    host,
                    client,
                    RequestedBy,
                    tenantId: DedicatedTenantId)
                .ConfigureAwait(false);

            var sharedRunStore = host.Services.GetRequiredService<IAiSharedRunStore>();
            var scaleOutRequestStore = host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();
            var runtimeInstanceRegistry = host.Services.GetRequiredService<IAiRuntimeInstanceRegistry>();
            var runtimeInstanceCapacityStore = host.Services.GetRequiredService<IAiRuntimeInstanceCapacityStore>();

            var pipelineName = $"mcp-http-process-missing-command-endpoint-{Guid.NewGuid():N}";

            var submittedSharedRunIds = await SubmitRunsAsync(
                    mcp,
                    pipelineName,
                    count: 1,
                    stepCount: 3,
                    flakyStepInterval: 0,
                    tenantId: DedicatedTenantId)
                .ConfigureAwait(false);

            var sharedRunId = Assert.Single(submittedSharedRunIds);
            var expectedScaleOutRequestId = $"scale-out-{sharedRunId}";
            var expectedRuntimeInstanceId = $"{controlPlaneId}:{DedicatedRuntimeInstanceIdPrefix}-1";

            var rejectedScaleOutRequest = await WaitForScaleOutRequestStatusAsync(
                    scaleOutRequestStore,
                    expectedScaleOutRequestId,
                    AiRuntimeScaleOutRequestStatus.Rejected,
                    TimeSpan.FromSeconds(30))
                .ConfigureAwait(false);

            Assert.Equal(expectedScaleOutRequestId, rejectedScaleOutRequest.RequestId);
            Assert.Equal(sharedRunId, rejectedScaleOutRequest.SharedRunId);
            Assert.Equal(controlPlaneId, rejectedScaleOutRequest.ControlPlaneId);
            Assert.Equal(DedicatedTenantId, rejectedScaleOutRequest.TenantId);
            Assert.Equal(pipelineName, rejectedScaleOutRequest.PipelineKey);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Rejected, rejectedScaleOutRequest.Status);
            Assert.Equal("http", rejectedScaleOutRequest.ProviderHint);
            Assert.Equal("http", rejectedScaleOutRequest.Metadata["providerHint"]);
            Assert.Equal("mcp-scaleout-watcher", rejectedScaleOutRequest.RejectedBy);

            Assert.True(
                rejectedScaleOutRequest.RejectionReason?.Contains(
                    "runtime-readiness-command-endpoint-missing",
                    StringComparison.OrdinalIgnoreCase) == true,
                $"Unexpected rejection reason: '{rejectedScaleOutRequest.RejectionReason}'.");

            Assert.True(string.IsNullOrWhiteSpace(rejectedScaleOutRequest.FulfilledRuntimeInstanceId));
            Assert.True(string.IsNullOrWhiteSpace(rejectedScaleOutRequest.FulfilledBy));

            var registered = await runtimeInstanceRegistry
                .GetAsync(expectedRuntimeInstanceId)
                .ConfigureAwait(false);

            var capacity = await runtimeInstanceCapacityStore
                .GetAsync(expectedRuntimeInstanceId)
                .ConfigureAwait(false);

            Assert.NotNull(registered);
            Assert.NotNull(capacity);

            Assert.Equal(expectedRuntimeInstanceId, registered!.RuntimeInstanceId);
            Assert.Equal(expectedRuntimeInstanceId, capacity!.RuntimeInstanceId);

            Assert.Equal(DedicatedTenantId, registered.Metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantId]);
            Assert.Equal(DedicatedTenantId, capacity.Metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantId]);

            Assert.Equal("Process", registered.Metadata["hostCreation.mode"]);
            Assert.Equal("Process", capacity.Metadata["hostCreation.mode"]);

            Assert.Equal("http", registered.Metadata["transport.name"]);
            Assert.Equal("http", capacity.Metadata["transport.name"]);

            Assert.True(
                registered.Metadata.TryGetValue("transport.endpoint", out var registeredEndpoint) &&
                registeredEndpoint.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase),
                $"Unexpected registered transport endpoint: '{registered.Metadata.GetValueOrDefault("transport.endpoint")}'.");

            Assert.True(
                capacity.Metadata.TryGetValue("transport.endpoint", out var capacityEndpoint) &&
                capacityEndpoint.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase),
                $"Unexpected capacity transport endpoint: '{capacity.Metadata.GetValueOrDefault("transport.endpoint")}'.");

            var finalSharedRun = await sharedRunStore
                .GetAsync(sharedRunId)
                .ConfigureAwait(false);

            Assert.NotNull(finalSharedRun);
            Assert.Equal(AiSharedRunStatus.ScaleOutRequested, finalSharedRun!.Status);
            Assert.True(string.IsNullOrWhiteSpace(finalSharedRun.LocalRunId));
            Assert.True(string.IsNullOrWhiteSpace(finalSharedRun.ExecutionId));

            this.output.WriteLine(
                $"PROCESS HOST MISSING COMMAND ENDPOINT READINESS FAILURE VALIDATED. " +
                $"ControlPlaneId='{controlPlaneId}', SharedRunId='{sharedRunId}', " +
                $"RequestId='{rejectedScaleOutRequest.RequestId}', TenantId='{DedicatedTenantId}', " +
                $"ExpectedRuntimeInstanceId='{expectedRuntimeInstanceId}', " +
                $"RejectedReason='{rejectedScaleOutRequest.RejectionReason}', " +
                $"RegisteredEndpoint='{registeredEndpoint}', CapacityEndpoint='{capacityEndpoint}', " +
                $"RuntimeHostAssemblyPath='{runtimeHostAssemblyPath}'.");
        }

        /// <summary>
        /// Verifies that process-based HTTP scale-out does not start duplicate runtime processes for the same Redis scale-out request.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task ControlPlaneWithHttpRuntimeInstances_With_Process_HostCreation_Mode_Should_Not_Start_Duplicate_Runtime_Process_For_Same_ScaleOut_Request()
        {
            var controlPlaneId = GenericMcpServerTestSettings.CreateControlPlaneId("http-process-idempotent-provisioning-production");
            var runtimeHostAssemblyPath = GenericMcpRuntimeHostAssemblyResolver.ResolveRuntimeHostAssemblyPath();

            var controlPlaneSettings = GenericMcpServerTestSettings.CreateHttpProcessHostScaleOutOnlyControlPlaneSettings(
                controlPlaneId,
                runtimeHostAssemblyPath);

            this.output.WriteLine("Process idempotent provisioning scenario: duplicate watcher cycles must not start duplicate runtime processes.");
            this.output.WriteLine($"Runtime host assembly path: '{runtimeHostAssemblyPath}'.");
            this.output.WriteLine($"TEST SETTING AiHttpRuntimeScaleOut:Mode='{controlPlaneSettings["AiHttpRuntimeScaleOut:Mode"]}'.");
            this.output.WriteLine($"TEST SETTING AiHttpRuntimeScaleOut:HostCreationMode='{controlPlaneSettings["AiHttpRuntimeScaleOut:HostCreationMode"]}'.");
            this.output.WriteLine($"TEST SETTING AiRuntimeProcessHostCreation:RuntimeHostAssemblyPath='{controlPlaneSettings["AiRuntimeProcessHostCreation:RuntimeHostAssemblyPath"]}'.");

            await using var host = new GenericMcpServerTestHost(controlPlaneSettings);
            using var client = host.CreateClient();

            AssertRedisStoresPublisherWatcherHttpProviderAndProcessHostManager(
                host.Services,
                runtimeHostAssemblyPath);

            var mcp = await McpRbacTestClientHelper
                .CreateConfiguredClientAsync(
                    host,
                    client,
                    RequestedBy,
                    tenantId: DedicatedTenantId)
                .ConfigureAwait(false);

            var sharedRunStore = host.Services.GetRequiredService<IAiSharedRunStore>();
            var sharedQueue = host.Services.GetRequiredService<IAiSharedQueue>();
            var scaleOutRequestStore = host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();
            var runtimeInstanceRegistry = host.Services.GetRequiredService<IAiRuntimeInstanceRegistry>();
            var runtimeInstanceCapacityStore = host.Services.GetRequiredService<IAiRuntimeInstanceCapacityStore>();

            var pipelineName = $"mcp-http-process-idempotent-provisioning-{Guid.NewGuid():N}";

            var submittedSharedRunIds = await SubmitRunsAsync(
                    mcp,
                    pipelineName,
                    count: 1,
                    stepCount: 3,
                    flakyStepInterval: 0,
                    tenantId: DedicatedTenantId)
                .ConfigureAwait(false);

            var sharedRunId = Assert.Single(submittedSharedRunIds);
            var expectedScaleOutRequestId = $"scale-out-{sharedRunId}";
            var expectedRuntimeInstanceId = $"{controlPlaneId}:{DedicatedRuntimeInstanceIdPrefix}-1";
            var unexpectedDuplicateRuntimeInstanceId = $"{controlPlaneId}:{DedicatedRuntimeInstanceIdPrefix}-2";

            var fulfilledScaleOutRequest = await WaitForScaleOutRequestStatusAsync(
                    scaleOutRequestStore,
                    expectedScaleOutRequestId,
                    AiRuntimeScaleOutRequestStatus.Fulfilled,
                    TimeSpan.FromSeconds(60))
                .ConfigureAwait(false);

            Assert.Equal(expectedScaleOutRequestId, fulfilledScaleOutRequest.RequestId);
            Assert.Equal(sharedRunId, fulfilledScaleOutRequest.SharedRunId);
            Assert.Equal(controlPlaneId, fulfilledScaleOutRequest.ControlPlaneId);
            Assert.Equal(DedicatedTenantId, fulfilledScaleOutRequest.TenantId);
            Assert.Equal(pipelineName, fulfilledScaleOutRequest.PipelineKey);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Fulfilled, fulfilledScaleOutRequest.Status);
            Assert.Equal(expectedRuntimeInstanceId, fulfilledScaleOutRequest.FulfilledRuntimeInstanceId);
            Assert.Equal("mcp-scaleout-watcher", fulfilledScaleOutRequest.FulfilledBy);

            var registered = await runtimeInstanceRegistry
                .GetAsync(expectedRuntimeInstanceId)
                .ConfigureAwait(false);

            var capacity = await runtimeInstanceCapacityStore
                .GetAsync(expectedRuntimeInstanceId)
                .ConfigureAwait(false);

            Assert.NotNull(registered);
            Assert.NotNull(capacity);

            Assert.Equal(expectedRuntimeInstanceId, registered!.RuntimeInstanceId);
            Assert.Equal(expectedRuntimeInstanceId, capacity!.RuntimeInstanceId);

            Assert.Equal(DedicatedTenantId, registered.Metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantId]);
            Assert.Equal(DedicatedTenantId, capacity.Metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantId]);
            Assert.Equal("Process", registered.Metadata["hostCreation.mode"]);
            Assert.Equal("Process", capacity.Metadata["hostCreation.mode"]);

            var sharedRunAfterFulfillment = await sharedRunStore
                .GetAsync(sharedRunId)
                .ConfigureAwait(false);

            Assert.NotNull(sharedRunAfterFulfillment);

            var queueItemAfterFulfillment = await sharedQueue
                .GetAsync(sharedRunId)
                .ConfigureAwait(false);

            AssertScaleOutRequeueOrDispatchState(
                sharedRunAfterFulfillment!,
                queueItemAfterFulfillment,
                expectedRuntimeInstanceId,
                expectedScaleOutRequestId);

            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            var requestAfterAdditionalWatcherCycles = await scaleOutRequestStore
                .GetAsync(expectedScaleOutRequestId)
                .ConfigureAwait(false);

            Assert.NotNull(requestAfterAdditionalWatcherCycles);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Fulfilled, requestAfterAdditionalWatcherCycles!.Status);
            Assert.Equal(expectedRuntimeInstanceId, requestAfterAdditionalWatcherCycles.FulfilledRuntimeInstanceId);
            Assert.Equal(fulfilledScaleOutRequest.FulfilledAtUtc, requestAfterAdditionalWatcherCycles.FulfilledAtUtc);

            var queueItemAfterAdditionalWatcherCycles = await sharedQueue
                .GetAsync(sharedRunId)
                .ConfigureAwait(false);

            var sharedRunAfterAdditionalWatcherCycles = await sharedRunStore
                .GetAsync(sharedRunId)
                .ConfigureAwait(false);

            Assert.NotNull(sharedRunAfterAdditionalWatcherCycles);

            AssertScaleOutRequeueOrDispatchState(
                sharedRunAfterAdditionalWatcherCycles!,
                queueItemAfterAdditionalWatcherCycles,
                expectedRuntimeInstanceId,
                expectedScaleOutRequestId);

            var duplicateRegistered = await runtimeInstanceRegistry
                .GetAsync(unexpectedDuplicateRuntimeInstanceId)
                .ConfigureAwait(false);

            var duplicateCapacity = await runtimeInstanceCapacityStore
                .GetAsync(unexpectedDuplicateRuntimeInstanceId)
                .ConfigureAwait(false);

            Assert.Null(duplicateRegistered);
            Assert.Null(duplicateCapacity);

            var finalSharedRun = await sharedRunStore
                .GetAsync(sharedRunId)
                .ConfigureAwait(false);

            Assert.NotNull(finalSharedRun);

            Assert.Contains(
                finalSharedRun!.Status,
                new[]
                {
            AiSharedRunStatus.ScaleOutRequested,
            AiSharedRunStatus.Dispatched,
            AiSharedRunStatus.Completed
                });

            if (finalSharedRun.Status is AiSharedRunStatus.Dispatched or AiSharedRunStatus.Completed)
            {
                Assert.Equal(expectedRuntimeInstanceId, finalSharedRun.AssignedRuntimeInstanceId);
            }
            else
            {
                Assert.True(string.IsNullOrWhiteSpace(finalSharedRun.AssignedRuntimeInstanceId));
            }

            Assert.NotEqual(unexpectedDuplicateRuntimeInstanceId, finalSharedRun.AssignedRuntimeInstanceId);

            this.output.WriteLine(
                $"PROCESS HOST IDEMPOTENT PROVISIONING VALIDATED. " +
                $"ControlPlaneId='{controlPlaneId}', SharedRunId='{sharedRunId}', " +
                $"RequestId='{fulfilledScaleOutRequest.RequestId}', TenantId='{DedicatedTenantId}', " +
                $"RuntimeInstanceId='{expectedRuntimeInstanceId}', " +
                $"UnexpectedDuplicateRuntimeInstanceId='{unexpectedDuplicateRuntimeInstanceId}', " +
                $"RuntimeHostAssemblyPath='{runtimeHostAssemblyPath}'.");
        }

        /// <summary>
        /// Verifies that process-based HTTP scale-out is rejected when the runtime process starts but the HTTP command endpoint is not available.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task ControlPlaneWithHttpRuntimeInstances_With_Process_HostCreation_Mode_Should_Not_Fulfill_When_Runtime_Command_Endpoint_Is_Missing()
        {
            var controlPlaneId = GenericMcpServerTestSettings.CreateControlPlaneId("http-process-command-endpoint-missing-production");
            var runtimeHostAssemblyPath = GenericMcpRuntimeHostAssemblyResolver.ResolveRuntimeHostAssemblyPath();

            var controlPlaneSettings = GenericMcpServerTestSettings.CreateHttpProcessHostScaleOutOnlyControlPlaneSettings(
                controlPlaneId,
                runtimeHostAssemblyPath);

            controlPlaneSettings["AiRuntimeProcessHostCreation:EnvironmentVariables:Tests__DisableRuntimeCommandEndpoint"] = "true";

            this.output.WriteLine("Process readiness failure scenario: runtime process starts but command endpoint is disabled.");
            this.output.WriteLine($"Runtime host assembly path: '{runtimeHostAssemblyPath}'.");
            this.output.WriteLine($"TEST SETTING AiHttpRuntimeScaleOut:Mode='{controlPlaneSettings["AiHttpRuntimeScaleOut:Mode"]}'.");
            this.output.WriteLine($"TEST SETTING AiHttpRuntimeScaleOut:HostCreationMode='{controlPlaneSettings["AiHttpRuntimeScaleOut:HostCreationMode"]}'.");
            this.output.WriteLine($"TEST SETTING AiRuntimeProcessHostCreation:RuntimeHostAssemblyPath='{controlPlaneSettings["AiRuntimeProcessHostCreation:RuntimeHostAssemblyPath"]}'.");
            this.output.WriteLine($"TEST SETTING AiRuntimeProcessHostCreation:EnvironmentVariables:Tests__DisableRuntimeCommandEndpoint='{controlPlaneSettings["AiRuntimeProcessHostCreation:EnvironmentVariables:Tests__DisableRuntimeCommandEndpoint"]}'.");

            await using var host = new GenericMcpServerTestHost(controlPlaneSettings);
            using var client = host.CreateClient();

            AssertRedisStoresPublisherWatcherHttpProviderAndProcessHostManager(
                host.Services,
                runtimeHostAssemblyPath);

            var mcp = await McpRbacTestClientHelper
                .CreateConfiguredClientAsync(
                    host,
                    client,
                    RequestedBy,
                    tenantId: DedicatedTenantId)
                .ConfigureAwait(false);

            var sharedRunStore = host.Services.GetRequiredService<IAiSharedRunStore>();
            var sharedQueue = host.Services.GetRequiredService<IAiSharedQueue>();
            var scaleOutRequestStore = host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();
            var runtimeInstanceRegistry = host.Services.GetRequiredService<IAiRuntimeInstanceRegistry>();
            var runtimeInstanceCapacityStore = host.Services.GetRequiredService<IAiRuntimeInstanceCapacityStore>();

            var pipelineName = $"mcp-http-process-command-endpoint-missing-{Guid.NewGuid():N}";

            var submittedSharedRunIds = await SubmitRunsAsync(
                    mcp,
                    pipelineName,
                    count: 1,
                    stepCount: 3,
                    flakyStepInterval: 0,
                    tenantId: DedicatedTenantId)
                .ConfigureAwait(false);

            var sharedRunId = Assert.Single(submittedSharedRunIds);
            var expectedScaleOutRequestId = $"scale-out-{sharedRunId}";
            var expectedRuntimeInstanceId = $"{controlPlaneId}:{DedicatedRuntimeInstanceIdPrefix}-1";
            var unexpectedDuplicateRuntimeInstanceId = $"{controlPlaneId}:{DedicatedRuntimeInstanceIdPrefix}-2";

            var rejectedScaleOutRequest = await WaitForScaleOutRequestStatusAsync(
                    scaleOutRequestStore,
                    expectedScaleOutRequestId,
                    AiRuntimeScaleOutRequestStatus.Rejected,
                    TimeSpan.FromSeconds(60))
                .ConfigureAwait(false);

            Assert.Equal(expectedScaleOutRequestId, rejectedScaleOutRequest.RequestId);
            Assert.Equal(sharedRunId, rejectedScaleOutRequest.SharedRunId);
            Assert.Equal(controlPlaneId, rejectedScaleOutRequest.ControlPlaneId);
            Assert.Equal(DedicatedTenantId, rejectedScaleOutRequest.TenantId);
            Assert.Equal(pipelineName, rejectedScaleOutRequest.PipelineKey);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Rejected, rejectedScaleOutRequest.Status);
            Assert.Equal("mcp-scaleout-watcher", rejectedScaleOutRequest.RejectedBy);
            Assert.Equal("runtime-readiness-command-endpoint-missing", rejectedScaleOutRequest.RejectionReason);
            Assert.True(string.IsNullOrWhiteSpace(rejectedScaleOutRequest.FulfilledRuntimeInstanceId));

            var sharedRunAfterRejection = await sharedRunStore
                .GetAsync(sharedRunId)
                .ConfigureAwait(false);

            Assert.NotNull(sharedRunAfterRejection);
            Assert.Equal(AiSharedRunStatus.ScaleOutRequested, sharedRunAfterRejection!.Status);
            Assert.True(string.IsNullOrWhiteSpace(sharedRunAfterRejection.AssignedRuntimeInstanceId));

            var queueItemAfterRejection = await sharedQueue
                .GetAsync(sharedRunId)
                .ConfigureAwait(false);

            Assert.Null(queueItemAfterRejection);

            var registered = await runtimeInstanceRegistry
                .GetAsync(expectedRuntimeInstanceId)
                .ConfigureAwait(false);

            var capacity = await runtimeInstanceCapacityStore
                .GetAsync(expectedRuntimeInstanceId)
                .ConfigureAwait(false);

            if (registered is not null)
            {
                Assert.Equal(expectedRuntimeInstanceId, registered.RuntimeInstanceId);
                Assert.Equal(DedicatedTenantId, registered.Metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantId]);
                Assert.Equal("Process", registered.Metadata["hostCreation.mode"]);
            }

            if (capacity is not null)
            {
                Assert.Equal(expectedRuntimeInstanceId, capacity.RuntimeInstanceId);
                Assert.Equal(DedicatedTenantId, capacity.Metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantId]);
                Assert.Equal("Process", capacity.Metadata["hostCreation.mode"]);
            }

            var duplicateRegistered = await runtimeInstanceRegistry
                .GetAsync(unexpectedDuplicateRuntimeInstanceId)
                .ConfigureAwait(false);

            var duplicateCapacity = await runtimeInstanceCapacityStore
                .GetAsync(unexpectedDuplicateRuntimeInstanceId)
                .ConfigureAwait(false);

            Assert.Null(duplicateRegistered);
            Assert.Null(duplicateCapacity);

            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            var requestAfterAdditionalWatcherCycles = await scaleOutRequestStore
                .GetAsync(expectedScaleOutRequestId)
                .ConfigureAwait(false);

            Assert.NotNull(requestAfterAdditionalWatcherCycles);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Rejected, requestAfterAdditionalWatcherCycles!.Status);
            Assert.Equal("runtime-readiness-command-endpoint-missing", requestAfterAdditionalWatcherCycles.RejectionReason);
            Assert.True(string.IsNullOrWhiteSpace(requestAfterAdditionalWatcherCycles.FulfilledRuntimeInstanceId));
            Assert.Equal(rejectedScaleOutRequest.RejectedAtUtc, requestAfterAdditionalWatcherCycles.RejectedAtUtc);

            var finalSharedRun = await sharedRunStore
                .GetAsync(sharedRunId)
                .ConfigureAwait(false);

            Assert.NotNull(finalSharedRun);
            Assert.Equal(AiSharedRunStatus.ScaleOutRequested, finalSharedRun!.Status);
            Assert.True(string.IsNullOrWhiteSpace(finalSharedRun.AssignedRuntimeInstanceId));
            Assert.NotEqual(unexpectedDuplicateRuntimeInstanceId, finalSharedRun.AssignedRuntimeInstanceId);

            var finalQueueItem = await sharedQueue
                .GetAsync(sharedRunId)
                .ConfigureAwait(false);

            Assert.Null(finalQueueItem);

            this.output.WriteLine(
                $"PROCESS HOST READINESS FAILURE VALIDATED. " +
                $"ControlPlaneId='{controlPlaneId}', SharedRunId='{sharedRunId}', " +
                $"RequestId='{rejectedScaleOutRequest.RequestId}', TenantId='{DedicatedTenantId}', " +
                $"ExpectedRuntimeInstanceId='{expectedRuntimeInstanceId}', " +
                $"UnexpectedDuplicateRuntimeInstanceId='{unexpectedDuplicateRuntimeInstanceId}', " +
                $"RejectionReason='{rejectedScaleOutRequest.RejectionReason}', " +
                $"RuntimeHostAssemblyPath='{runtimeHostAssemblyPath}'.");
        }

        /// <summary>
        /// Asserts the valid states after a scale-out request has been fulfilled and the linked shared run has been requeued.
        /// </summary>
        /// <param name="sharedRun">The shared run.</param>
        /// <param name="queueItem">The optional queue item. It can be null when the queue pump already consumed it.</param>
        /// <param name="expectedRuntimeInstanceId">The expected runtime instance id.</param>
        /// <param name="expectedScaleOutRequestId">The expected scale-out request id.</param>
        private static void AssertScaleOutRequeueOrDispatchState(
            AiSharedRunRecord sharedRun,
            AiSharedQueueItem? queueItem,
            string expectedRuntimeInstanceId,
            string expectedScaleOutRequestId)
        {
            ArgumentNullException.ThrowIfNull(sharedRun);
            ArgumentException.ThrowIfNullOrWhiteSpace(expectedRuntimeInstanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(expectedScaleOutRequestId);

            Assert.Contains(
                sharedRun.Status,
                new[]
                {
            AiSharedRunStatus.ScaleOutRequested,
            AiSharedRunStatus.Dispatched,
            AiSharedRunStatus.Completed
                });

            if (queueItem is not null)
            {
                Assert.Contains(
                    queueItem.Status,
                    new[]
                    {
                AiSharedQueueItemStatus.Pending,
                AiSharedQueueItemStatus.Dispatched
                    });

                Assert.Equal(expectedRuntimeInstanceId, queueItem.Metadata["scaleOutRuntimeInstanceId"]);
                Assert.Equal("true", queueItem.Metadata["scaleOutRequeued"]);
                Assert.Equal(expectedScaleOutRequestId, queueItem.Metadata["scaleOutRequestId"]);
            }

            if (sharedRun.Status is AiSharedRunStatus.Dispatched or AiSharedRunStatus.Completed)
            {
                Assert.Equal(expectedRuntimeInstanceId, sharedRun.AssignedRuntimeInstanceId);
                return;
            }

            Assert.Equal(AiSharedRunStatus.ScaleOutRequested, sharedRun.Status);
            Assert.True(string.IsNullOrWhiteSpace(sharedRun.AssignedRuntimeInstanceId));

            if (queueItem is null)
            {
                throw new Xunit.Sdk.XunitException(
                    "The shared run is still ScaleOutRequested, but the requeued shared queue item was not found.");
            }
        }

        /// <summary>
        /// Asserts that Redis stores, HTTP provider, watcher, and process host manager are correctly wired.
        /// </summary>
        /// <param name="services">The service provider.</param>
        /// <param name="runtimeHostAssemblyPath">The runtime host assembly path.</param>
        private void AssertRedisStoresPublisherWatcherHttpProviderAndProcessHostManager(
            IServiceProvider services,
            string runtimeHostAssemblyPath)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeHostAssemblyPath);

            var sharedRunStore = services.GetRequiredService<IAiSharedRunStore>();
            var sharedQueue = services.GetRequiredService<IAiSharedQueue>();
            var reservationStore = services.GetRequiredService<IAiRuntimeAdmissionReservationStore>();
            var scaleOutRequestStore = services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();
            var scaleOutPublisher = services.GetRequiredService<IAiRuntimeScaleOutRequestPublisher>();
            var scaleOutSelector = services.GetRequiredService<IAiRuntimeScaleOutProviderSelector>();
            var httpProvisioner = services.GetRequiredService<IAiHttpRuntimeScaleOutProvisioner>();
            var admissionOptions = services.GetRequiredService<IOptions<AiRunAdmissionOptions>>().Value;
            var watcherOptions = services.GetRequiredService<IOptions<AiRuntimeScaleOutRequestWatcherOptions>>().Value;
            var httpScaleOutOptions = services.GetRequiredService<IOptions<AiHttpRuntimeScaleOutOptions>>().Value;
            var processOptions = services.GetRequiredService<IOptions<AiRuntimeProcessHostCreationOptions>>().Value;
            var runtimeInstanceProviders = services.GetServices<IAiRuntimeInstanceProvider>().ToArray();
            var hostedServices = services.GetServices<IHostedService>().ToArray();
            var hostManager = services.GetRequiredService<IAiRuntimeHostManager>();
            var strategies = services.GetServices<IAiRuntimeHostCreationStrategy>().ToArray();

            this.output.WriteLine($"Process production assert: IAiSharedRunStore='{sharedRunStore.GetType().FullName}'.");
            this.output.WriteLine($"Process production assert: IAiSharedQueue='{sharedQueue.GetType().FullName}'.");
            this.output.WriteLine($"Process production assert: IAiRuntimeAdmissionReservationStore='{reservationStore.GetType().FullName}'.");
            this.output.WriteLine($"Process production assert: IAiRuntimeScaleOutRequestStore='{scaleOutRequestStore.GetType().FullName}'.");
            this.output.WriteLine($"Process production assert: IAiRuntimeScaleOutRequestPublisher='{scaleOutPublisher.GetType().FullName}'.");
            this.output.WriteLine($"Process production assert: IAiRuntimeScaleOutProviderSelector='{scaleOutSelector.GetType().FullName}'.");
            this.output.WriteLine($"Process production assert: IAiHttpRuntimeScaleOutProvisioner='{httpProvisioner.GetType().FullName}'.");
            this.output.WriteLine($"Process production assert: IAiRuntimeHostManager='{hostManager.GetType().FullName}'.");
            this.output.WriteLine("Process production assert: Runtime instance providers: " + string.Join(" | ", runtimeInstanceProviders.Select(provider => provider.GetType().FullName)));
            this.output.WriteLine("Process production assert: IHostedService registrations: " + string.Join(" | ", hostedServices.Select(service => service.GetType().FullName)));
            this.output.WriteLine("Process production assert: IAiRuntimeHostCreationStrategy registrations: " + string.Join(" | ", strategies.Select(strategy => $"{strategy.Mode}:{strategy.GetType().FullName}")));
            this.output.WriteLine($"Process production assert: Admission.Enabled='{admissionOptions.Enabled}', MaxInstanceCount='{admissionOptions.MaxInstanceCount?.ToString() ?? "null"}', EnableScaleOutRequest='{admissionOptions.EnableScaleOutRequest}', EnableGlobalQueueFallback='{admissionOptions.EnableGlobalQueueFallback}', RejectWhenNoCapacity='{admissionOptions.RejectWhenNoCapacity}'.");
            this.output.WriteLine($"Process production assert: Watcher.Enabled='{watcherOptions.Enabled}', WatcherId='{watcherOptions.WatcherId}', ControlPlaneId='{watcherOptions.ControlPlaneId}', Interval='{watcherOptions.Interval}', MaxRequestsPerCycle='{watcherOptions.MaxRequestsPerCycle}'.");
            this.output.WriteLine($"Process production assert: HttpScaleOut.Enabled='{httpScaleOutOptions.Enabled}', Mode='{httpScaleOutOptions.Mode}', HostCreationMode='{httpScaleOutOptions.HostCreationMode}', RuntimeInstanceIdPrefix='{httpScaleOutOptions.DefaultRuntimeInstanceIdPrefix}', EndpointTemplate='{httpScaleOutOptions.EndpointTemplate}'.");
            this.output.WriteLine($"Process production assert: ProcessHost.Enabled='{processOptions.Enabled}', RuntimeHostAssemblyPath='{processOptions.RuntimeHostAssemblyPath}', BasePort='{processOptions.BasePort}', MaxPort='{processOptions.MaxPort}'.");

            Assert.IsType<RedisAiSharedRunStore>(sharedRunStore);
            Assert.IsType<RedisAiSharedQueue>(sharedQueue);
            Assert.IsType<RedisAiRuntimeAdmissionReservationStore>(reservationStore);
            Assert.IsType<RedisAiRuntimeScaleOutRequestStore>(scaleOutRequestStore);
            Assert.IsType<StoreBackedAiRuntimeScaleOutRequestPublisher>(scaleOutPublisher);
            Assert.IsType<AiRuntimeScaleOutProviderSelector>(scaleOutSelector);
            Assert.IsType<AiHttpRuntimeScaleOutProvisioner>(httpProvisioner);
            Assert.IsType<AiRuntimeHostCreationManager>(hostManager);

            Assert.Contains(runtimeInstanceProviders, provider => provider.GetType() == typeof(HttpAiRuntimeInstanceProvider));

            Assert.True(admissionOptions.Enabled, "Admission must be enabled for this scenario.");
            Assert.True(admissionOptions.EnableScaleOutRequest, "Scale-out request must be enabled for this scenario.");
            Assert.False(admissionOptions.EnableGlobalQueueFallback, "Global queue fallback must be disabled so admission requests scale-out.");
            Assert.False(admissionOptions.RejectWhenNoCapacity, "RejectWhenNoCapacity must be false so admission can request scale-out.");
            Assert.Equal(3, admissionOptions.MaxInstanceCount);

            Assert.True(watcherOptions.Enabled, "Scale-out watcher must be enabled.");
            Assert.Equal("mcp-scaleout-watcher", watcherOptions.WatcherId);

            Assert.True(httpScaleOutOptions.Enabled, "HTTP scale-out provisioner must be enabled.");
            Assert.Equal(AiRuntimeHostCreationMode.Process, httpScaleOutOptions.HostCreationMode);

            Assert.True(processOptions.Enabled, "Process host creation must be enabled.");
            Assert.Equal(runtimeHostAssemblyPath, processOptions.RuntimeHostAssemblyPath);

            Assert.Contains(hostedServices, service => service.GetType() == typeof(AiRuntimeScaleOutRequestWatcherHostedService));
            Assert.Contains(strategies, strategy => strategy.GetType() == typeof(FixtureAiRuntimeHostCreationStrategy));
            Assert.Contains(strategies, strategy => strategy.GetType() == typeof(ProcessAiRuntimeHostCreationStrategy));
        }

        /// <summary>
        /// Asserts tenant runtime settings values.
        /// </summary>
        /// <param name="settings">The tenant runtime settings.</param>
        /// <param name="tenantId">The expected tenant identifier.</param>
        /// <param name="isolationMode">The expected isolation mode.</param>
        /// <param name="preferDedicatedCapacity">A value indicating whether dedicated capacity is preferred.</param>
        /// <param name="allowSharedFallback">A value indicating whether shared fallback is allowed.</param>
        /// <param name="maxRuntimeInstances">The expected maximum runtime instance count.</param>
        /// <param name="workerCountPerInstance">The expected worker count per instance.</param>
        /// <param name="maxConcurrentRunsPerInstance">The expected maximum concurrent runs per instance.</param>
        /// <param name="localQueueCapacity">The expected local queue capacity.</param>
        /// <param name="runtimeInstanceIdPrefix">The expected runtime instance id prefix.</param>
        private static void AssertTenantSettings(
            AiTenantRuntimeSettings settings,
            string tenantId,
            AiRuntimeInstanceIsolationMode isolationMode,
            bool preferDedicatedCapacity,
            bool allowSharedFallback,
            int maxRuntimeInstances,
            int workerCountPerInstance,
            int maxConcurrentRunsPerInstance,
            int? localQueueCapacity,
            string runtimeInstanceIdPrefix)
        {
            ArgumentNullException.ThrowIfNull(settings);

            Assert.Equal(tenantId, settings.TenantId);
            Assert.Equal(isolationMode, settings.IsolationMode);
            Assert.Equal(preferDedicatedCapacity, settings.PreferDedicatedCapacity);
            Assert.Equal(allowSharedFallback, settings.AllowSharedFallback);
            Assert.Equal(maxRuntimeInstances, settings.MaxRuntimeInstances);
            Assert.Equal(workerCountPerInstance, settings.WorkerCountPerInstance);
            Assert.Equal(maxConcurrentRunsPerInstance, settings.MaxConcurrentRunsPerInstance);
            Assert.Equal(localQueueCapacity, settings.LocalQueueCapacity);
            Assert.Equal(runtimeInstanceIdPrefix, settings.RuntimeInstanceIdPrefix);
        }

        /// <summary>
        /// Asserts that a fulfilled scale-out request uses tenant runtime settings.
        /// </summary>
        /// <param name="scaleOutRequest">The scale-out request record.</param>
        /// <param name="tenantId">The expected tenant identifier.</param>
        /// <param name="tenantGroupId">The expected tenant group identifier.</param>
        /// <param name="isolationMode">The expected isolation mode.</param>
        /// <param name="preferDedicatedCapacity">A value indicating whether dedicated capacity is preferred.</param>
        /// <param name="allowSharedFallback">A value indicating whether shared fallback is allowed.</param>
        /// <param name="maxRuntimeInstances">The expected maximum runtime instance count.</param>
        /// <param name="workerCountPerInstance">The expected worker count per instance.</param>
        /// <param name="maxConcurrentRunsPerInstance">The expected maximum concurrent runs per instance.</param>
        /// <param name="localQueueCapacity">The expected tenant local queue capacity. This value may be null before provider fallback resolution.</param>
        /// <param name="runtimeInstanceIdPrefix">The expected runtime instance id prefix.</param>
        private static void AssertScaleOutRequestUsesTenantSettings(
            AiRuntimeScaleOutRequestRecord scaleOutRequest,
            string tenantId,
            string? tenantGroupId,
            AiRuntimeInstanceIsolationMode isolationMode,
            bool preferDedicatedCapacity,
            bool allowSharedFallback,
            int maxRuntimeInstances,
            int expectedScaleOutMaxInstanceCount,
            int workerCountPerInstance,
            int maxConcurrentRunsPerInstance,
            int? localQueueCapacity,
            string runtimeInstanceIdPrefix)
        {
            ArgumentNullException.ThrowIfNull(scaleOutRequest);

            Assert.Equal(tenantId, scaleOutRequest.TenantId);
            Assert.Equal(tenantGroupId, scaleOutRequest.TenantGroupId);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Fulfilled, scaleOutRequest.Status);
            Assert.Equal("http", scaleOutRequest.ProviderHint);
            Assert.Equal("http", scaleOutRequest.Metadata["providerHint"]);
            Assert.Equal(isolationMode, scaleOutRequest.IsolationMode);
            Assert.Equal(preferDedicatedCapacity, scaleOutRequest.PreferDedicatedCapacity);
            Assert.Equal(allowSharedFallback, scaleOutRequest.AllowSharedFallback);
            Assert.Equal(maxRuntimeInstances, scaleOutRequest.MaxRuntimeInstances);
            Assert.Equal(workerCountPerInstance, scaleOutRequest.WorkerCountPerInstance);
            Assert.Equal(maxConcurrentRunsPerInstance, scaleOutRequest.MaxConcurrentRunsPerInstance);
            Assert.Equal(localQueueCapacity, scaleOutRequest.LocalQueueCapacity);
            Assert.Equal(runtimeInstanceIdPrefix, scaleOutRequest.RuntimeInstanceIdPrefix);
            Assert.Equal(0, scaleOutRequest.AvailableInstanceCount);
            Assert.Equal(0, scaleOutRequest.CurrentInstanceCount);
            Assert.Equal(expectedScaleOutMaxInstanceCount, scaleOutRequest.MaxInstanceCount);
            Assert.Equal(1, scaleOutRequest.RequestedTargetInstanceCount);
            Assert.Equal("mcp-scaleout-watcher", scaleOutRequest.ObservedBy);
            Assert.Equal("mcp-scaleout-watcher", scaleOutRequest.FulfilledBy);
            Assert.False(string.IsNullOrWhiteSpace(scaleOutRequest.FulfilledRuntimeInstanceId));

            Assert.Equal(isolationMode.ToString(), scaleOutRequest.Metadata[AiRuntimeInstanceIsolationMetadataKeys.IsolationMode]);
            Assert.Equal(preferDedicatedCapacity.ToString(), scaleOutRequest.Metadata[AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity]);
            Assert.Equal(allowSharedFallback.ToString(), scaleOutRequest.Metadata[AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback]);
            Assert.Equal(maxRuntimeInstances.ToString(), scaleOutRequest.Metadata["runtime.maxRuntimeInstances"]);
            Assert.Equal(workerCountPerInstance.ToString(), scaleOutRequest.Metadata["runtime.workerCountPerInstance"]);
            Assert.Equal(maxConcurrentRunsPerInstance.ToString(), scaleOutRequest.Metadata["runtime.maxConcurrentRunsPerInstance"]);
            Assert.Equal(runtimeInstanceIdPrefix, scaleOutRequest.Metadata["runtime.instanceIdPrefix"]);

            if (localQueueCapacity.HasValue)
            {
                Assert.Equal(localQueueCapacity.Value.ToString(), scaleOutRequest.Metadata["runtime.localQueueCapacity"]);
            }
            else if (scaleOutRequest.Metadata.TryGetValue("runtime.localQueueCapacity", out var metadataLocalQueueCapacity))
            {
                Assert.True(
                    string.IsNullOrWhiteSpace(metadataLocalQueueCapacity),
                    $"Expected empty runtime.localQueueCapacity metadata when tenant setting is null, but found '{metadataLocalQueueCapacity}'.");
            }
        }

        /// <summary>
        /// Asserts that runtime registration snapshot and capacity descriptor use tenant runtime settings.
        /// </summary>
        /// <param name="registered">The runtime instance snapshot.</param>
        /// <param name="capacity">The runtime capacity descriptor.</param>
        /// <param name="runtimeInstanceId">The expected runtime instance identifier.</param>
        /// <param name="tenantId">The expected tenant identifier.</param>
        /// <param name="tenantGroupId">The expected tenant group identifier.</param>
        /// <param name="isolationMode">The expected isolation mode.</param>
        /// <param name="preferDedicatedCapacity">A value indicating whether dedicated capacity is preferred.</param>
        /// <param name="allowSharedFallback">A value indicating whether shared fallback is allowed.</param>
        /// <param name="maxRuntimeInstances">The expected maximum runtime instance count.</param>
        /// <param name="workerCountPerInstance">The expected worker count per instance.</param>
        /// <param name="maxConcurrentRunsPerInstance">The expected maximum concurrent runs per instance.</param>
        /// <param name="localQueueCapacity">The resolved local queue capacity after provider fallback resolution.</param>
        /// <param name="runtimeInstanceIdPrefix">The expected runtime instance id prefix.</param>
        private static void AssertRuntimeRegistrationAndCapacityUseTenantSettings(
            AiRuntimeInstanceSnapshot registered,
            AiRuntimeInstanceCapacityDescriptor capacity,
            string runtimeInstanceId,
            string tenantId,
            string? tenantGroupId,
            AiRuntimeInstanceIsolationMode isolationMode,
            bool preferDedicatedCapacity,
            bool allowSharedFallback,
            int maxRuntimeInstances,
            int workerCountPerInstance,
            int maxConcurrentRunsPerInstance,
            int localQueueCapacity,
            string runtimeInstanceIdPrefix)
        {
            ArgumentNullException.ThrowIfNull(registered);
            ArgumentNullException.ThrowIfNull(capacity);

            Assert.Equal(runtimeInstanceId, registered.RuntimeInstanceId);
            Assert.Equal(runtimeInstanceId, capacity.RuntimeInstanceId);

            Assert.Equal(workerCountPerInstance, capacity.WorkerCount);
            Assert.Equal(maxConcurrentRunsPerInstance, capacity.MaxConcurrentRuns);
            Assert.True(capacity.CanAcceptRun);
            Assert.Equal(AiRuntimeInstanceStatus.Ready, capacity.Status);

            Assert.Equal("http", registered.Metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName]);
            Assert.Equal("http", capacity.Metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName]);
            Assert.Equal("http", registered.Metadata["provider.name"]);
            Assert.Equal("http", capacity.Metadata["provider.name"]);

            Assert.Equal(AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName, registered.Metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName]);
            Assert.Equal(AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName, capacity.Metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName]);

            Assert.Equal("Process", registered.Metadata["hostCreation.mode"]);
            Assert.Equal("Process", capacity.Metadata["hostCreation.mode"]);

            Assert.Equal(tenantId, registered.Metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantId]);
            Assert.Equal(tenantId, capacity.Metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantId]);

            if (!string.IsNullOrWhiteSpace(tenantGroupId))
            {
                Assert.Equal(tenantGroupId, registered.Metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId]);
                Assert.Equal(tenantGroupId, capacity.Metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId]);
            }

            Assert.Equal(isolationMode.ToString(), registered.Metadata[AiRuntimeInstanceIsolationMetadataKeys.IsolationMode]);
            Assert.Equal(isolationMode.ToString(), capacity.Metadata[AiRuntimeInstanceIsolationMetadataKeys.IsolationMode]);
            Assert.Equal(preferDedicatedCapacity.ToString(), registered.Metadata[AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity]);
            Assert.Equal(preferDedicatedCapacity.ToString(), capacity.Metadata[AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity]);
            Assert.Equal(allowSharedFallback.ToString(), registered.Metadata[AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback]);
            Assert.Equal(allowSharedFallback.ToString(), capacity.Metadata[AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback]);
            Assert.Equal(maxRuntimeInstances.ToString(), registered.Metadata["runtime.maxRuntimeInstances"]);
            Assert.Equal(maxRuntimeInstances.ToString(), capacity.Metadata["runtime.maxRuntimeInstances"]);
            Assert.Equal(workerCountPerInstance.ToString(), registered.Metadata["runtime.workerCountPerInstance"]);
            Assert.Equal(workerCountPerInstance.ToString(), capacity.Metadata["runtime.workerCountPerInstance"]);
            Assert.Equal(maxConcurrentRunsPerInstance.ToString(), registered.Metadata["runtime.maxConcurrentRunsPerInstance"]);
            Assert.Equal(maxConcurrentRunsPerInstance.ToString(), capacity.Metadata["runtime.maxConcurrentRunsPerInstance"]);
            Assert.Equal(localQueueCapacity.ToString(), registered.Metadata["runtime.localQueueCapacity"]);
            Assert.Equal(localQueueCapacity.ToString(), capacity.Metadata["runtime.localQueueCapacity"]);
            Assert.Equal(runtimeInstanceIdPrefix, registered.Metadata["runtime.instanceIdPrefix"]);
            Assert.Equal(runtimeInstanceIdPrefix, capacity.Metadata["runtime.instanceIdPrefix"]);
        }

        /// <summary>
        /// Creates tenant-visible Redis runtime registry and capacity stores.
        /// </summary>
        /// <param name="services">The service provider.</param>
        /// <param name="tenantId">The tenant identifier.</param>
        /// <param name="tenantGroupId">The tenant group identifier.</param>
        /// <param name="project">The project name.</param>
        /// <returns>The tenant-visible registry and capacity store.</returns>
        private static (IAiRuntimeInstanceRegistry Registry, IAiRuntimeInstanceCapacityStore CapacityStore) CreateTenantVisibleRedisRuntimeStores(
            IServiceProvider services,
            string tenantId,
            string? tenantGroupId,
            string project)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
            ArgumentException.ThrowIfNullOrWhiteSpace(project);

            var redis = services.GetRequiredService<IConnectionMultiplexer>();
            var registrationOptions = services.GetRequiredService<IOptions<AiRuntimeInstanceRegistrationOptions>>();
            var controlPlaneIdResolver = services.GetRequiredService<IAiControlPlaneIdResolver>();
            var visibilityEvaluator = services.GetRequiredService<IAiRuntimeInstanceVisibilityEvaluator>();
            var executionContextProvider = new MutableExecutionContextSnapshotProvider
            {
                Current = CreateRuntimeVisibilityExecutionContextSnapshot(tenantId, tenantGroupId, project)
            };

            return (
                new RedisAiRuntimeInstanceRegistry(redis, registrationOptions, controlPlaneIdResolver, visibilityEvaluator, executionContextProvider),
                new RedisAiRuntimeInstanceCapacityStore(redis, registrationOptions, controlPlaneIdResolver, visibilityEvaluator, executionContextProvider));
        }

        /// <summary>
        /// Waits for a scale-out request to reach a specific status.
        /// </summary>
        /// <param name="store">The scale-out request store.</param>
        /// <param name="requestId">The request identifier.</param>
        /// <param name="expectedStatus">The expected request status.</param>
        /// <param name="timeout">The timeout.</param>
        /// <returns>The matching scale-out request record.</returns>
        private static async Task<AiRuntimeScaleOutRequestRecord> WaitForScaleOutRequestStatusAsync(
            IAiRuntimeScaleOutRequestStore store,
            string requestId,
            AiRuntimeScaleOutRequestStatus expectedStatus,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            AiRuntimeScaleOutRequestRecord? last = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                last = await store
                    .GetAsync(requestId)
                    .ConfigureAwait(false);

                if (last is not null && last.Status == expectedStatus)
                {
                    return last;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }

            if (last is not null)
            {
                throw new TimeoutException(
                    $"Scale-out request '{requestId}' did not reach status '{expectedStatus}' within '{timeout}'. " +
                    $"LastStatus='{last.Status}', RejectionReason='{last.RejectionReason}', RejectedBy='{last.RejectedBy}', " +
                    $"FulfilledRuntimeInstanceId='{last.FulfilledRuntimeInstanceId}', FulfilledBy='{last.FulfilledBy}', " +
                    $"Metadata='{FormatMetadata(last.Metadata)}'.");
            }

            throw new TimeoutException(
                $"Scale-out request '{requestId}' did not reach status '{expectedStatus}' within '{timeout}'. LastStatus='null'.");
        }

        /// <summary>
        /// Formats metadata for error output.
        /// </summary>
        /// <param name="metadata">The metadata.</param>
        /// <returns>The formatted metadata.</returns>
        private static string FormatMetadata(
            IEnumerable<KeyValuePair<string, string>>? metadata)
        {
            if (metadata is null)
            {
                return string.Empty;
            }

            return string.Join(
                " | ",
                metadata
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key}={pair.Value}"));
        }

        /// <summary>
        /// Creates an execution context snapshot that can see runtime registry and capacity records for a tenant.
        /// </summary>
        /// <param name="tenantId">The tenant identifier.</param>
        /// <param name="tenantGroupId">The tenant group identifier.</param>
        /// <param name="project">The project name.</param>
        /// <returns>The execution context snapshot.</returns>
        private static ExecutionContextSnapshot CreateRuntimeVisibilityExecutionContextSnapshot(
            string tenantId,
            string? tenantGroupId,
            string project)
        {
            return new ExecutionContextSnapshot
            {
                TenantId = tenantId,
                TenantGroupId = tenantGroupId,
                ContextKey = $"ctx-{project}",
                CurrentNamespace = tenantId,
                Namespaces = new List<NamespaceEntry>
                {
                    new()
                    {
                        Name = tenantId,
                        Trns = new HashSet<string>
                        {
                            $"trn:{project}:runtime-instance:registry:read",
                            $"trn:{project}:runtime-instance:registry:list",
                            $"trn:{project}:runtime-instance:capacity:read",
                            $"trn:{project}:shared-run:registry:read",
                            $"trn:{project}:shared-run:registry:list"
                        }
                    }
                },
                UserId = RequestedBy,
                Project = project
            };
        }

        /// <summary>
        /// Submits multiple runs through MCP.
        /// </summary>
        /// <param name="mcp">The MCP test client.</param>
        /// <param name="pipelineName">The pipeline name.</param>
        /// <param name="count">The number of runs.</param>
        /// <param name="stepCount">The step count.</param>
        /// <param name="flakyStepInterval">The flaky step interval.</param>
        /// <param name="tenantId">The optional tenant identifier.</param>
        /// <returns>The submitted shared run identifiers.</returns>
        private static async Task<IReadOnlySet<string>> SubmitRunsAsync(
            McpTestClient mcp,
            string pipelineName,
            int count,
            int stepCount,
            int flakyStepInterval,
            string? tenantId = null)
        {
            var submitRequest = CreateSubmitRequest(
                pipelineName,
                stepCount: stepCount,
                flakyStepInterval: flakyStepInterval,
                tenantId: tenantId);

            var submitResults = await mcp
                .SubmitManyRunsAsync(submitRequest, count)
                .ConfigureAwait(false);

            Assert.Equal(count, submitResults.Count);
            Assert.All(submitResults, result => Assert.True(result.Success, result.FailureReason ?? result.Message));

            var submittedSharedRunIds = submitResults
                .Select(ExtractSharedRunId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(count, submittedSharedRunIds.Count);

            return submittedSharedRunIds;
        }

        /// <summary>
        /// Extracts a shared run identifier from a submit result object.
        /// </summary>
        /// <param name="submitResult">The submit result.</param>
        /// <returns>The shared run identifier.</returns>
        private static string ExtractSharedRunId(
            object submitResult)
        {
            ArgumentNullException.ThrowIfNull(submitResult);

            var resultType = submitResult.GetType();

            var directSharedRunId = resultType
                .GetProperty("SharedRunId")
                ?.GetValue(submitResult) as string;

            if (!string.IsNullOrWhiteSpace(directSharedRunId))
            {
                return directSharedRunId;
            }

            var runId = resultType
                .GetProperty("RunId")
                ?.GetValue(submitResult) as string;

            if (!string.IsNullOrWhiteSpace(runId))
            {
                return runId;
            }

            var sharedRun = resultType
                .GetProperty("SharedRun")
                ?.GetValue(submitResult);

            if (sharedRun is not null)
            {
                var sharedRunId = sharedRun
                    .GetType()
                    .GetProperty("SharedRunId")
                    ?.GetValue(sharedRun) as string;

                if (!string.IsNullOrWhiteSpace(sharedRunId))
                {
                    return sharedRunId;
                }
            }

            var run = resultType
                .GetProperty("Run")
                ?.GetValue(submitResult);

            if (run is not null)
            {
                var sharedRunId = run
                    .GetType()
                    .GetProperty("SharedRunId")
                    ?.GetValue(run) as string;

                if (!string.IsNullOrWhiteSpace(sharedRunId))
                {
                    return sharedRunId;
                }
            }

            throw new InvalidOperationException(
                $"Could not extract SharedRunId from submit result type '{resultType.FullName}'.");
        }

        /// <summary>
        /// Creates a shared runtime controller submit request.
        /// </summary>
        /// <param name="pipelineName">The pipeline name.</param>
        /// <param name="stepCount">The step count.</param>
        /// <param name="flakyStepInterval">The flaky step interval.</param>
        /// <param name="tenantId">The optional tenant identifier.</param>
        /// <returns>The controller request.</returns>
        private static AiSharedRuntimeControllerRequest CreateSubmitRequest(
            string pipelineName,
            int stepCount,
            int flakyStepInterval,
            string? tenantId = null)
        {
            return new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                PipelineKey = pipelineName,
                TenantId = tenantId ?? UnknownTenantId,
                RequestedBy = RequestedBy,
                Source = Source,
                RunRequest = McpTestPipelineFactory.CreateRunRequest(
                    pipelineName,
                    stepCount: stepCount,
                    flakyStepInterval: flakyStepInterval)
            };
        }

        /// <summary>
        /// Submits a single run and waits for the associated scale-out request to be fulfilled.
        /// </summary>
        /// <param name="mcp">The MCP test client.</param>
        /// <param name="sharedRunStore">The shared run store.</param>
        /// <param name="scaleOutRequestStore">The scale-out request store.</param>
        /// <param name="controlPlaneId">The control plane identifier.</param>
        /// <param name="pipelineName">The pipeline name.</param>
        /// <param name="tenantId">The tenant identifier.</param>
        /// <param name="timeout">The timeout.</param>
        /// <returns>The shared run and fulfilled scale-out request.</returns>
        private static async Task<(string SharedRunId, AiSharedRunRecord SharedRun, AiRuntimeScaleOutRequestRecord ScaleOutRequest)> SubmitSingleRunAndWaitForFulfilledScaleOutAsync(
            McpTestClient mcp,
            IAiSharedRunStore sharedRunStore,
            IAiRuntimeScaleOutRequestStore scaleOutRequestStore,
            string controlPlaneId,
            string pipelineName,
            string tenantId,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(mcp);
            ArgumentNullException.ThrowIfNull(sharedRunStore);
            ArgumentNullException.ThrowIfNull(scaleOutRequestStore);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

            var submittedSharedRunIds = await SubmitRunsAsync(
                    mcp,
                    pipelineName,
                    count: 1,
                    stepCount: 3,
                    flakyStepInterval: 0,
                    tenantId: tenantId)
                .ConfigureAwait(false);

            var sharedRunId = Assert.Single(submittedSharedRunIds);

            var sharedRun = await sharedRunStore
                .GetAsync(sharedRunId)
                .ConfigureAwait(false);

            Assert.NotNull(sharedRun);
            Assert.Equal(AiSharedRunStatus.ScaleOutRequested, sharedRun!.Status);
            Assert.Equal(controlPlaneId, sharedRun.ControlPlaneId);
            Assert.Equal(pipelineName, sharedRun.PipelineKey);
            Assert.Equal(tenantId, sharedRun.ExecutionContextSnapshot.TenantId);
            Assert.Equal(tenantId, sharedRun.RunRequest.ExecutionContextSnapshot?.TenantId);
            Assert.NotNull(sharedRun.AdmissionDecision);
            Assert.Equal(AiRunAdmissionDecisionType.RequestScaleOut, sharedRun.AdmissionDecision.DecisionType);
            Assert.Equal(tenantId, sharedRun.AdmissionDecision.TenantId);

            var expectedScaleOutRequestId = $"scale-out-{sharedRunId}";

            var scaleOutRequest = await WaitForScaleOutRequestStatusAsync(
                    scaleOutRequestStore,
                    expectedScaleOutRequestId,
                    AiRuntimeScaleOutRequestStatus.Fulfilled,
                    timeout)
                .ConfigureAwait(false);

            Assert.Equal(expectedScaleOutRequestId, scaleOutRequest.RequestId);
            Assert.Equal(sharedRunId, scaleOutRequest.SharedRunId);
            Assert.Equal(controlPlaneId, scaleOutRequest.ControlPlaneId);
            Assert.Equal(tenantId, scaleOutRequest.TenantId);
            Assert.Equal(pipelineName, scaleOutRequest.PipelineKey);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Fulfilled, scaleOutRequest.Status);
            Assert.Equal("http", scaleOutRequest.ProviderHint);
            Assert.Equal("http", scaleOutRequest.Metadata["providerHint"]);
            Assert.False(string.IsNullOrWhiteSpace(scaleOutRequest.FulfilledRuntimeInstanceId));

            return (sharedRunId, sharedRun, scaleOutRequest);
        }

        /// <summary>
        /// Mutable execution context snapshot provider used by tenant-visible Redis stores in tests.
        /// </summary>
        private sealed class MutableExecutionContextSnapshotProvider : IExecutionContextSnapshotProvider
        {
            /// <summary>
            /// Gets or sets the current execution context snapshot.
            /// </summary>
            public ExecutionContextSnapshot? Current { get; set; }

            /// <inheritdoc />
            public ExecutionContextSnapshot MapToSnapshot()
            {
                return this.Current
                    ?? throw new InvalidOperationException("No execution context snapshot is currently configured for the test.");
            }
        }
    }
}