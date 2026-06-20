using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
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
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http.ScaleOut;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue.Redis;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Http
{
    /// <summary>
    /// Contains MCP integration tests for HTTP runtime provider scale-out orchestration.
    /// </summary>
    /// <remarks>
    /// These scenarios validate the HTTP scale-out control-plane flow:
    ///
    /// <list type="number">
    /// <item><description>MCP submits a shared run.</description></item>
    /// <item><description>Admission finds no available runtime capacity.</description></item>
    /// <item><description>The shared run is marked as <see cref="AiSharedRunStatus.ScaleOutRequested" />.</description></item>
    /// <item><description>A Redis-backed scale-out request is created with provider hint <c>http</c>.</description></item>
    /// <item><description>The scale-out watcher observes the request.</description></item>
    /// <item><description>The selector resolves the HTTP runtime instance provider.</description></item>
    /// <item><description>The HTTP provider delegates scale-out to the HTTP provisioner.</description></item>
    /// <item><description>The HTTP provisioner registers HTTP runtime capacity.</description></item>
    /// <item><description>The Redis-backed scale-out request is fulfilled.</description></item>
    /// </list>
    ///
    /// This test intentionally stops before dispatching to a real HTTP runtime endpoint.
    /// </remarks>
    public sealed class HttpRuntimeProviderSharedRunScaleOutScenarioTests
    {
        /// <summary>
        /// Actor used by HTTP scale-out scenario tests.
        /// </summary>
        private const string RequestedBy = "mcp-http-scaleout-integration-test";

        /// <summary>
        /// Source used by HTTP scale-out scenario tests.
        /// </summary>
        private const string Source = "mcp-http-scaleout-test";

        /// <summary>
        /// Tenant used by the default shared HTTP scale-out scenario.
        /// </summary>
        private const string TenantId = "test-tenant";

        /// <summary>
        /// Runtime id prefix expected from default shared tenant runtime settings.
        /// </summary>
        private const string SharedRuntimeInstanceIdPrefix = "runtime-instance";

        /// <summary>
        /// Dedicated tenant used by HTTP tenant-aware scale-out propagation tests.
        /// </summary>
        private const string TenantAwareTenantId = "tenant-a";

        /// <summary>
        /// Runtime id prefix expected from hardcoded tenant-a runtime settings.
        /// </summary>
        private const string TenantAwareRuntimeInstanceIdPrefix = "tenant-a-runtime";

        /// <summary>
        /// Hybrid tenant used by HTTP tenant-aware scale-out propagation tests.
        /// </summary>
        private const string HybridTenantId = "tenant-b";

        /// <summary>
        /// Runtime id prefix expected from hardcoded tenant-b runtime settings.
        /// </summary>
        private const string HybridRuntimeInstanceIdPrefix = "tenant-b-runtime";

        /// <summary>
        /// The test output helper.
        /// </summary>
        private readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpRuntimeProviderSharedRunScaleOutScenarioTests" /> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public HttpRuntimeProviderSharedRunScaleOutScenarioTests(
            ITestOutputHelper output)
        {
            this.output =
                output;
        }

        /// <summary>
        /// Verifies that a real MCP control-plane host creates and fulfills a Redis-backed
        /// scale-out request using the HTTP runtime provider and HTTP scale-out provisioner.
        /// </summary>
        [Fact]
        public async Task ControlPlaneWithHttpRuntimeInstances_With_No_Runtime_Capacity_Should_Fulfill_Redis_ScaleOut_Request_Using_Http_Provider()
        {
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "http-scaleout-request");

            var controlPlaneSettings =
                GenericMcpServerTestSettings.CreateHttpScaleOutOnlyControlPlaneSettings(
                    controlPlaneId);

            await using var host =
                new GenericMcpServerTestHost(
                    controlPlaneSettings);

            using var client =
                host.CreateClient();

            AssertRedisStoresPublisherWatcherAndHttpProvider(
                host.Services);

            var mcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        host,
                        client,
                        RequestedBy,
                        tenantId: TenantId)
                    .ConfigureAwait(false);

            var sharedRunStore =
                host.Services.GetRequiredService<IAiSharedRunStore>();

            var scaleOutRequestStore =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var runtimeInstanceRegistry =
                host.Services.GetRequiredService<IAiRuntimeInstanceRegistry>();

            var runtimeInstanceCapacityStore =
                host.Services.GetRequiredService<IAiRuntimeInstanceCapacityStore>();

            var pipelineName =
                $"mcp-http-scaleout-request-{Guid.NewGuid():N}";

            var expectedSharedRunIds =
                await SubmitRunsAsync(
                        mcp,
                        pipelineName,
                        count: 1,
                        stepCount: 3,
                        flakyStepInterval: 0,
                        tenantId: TenantId)
                    .ConfigureAwait(false);

            var sharedRunId =
                Assert.Single(
                    expectedSharedRunIds);

            var sharedRun =
                await sharedRunStore
                    .GetAsync(
                        sharedRunId)
                    .ConfigureAwait(false);

            Assert.NotNull(
                sharedRun);

            Assert.Equal(
                AiSharedRunStatus.ScaleOutRequested,
                sharedRun!.Status);

            Assert.Equal(
                controlPlaneId,
                sharedRun.ControlPlaneId);

            Assert.Equal(
                pipelineName,
                sharedRun.PipelineKey);

            Assert.Equal(
                TenantId,
                sharedRun.ExecutionContextSnapshot.TenantId);

            Assert.Equal(
                TenantId,
                sharedRun.RunRequest.ExecutionContextSnapshot?.TenantId);

            Assert.NotNull(
                sharedRun.AdmissionDecision);

            Assert.Equal(
                AiRunAdmissionDecisionType.RequestScaleOut,
                sharedRun.AdmissionDecision.DecisionType);

            Assert.Equal(
                TenantId,
                sharedRun.AdmissionDecision.TenantId);

            Assert.Equal(
                AiRuntimeInstanceIsolationMode.Shared,
                sharedRun.AdmissionDecision.TenantRuntimeSettings?.IsolationMode);

            Assert.False(
                sharedRun.AdmissionDecision.TenantRuntimeSettings?.PreferDedicatedCapacity);

            Assert.True(
                sharedRun.AdmissionDecision.TenantRuntimeSettings?.AllowSharedFallback);

            Assert.Equal(
                1,
                sharedRun.AdmissionDecision.TenantRuntimeSettings?.MaxRuntimeInstances);

            Assert.Equal(
                SharedRuntimeInstanceIdPrefix,
                sharedRun.AdmissionDecision.TenantRuntimeSettings?.RuntimeInstanceIdPrefix);

            var expectedScaleOutRequestId =
                $"scale-out-{sharedRunId}";

            var scaleOutRequest =
                await WaitForScaleOutRequestStatusAsync(
                        scaleOutRequestStore,
                        expectedScaleOutRequestId,
                        AiRuntimeScaleOutRequestStatus.Fulfilled,
                        TimeSpan.FromSeconds(15))
                    .ConfigureAwait(false);

            Assert.Equal(
                expectedScaleOutRequestId,
                scaleOutRequest.RequestId);

            Assert.Equal(
                sharedRunId,
                scaleOutRequest.SharedRunId);

            Assert.Equal(
                controlPlaneId,
                scaleOutRequest.ControlPlaneId);

            Assert.Equal(
                TenantId,
                scaleOutRequest.TenantId);

            Assert.Equal(
                pipelineName,
                scaleOutRequest.PipelineKey);

            Assert.Equal(
                AiRuntimeScaleOutRequestStatus.Fulfilled,
                scaleOutRequest.Status);

            Assert.Equal(
                "http",
                scaleOutRequest.ProviderHint);

            Assert.Equal(
                "http",
                scaleOutRequest.Metadata["providerHint"]);

            Assert.Equal(
                AiRuntimeInstanceIsolationMode.Shared,
                scaleOutRequest.IsolationMode);

            Assert.False(
                scaleOutRequest.PreferDedicatedCapacity);

            Assert.True(
                scaleOutRequest.AllowSharedFallback);

            Assert.Equal(
                1,
                scaleOutRequest.MaxRuntimeInstances);

            Assert.Equal(
                SharedRuntimeInstanceIdPrefix,
                scaleOutRequest.RuntimeInstanceIdPrefix);

            Assert.Equal(
                10,
                scaleOutRequest.WorkerCountPerInstance);

            Assert.Equal(
                3,
                scaleOutRequest.MaxConcurrentRunsPerInstance);

            Assert.Equal(
                0,
                scaleOutRequest.AvailableInstanceCount);

            Assert.Equal(
                0,
                scaleOutRequest.CurrentInstanceCount);

            Assert.Equal(
                3,
                scaleOutRequest.MaxInstanceCount);

            Assert.Equal(
                1,
                scaleOutRequest.RequestedTargetInstanceCount);

            Assert.Equal(
                "mcp-scaleout-watcher",
                scaleOutRequest.ObservedBy);

            Assert.Equal(
                "mcp-scaleout-watcher",
                scaleOutRequest.FulfilledBy);

            Assert.False(
                string.IsNullOrWhiteSpace(
                    scaleOutRequest.FulfilledRuntimeInstanceId));

            Assert.Contains(
                $":{SharedRuntimeInstanceIdPrefix}-1",
                scaleOutRequest.FulfilledRuntimeInstanceId,
                StringComparison.Ordinal);

            Assert.Equal(
                "Shared",
                scaleOutRequest.Metadata["runtime.isolationMode"]);

            Assert.Equal(
                "False",
                scaleOutRequest.Metadata["runtime.preferDedicatedCapacity"]);

            Assert.Equal(
                "True",
                scaleOutRequest.Metadata["runtime.allowSharedFallback"]);

            Assert.Equal(
                "1",
                scaleOutRequest.Metadata["runtime.maxRuntimeInstances"]);

            Assert.Equal(
                SharedRuntimeInstanceIdPrefix,
                scaleOutRequest.Metadata["runtime.instanceIdPrefix"]);

            Assert.Equal(
                "10",
                scaleOutRequest.Metadata["runtime.workerCountPerInstance"]);

            Assert.Equal(
                "3",
                scaleOutRequest.Metadata["runtime.maxConcurrentRunsPerInstance"]);

            var fulfilledRuntimeInstanceId =
                scaleOutRequest.FulfilledRuntimeInstanceId!;

            var registered =
                await runtimeInstanceRegistry
                    .GetAsync(
                        fulfilledRuntimeInstanceId)
                    .ConfigureAwait(false);

            var capacity =
                await runtimeInstanceCapacityStore
                    .GetAsync(
                        fulfilledRuntimeInstanceId)
                    .ConfigureAwait(false);

            Assert.NotNull(
                registered);

            Assert.NotNull(
                capacity);

            Assert.Equal(
                fulfilledRuntimeInstanceId,
                registered!.RuntimeInstanceId);

            Assert.Equal(
                fulfilledRuntimeInstanceId,
                capacity!.RuntimeInstanceId);

            Assert.Equal(
                "http",
                registered.Metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName]);

            Assert.Equal(
                "http",
                capacity.Metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName]);

            Assert.Equal(
                "http",
                registered.Metadata["provider.name"]);

            Assert.Equal(
                "http",
                capacity.Metadata["provider.name"]);

            Assert.Equal(
                AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                registered.Metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName]);

            Assert.Equal(
                AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                capacity.Metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName]);

            Assert.Equal(
                "Shared",
                registered.Metadata["runtime.isolationMode"]);

            Assert.Equal(
                "Shared",
                capacity.Metadata["runtime.isolationMode"]);

            output.WriteLine(
                $"Redis HTTP scale-out request fulfilled. ControlPlaneId='{controlPlaneId}', " +
                $"SharedRunId='{sharedRunId}', RequestId='{scaleOutRequest.RequestId}', " +
                $"RuntimeInstanceId='{fulfilledRuntimeInstanceId}', PipelineKey='{pipelineName}'.");
        }

        /// <summary>
        /// Verifies that a real MCP control-plane host propagates dedicated tenant runtime
        /// settings into the Redis-backed HTTP scale-out request and HTTP provisioned capacity.
        /// </summary>
        [Fact]
        public async Task ControlPlaneWithHttpRuntimeInstances_With_Dedicated_Tenant_Should_Fulfill_Tenant_Aware_Redis_ScaleOut_Request_Using_Http_Provider()
        {
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "http-dedicated-tenant-scaleout-request");

            var controlPlaneSettings =
                GenericMcpServerTestSettings.CreateHttpScaleOutOnlyControlPlaneSettings(
                    controlPlaneId);

            await using var host =
                new GenericMcpServerTestHost(
                    controlPlaneSettings);

            using var client =
                host.CreateClient();

            AssertRedisStoresPublisherWatcherAndHttpProvider(
                host.Services);

            var tenantRuntimeSettingsProvider =
                host.Services.GetRequiredService<IAiTenantRuntimeSettingsProvider>();

            var tenantRuntimeSettings =
                tenantRuntimeSettingsProvider.GetSettings(
                    TenantAwareTenantId,
                    null);

            output.WriteLine(
                $"HTTP dedicated tenant settings resolved. TenantId='{tenantRuntimeSettings.TenantId}', " +
                $"TenantGroupId='{tenantRuntimeSettings.TenantGroupId ?? "null"}', " +
                $"IsolationMode='{tenantRuntimeSettings.IsolationMode}', " +
                $"RuntimeInstanceIdPrefix='{tenantRuntimeSettings.RuntimeInstanceIdPrefix}', " +
                $"MaxRuntimeInstances='{tenantRuntimeSettings.MaxRuntimeInstances}'.");

            Assert.Equal(TenantAwareTenantId, tenantRuntimeSettings.TenantId);
            Assert.Equal(AiRuntimeInstanceIsolationMode.Dedicated, tenantRuntimeSettings.IsolationMode);
            Assert.True(tenantRuntimeSettings.PreferDedicatedCapacity);
            Assert.False(tenantRuntimeSettings.AllowSharedFallback);
            Assert.Equal(3, tenantRuntimeSettings.MaxRuntimeInstances);
            Assert.Equal(TenantAwareRuntimeInstanceIdPrefix, tenantRuntimeSettings.RuntimeInstanceIdPrefix);
            Assert.Equal(10, tenantRuntimeSettings.WorkerCountPerInstance);
            Assert.Equal(5, tenantRuntimeSettings.MaxConcurrentRunsPerInstance);
            Assert.Equal(500, tenantRuntimeSettings.LocalQueueCapacity);

            var mcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        host,
                        client,
                        RequestedBy,
                        tenantId: TenantAwareTenantId)
                    .ConfigureAwait(false);

            var sharedRunStore =
                host.Services.GetRequiredService<IAiSharedRunStore>();

            var scaleOutRequestStore =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var redis =
                host.Services.GetRequiredService<IConnectionMultiplexer>();

            var registrationOptions =
                host.Services.GetRequiredService<IOptions<AiRuntimeInstanceRegistrationOptions>>();

            var controlPlaneIdResolver =
                host.Services.GetRequiredService<IAiControlPlaneIdResolver>();

            var visibilityEvaluator =
                host.Services.GetRequiredService<IAiRuntimeInstanceVisibilityEvaluator>();

            var executionContextProvider =
                new MutableExecutionContextSnapshotProvider();

            var pipelineName =
                $"mcp-http-dedicated-tenant-scaleout-{Guid.NewGuid():N}";

            var expectedSharedRunIds =
                await SubmitRunsAsync(
                        mcp,
                        pipelineName,
                        count: 1,
                        stepCount: 3,
                        flakyStepInterval: 0,
                        tenantId: TenantAwareTenantId)
                    .ConfigureAwait(false);

            var sharedRunId =
                Assert.Single(
                    expectedSharedRunIds);

            var sharedRun =
                await sharedRunStore
                    .GetAsync(
                        sharedRunId)
                    .ConfigureAwait(false);

            Assert.NotNull(sharedRun);
            Assert.Equal(AiSharedRunStatus.ScaleOutRequested, sharedRun!.Status);
            Assert.Equal(controlPlaneId, sharedRun.ControlPlaneId);
            Assert.Equal(pipelineName, sharedRun.PipelineKey);
            Assert.Equal(TenantAwareTenantId, sharedRun.ExecutionContextSnapshot.TenantId);
            Assert.Equal(TenantAwareTenantId, sharedRun.RunRequest.ExecutionContextSnapshot?.TenantId);

            var expectedTenantGroupId =
                sharedRun.ExecutionContextSnapshot.TenantGroupId;

            Assert.False(string.IsNullOrWhiteSpace(expectedTenantGroupId));

            Assert.NotNull(sharedRun.AdmissionDecision);
            Assert.Equal(AiRunAdmissionDecisionType.RequestScaleOut, sharedRun.AdmissionDecision.DecisionType);
            Assert.Equal(TenantAwareTenantId, sharedRun.AdmissionDecision.TenantId);
            Assert.Equal(expectedTenantGroupId, sharedRun.AdmissionDecision.TenantGroupId);
            Assert.NotNull(sharedRun.AdmissionDecision.TenantRuntimeSettings);

            Assert.Equal(AiRuntimeInstanceIsolationMode.Dedicated, sharedRun.AdmissionDecision.TenantRuntimeSettings!.IsolationMode);
            Assert.True(sharedRun.AdmissionDecision.TenantRuntimeSettings.PreferDedicatedCapacity);
            Assert.False(sharedRun.AdmissionDecision.TenantRuntimeSettings.AllowSharedFallback);
            Assert.Equal(3, sharedRun.AdmissionDecision.TenantRuntimeSettings.MaxRuntimeInstances);
            Assert.Equal(TenantAwareRuntimeInstanceIdPrefix, sharedRun.AdmissionDecision.TenantRuntimeSettings.RuntimeInstanceIdPrefix);
            Assert.Equal(10, sharedRun.AdmissionDecision.TenantRuntimeSettings.WorkerCountPerInstance);
            Assert.Equal(5, sharedRun.AdmissionDecision.TenantRuntimeSettings.MaxConcurrentRunsPerInstance);
            Assert.Equal(500, sharedRun.AdmissionDecision.TenantRuntimeSettings.LocalQueueCapacity);

            output.WriteLine(
                $"HTTP dedicated shared run submitted. SharedRunId='{sharedRunId}', " +
                $"TenantId='{sharedRun.ExecutionContextSnapshot.TenantId}', " +
                $"TenantGroupId='{expectedTenantGroupId}', " +
                $"Status='{sharedRun.Status}', PipelineKey='{pipelineName}'.");

            var expectedScaleOutRequestId =
                $"scale-out-{sharedRunId}";

            var scaleOutRequest =
                await WaitForScaleOutRequestStatusAsync(
                        scaleOutRequestStore,
                        expectedScaleOutRequestId,
                        AiRuntimeScaleOutRequestStatus.Fulfilled,
                        TimeSpan.FromSeconds(15))
                    .ConfigureAwait(false);

            Assert.Equal(expectedScaleOutRequestId, scaleOutRequest.RequestId);
            Assert.Equal(sharedRunId, scaleOutRequest.SharedRunId);
            Assert.Equal(controlPlaneId, scaleOutRequest.ControlPlaneId);
            Assert.Equal(TenantAwareTenantId, scaleOutRequest.TenantId);
            Assert.Equal(expectedTenantGroupId, scaleOutRequest.TenantGroupId);
            Assert.Equal(pipelineName, scaleOutRequest.PipelineKey);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Fulfilled, scaleOutRequest.Status);
            Assert.Equal("http", scaleOutRequest.ProviderHint);
            Assert.Equal("http", scaleOutRequest.Metadata["providerHint"]);
            Assert.Equal(AiRuntimeInstanceIsolationMode.Dedicated, scaleOutRequest.IsolationMode);
            Assert.True(scaleOutRequest.PreferDedicatedCapacity);
            Assert.False(scaleOutRequest.AllowSharedFallback);
            Assert.Equal(3, scaleOutRequest.MaxRuntimeInstances);
            Assert.Equal(TenantAwareRuntimeInstanceIdPrefix, scaleOutRequest.RuntimeInstanceIdPrefix);
            Assert.Equal(10, scaleOutRequest.WorkerCountPerInstance);
            Assert.Equal(5, scaleOutRequest.MaxConcurrentRunsPerInstance);
            Assert.Equal(500, scaleOutRequest.LocalQueueCapacity);
            Assert.Equal(0, scaleOutRequest.AvailableInstanceCount);
            Assert.Equal(0, scaleOutRequest.CurrentInstanceCount);
            Assert.Equal(3, scaleOutRequest.MaxInstanceCount);
            Assert.Equal(1, scaleOutRequest.RequestedTargetInstanceCount);
            Assert.Equal("mcp-scaleout-watcher", scaleOutRequest.ObservedBy);
            Assert.Equal("mcp-scaleout-watcher", scaleOutRequest.FulfilledBy);

            Assert.False(
                string.IsNullOrWhiteSpace(
                    scaleOutRequest.FulfilledRuntimeInstanceId));

            Assert.Contains(
                $":{TenantAwareRuntimeInstanceIdPrefix}-1",
                scaleOutRequest.FulfilledRuntimeInstanceId,
                StringComparison.Ordinal);

            Assert.DoesNotContain(
                $":{SharedRuntimeInstanceIdPrefix}-1",
                scaleOutRequest.FulfilledRuntimeInstanceId,
                StringComparison.Ordinal);

            Assert.Equal("Dedicated", scaleOutRequest.Metadata["runtime.isolationMode"]);
            Assert.Equal("True", scaleOutRequest.Metadata["runtime.preferDedicatedCapacity"]);
            Assert.Equal("False", scaleOutRequest.Metadata["runtime.allowSharedFallback"]);
            Assert.Equal("3", scaleOutRequest.Metadata["runtime.maxRuntimeInstances"]);
            Assert.Equal(TenantAwareRuntimeInstanceIdPrefix, scaleOutRequest.Metadata["runtime.instanceIdPrefix"]);
            Assert.Equal("10", scaleOutRequest.Metadata["runtime.workerCountPerInstance"]);
            Assert.Equal("5", scaleOutRequest.Metadata["runtime.maxConcurrentRunsPerInstance"]);
            Assert.Equal("500", scaleOutRequest.Metadata["runtime.localQueueCapacity"]);

            var fulfilledRuntimeInstanceId =
                scaleOutRequest.FulfilledRuntimeInstanceId!;

            executionContextProvider.Current =
                CreateRuntimeVisibilityExecutionContextSnapshot(
                    TenantAwareTenantId,
                    expectedTenantGroupId,
                    "http-dedicated-tenant-scaleout-request");

            var runtimeInstanceRegistry =
                new RedisAiRuntimeInstanceRegistry(
                    redis,
                    registrationOptions,
                    controlPlaneIdResolver,
                    visibilityEvaluator,
                    executionContextProvider);

            var runtimeInstanceCapacityStore =
                new RedisAiRuntimeInstanceCapacityStore(
                    redis,
                    registrationOptions,
                    controlPlaneIdResolver,
                    visibilityEvaluator,
                    executionContextProvider);

            var registered =
                await runtimeInstanceRegistry
                    .GetAsync(
                        fulfilledRuntimeInstanceId)
                    .ConfigureAwait(false);

            var capacity =
                await runtimeInstanceCapacityStore
                    .GetAsync(
                        fulfilledRuntimeInstanceId)
                    .ConfigureAwait(false);

            Assert.NotNull(registered);
            Assert.NotNull(capacity);
            Assert.Equal(fulfilledRuntimeInstanceId, registered!.RuntimeInstanceId);
            Assert.Equal(fulfilledRuntimeInstanceId, capacity!.RuntimeInstanceId);
            Assert.Equal("http", registered.Metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName]);
            Assert.Equal("http", capacity.Metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName]);
            Assert.Equal("http", registered.Metadata["provider.name"]);
            Assert.Equal("http", capacity.Metadata["provider.name"]);

            Assert.Equal(
                AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                registered.Metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName]);

            Assert.Equal(
                AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                capacity.Metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName]);

            Assert.Equal(TenantAwareTenantId, registered.Metadata["tenant.id"]);
            Assert.Equal(TenantAwareTenantId, capacity.Metadata["tenant.id"]);
            Assert.Equal(expectedTenantGroupId, registered.Metadata["tenant.group.id"]);
            Assert.Equal(expectedTenantGroupId, capacity.Metadata["tenant.group.id"]);
            Assert.Equal("Dedicated", registered.Metadata["runtime.isolationMode"]);
            Assert.Equal("Dedicated", capacity.Metadata["runtime.isolationMode"]);
            Assert.Equal("True", registered.Metadata["runtime.preferDedicatedCapacity"]);
            Assert.Equal("True", capacity.Metadata["runtime.preferDedicatedCapacity"]);
            Assert.Equal("False", registered.Metadata["runtime.allowSharedFallback"]);
            Assert.Equal("False", capacity.Metadata["runtime.allowSharedFallback"]);
            Assert.Equal("3", registered.Metadata["runtime.maxRuntimeInstances"]);
            Assert.Equal("3", capacity.Metadata["runtime.maxRuntimeInstances"]);
            Assert.Equal(TenantAwareRuntimeInstanceIdPrefix, registered.Metadata["runtime.instanceIdPrefix"]);
            Assert.Equal(TenantAwareRuntimeInstanceIdPrefix, capacity.Metadata["runtime.instanceIdPrefix"]);
            Assert.Equal("10", registered.Metadata["runtime.workerCountPerInstance"]);
            Assert.Equal("10", capacity.Metadata["runtime.workerCountPerInstance"]);
            Assert.Equal("5", registered.Metadata["runtime.maxConcurrentRunsPerInstance"]);
            Assert.Equal("5", capacity.Metadata["runtime.maxConcurrentRunsPerInstance"]);
            Assert.Equal("500", registered.Metadata["runtime.localQueueCapacity"]);
            Assert.Equal("500", capacity.Metadata["runtime.localQueueCapacity"]);

            output.WriteLine(
                $"Redis HTTP dedicated tenant scale-out request fulfilled. ControlPlaneId='{controlPlaneId}', " +
                $"SharedRunId='{sharedRunId}', RequestId='{scaleOutRequest.RequestId}', " +
                $"TenantId='{scaleOutRequest.TenantId}', TenantGroupId='{scaleOutRequest.TenantGroupId}', " +
                $"IsolationMode='{scaleOutRequest.IsolationMode}', RuntimeInstanceId='{fulfilledRuntimeInstanceId}', " +
                $"PipelineKey='{pipelineName}'.");
        }

        /// <summary>
        /// Verifies that Redis-backed control-plane stores, the store-backed scale-out publisher,
        /// the selector, HTTP provider, HTTP scale-out provisioner, admission policy, and watcher hosted service are registered correctly.
        /// </summary>
        /// <param name="services">The service provider to inspect.</param>
        private void AssertRedisStoresPublisherWatcherAndHttpProvider(
            IServiceProvider services)
        {
            ArgumentNullException.ThrowIfNull(
                services);

            var sharedRunStore =
                services.GetRequiredService<IAiSharedRunStore>();

            var sharedQueue =
                services.GetRequiredService<IAiSharedQueue>();

            var reservationStore =
                services.GetRequiredService<IAiRuntimeAdmissionReservationStore>();

            var scaleOutRequestStore =
                services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var scaleOutPublisher =
                services.GetRequiredService<IAiRuntimeScaleOutRequestPublisher>();

            var scaleOutSelector =
                services.GetRequiredService<IAiRuntimeScaleOutProviderSelector>();

            var httpProvisioner =
                services.GetRequiredService<IAiHttpRuntimeScaleOutProvisioner>();

            var admissionOptions =
                services.GetRequiredService<IOptions<AiRunAdmissionOptions>>().Value;

            var providers =
                services.GetServices<IAiRuntimeInstanceProvider>().ToArray();

            var watcherOptions =
                services.GetRequiredService<IOptions<AiRuntimeScaleOutRequestWatcherOptions>>().Value;

            var httpScaleOutOptions =
                services.GetRequiredService<IOptions<AiHttpRuntimeScaleOutOptions>>().Value;

            var hostedServices =
                services.GetServices<IHostedService>().ToArray();

            output.WriteLine(
                $"Redis HTTP scale-out assert: IAiSharedRunStore='{sharedRunStore.GetType().FullName}'.");

            output.WriteLine(
                $"Redis HTTP scale-out assert: IAiSharedQueue='{sharedQueue.GetType().FullName}'.");

            output.WriteLine(
                $"Redis HTTP scale-out assert: IAiRuntimeAdmissionReservationStore='{reservationStore.GetType().FullName}'.");

            output.WriteLine(
                $"Redis HTTP scale-out assert: IAiRuntimeScaleOutRequestStore='{scaleOutRequestStore.GetType().FullName}'.");

            output.WriteLine(
                $"Redis HTTP scale-out assert: IAiRuntimeScaleOutRequestPublisher='{scaleOutPublisher.GetType().FullName}'.");

            output.WriteLine(
                $"Redis HTTP scale-out assert: IAiRuntimeScaleOutProviderSelector='{scaleOutSelector.GetType().FullName}'.");

            output.WriteLine(
                $"Redis HTTP scale-out assert: IAiHttpRuntimeScaleOutProvisioner='{httpProvisioner.GetType().FullName}'.");

            output.WriteLine(
                $"Redis HTTP scale-out assert: Admission.Enabled='{admissionOptions.Enabled}', " +
                $"MaxInstanceCount='{admissionOptions.MaxInstanceCount?.ToString() ?? "null"}', " +
                $"EnableScaleOutRequest='{admissionOptions.EnableScaleOutRequest}', " +
                $"EnableGlobalQueueFallback='{admissionOptions.EnableGlobalQueueFallback}', " +
                $"RejectWhenNoCapacity='{admissionOptions.RejectWhenNoCapacity}'.");

            output.WriteLine(
                $"Redis HTTP scale-out assert: Watcher.Enabled='{watcherOptions.Enabled}', WatcherId='{watcherOptions.WatcherId}', ControlPlaneId='{watcherOptions.ControlPlaneId}', Interval='{watcherOptions.Interval}', MaxRequestsPerCycle='{watcherOptions.MaxRequestsPerCycle}'.");

            output.WriteLine(
                $"Redis HTTP scale-out assert: HttpScaleOut.Enabled='{httpScaleOutOptions.Enabled}', RuntimeInstanceIdPrefix='{httpScaleOutOptions.DefaultRuntimeInstanceIdPrefix}', EndpointTemplate='{httpScaleOutOptions.EndpointTemplate}'.");

            output.WriteLine(
                "Redis HTTP scale-out assert: Runtime providers: " +
                string.Join(
                    " | ",
                    providers.Select(provider => provider.GetType().FullName)));

            output.WriteLine(
                "Redis HTTP scale-out assert: IHostedService registrations: " +
                string.Join(
                    " | ",
                    hostedServices.Select(service => service.GetType().FullName)));

            Assert.IsType<RedisAiSharedRunStore>(
                sharedRunStore);

            Assert.IsType<RedisAiSharedQueue>(
                sharedQueue);

            Assert.IsType<RedisAiRuntimeAdmissionReservationStore>(
                reservationStore);

            Assert.IsType<RedisAiRuntimeScaleOutRequestStore>(
                scaleOutRequestStore);

            Assert.IsType<StoreBackedAiRuntimeScaleOutRequestPublisher>(
                scaleOutPublisher);

            Assert.IsType<AiRuntimeScaleOutProviderSelector>(
                scaleOutSelector);

            Assert.IsType<AiHttpRuntimeScaleOutProvisioner>(
                httpProvisioner);

            Assert.Contains(
                providers,
                provider => provider.GetType() == typeof(HttpAiRuntimeInstanceProvider));

            Assert.True(
                admissionOptions.Enabled,
                "Admission must be enabled for this scenario.");

            Assert.True(
                admissionOptions.EnableScaleOutRequest,
                "Scale-out request must be enabled for this scenario.");

            Assert.False(
                admissionOptions.EnableGlobalQueueFallback,
                "Global queue fallback must be disabled for this scenario, otherwise admission returns QueuedGlobally.");

            Assert.False(
                admissionOptions.RejectWhenNoCapacity,
                "RejectWhenNoCapacity must be false so admission can request scale-out instead of rejecting.");

            Assert.Equal(
                3,
                admissionOptions.MaxInstanceCount);

            Assert.True(
                watcherOptions.Enabled,
                "Scale-out watcher options should be enabled for this scenario.");

            Assert.Equal(
                "mcp-scaleout-watcher",
                watcherOptions.WatcherId);

            Assert.True(
                httpScaleOutOptions.Enabled,
                "HTTP scale-out provisioner must be enabled for this scenario.");

            Assert.Contains(
                hostedServices,
                service => service.GetType() == typeof(AiRuntimeScaleOutRequestWatcherHostedService));
        }

        /// <summary>
        /// Verifies that a real MCP control-plane host propagates hybrid tenant runtime
        /// settings into the Redis-backed HTTP scale-out request and HTTP provisioned capacity.
        /// </summary>
        [Fact]
        public async Task ControlPlaneWithHttpRuntimeInstances_With_Hybrid_Tenant_Should_Fulfill_Tenant_Aware_Redis_ScaleOut_Request_Using_Http_Provider()
        {
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "http-hybrid-tenant-scaleout-request");

            var controlPlaneSettings =
                GenericMcpServerTestSettings.CreateHttpScaleOutOnlyControlPlaneSettings(
                    controlPlaneId);

            await using var host =
                new GenericMcpServerTestHost(
                    controlPlaneSettings);

            using var client =
                host.CreateClient();

            AssertRedisStoresPublisherWatcherAndHttpProvider(
                host.Services);

            var tenantRuntimeSettingsProvider =
                host.Services.GetRequiredService<IAiTenantRuntimeSettingsProvider>();

            var tenantRuntimeSettings =
                tenantRuntimeSettingsProvider.GetSettings(
                    HybridTenantId,
                    null);

            output.WriteLine(
                $"HTTP hybrid tenant settings resolved. TenantId='{tenantRuntimeSettings.TenantId}', " +
                $"TenantGroupId='{tenantRuntimeSettings.TenantGroupId ?? "null"}', " +
                $"IsolationMode='{tenantRuntimeSettings.IsolationMode}', " +
                $"RuntimeInstanceIdPrefix='{tenantRuntimeSettings.RuntimeInstanceIdPrefix}', " +
                $"MaxRuntimeInstances='{tenantRuntimeSettings.MaxRuntimeInstances}', " +
                $"AllowSharedFallback='{tenantRuntimeSettings.AllowSharedFallback}'.");

            Assert.Equal(HybridTenantId, tenantRuntimeSettings.TenantId);
            Assert.Equal(AiRuntimeInstanceIsolationMode.Hybrid, tenantRuntimeSettings.IsolationMode);
            Assert.True(tenantRuntimeSettings.PreferDedicatedCapacity);
            Assert.True(tenantRuntimeSettings.AllowSharedFallback);
            Assert.Equal(2, tenantRuntimeSettings.MaxRuntimeInstances);
            Assert.Equal(HybridRuntimeInstanceIdPrefix, tenantRuntimeSettings.RuntimeInstanceIdPrefix);
            Assert.Equal(5, tenantRuntimeSettings.WorkerCountPerInstance);
            Assert.Equal(3, tenantRuntimeSettings.MaxConcurrentRunsPerInstance);
            Assert.Equal(250, tenantRuntimeSettings.LocalQueueCapacity);

            var mcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        host,
                        client,
                        RequestedBy,
                        tenantId: HybridTenantId)
                    .ConfigureAwait(false);

            var sharedRunStore =
                host.Services.GetRequiredService<IAiSharedRunStore>();

            var scaleOutRequestStore =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var redis =
                host.Services.GetRequiredService<IConnectionMultiplexer>();

            var registrationOptions =
                host.Services.GetRequiredService<IOptions<AiRuntimeInstanceRegistrationOptions>>();

            var controlPlaneIdResolver =
                host.Services.GetRequiredService<IAiControlPlaneIdResolver>();

            var visibilityEvaluator =
                host.Services.GetRequiredService<IAiRuntimeInstanceVisibilityEvaluator>();

            var executionContextProvider =
                new MutableExecutionContextSnapshotProvider();

            var pipelineName =
                $"mcp-http-hybrid-tenant-scaleout-{Guid.NewGuid():N}";

            var expectedSharedRunIds =
                await SubmitRunsAsync(
                        mcp,
                        pipelineName,
                        count: 1,
                        stepCount: 3,
                        flakyStepInterval: 0,
                        tenantId: HybridTenantId)
                    .ConfigureAwait(false);

            var sharedRunId =
                Assert.Single(
                    expectedSharedRunIds);

            var sharedRun =
                await sharedRunStore
                    .GetAsync(
                        sharedRunId)
                    .ConfigureAwait(false);

            Assert.NotNull(sharedRun);
            Assert.Equal(AiSharedRunStatus.ScaleOutRequested, sharedRun!.Status);
            Assert.Equal(controlPlaneId, sharedRun.ControlPlaneId);
            Assert.Equal(pipelineName, sharedRun.PipelineKey);
            Assert.Equal(HybridTenantId, sharedRun.ExecutionContextSnapshot.TenantId);
            Assert.Equal(HybridTenantId, sharedRun.RunRequest.ExecutionContextSnapshot?.TenantId);

            var expectedTenantGroupId =
                sharedRun.ExecutionContextSnapshot.TenantGroupId;

            Assert.False(string.IsNullOrWhiteSpace(expectedTenantGroupId));

            Assert.NotNull(sharedRun.AdmissionDecision);
            Assert.Equal(AiRunAdmissionDecisionType.RequestScaleOut, sharedRun.AdmissionDecision.DecisionType);
            Assert.Equal(HybridTenantId, sharedRun.AdmissionDecision.TenantId);
            Assert.Equal(expectedTenantGroupId, sharedRun.AdmissionDecision.TenantGroupId);
            Assert.NotNull(sharedRun.AdmissionDecision.TenantRuntimeSettings);

            Assert.Equal(AiRuntimeInstanceIsolationMode.Hybrid, sharedRun.AdmissionDecision.TenantRuntimeSettings!.IsolationMode);
            Assert.True(sharedRun.AdmissionDecision.TenantRuntimeSettings.PreferDedicatedCapacity);
            Assert.True(sharedRun.AdmissionDecision.TenantRuntimeSettings.AllowSharedFallback);
            Assert.Equal(2, sharedRun.AdmissionDecision.TenantRuntimeSettings.MaxRuntimeInstances);
            Assert.Equal(HybridRuntimeInstanceIdPrefix, sharedRun.AdmissionDecision.TenantRuntimeSettings.RuntimeInstanceIdPrefix);
            Assert.Equal(5, sharedRun.AdmissionDecision.TenantRuntimeSettings.WorkerCountPerInstance);
            Assert.Equal(3, sharedRun.AdmissionDecision.TenantRuntimeSettings.MaxConcurrentRunsPerInstance);
            Assert.Equal(250, sharedRun.AdmissionDecision.TenantRuntimeSettings.LocalQueueCapacity);

            output.WriteLine(
                $"HTTP hybrid shared run submitted. SharedRunId='{sharedRunId}', " +
                $"TenantId='{sharedRun.ExecutionContextSnapshot.TenantId}', " +
                $"TenantGroupId='{expectedTenantGroupId}', " +
                $"Status='{sharedRun.Status}', PipelineKey='{pipelineName}'.");

            var expectedScaleOutRequestId =
                $"scale-out-{sharedRunId}";

            var scaleOutRequest =
                await WaitForScaleOutRequestStatusAsync(
                        scaleOutRequestStore,
                        expectedScaleOutRequestId,
                        AiRuntimeScaleOutRequestStatus.Fulfilled,
                        TimeSpan.FromSeconds(15))
                    .ConfigureAwait(false);

            Assert.Equal(expectedScaleOutRequestId, scaleOutRequest.RequestId);
            Assert.Equal(sharedRunId, scaleOutRequest.SharedRunId);
            Assert.Equal(controlPlaneId, scaleOutRequest.ControlPlaneId);
            Assert.Equal(HybridTenantId, scaleOutRequest.TenantId);
            Assert.Equal(expectedTenantGroupId, scaleOutRequest.TenantGroupId);
            Assert.Equal(pipelineName, scaleOutRequest.PipelineKey);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Fulfilled, scaleOutRequest.Status);
            Assert.Equal("http", scaleOutRequest.ProviderHint);
            Assert.Equal("http", scaleOutRequest.Metadata["providerHint"]);
            Assert.Equal(AiRuntimeInstanceIsolationMode.Hybrid, scaleOutRequest.IsolationMode);
            Assert.True(scaleOutRequest.PreferDedicatedCapacity);
            Assert.True(scaleOutRequest.AllowSharedFallback);
            Assert.Equal(2, scaleOutRequest.MaxRuntimeInstances);
            Assert.Equal(HybridRuntimeInstanceIdPrefix, scaleOutRequest.RuntimeInstanceIdPrefix);
            Assert.Equal(5, scaleOutRequest.WorkerCountPerInstance);
            Assert.Equal(3, scaleOutRequest.MaxConcurrentRunsPerInstance);
            Assert.Equal(250, scaleOutRequest.LocalQueueCapacity);
            Assert.Equal(0, scaleOutRequest.AvailableInstanceCount);
            Assert.Equal(0, scaleOutRequest.CurrentInstanceCount);
            Assert.Equal(2, scaleOutRequest.MaxInstanceCount);
            Assert.Equal(1, scaleOutRequest.RequestedTargetInstanceCount);
            Assert.Equal("mcp-scaleout-watcher", scaleOutRequest.ObservedBy);
            Assert.Equal("mcp-scaleout-watcher", scaleOutRequest.FulfilledBy);

            Assert.False(
                string.IsNullOrWhiteSpace(
                    scaleOutRequest.FulfilledRuntimeInstanceId));

            Assert.Contains(
                $":{HybridRuntimeInstanceIdPrefix}-1",
                scaleOutRequest.FulfilledRuntimeInstanceId,
                StringComparison.Ordinal);

            Assert.DoesNotContain(
                $":{TenantAwareRuntimeInstanceIdPrefix}-1",
                scaleOutRequest.FulfilledRuntimeInstanceId,
                StringComparison.Ordinal);

            Assert.DoesNotContain(
                $":{SharedRuntimeInstanceIdPrefix}-1",
                scaleOutRequest.FulfilledRuntimeInstanceId,
                StringComparison.Ordinal);

            Assert.Equal("Hybrid", scaleOutRequest.Metadata["runtime.isolationMode"]);
            Assert.Equal("True", scaleOutRequest.Metadata["runtime.preferDedicatedCapacity"]);
            Assert.Equal("True", scaleOutRequest.Metadata["runtime.allowSharedFallback"]);
            Assert.Equal("2", scaleOutRequest.Metadata["runtime.maxRuntimeInstances"]);
            Assert.Equal(HybridRuntimeInstanceIdPrefix, scaleOutRequest.Metadata["runtime.instanceIdPrefix"]);
            Assert.Equal("5", scaleOutRequest.Metadata["runtime.workerCountPerInstance"]);
            Assert.Equal("3", scaleOutRequest.Metadata["runtime.maxConcurrentRunsPerInstance"]);
            Assert.Equal("250", scaleOutRequest.Metadata["runtime.localQueueCapacity"]);

            var fulfilledRuntimeInstanceId =
                scaleOutRequest.FulfilledRuntimeInstanceId!;

            executionContextProvider.Current =
                CreateRuntimeVisibilityExecutionContextSnapshot(
                    HybridTenantId,
                    expectedTenantGroupId,
                    "http-hybrid-tenant-scaleout-request");

            var runtimeInstanceRegistry =
                new RedisAiRuntimeInstanceRegistry(
                    redis,
                    registrationOptions,
                    controlPlaneIdResolver,
                    visibilityEvaluator,
                    executionContextProvider);

            var runtimeInstanceCapacityStore =
                new RedisAiRuntimeInstanceCapacityStore(
                    redis,
                    registrationOptions,
                    controlPlaneIdResolver,
                    visibilityEvaluator,
                    executionContextProvider);

            var registered =
                await runtimeInstanceRegistry
                    .GetAsync(
                        fulfilledRuntimeInstanceId)
                    .ConfigureAwait(false);

            var capacity =
                await runtimeInstanceCapacityStore
                    .GetAsync(
                        fulfilledRuntimeInstanceId)
                    .ConfigureAwait(false);

            Assert.NotNull(registered);
            Assert.NotNull(capacity);
            Assert.Equal(fulfilledRuntimeInstanceId, registered!.RuntimeInstanceId);
            Assert.Equal(fulfilledRuntimeInstanceId, capacity!.RuntimeInstanceId);
            Assert.Equal("http", registered.Metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName]);
            Assert.Equal("http", capacity.Metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName]);
            Assert.Equal("http", registered.Metadata["provider.name"]);
            Assert.Equal("http", capacity.Metadata["provider.name"]);

            Assert.Equal(
                AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                registered.Metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName]);

            Assert.Equal(
                AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                capacity.Metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName]);

            Assert.Equal(HybridTenantId, registered.Metadata["tenant.id"]);
            Assert.Equal(HybridTenantId, capacity.Metadata["tenant.id"]);
            Assert.Equal(expectedTenantGroupId, registered.Metadata["tenant.group.id"]);
            Assert.Equal(expectedTenantGroupId, capacity.Metadata["tenant.group.id"]);
            Assert.Equal("Hybrid", registered.Metadata["runtime.isolationMode"]);
            Assert.Equal("Hybrid", capacity.Metadata["runtime.isolationMode"]);
            Assert.Equal("True", registered.Metadata["runtime.preferDedicatedCapacity"]);
            Assert.Equal("True", capacity.Metadata["runtime.preferDedicatedCapacity"]);
            Assert.Equal("True", registered.Metadata["runtime.allowSharedFallback"]);
            Assert.Equal("True", capacity.Metadata["runtime.allowSharedFallback"]);
            Assert.Equal("2", registered.Metadata["runtime.maxRuntimeInstances"]);
            Assert.Equal("2", capacity.Metadata["runtime.maxRuntimeInstances"]);
            Assert.Equal(HybridRuntimeInstanceIdPrefix, registered.Metadata["runtime.instanceIdPrefix"]);
            Assert.Equal(HybridRuntimeInstanceIdPrefix, capacity.Metadata["runtime.instanceIdPrefix"]);
            Assert.Equal("5", registered.Metadata["runtime.workerCountPerInstance"]);
            Assert.Equal("5", capacity.Metadata["runtime.workerCountPerInstance"]);
            Assert.Equal("3", registered.Metadata["runtime.maxConcurrentRunsPerInstance"]);
            Assert.Equal("3", capacity.Metadata["runtime.maxConcurrentRunsPerInstance"]);
            Assert.Equal("250", registered.Metadata["runtime.localQueueCapacity"]);
            Assert.Equal("250", capacity.Metadata["runtime.localQueueCapacity"]);

            output.WriteLine(
                $"Redis HTTP hybrid tenant scale-out request fulfilled. ControlPlaneId='{controlPlaneId}', " +
                $"SharedRunId='{sharedRunId}', RequestId='{scaleOutRequest.RequestId}', " +
                $"TenantId='{scaleOutRequest.TenantId}', TenantGroupId='{scaleOutRequest.TenantGroupId}', " +
                $"IsolationMode='{scaleOutRequest.IsolationMode}', RuntimeInstanceId='{fulfilledRuntimeInstanceId}', " +
                $"PipelineKey='{pipelineName}'.");
        }

        /// <summary>
        /// Verifies that a dedicated tenant does not fall back to an existing shared HTTP runtime capacity.
        /// </summary>
        [Fact]
        public async Task ControlPlaneWithHttpRuntimeInstances_With_Dedicated_Tenant_Should_Not_Fallback_To_Shared_Http_Runtime_When_Available()
        {
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "http-dedicated-no-shared-fallback");

            var controlPlaneSettings =
                GenericMcpServerTestSettings.CreateHttpScaleOutOnlyControlPlaneSettings(
                    controlPlaneId);

            await using var host =
                new GenericMcpServerTestHost(
                    controlPlaneSettings);

            using var client =
                host.CreateClient();

            AssertRedisStoresPublisherWatcherAndHttpProvider(
                host.Services);

            var tenantRuntimeSettingsProvider =
                host.Services.GetRequiredService<IAiTenantRuntimeSettingsProvider>();

            var sharedTenantSettings =
                tenantRuntimeSettingsProvider.GetSettings(
                    TenantId,
                    null);

            var dedicatedTenantSettings =
                tenantRuntimeSettingsProvider.GetSettings(
                    TenantAwareTenantId,
                    null);

            Assert.Equal(AiRuntimeInstanceIsolationMode.Shared, sharedTenantSettings.IsolationMode);
            Assert.False(sharedTenantSettings.PreferDedicatedCapacity);
            Assert.True(sharedTenantSettings.AllowSharedFallback);
            Assert.Equal(SharedRuntimeInstanceIdPrefix, sharedTenantSettings.RuntimeInstanceIdPrefix);

            Assert.Equal(AiRuntimeInstanceIsolationMode.Dedicated, dedicatedTenantSettings.IsolationMode);
            Assert.True(dedicatedTenantSettings.PreferDedicatedCapacity);
            Assert.False(dedicatedTenantSettings.AllowSharedFallback);
            Assert.Equal(TenantAwareRuntimeInstanceIdPrefix, dedicatedTenantSettings.RuntimeInstanceIdPrefix);

            var httpProvisioner =
                host.Services.GetRequiredService<IAiHttpRuntimeScaleOutProvisioner>();

            var sharedProvisionResult =
                await httpProvisioner
                    .ProvisionAsync(
                        new AiRuntimeScaleOutProviderRequest
                        {
                            RequestId = $"shared-http-bootstrap-{Guid.NewGuid():N}",
                            SharedRunId = $"shared-http-bootstrap-run-{Guid.NewGuid():N}",
                            ControlPlaneId = controlPlaneId,
                            ExecutionContextSnapshot = CreateRuntimeVisibilityExecutionContextSnapshot(
                                TenantId,
                                sharedTenantSettings.TenantGroupId,
                                "shared-http-bootstrap"),
                            TenantId = TenantId,
                            TenantGroupId = sharedTenantSettings.TenantGroupId,
                            PipelineKey = $"shared-http-bootstrap-pipeline-{Guid.NewGuid():N}",
                            IsolationMode = sharedTenantSettings.IsolationMode,
                            PreferDedicatedCapacity = sharedTenantSettings.PreferDedicatedCapacity,
                            AllowSharedFallback = sharedTenantSettings.AllowSharedFallback,
                            MaxRuntimeInstances = sharedTenantSettings.MaxRuntimeInstances,
                            RuntimeInstanceIdPrefix = sharedTenantSettings.RuntimeInstanceIdPrefix,
                            WorkerCountPerInstance = sharedTenantSettings.WorkerCountPerInstance,
                            MaxConcurrentRunsPerInstance = sharedTenantSettings.MaxConcurrentRunsPerInstance,
                            LocalQueueCapacity = sharedTenantSettings.LocalQueueCapacity,
                            VisibleInstanceCount = 0,
                            AvailableInstanceCount = 0,
                            CurrentInstanceCount = 0,
                            MaxInstanceCount = sharedTenantSettings.MaxRuntimeInstances,
                            RequestedTargetInstanceCount = 1,
                            ProviderHint = "http",
                            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["test"] = "shared-http-bootstrap"
                            }
                        })
                    .ConfigureAwait(false);

            Assert.True(sharedProvisionResult.Success, sharedProvisionResult.Message);
            Assert.False(string.IsNullOrWhiteSpace(sharedProvisionResult.RuntimeInstanceId));
            Assert.Contains($":{SharedRuntimeInstanceIdPrefix}-1", sharedProvisionResult.RuntimeInstanceId, StringComparison.Ordinal);

            var sharedRuntimeInstanceId =
                sharedProvisionResult.RuntimeInstanceId!;

            var dedicatedMcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        host,
                        client,
                        RequestedBy,
                        tenantId: TenantAwareTenantId)
                    .ConfigureAwait(false);

            var sharedRunStore =
                host.Services.GetRequiredService<IAiSharedRunStore>();

            var scaleOutRequestStore =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var redis =
                host.Services.GetRequiredService<IConnectionMultiplexer>();

            var registrationOptions =
                host.Services.GetRequiredService<IOptions<AiRuntimeInstanceRegistrationOptions>>();

            var controlPlaneIdResolver =
                host.Services.GetRequiredService<IAiControlPlaneIdResolver>();

            var visibilityEvaluator =
                host.Services.GetRequiredService<IAiRuntimeInstanceVisibilityEvaluator>();

            var executionContextProvider =
                new MutableExecutionContextSnapshotProvider();

            var dedicatedPipelineName =
                $"mcp-http-dedicated-no-shared-fallback-{Guid.NewGuid():N}";

            var dedicatedRunIds =
                await SubmitRunsAsync(
                        dedicatedMcp,
                        dedicatedPipelineName,
                        count: 1,
                        stepCount: 3,
                        flakyStepInterval: 0,
                        tenantId: TenantAwareTenantId)
                    .ConfigureAwait(false);

            var dedicatedSharedRunId =
                Assert.Single(
                    dedicatedRunIds);

            var dedicatedRunAfterSubmit =
                await sharedRunStore
                    .GetAsync(
                        dedicatedSharedRunId)
                    .ConfigureAwait(false);

            Assert.NotNull(dedicatedRunAfterSubmit);
            Assert.Equal(controlPlaneId, dedicatedRunAfterSubmit!.ControlPlaneId);
            Assert.Equal(dedicatedPipelineName, dedicatedRunAfterSubmit.PipelineKey);
            Assert.Equal(TenantAwareTenantId, dedicatedRunAfterSubmit.ExecutionContextSnapshot.TenantId);
            Assert.Equal(TenantAwareTenantId, dedicatedRunAfterSubmit.RunRequest.ExecutionContextSnapshot?.TenantId);
            Assert.Equal(AiSharedRunStatus.ScaleOutRequested, dedicatedRunAfterSubmit.Status);

            var expectedTenantGroupId =
                dedicatedRunAfterSubmit.ExecutionContextSnapshot.TenantGroupId;

            Assert.False(string.IsNullOrWhiteSpace(expectedTenantGroupId));

            Assert.NotNull(dedicatedRunAfterSubmit.AdmissionDecision);
            Assert.Equal(AiRunAdmissionDecisionType.RequestScaleOut, dedicatedRunAfterSubmit.AdmissionDecision.DecisionType);
            Assert.Equal(TenantAwareTenantId, dedicatedRunAfterSubmit.AdmissionDecision.TenantId);
            Assert.Equal(expectedTenantGroupId, dedicatedRunAfterSubmit.AdmissionDecision.TenantGroupId);
            Assert.Null(dedicatedRunAfterSubmit.AdmissionDecision.AssignedRuntimeInstanceId);
            Assert.True(dedicatedRunAfterSubmit.AdmissionDecision.ShouldRequestScaleOut);
            Assert.Equal(AiRuntimeInstanceIsolationMode.Dedicated, dedicatedRunAfterSubmit.AdmissionDecision.TenantRuntimeSettings?.IsolationMode);
            Assert.True(dedicatedRunAfterSubmit.AdmissionDecision.TenantRuntimeSettings?.PreferDedicatedCapacity);
            Assert.False(dedicatedRunAfterSubmit.AdmissionDecision.TenantRuntimeSettings?.AllowSharedFallback);
            Assert.Equal(3, dedicatedRunAfterSubmit.AdmissionDecision.TenantRuntimeSettings?.MaxRuntimeInstances);
            Assert.Equal(TenantAwareRuntimeInstanceIdPrefix, dedicatedRunAfterSubmit.AdmissionDecision.TenantRuntimeSettings?.RuntimeInstanceIdPrefix);

            var dedicatedScaleOutRequestId =
                $"scale-out-{dedicatedSharedRunId}";

            var dedicatedScaleOutRequest =
                await WaitForScaleOutRequestStatusAsync(
                        scaleOutRequestStore,
                        dedicatedScaleOutRequestId,
                        AiRuntimeScaleOutRequestStatus.Fulfilled,
                        TimeSpan.FromSeconds(15))
                    .ConfigureAwait(false);

            Assert.Equal(dedicatedScaleOutRequestId, dedicatedScaleOutRequest.RequestId);
            Assert.Equal(dedicatedSharedRunId, dedicatedScaleOutRequest.SharedRunId);
            Assert.Equal(controlPlaneId, dedicatedScaleOutRequest.ControlPlaneId);
            Assert.Equal(TenantAwareTenantId, dedicatedScaleOutRequest.TenantId);
            Assert.Equal(expectedTenantGroupId, dedicatedScaleOutRequest.TenantGroupId);
            Assert.Equal(dedicatedPipelineName, dedicatedScaleOutRequest.PipelineKey);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Fulfilled, dedicatedScaleOutRequest.Status);
            Assert.Equal("http", dedicatedScaleOutRequest.ProviderHint);
            Assert.Equal("http", dedicatedScaleOutRequest.Metadata["providerHint"]);
            Assert.Equal(AiRuntimeInstanceIsolationMode.Dedicated, dedicatedScaleOutRequest.IsolationMode);
            Assert.True(dedicatedScaleOutRequest.PreferDedicatedCapacity);
            Assert.False(dedicatedScaleOutRequest.AllowSharedFallback);
            Assert.Equal(3, dedicatedScaleOutRequest.MaxRuntimeInstances);
            Assert.Equal(TenantAwareRuntimeInstanceIdPrefix, dedicatedScaleOutRequest.RuntimeInstanceIdPrefix);
            Assert.Equal(10, dedicatedScaleOutRequest.WorkerCountPerInstance);
            Assert.Equal(5, dedicatedScaleOutRequest.MaxConcurrentRunsPerInstance);
            Assert.Equal(500, dedicatedScaleOutRequest.LocalQueueCapacity);
            Assert.Equal(0, dedicatedScaleOutRequest.AvailableInstanceCount);
            Assert.Equal(0, dedicatedScaleOutRequest.CurrentInstanceCount);
            Assert.Equal(3, dedicatedScaleOutRequest.MaxInstanceCount);
            Assert.Equal(1, dedicatedScaleOutRequest.RequestedTargetInstanceCount);
            Assert.Equal("mcp-scaleout-watcher", dedicatedScaleOutRequest.ObservedBy);
            Assert.Equal("mcp-scaleout-watcher", dedicatedScaleOutRequest.FulfilledBy);

            Assert.False(
                string.IsNullOrWhiteSpace(
                    dedicatedScaleOutRequest.FulfilledRuntimeInstanceId));

            Assert.Contains(
                $":{TenantAwareRuntimeInstanceIdPrefix}-1",
                dedicatedScaleOutRequest.FulfilledRuntimeInstanceId,
                StringComparison.Ordinal);

            Assert.DoesNotContain(
                $":{SharedRuntimeInstanceIdPrefix}-1",
                dedicatedScaleOutRequest.FulfilledRuntimeInstanceId,
                StringComparison.Ordinal);

            Assert.NotEqual(
                sharedRuntimeInstanceId,
                dedicatedScaleOutRequest.FulfilledRuntimeInstanceId);

            Assert.Equal("Dedicated", dedicatedScaleOutRequest.Metadata["runtime.isolationMode"]);
            Assert.Equal("True", dedicatedScaleOutRequest.Metadata["runtime.preferDedicatedCapacity"]);
            Assert.Equal("False", dedicatedScaleOutRequest.Metadata["runtime.allowSharedFallback"]);
            Assert.Equal("3", dedicatedScaleOutRequest.Metadata["runtime.maxRuntimeInstances"]);
            Assert.Equal(TenantAwareRuntimeInstanceIdPrefix, dedicatedScaleOutRequest.Metadata["runtime.instanceIdPrefix"]);
            Assert.Equal("10", dedicatedScaleOutRequest.Metadata["runtime.workerCountPerInstance"]);
            Assert.Equal("5", dedicatedScaleOutRequest.Metadata["runtime.maxConcurrentRunsPerInstance"]);
            Assert.Equal("500", dedicatedScaleOutRequest.Metadata["runtime.localQueueCapacity"]);

            var dedicatedRuntimeInstanceId =
                dedicatedScaleOutRequest.FulfilledRuntimeInstanceId!;

            executionContextProvider.Current =
                CreateRuntimeVisibilityExecutionContextSnapshot(
                    TenantAwareTenantId,
                    expectedTenantGroupId,
                    "http-dedicated-no-shared-fallback");

            var runtimeInstanceRegistry =
                new RedisAiRuntimeInstanceRegistry(
                    redis,
                    registrationOptions,
                    controlPlaneIdResolver,
                    visibilityEvaluator,
                    executionContextProvider);

            var runtimeInstanceCapacityStore =
                new RedisAiRuntimeInstanceCapacityStore(
                    redis,
                    registrationOptions,
                    controlPlaneIdResolver,
                    visibilityEvaluator,
                    executionContextProvider);

            var visibleRegistryInstances =
                await runtimeInstanceRegistry
                    .ListAsync(
                        includeStopped: false)
                    .ConfigureAwait(false);

            var visibleCapacityDescriptors =
                await runtimeInstanceCapacityStore
                    .ListAsync()
                    .ConfigureAwait(false);

            var visibleRegistryIds =
                visibleRegistryInstances
                    .Select(instance => instance.RuntimeInstanceId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var visibleCapacityIds =
                visibleCapacityDescriptors
                    .Select(capacity => capacity.RuntimeInstanceId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Assert.Contains(dedicatedRuntimeInstanceId, visibleRegistryIds);
            Assert.Contains(dedicatedRuntimeInstanceId, visibleCapacityIds);
            Assert.DoesNotContain(sharedRuntimeInstanceId, visibleRegistryIds);
            Assert.DoesNotContain(sharedRuntimeInstanceId, visibleCapacityIds);

            var registered =
                await runtimeInstanceRegistry
                    .GetAsync(
                        dedicatedRuntimeInstanceId)
                    .ConfigureAwait(false);

            var capacity =
                await runtimeInstanceCapacityStore
                    .GetAsync(
                        dedicatedRuntimeInstanceId)
                    .ConfigureAwait(false);

            Assert.NotNull(registered);
            Assert.NotNull(capacity);
            Assert.Equal(dedicatedRuntimeInstanceId, registered!.RuntimeInstanceId);
            Assert.Equal(dedicatedRuntimeInstanceId, capacity!.RuntimeInstanceId);
            Assert.Equal("http", registered.Metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName]);
            Assert.Equal("http", capacity.Metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName]);
            Assert.Equal("http", registered.Metadata["provider.name"]);
            Assert.Equal("http", capacity.Metadata["provider.name"]);

            Assert.Equal(
                AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                registered.Metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName]);

            Assert.Equal(
                AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                capacity.Metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName]);

            Assert.Equal(TenantAwareTenantId, registered.Metadata["tenant.id"]);
            Assert.Equal(TenantAwareTenantId, capacity.Metadata["tenant.id"]);
            Assert.Equal(expectedTenantGroupId, registered.Metadata["tenant.group.id"]);
            Assert.Equal(expectedTenantGroupId, capacity.Metadata["tenant.group.id"]);
            Assert.Equal("Dedicated", registered.Metadata["runtime.isolationMode"]);
            Assert.Equal("Dedicated", capacity.Metadata["runtime.isolationMode"]);
            Assert.Equal("True", registered.Metadata["runtime.preferDedicatedCapacity"]);
            Assert.Equal("True", capacity.Metadata["runtime.preferDedicatedCapacity"]);
            Assert.Equal("False", registered.Metadata["runtime.allowSharedFallback"]);
            Assert.Equal("False", capacity.Metadata["runtime.allowSharedFallback"]);
            Assert.Equal("3", registered.Metadata["runtime.maxRuntimeInstances"]);
            Assert.Equal("3", capacity.Metadata["runtime.maxRuntimeInstances"]);
            Assert.Equal(TenantAwareRuntimeInstanceIdPrefix, registered.Metadata["runtime.instanceIdPrefix"]);
            Assert.Equal(TenantAwareRuntimeInstanceIdPrefix, capacity.Metadata["runtime.instanceIdPrefix"]);
            Assert.Equal("10", registered.Metadata["runtime.workerCountPerInstance"]);
            Assert.Equal("10", capacity.Metadata["runtime.workerCountPerInstance"]);
            Assert.Equal("5", registered.Metadata["runtime.maxConcurrentRunsPerInstance"]);
            Assert.Equal("5", capacity.Metadata["runtime.maxConcurrentRunsPerInstance"]);
            Assert.Equal("500", registered.Metadata["runtime.localQueueCapacity"]);
            Assert.Equal("500", capacity.Metadata["runtime.localQueueCapacity"]);

            output.WriteLine(
                $"FINAL HTTP TENANT-A DEDICATED NO SHARED FALLBACK STATUS: " +
                $"SharedRunId='{dedicatedSharedRunId}', TenantId='{dedicatedScaleOutRequest.TenantId}', " +
                $"SharedHttpRuntimeInstanceId='{sharedRuntimeInstanceId}', " +
                $"DedicatedHttpRuntimeInstanceId='{dedicatedRuntimeInstanceId}', " +
                $"ScaleOutRequestStatus='{dedicatedScaleOutRequest.Status}', " +
                $"VisibleRegistryIds='{string.Join(" | ", visibleRegistryIds)}', " +
                $"VisibleCapacityIds='{string.Join(" | ", visibleCapacityIds)}'.");
        }

        /// <summary>
        /// Verifies that a hybrid tenant can fall back to an existing shared HTTP runtime capacity.
        /// </summary>
        [Fact]
        public async Task ControlPlaneWithHttpRuntimeInstances_With_Hybrid_Tenant_Should_Fallback_To_Shared_Http_Runtime_When_Available()
        {
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "http-hybrid-shared-fallback");

            var controlPlaneSettings =
                GenericMcpServerTestSettings.CreateHttpScaleOutOnlyControlPlaneSettings(
                    controlPlaneId);

            await using var host =
                new GenericMcpServerTestHost(
                    controlPlaneSettings);

            using var client =
                host.CreateClient();

            AssertRedisStoresPublisherWatcherAndHttpProvider(
                host.Services);

            var tenantRuntimeSettingsProvider =
                host.Services.GetRequiredService<IAiTenantRuntimeSettingsProvider>();

            var sharedTenantSettings =
                tenantRuntimeSettingsProvider.GetSettings(
                    TenantId,
                    null);

            var hybridTenantSettings =
                tenantRuntimeSettingsProvider.GetSettings(
                    HybridTenantId,
                    null);

            Assert.Equal(AiRuntimeInstanceIsolationMode.Shared, sharedTenantSettings.IsolationMode);
            Assert.False(sharedTenantSettings.PreferDedicatedCapacity);
            Assert.True(sharedTenantSettings.AllowSharedFallback);
            Assert.Equal(SharedRuntimeInstanceIdPrefix, sharedTenantSettings.RuntimeInstanceIdPrefix);

            Assert.Equal(AiRuntimeInstanceIsolationMode.Hybrid, hybridTenantSettings.IsolationMode);
            Assert.True(hybridTenantSettings.PreferDedicatedCapacity);
            Assert.True(hybridTenantSettings.AllowSharedFallback);
            Assert.Equal(2, hybridTenantSettings.MaxRuntimeInstances);
            Assert.Equal(HybridRuntimeInstanceIdPrefix, hybridTenantSettings.RuntimeInstanceIdPrefix);
            Assert.Equal(5, hybridTenantSettings.WorkerCountPerInstance);
            Assert.Equal(3, hybridTenantSettings.MaxConcurrentRunsPerInstance);
            Assert.Equal(250, hybridTenantSettings.LocalQueueCapacity);

            var httpProvisioner =
                host.Services.GetRequiredService<IAiHttpRuntimeScaleOutProvisioner>();

            var sharedProvisionResult =
                await httpProvisioner
                    .ProvisionAsync(
                        new AiRuntimeScaleOutProviderRequest
                        {
                            RequestId = $"shared-http-bootstrap-{Guid.NewGuid():N}",
                            SharedRunId = $"shared-http-bootstrap-run-{Guid.NewGuid():N}",
                            ControlPlaneId = controlPlaneId,
                            ExecutionContextSnapshot = CreateRuntimeVisibilityExecutionContextSnapshot(
                                TenantId,
                                sharedTenantSettings.TenantGroupId,
                                "shared-http-bootstrap-for-hybrid-fallback"),
                            TenantId = TenantId,
                            TenantGroupId = sharedTenantSettings.TenantGroupId,
                            PipelineKey = $"shared-http-bootstrap-pipeline-{Guid.NewGuid():N}",
                            IsolationMode = sharedTenantSettings.IsolationMode,
                            PreferDedicatedCapacity = sharedTenantSettings.PreferDedicatedCapacity,
                            AllowSharedFallback = sharedTenantSettings.AllowSharedFallback,
                            MaxRuntimeInstances = sharedTenantSettings.MaxRuntimeInstances,
                            RuntimeInstanceIdPrefix = sharedTenantSettings.RuntimeInstanceIdPrefix,
                            WorkerCountPerInstance = sharedTenantSettings.WorkerCountPerInstance,
                            MaxConcurrentRunsPerInstance = sharedTenantSettings.MaxConcurrentRunsPerInstance,
                            LocalQueueCapacity = sharedTenantSettings.LocalQueueCapacity,
                            VisibleInstanceCount = 0,
                            AvailableInstanceCount = 0,
                            CurrentInstanceCount = 0,
                            MaxInstanceCount = sharedTenantSettings.MaxRuntimeInstances,
                            RequestedTargetInstanceCount = 1,
                            ProviderHint = "http",
                            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["test"] = "shared-http-bootstrap-for-hybrid-fallback"
                            }
                        })
                    .ConfigureAwait(false);

            Assert.True(sharedProvisionResult.Success, sharedProvisionResult.Message);
            Assert.False(string.IsNullOrWhiteSpace(sharedProvisionResult.RuntimeInstanceId));
            Assert.Contains($":{SharedRuntimeInstanceIdPrefix}-1", sharedProvisionResult.RuntimeInstanceId, StringComparison.Ordinal);

            var sharedRuntimeInstanceId =
                sharedProvisionResult.RuntimeInstanceId!;

            var hybridMcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        host,
                        client,
                        RequestedBy,
                        tenantId: HybridTenantId)
                    .ConfigureAwait(false);

            var sharedRunStore =
                host.Services.GetRequiredService<IAiSharedRunStore>();

            var scaleOutRequestStore =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var redis =
                host.Services.GetRequiredService<IConnectionMultiplexer>();

            var registrationOptions =
                host.Services.GetRequiredService<IOptions<AiRuntimeInstanceRegistrationOptions>>();

            var controlPlaneIdResolver =
                host.Services.GetRequiredService<IAiControlPlaneIdResolver>();

            var visibilityEvaluator =
                host.Services.GetRequiredService<IAiRuntimeInstanceVisibilityEvaluator>();

            var executionContextProvider =
                new MutableExecutionContextSnapshotProvider();

            var hybridPipelineName =
                $"mcp-http-hybrid-shared-fallback-{Guid.NewGuid():N}";

            var hybridRunIds =
                await SubmitRunsAsync(
                        hybridMcp,
                        hybridPipelineName,
                        count: 1,
                        stepCount: 3,
                        flakyStepInterval: 0,
                        tenantId: HybridTenantId)
                    .ConfigureAwait(false);

            var hybridSharedRunId =
                Assert.Single(
                    hybridRunIds);

            var hybridRunAfterSubmit =
                await sharedRunStore
                    .GetAsync(
                        hybridSharedRunId)
                    .ConfigureAwait(false);

            Assert.NotNull(hybridRunAfterSubmit);
            Assert.Equal(controlPlaneId, hybridRunAfterSubmit!.ControlPlaneId);
            Assert.Equal(hybridPipelineName, hybridRunAfterSubmit.PipelineKey);
            Assert.Equal(HybridTenantId, hybridRunAfterSubmit.ExecutionContextSnapshot.TenantId);
            Assert.Equal(HybridTenantId, hybridRunAfterSubmit.RunRequest.ExecutionContextSnapshot?.TenantId);

            var expectedTenantGroupId =
                hybridRunAfterSubmit.ExecutionContextSnapshot.TenantGroupId;

            Assert.False(string.IsNullOrWhiteSpace(expectedTenantGroupId));

            Assert.NotNull(hybridRunAfterSubmit.AdmissionDecision);
            Assert.Equal(AiRunAdmissionDecisionType.AssignToInstance, hybridRunAfterSubmit.AdmissionDecision.DecisionType);
            Assert.Equal(HybridTenantId, hybridRunAfterSubmit.AdmissionDecision.TenantId);
            Assert.Equal(expectedTenantGroupId, hybridRunAfterSubmit.AdmissionDecision.TenantGroupId);
            Assert.Equal(sharedRuntimeInstanceId, hybridRunAfterSubmit.AdmissionDecision.AssignedRuntimeInstanceId);
            Assert.False(hybridRunAfterSubmit.AdmissionDecision.ShouldRequestScaleOut);
            Assert.Equal(AiRuntimeInstanceIsolationMode.Hybrid, hybridRunAfterSubmit.AdmissionDecision.TenantRuntimeSettings?.IsolationMode);
            Assert.True(hybridRunAfterSubmit.AdmissionDecision.TenantRuntimeSettings?.PreferDedicatedCapacity);
            Assert.True(hybridRunAfterSubmit.AdmissionDecision.TenantRuntimeSettings?.AllowSharedFallback);
            Assert.Equal(2, hybridRunAfterSubmit.AdmissionDecision.TenantRuntimeSettings?.MaxRuntimeInstances);
            Assert.Equal(HybridRuntimeInstanceIdPrefix, hybridRunAfterSubmit.AdmissionDecision.TenantRuntimeSettings?.RuntimeInstanceIdPrefix);
            Assert.Equal(5, hybridRunAfterSubmit.AdmissionDecision.TenantRuntimeSettings?.WorkerCountPerInstance);
            Assert.Equal(3, hybridRunAfterSubmit.AdmissionDecision.TenantRuntimeSettings?.MaxConcurrentRunsPerInstance);
            Assert.Equal(250, hybridRunAfterSubmit.AdmissionDecision.TenantRuntimeSettings?.LocalQueueCapacity);

            Assert.Equal(
                sharedRuntimeInstanceId,
                hybridRunAfterSubmit.AssignedRuntimeInstanceId);

            Assert.DoesNotContain(
                $":{HybridRuntimeInstanceIdPrefix}-1",
                hybridRunAfterSubmit.AssignedRuntimeInstanceId,
                StringComparison.Ordinal);

            Assert.Contains(
                $":{SharedRuntimeInstanceIdPrefix}-1",
                hybridRunAfterSubmit.AssignedRuntimeInstanceId,
                StringComparison.Ordinal);

            var hybridScaleOutRequestId =
                $"scale-out-{hybridSharedRunId}";

            await Task
                .Delay(
                    TimeSpan.FromMilliseconds(750))
                .ConfigureAwait(false);

            var unexpectedHybridScaleOutRequest =
                await scaleOutRequestStore
                    .GetAsync(
                        hybridScaleOutRequestId)
                    .ConfigureAwait(false);

            Assert.Null(unexpectedHybridScaleOutRequest);

            executionContextProvider.Current =
                CreateRuntimeVisibilityExecutionContextSnapshot(
                    HybridTenantId,
                    expectedTenantGroupId,
                    "http-hybrid-shared-fallback");

            var runtimeInstanceRegistry =
                new RedisAiRuntimeInstanceRegistry(
                    redis,
                    registrationOptions,
                    controlPlaneIdResolver,
                    visibilityEvaluator,
                    executionContextProvider);

            var runtimeInstanceCapacityStore =
                new RedisAiRuntimeInstanceCapacityStore(
                    redis,
                    registrationOptions,
                    controlPlaneIdResolver,
                    visibilityEvaluator,
                    executionContextProvider);

            var visibleRegistryInstances =
                await runtimeInstanceRegistry
                    .ListAsync(
                        includeStopped: false)
                    .ConfigureAwait(false);

            var visibleCapacityDescriptors =
                await runtimeInstanceCapacityStore
                    .ListAsync()
                    .ConfigureAwait(false);

            var visibleRegistryIds =
                visibleRegistryInstances
                    .Select(instance => instance.RuntimeInstanceId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var visibleCapacityIds =
                visibleCapacityDescriptors
                    .Select(capacity => capacity.RuntimeInstanceId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Assert.Contains(sharedRuntimeInstanceId, visibleRegistryIds);
            Assert.Contains(sharedRuntimeInstanceId, visibleCapacityIds);

            var registered =
                await runtimeInstanceRegistry
                    .GetAsync(
                        sharedRuntimeInstanceId)
                    .ConfigureAwait(false);

            var capacity =
                await runtimeInstanceCapacityStore
                    .GetAsync(
                        sharedRuntimeInstanceId)
                    .ConfigureAwait(false);

            Assert.NotNull(registered);
            Assert.NotNull(capacity);
            Assert.Equal(sharedRuntimeInstanceId, registered!.RuntimeInstanceId);
            Assert.Equal(sharedRuntimeInstanceId, capacity!.RuntimeInstanceId);
            Assert.Equal("http", registered.Metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName]);
            Assert.Equal("http", capacity.Metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName]);
            Assert.Equal("http", registered.Metadata["provider.name"]);
            Assert.Equal("http", capacity.Metadata["provider.name"]);

            Assert.Equal(
                AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                registered.Metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName]);

            Assert.Equal(
                AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                capacity.Metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName]);

            Assert.Equal("Shared", registered.Metadata["runtime.isolationMode"]);
            Assert.Equal("Shared", capacity.Metadata["runtime.isolationMode"]);
            Assert.Equal("False", registered.Metadata["runtime.preferDedicatedCapacity"]);
            Assert.Equal("False", capacity.Metadata["runtime.preferDedicatedCapacity"]);
            Assert.Equal("True", registered.Metadata["runtime.allowSharedFallback"]);
            Assert.Equal("True", capacity.Metadata["runtime.allowSharedFallback"]);
            Assert.Equal(SharedRuntimeInstanceIdPrefix, registered.Metadata["runtime.instanceIdPrefix"]);
            Assert.Equal(SharedRuntimeInstanceIdPrefix, capacity.Metadata["runtime.instanceIdPrefix"]);

            output.WriteLine(
                $"FINAL HTTP TENANT-B HYBRID SHARED FALLBACK STATUS: " +
                $"SharedRunId='{hybridSharedRunId}', TenantId='{hybridRunAfterSubmit.ExecutionContextSnapshot.TenantId}', " +
                $"AssignedRuntimeInstanceId='{hybridRunAfterSubmit.AssignedRuntimeInstanceId}', " +
                $"SharedHttpRuntimeInstanceId='{sharedRuntimeInstanceId}', " +
                $"UnexpectedHybridScaleOutRequest='{unexpectedHybridScaleOutRequest is not null}', " +
                $"VisibleRegistryIds='{string.Join(" | ", visibleRegistryIds)}', " +
                $"VisibleCapacityIds='{string.Join(" | ", visibleCapacityIds)}'.");
        }

        [Fact]
        public async Task ControlPlaneWithHttpRuntimeInstances_With_HostManager_Mode_Should_Fulfill_Redis_ScaleOut_Request_Using_Http_Provider()
        {
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "http-hostmanager-scaleout-request");

            var controlPlaneSettings =
                GenericMcpServerTestSettings.CreateHttpScaleOutOnlyControlPlaneSettings(
                    controlPlaneId,
                    useHostManagerMode: true);

            await using var host =
                new GenericMcpServerTestHost(
                    controlPlaneSettings);

            using var client =
                host.CreateClient();

            AssertRedisStoresPublisherWatcherAndHttpProvider(
                host.Services);

            var mcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        host,
                        client,
                        RequestedBy,
                        tenantId: TenantId)
                    .ConfigureAwait(false);

            var sharedRunStore =
                host.Services.GetRequiredService<IAiSharedRunStore>();

            var scaleOutRequestStore =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var runtimeInstanceRegistry =
                host.Services.GetRequiredService<IAiRuntimeInstanceRegistry>();

            var runtimeInstanceCapacityStore =
                host.Services.GetRequiredService<IAiRuntimeInstanceCapacityStore>();

            var pipelineName =
                $"mcp-http-hostmanager-scaleout-{Guid.NewGuid():N}";

            var result =
                await SubmitSingleRunAndWaitForFulfilledScaleOutAsync(
                        mcp,
                        sharedRunStore,
                        scaleOutRequestStore,
                        controlPlaneId,
                        pipelineName,
                        TenantId,
                        TimeSpan.FromSeconds(15))
                    .ConfigureAwait(false);

            Assert.Equal(AiRuntimeInstanceIsolationMode.Shared, result.ScaleOutRequest.IsolationMode);
            Assert.False(result.ScaleOutRequest.PreferDedicatedCapacity);
            Assert.True(result.ScaleOutRequest.AllowSharedFallback);
            Assert.Equal(1, result.ScaleOutRequest.MaxRuntimeInstances);
            Assert.Equal(SharedRuntimeInstanceIdPrefix, result.ScaleOutRequest.RuntimeInstanceIdPrefix);
            Assert.Equal(10, result.ScaleOutRequest.WorkerCountPerInstance);
            Assert.Equal(3, result.ScaleOutRequest.MaxConcurrentRunsPerInstance);

            var fulfilledRuntimeInstanceId =
                result.ScaleOutRequest.FulfilledRuntimeInstanceId!;

            Assert.Contains(
                $":{SharedRuntimeInstanceIdPrefix}-1",
                fulfilledRuntimeInstanceId,
                StringComparison.Ordinal);

            var registered =
                await runtimeInstanceRegistry
                    .GetAsync(fulfilledRuntimeInstanceId)
                    .ConfigureAwait(false);

            var capacity =
                await runtimeInstanceCapacityStore
                    .GetAsync(fulfilledRuntimeInstanceId)
                    .ConfigureAwait(false);

            Assert.NotNull(registered);
            Assert.NotNull(capacity);
            Assert.Equal(fulfilledRuntimeInstanceId, registered!.RuntimeInstanceId);
            Assert.Equal(fulfilledRuntimeInstanceId, capacity!.RuntimeInstanceId);
            Assert.Equal("http", registered.Metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName]);
            Assert.Equal("http", capacity.Metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName]);
            Assert.Equal("http", registered.Metadata["provider.name"]);
            Assert.Equal("http", capacity.Metadata["provider.name"]);
            Assert.Equal(AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName, registered.Metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName]);
            Assert.Equal(AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName, capacity.Metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName]);
            Assert.Equal("Shared", registered.Metadata["runtime.isolationMode"]);
            Assert.Equal("Shared", capacity.Metadata["runtime.isolationMode"]);

            output.WriteLine(
                $"Redis HTTP HostManager scale-out request fulfilled. ControlPlaneId='{controlPlaneId}', " +
                $"SharedRunId='{result.SharedRunId}', RequestId='{result.ScaleOutRequest.RequestId}', " +
                $"RuntimeInstanceId='{fulfilledRuntimeInstanceId}', PipelineKey='{pipelineName}'.");
        }

        private static async Task WaitUntilAsync(
            Func<Task<bool>> condition,
            TimeSpan timeout,
            TimeSpan? delay = null)
        {
            ArgumentNullException.ThrowIfNull(condition);

            var stopAt =
                DateTimeOffset.UtcNow.Add(timeout);

            var pollDelay =
                delay
                ?? TimeSpan.FromMilliseconds(200);

            while (DateTimeOffset.UtcNow < stopAt)
            {
                if (await condition().ConfigureAwait(false))
                {
                    return;
                }

                await Task
                    .Delay(pollDelay)
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                "Condition was not reached in time.");
        }

        /// <summary>
        /// Waits until a scale-out request reaches the expected status.
        /// </summary>
        /// <param name="store">The scale-out request store.</param>
        /// <param name="requestId">The scale-out request identifier.</param>
        /// <param name="expectedStatus">The expected scale-out request status.</param>
        /// <param name="timeout">The maximum wait duration.</param>
        /// <returns>The matching scale-out request record.</returns>
        private static async Task<AiRuntimeScaleOutRequestRecord> WaitForScaleOutRequestStatusAsync(
            IAiRuntimeScaleOutRequestStore store,
            string requestId,
            AiRuntimeScaleOutRequestStatus expectedStatus,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(
                store);

            ArgumentException.ThrowIfNullOrWhiteSpace(
                requestId);

            var deadline =
                DateTimeOffset.UtcNow.Add(
                    timeout);

            AiRuntimeScaleOutRequestRecord? last =
                null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                last =
                    await store
                        .GetAsync(
                            requestId)
                        .ConfigureAwait(false);

                if (last is not null &&
                    last.Status == expectedStatus)
                {
                    return last;
                }

                await Task
                    .Delay(
                        TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"Scale-out request '{requestId}' did not reach status '{expectedStatus}' within '{timeout}'. LastStatus='{last?.Status.ToString() ?? "missing"}'.");
        }

        /// <summary>
        /// Creates an execution context snapshot for runtime visibility registry and capacity checks.
        /// </summary>
        /// <param name="tenantId">The tenant identifier.</param>
        /// <param name="tenantGroupId">The tenant group identifier.</param>
        /// <param name="project">The project identifier.</param>
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
        /// Submits a number of shared runtime runs for the specified pipeline.
        /// </summary>
        /// <param name="mcp">The MCP test client.</param>
        /// <param name="pipelineName">The pipeline key.</param>
        /// <param name="count">The number of runs to submit.</param>
        /// <param name="stepCount">The number of pipeline steps.</param>
        /// <param name="flakyStepInterval">The flaky interval.</param>
        /// <param name="tenantId">The optional tenant id for the submit request.</param>
        /// <returns>The submitted shared run ids.</returns>
        private static async Task<IReadOnlySet<string>> SubmitRunsAsync(
            McpTestClient mcp,
            string pipelineName,
            int count,
            int stepCount,
            int flakyStepInterval,
            string? tenantId = null)
        {
            var submitRequest =
                CreateSubmitRequest(
                    pipelineName,
                    stepCount: stepCount,
                    flakyStepInterval: flakyStepInterval,
                    tenantId: tenantId);

            var submitResults =
                await mcp
                    .SubmitManyRunsAsync(
                        submitRequest,
                        count)
                    .ConfigureAwait(false);

            Assert.Equal(
                count,
                submitResults.Count);

            Assert.All(
                submitResults,
                result => Assert.True(
                    result.Success,
                    result.FailureReason ?? result.Message));

            var submittedSharedRunIds =
                submitResults
                    .Select(ExtractSharedRunId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(
                count,
                submittedSharedRunIds.Count);

            return submittedSharedRunIds;
        }

        /// <summary>
        /// Extracts the shared run id from a submit result.
        /// </summary>
        /// <param name="submitResult">The submit result.</param>
        /// <returns>The shared run id.</returns>
        private static string ExtractSharedRunId(
            object submitResult)
        {
            ArgumentNullException.ThrowIfNull(
                submitResult);

            var resultType =
                submitResult.GetType();

            var directSharedRunId =
                resultType
                    .GetProperty("SharedRunId")
                    ?.GetValue(submitResult) as string;

            if (!string.IsNullOrWhiteSpace(
                    directSharedRunId))
            {
                return directSharedRunId;
            }

            var runId =
                resultType
                    .GetProperty("RunId")
                    ?.GetValue(submitResult) as string;

            if (!string.IsNullOrWhiteSpace(
                    runId))
            {
                return runId;
            }

            var sharedRun =
                resultType
                    .GetProperty("SharedRun")
                    ?.GetValue(submitResult);

            if (sharedRun is not null)
            {
                var sharedRunId =
                    sharedRun
                        .GetType()
                        .GetProperty("SharedRunId")
                        ?.GetValue(sharedRun) as string;

                if (!string.IsNullOrWhiteSpace(
                        sharedRunId))
                {
                    return sharedRunId;
                }
            }

            var run =
                resultType
                    .GetProperty("Run")
                    ?.GetValue(submitResult);

            if (run is not null)
            {
                var sharedRunId =
                    run
                        .GetType()
                        .GetProperty("SharedRunId")
                        ?.GetValue(run) as string;

                if (!string.IsNullOrWhiteSpace(
                        sharedRunId))
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
        /// <param name="pipelineName">The pipeline key.</param>
        /// <param name="stepCount">The number of steps.</param>
        /// <param name="flakyStepInterval">The flaky interval.</param>
        /// <param name="tenantId">The optional tenant id for the submit request.</param>
        /// <returns>The submit request.</returns>
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
                TenantId = tenantId ?? TenantId,
                RequestedBy = RequestedBy,
                Source = Source,
                RunRequest = McpTestPipelineFactory.CreateRunRequest(
                    pipelineName,
                    stepCount: stepCount,
                    flakyStepInterval: flakyStepInterval)
            };
        }

        /// <summary>
        /// Mutable execution context snapshot provider used by runtime visibility checks.
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
                    ?? throw new InvalidOperationException(
                        "No execution context snapshot is currently configured for the test.");
            }
        }

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

            var submittedSharedRunIds =
                await SubmitRunsAsync(
                        mcp,
                        pipelineName,
                        count: 1,
                        stepCount: 3,
                        flakyStepInterval: 0,
                        tenantId: tenantId)
                    .ConfigureAwait(false);

            var sharedRunId =
                Assert.Single(submittedSharedRunIds);

            var sharedRun =
                await sharedRunStore
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

            var expectedScaleOutRequestId =
                $"scale-out-{sharedRunId}";

            var scaleOutRequest =
                await WaitForScaleOutRequestStatusAsync(
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
    }
}