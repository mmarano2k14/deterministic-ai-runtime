using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Pool;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.ControlPlane.RuntimeInstances.Pool;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Multiplexed.AI.Runtime.ControlPlane.Admission.Reservations;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Local;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue.Redis;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios
{
    /// <summary>
    /// Contains MCP integration tests for runtime scale-out request orchestration.
    /// </summary>
    /// <remarks>
    /// These scenarios validate the real local scale-out flow:
    ///
    /// <list type="number">
    /// <item><description>MCP submits a shared run.</description></item>
    /// <item><description>Admission finds no available runtime capacity.</description></item>
    /// <item><description>The shared run is marked as <see cref="AiSharedRunStatus.ScaleOutRequested" />.</description></item>
    /// <item><description>A Redis-backed scale-out request is created.</description></item>
    /// <item><description>The scale-out watcher observes the request.</description></item>
    /// <item><description>The selector resolves the local runtime instance provider.</description></item>
    /// <item><description>The local provider delegates scale-out to the local runtime instance scaler.</description></item>
    /// <item><description>The scaler creates and starts a real local runtime instance host.</description></item>
    /// <item><description>The Redis-backed scale-out request is fulfilled.</description></item>
    /// </list>
    /// </remarks>
    public sealed class SharedRunScaleOutScenarioTests
    {
        /// <summary>
        /// Actor used by scale-out scenario tests.
        /// </summary>
        private const string RequestedBy = "mcp-scaleout-integration-test";

        /// <summary>
        /// Source used by scale-out scenario tests.
        /// </summary>
        private const string Source = "mcp-scaleout-test";

        /// <summary>
        /// Tenant used by existing scale-out scenario tests.
        /// </summary>
        private const string TenantId = "test-tenant";

        /// <summary>
        /// Dedicated tenant used by tenant-aware scale-out propagation tests.
        /// </summary>
        private const string TenantAwareTenantId = "tenant-a";

        /// <summary>
        /// Runtime id prefix used by the default local scaler scenario.
        /// </summary>
        private const string LocalRuntimeInstanceIdPrefix = "mcp-scaleout-runtime";

        /// <summary>
        /// Runtime id prefix expected from hardcoded tenant-a runtime settings.
        /// </summary>
        private const string TenantAwareRuntimeInstanceIdPrefix = "tenant-a-runtime";

        /// <summary>
        /// Hybrid tenant used by tenant-aware scale-out fallback propagation tests.
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
        /// Initializes a new instance of the <see cref="SharedRunScaleOutScenarioTests" /> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public SharedRunScaleOutScenarioTests(
            ITestOutputHelper output)
        {
            this.output =
                output;
        }

        /// <summary>
        /// Verifies that a real MCP control-plane host creates and fulfills a Redis-backed
        /// scale-out request using the local provider and local runtime instance scaler.
        /// </summary>
        [Fact]
        public async Task ControlPlaneWithLocalRuntimeInstances_With_No_Runtime_Capacity_Should_Fulfill_Redis_ScaleOut_Request_Using_Local_Scaler()
        {
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "local-scaleout-request");

            var controlPlaneSettings =
                GenericMcpServerTestSettings.CreateLocalScaleOutOnlyControlPlaneSettings(
                    controlPlaneId);

            await using var host =
                new GenericMcpServerTestHost(
                    controlPlaneSettings);

            using var client =
                host.CreateClient();

            AssertRedisStoresPublisherWatcherAndLocalScaler(
                host.Services);

            var mcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        host,
                        client,
                        RequestedBy,
                        tenantId: TenantId)
                    .ConfigureAwait(false);

            var scaleOutRequestStore =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var scaler =
                host.Services.GetRequiredService<IAiLocalRuntimeInstanceScaler>();

            Assert.Equal(
                0,
                scaler.ActiveInstanceCount);

            var pipelineName =
                $"mcp-local-scaleout-request-{Guid.NewGuid():N}";

            var expectedSharedRunIds =
                await SubmitRunsAsync(
                        mcp,
                        pipelineName,
                        count: 1,
                        stepCount: 3,
                        flakyStepInterval: 0)
                    .ConfigureAwait(false);

            var sharedRunId =
                Assert.Single(
                    expectedSharedRunIds);

            var sharedRunStore =
                host.Services.GetRequiredService<IAiSharedRunStore>();

            var sharedRun =
                await sharedRunStore
                    .GetAsync(
                        sharedRunId)
                    .ConfigureAwait(false);

            Assert.NotNull(
                sharedRun);

            output.WriteLine(
                $"Shared run after submit. SharedRunId='{sharedRun.SharedRunId}', Status='{sharedRun.Status}', ControlPlaneId='{sharedRun.ControlPlaneId}', PipelineKey='{sharedRun.PipelineKey}'.");

            Assert.Equal(
                AiSharedRunStatus.ScaleOutRequested,
                sharedRun.Status);

            Assert.Equal(
                controlPlaneId,
                sharedRun.ControlPlaneId);

            Assert.Equal(
                pipelineName,
                sharedRun.PipelineKey);

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
                "local",
                scaleOutRequest.ProviderHint);

            Assert.Equal(
                "local",
                scaleOutRequest.Metadata["providerHint"]);

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
                $":{LocalRuntimeInstanceIdPrefix}-1",
                scaleOutRequest.FulfilledRuntimeInstanceId,
                StringComparison.Ordinal);

            await WaitUntilAsync(
                    () => scaler.ActiveInstanceCount >= 1,
                    TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            Assert.True(
                scaler.ActiveInstanceCount >= 1,
                $"Expected the local scaler to create at least one runtime instance. ActiveInstanceCount='{scaler.ActiveInstanceCount}'.");

            output.WriteLine(
                $"Redis local scale-out request fulfilled by watcher. ControlPlaneId='{controlPlaneId}', SharedRunId='{sharedRunId}', RequestId='{scaleOutRequest.RequestId}', RuntimeInstanceId='{scaleOutRequest.FulfilledRuntimeInstanceId}', PipelineKey='{pipelineName}', ActiveLocalInstances='{scaler.ActiveInstanceCount}'.");
        }

        /// <summary>
        /// Verifies that a shared run which triggered scale-out is requeued,
        /// consumed by the shared queue pump, dispatched to the newly created local runtime instance,
        /// and reaches a terminal runtime execution status.
        /// </summary>
        [Fact]
        public async Task ControlPlaneWithLocalRuntimeInstances_With_No_Runtime_Capacity_Should_ScaleOut_Requeue_Dispatch_And_Execute_Run()
        {
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "local-scaleout-execute");

            var controlPlaneSettings =
                GenericMcpServerTestSettings.CreateLocalScaleOutOnlyControlPlaneSettings(
                    controlPlaneId);

            await using var host =
                new GenericMcpServerTestHost(
                    controlPlaneSettings);

            using var client =
                host.CreateClient();

            AssertRedisStoresPublisherWatcherAndLocalScaler(
                host.Services);

            var mcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        host,
                        client,
                        RequestedBy,
                        tenantId: TenantId)
                    .ConfigureAwait(false);

            var sharedQueue =
                host.Services.GetRequiredService<IAiSharedQueue>();

            var scaleOutRequestStore =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var scaler =
                host.Services.GetRequiredService<IAiLocalRuntimeInstanceScaler>();

            Assert.Equal(
                0,
                scaler.ActiveInstanceCount);

            var pipelineName =
                $"mcp-local-scaleout-execute-{Guid.NewGuid():N}";

            var expectedSharedRunIds =
                await SubmitRunsAsync(
                        mcp,
                        pipelineName,
                        count: 1,
                        stepCount: 3,
                        flakyStepInterval: 0)
                    .ConfigureAwait(false);

            var sharedRunId =
                Assert.Single(
                    expectedSharedRunIds);

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
                AiRuntimeScaleOutRequestStatus.Fulfilled,
                scaleOutRequest.Status);

            Assert.Equal(
                "local",
                scaleOutRequest.ProviderHint);

            Assert.False(
                string.IsNullOrWhiteSpace(
                    scaleOutRequest.FulfilledRuntimeInstanceId));

            Assert.Contains(
                $":{LocalRuntimeInstanceIdPrefix}-1",
                scaleOutRequest.FulfilledRuntimeInstanceId,
                StringComparison.Ordinal);

            await WaitUntilAsync(
                    () => scaler.ActiveInstanceCount >= 1,
                    TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            var dispatchedRuns =
                await McpTestWaitHelpers
                    .WaitForDispatchedRunsAsync(
                        mcp,
                        pipelineName,
                        expectedSharedRunIds,
                        expectedCount: 1,
                        timeout: TimeSpan.FromSeconds(30))
                    .ConfigureAwait(false);

            var dispatchedRun =
                Assert.Single(
                    dispatchedRuns);

            Assert.Equal(
                sharedRunId,
                dispatchedRun.SharedRunId);

            Assert.Equal(
                controlPlaneId,
                dispatchedRun.ControlPlaneId);

            Assert.Equal(
                pipelineName,
                dispatchedRun.PipelineKey);

            Assert.False(
                string.IsNullOrWhiteSpace(
                    dispatchedRun.AssignedRuntimeInstanceId));

            Assert.Contains(
                $":{LocalRuntimeInstanceIdPrefix}-1",
                dispatchedRun.AssignedRuntimeInstanceId,
                StringComparison.Ordinal);

            Assert.False(
                string.IsNullOrWhiteSpace(
                    dispatchedRun.LocalRunId));

            var executionStatus =
                await McpTestWaitHelpers
                    .WaitForRuntimeRunExecutionIdAsync(
                        mcp,
                        dispatchedRun,
                        TimeSpan.FromSeconds(30))
                    .ConfigureAwait(false);

            var executionId =
                executionStatus.ExecutionId ??
                executionStatus.RunState?.ExecutionId;

            Assert.False(
                string.IsNullOrWhiteSpace(
                    executionId));

            var terminalStatuses =
                await McpTestWaitHelpers
                    .WaitForTerminalRuntimeRunStatusesAsync(
                        mcp,
                        dispatchedRuns,
                        TimeSpan.FromSeconds(60))
                    .ConfigureAwait(false);

            var finalRuntimeStatus =
                Assert.Single(
                    terminalStatuses);

            var queueItem =
                await sharedQueue
                    .GetAsync(
                        sharedRunId)
                    .ConfigureAwait(false);

            Assert.NotNull(
                queueItem);

            Assert.Equal(
                AiSharedQueueItemStatus.Dispatched,
                queueItem.Status);

            output.WriteLine(
                $"FINAL SCALE-OUT EXECUTION STATUS: SharedRunId='{dispatchedRun.SharedRunId}', " +
                $"SharedRunStatus='{dispatchedRun.Status}', " +
                $"AssignedRuntimeInstanceId='{dispatchedRun.AssignedRuntimeInstanceId}', " +
                $"LocalRunId='{dispatchedRun.LocalRunId}', " +
                $"ExecutionId='{executionId}', " +
                $"RuntimeRunStatus='{finalRuntimeStatus.RunState?.Status}', " +
                $"QueueStatus='{queueItem.Status}', " +
                $"ScaleOutRequestStatus='{scaleOutRequest.Status}', " +
                $"ScaleOutRuntimeInstanceId='{scaleOutRequest.FulfilledRuntimeInstanceId}', " +
                $"ActiveLocalInstances='{scaler.ActiveInstanceCount}'.");
        }

        
        /// <summary>
        /// Verifies that a real MCP control-plane host propagates dedicated tenant runtime
        /// settings from admission into the Redis-backed scale-out request and the local scaler.
        /// </summary>
        [Fact]
        public async Task ControlPlaneWithLocalRuntimeInstances_With_Dedicated_Tenant_Should_Create_Tenant_Aware_ScaleOut_Request()
        {
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "tenant-aware-local-scaleout-request");

            var controlPlaneSettings =
                GenericMcpServerTestSettings.CreateLocalScaleOutOnlyControlPlaneSettings(
                    controlPlaneId);

            await using var host =
                new GenericMcpServerTestHost(
                    controlPlaneSettings);

            using var client =
                host.CreateClient();

            AssertRedisStoresPublisherWatcherAndLocalScaler(
                host.Services);

            var tenantRuntimeSettingsProvider =
                host.Services.GetRequiredService<IAiTenantRuntimeSettingsProvider>();

            var tenantRuntimeSettings =
                tenantRuntimeSettingsProvider.GetSettings(
                    TenantAwareTenantId,
                    null);

            output.WriteLine(
                $"Tenant-aware settings resolved. TenantId='{tenantRuntimeSettings.TenantId}', IsolationMode='{tenantRuntimeSettings.IsolationMode}', RuntimeInstanceIdPrefix='{tenantRuntimeSettings.RuntimeInstanceIdPrefix}', MaxRuntimeInstances='{tenantRuntimeSettings.MaxRuntimeInstances}'.");

            Assert.Equal(
                TenantAwareTenantId,
                tenantRuntimeSettings.TenantId);

            Assert.Equal(
                AiRuntimeInstanceIsolationMode.Dedicated,
                tenantRuntimeSettings.IsolationMode);

            Assert.Equal(
                TenantAwareRuntimeInstanceIdPrefix,
                tenantRuntimeSettings.RuntimeInstanceIdPrefix);

            var mcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        host,
                        client,
                        RequestedBy,
                        tenantId: TenantAwareTenantId)
                    .ConfigureAwait(false);

            var scaleOutRequestStore =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var scaler =
                host.Services.GetRequiredService<IAiLocalRuntimeInstanceScaler>();

            Assert.Equal(
                0,
                scaler.ActiveInstanceCount);

            var pipelineName =
                $"mcp-tenant-aware-local-scaleout-{Guid.NewGuid():N}";

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

            var sharedRunStore =
                host.Services.GetRequiredService<IAiSharedRunStore>();

            var sharedRun =
                await sharedRunStore
                    .GetAsync(
                        sharedRunId)
                    .ConfigureAwait(false);

            Assert.NotNull(
                sharedRun);

            output.WriteLine(
                $"Tenant-aware shared run after submit. SharedRunId='{sharedRun.SharedRunId}', Status='{sharedRun.Status}', ControlPlaneId='{sharedRun.ControlPlaneId}', PipelineKey='{sharedRun.PipelineKey}', TenantId='{sharedRun.ExecutionContextSnapshot.TenantId}'.");

            Assert.Equal(
                AiSharedRunStatus.ScaleOutRequested,
                sharedRun.Status);

            Assert.Equal(
                controlPlaneId,
                sharedRun.ControlPlaneId);

            Assert.Equal(
                pipelineName,
                sharedRun.PipelineKey);

            Assert.Equal(
                TenantAwareTenantId,
                sharedRun.ExecutionContextSnapshot.TenantId);

            Assert.NotNull(
                sharedRun.AdmissionDecision);

            Assert.Equal(
                TenantAwareTenantId,
                sharedRun.AdmissionDecision.TenantId);

            output.WriteLine(
                $"Tenant-aware admission decision stored with shared run. DecisionType='{sharedRun.AdmissionDecision.DecisionType}', TenantId='{sharedRun.AdmissionDecision.TenantId}', TenantGroupId='{sharedRun.AdmissionDecision.TenantGroupId}', StoredIsolationMode='{sharedRun.AdmissionDecision.TenantRuntimeSettings?.IsolationMode.ToString() ?? "null"}'.");

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
                TenantAwareTenantId,
                scaleOutRequest.TenantId);

            Assert.Equal(
                pipelineName,
                scaleOutRequest.PipelineKey);

            Assert.Equal(
                AiRuntimeInstanceIsolationMode.Dedicated,
                scaleOutRequest.IsolationMode);

            Assert.True(
                scaleOutRequest.PreferDedicatedCapacity);

            Assert.False(
                scaleOutRequest.AllowSharedFallback);

            Assert.Equal(
                3,
                scaleOutRequest.MaxRuntimeInstances);

            Assert.Equal(
                TenantAwareRuntimeInstanceIdPrefix,
                scaleOutRequest.RuntimeInstanceIdPrefix);

            Assert.Equal(
                10,
                scaleOutRequest.WorkerCountPerInstance);

            Assert.Equal(
                5,
                scaleOutRequest.MaxConcurrentRunsPerInstance);

            Assert.Equal(
                500,
                scaleOutRequest.LocalQueueCapacity);

            Assert.Equal(
                AiRuntimeScaleOutRequestStatus.Fulfilled,
                scaleOutRequest.Status);

            Assert.Equal(
                "local",
                scaleOutRequest.ProviderHint);

            Assert.Equal(
                "Dedicated",
                scaleOutRequest.Metadata["runtime.isolationMode"]);

            Assert.Equal(
                "True",
                scaleOutRequest.Metadata["runtime.preferDedicatedCapacity"]);

            Assert.Equal(
                "False",
                scaleOutRequest.Metadata["runtime.allowSharedFallback"]);

            Assert.Equal(
                "3",
                scaleOutRequest.Metadata["runtime.maxRuntimeInstances"]);

            Assert.Equal(
                TenantAwareRuntimeInstanceIdPrefix,
                scaleOutRequest.Metadata["runtime.instanceIdPrefix"]);

            Assert.Equal(
                "10",
                scaleOutRequest.Metadata["runtime.workerCountPerInstance"]);

            Assert.Equal(
                "5",
                scaleOutRequest.Metadata["runtime.maxConcurrentRunsPerInstance"]);

            Assert.Equal(
                "500",
                scaleOutRequest.Metadata["runtime.localQueueCapacity"]);

            Assert.False(
                string.IsNullOrWhiteSpace(
                    scaleOutRequest.FulfilledRuntimeInstanceId));

            Assert.Contains(
                $":{TenantAwareRuntimeInstanceIdPrefix}-1",
                scaleOutRequest.FulfilledRuntimeInstanceId,
                StringComparison.Ordinal);

            await WaitUntilAsync(
                    () => scaler.ActiveInstanceCount >= 1,
                    TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            Assert.True(
                scaler.ActiveInstanceCount >= 1,
                $"Expected the local scaler to create at least one tenant-aware runtime instance. ActiveInstanceCount='{scaler.ActiveInstanceCount}'.");

            output.WriteLine(
                $"Tenant-aware Redis local scale-out request fulfilled. ControlPlaneId='{controlPlaneId}', SharedRunId='{sharedRunId}', RequestId='{scaleOutRequest.RequestId}', TenantId='{scaleOutRequest.TenantId}', IsolationMode='{scaleOutRequest.IsolationMode}', RuntimeInstancePrefix='{scaleOutRequest.RuntimeInstanceIdPrefix}', RuntimeInstanceId='{scaleOutRequest.FulfilledRuntimeInstanceId}', PipelineKey='{pipelineName}', ActiveLocalInstances='{scaler.ActiveInstanceCount}'.");
        }


        /// <summary>
        /// Verifies that Redis-backed control-plane stores, the store-backed scale-out publisher,
        /// the selector, local provider, local scaler, admission policy, and watcher hosted service are registered correctly.
        /// </summary>
        /// <param name="services">The service provider to inspect.</param>
        private void AssertRedisStoresPublisherWatcherAndLocalScaler(
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

            var localScaler =
                services.GetRequiredService<IAiLocalRuntimeInstanceScaler>();

            var admissionOptions =
                services.GetRequiredService<IOptions<AiRunAdmissionOptions>>().Value;

            var providers =
                services.GetServices<IAiRuntimeInstanceProvider>().ToArray();

            var watcherOptions =
                services.GetRequiredService<IOptions<AiRuntimeScaleOutRequestWatcherOptions>>().Value;

            var localPoolOptions =
                services.GetRequiredService<IOptions<AiLocalRuntimeInstancePoolOptions>>().Value;

            var hostedServices =
                services.GetServices<IHostedService>().ToArray();

            output.WriteLine(
                $"Redis local scale-out assert: IAiSharedRunStore='{sharedRunStore.GetType().FullName}'.");

            output.WriteLine(
                $"Redis local scale-out assert: IAiSharedQueue='{sharedQueue.GetType().FullName}'.");

            output.WriteLine(
                $"Redis local scale-out assert: IAiRuntimeAdmissionReservationStore='{reservationStore.GetType().FullName}'.");

            output.WriteLine(
                $"Redis local scale-out assert: IAiRuntimeScaleOutRequestStore='{scaleOutRequestStore.GetType().FullName}'.");

            output.WriteLine(
                $"Redis local scale-out assert: IAiRuntimeScaleOutRequestPublisher='{scaleOutPublisher.GetType().FullName}'.");

            output.WriteLine(
                $"Redis local scale-out assert: IAiRuntimeScaleOutProviderSelector='{scaleOutSelector.GetType().FullName}'.");

            output.WriteLine(
                $"Redis local scale-out assert: IAiLocalRuntimeInstanceScaler='{localScaler.GetType().FullName}', ActiveInstanceCount='{localScaler.ActiveInstanceCount}'.");

            output.WriteLine(
                $"Redis local scale-out assert: Admission.Enabled='{admissionOptions.Enabled}', " +
                $"MaxInstanceCount='{admissionOptions.MaxInstanceCount?.ToString() ?? "null"}', " +
                $"EnableScaleOutRequest='{admissionOptions.EnableScaleOutRequest}', " +
                $"EnableGlobalQueueFallback='{admissionOptions.EnableGlobalQueueFallback}', " +
                $"RejectWhenNoCapacity='{admissionOptions.RejectWhenNoCapacity}'.");

            output.WriteLine(
                $"Redis local scale-out assert: LocalPool.Enabled='{localPoolOptions.Enabled}', InstanceCount='{localPoolOptions.InstanceCount}', RuntimeInstanceIdPrefix='{localPoolOptions.RuntimeInstanceIdPrefix}'.");

            output.WriteLine(
                $"Redis local scale-out assert: Watcher.Enabled='{watcherOptions.Enabled}', WatcherId='{watcherOptions.WatcherId}', ControlPlaneId='{watcherOptions.ControlPlaneId}', Interval='{watcherOptions.Interval}', MaxRequestsPerCycle='{watcherOptions.MaxRequestsPerCycle}'.");

            output.WriteLine(
                "Redis local scale-out assert: Runtime providers: " +
                string.Join(
                    " | ",
                    providers.Select(provider => provider.GetType().FullName)));

            output.WriteLine(
                "Redis local scale-out assert: IHostedService registrations: " +
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

            Assert.IsType<AiLocalRuntimeInstanceScaler>(
                localScaler);

            Assert.Contains(
                providers,
                provider => provider.GetType() == typeof(LocalAiRuntimeInstanceProvider));

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

            Assert.False(
                localPoolOptions.Enabled,
                "The local pool startup must be disabled in this scenario so admission sees no initial runtime capacity.");

            Assert.Equal(
                LocalRuntimeInstanceIdPrefix,
                localPoolOptions.RuntimeInstanceIdPrefix);

            Assert.Contains(
                hostedServices,
                service => service.GetType() == typeof(AiRuntimeScaleOutRequestWatcherHostedService));
        }

        /// <summary>
        /// Verifies that a real MCP control-plane host propagates hybrid tenant runtime
        /// settings from admission into the Redis-backed scale-out request and the local scaler.
        /// </summary>
        [Fact]
        public async Task ControlPlaneWithLocalRuntimeInstances_With_Hybrid_Tenant_Should_Create_Tenant_Aware_ScaleOut_Request()
        {
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "hybrid-tenant-local-scaleout-request");

            var controlPlaneSettings =
                GenericMcpServerTestSettings.CreateLocalScaleOutOnlyControlPlaneSettings(
                    controlPlaneId);

            await using var host =
                new GenericMcpServerTestHost(
                    controlPlaneSettings);

            using var client =
                host.CreateClient();

            AssertRedisStoresPublisherWatcherAndLocalScaler(
                host.Services);

            var tenantRuntimeSettingsProvider =
                host.Services.GetRequiredService<IAiTenantRuntimeSettingsProvider>();

            var tenantRuntimeSettings =
                tenantRuntimeSettingsProvider.GetSettings(
                    HybridTenantId,
                    null);

            output.WriteLine(
                $"Hybrid tenant settings resolved. TenantId='{tenantRuntimeSettings.TenantId}', IsolationMode='{tenantRuntimeSettings.IsolationMode}', RuntimeInstanceIdPrefix='{tenantRuntimeSettings.RuntimeInstanceIdPrefix}', MaxRuntimeInstances='{tenantRuntimeSettings.MaxRuntimeInstances}', AllowSharedFallback='{tenantRuntimeSettings.AllowSharedFallback}'.");

            Assert.Equal(
                HybridTenantId,
                tenantRuntimeSettings.TenantId);

            Assert.Equal(
                AiRuntimeInstanceIsolationMode.Hybrid,
                tenantRuntimeSettings.IsolationMode);

            Assert.Equal(
                HybridRuntimeInstanceIdPrefix,
                tenantRuntimeSettings.RuntimeInstanceIdPrefix);

            Assert.True(
                tenantRuntimeSettings.PreferDedicatedCapacity);

            Assert.True(
                tenantRuntimeSettings.AllowSharedFallback);

            Assert.Equal(
                2,
                tenantRuntimeSettings.MaxRuntimeInstances);

            Assert.Equal(
                5,
                tenantRuntimeSettings.WorkerCountPerInstance);

            Assert.Equal(
                3,
                tenantRuntimeSettings.MaxConcurrentRunsPerInstance);

            Assert.Equal(
                250,
                tenantRuntimeSettings.LocalQueueCapacity);

            var mcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        host,
                        client,
                        RequestedBy,
                        tenantId: HybridTenantId)
                    .ConfigureAwait(false);

            var scaleOutRequestStore =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var scaler =
                host.Services.GetRequiredService<IAiLocalRuntimeInstanceScaler>();

            Assert.Equal(
                0,
                scaler.ActiveInstanceCount);

            var pipelineName =
                $"mcp-hybrid-tenant-local-scaleout-{Guid.NewGuid():N}";

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

            var sharedRunStore =
                host.Services.GetRequiredService<IAiSharedRunStore>();

            var sharedRun =
                await sharedRunStore
                    .GetAsync(
                        sharedRunId)
                    .ConfigureAwait(false);

            Assert.NotNull(
                sharedRun);

            output.WriteLine(
                $"Hybrid tenant shared run after submit. SharedRunId='{sharedRun.SharedRunId}', Status='{sharedRun.Status}', ControlPlaneId='{sharedRun.ControlPlaneId}', PipelineKey='{sharedRun.PipelineKey}', TenantId='{sharedRun.ExecutionContextSnapshot.TenantId}'.");

            Assert.Equal(
                AiSharedRunStatus.ScaleOutRequested,
                sharedRun.Status);

            Assert.Equal(
                controlPlaneId,
                sharedRun.ControlPlaneId);

            Assert.Equal(
                pipelineName,
                sharedRun.PipelineKey);

            Assert.Equal(
                HybridTenantId,
                sharedRun.ExecutionContextSnapshot.TenantId);

            Assert.NotNull(
                sharedRun.AdmissionDecision);

            Assert.Equal(
                HybridTenantId,
                sharedRun.AdmissionDecision.TenantId);

            output.WriteLine(
                $"Hybrid tenant admission decision stored with shared run. DecisionType='{sharedRun.AdmissionDecision.DecisionType}', TenantId='{sharedRun.AdmissionDecision.TenantId}', TenantGroupId='{sharedRun.AdmissionDecision.TenantGroupId}', StoredIsolationMode='{sharedRun.AdmissionDecision.TenantRuntimeSettings?.IsolationMode.ToString() ?? "null"}'.");

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
                HybridTenantId,
                scaleOutRequest.TenantId);

            Assert.Equal(
                pipelineName,
                scaleOutRequest.PipelineKey);

            Assert.Equal(
                AiRuntimeInstanceIsolationMode.Hybrid,
                scaleOutRequest.IsolationMode);

            Assert.True(
                scaleOutRequest.PreferDedicatedCapacity);

            Assert.True(
                scaleOutRequest.AllowSharedFallback);

            Assert.Equal(
                2,
                scaleOutRequest.MaxRuntimeInstances);

            Assert.Equal(
                HybridRuntimeInstanceIdPrefix,
                scaleOutRequest.RuntimeInstanceIdPrefix);

            Assert.Equal(
                5,
                scaleOutRequest.WorkerCountPerInstance);

            Assert.Equal(
                3,
                scaleOutRequest.MaxConcurrentRunsPerInstance);

            Assert.Equal(
                250,
                scaleOutRequest.LocalQueueCapacity);

            Assert.Equal(
                AiRuntimeScaleOutRequestStatus.Fulfilled,
                scaleOutRequest.Status);

            Assert.Equal(
                "local",
                scaleOutRequest.ProviderHint);

            Assert.Equal(
                "Hybrid",
                scaleOutRequest.Metadata["runtime.isolationMode"]);

            Assert.Equal(
                "True",
                scaleOutRequest.Metadata["runtime.preferDedicatedCapacity"]);

            Assert.Equal(
                "True",
                scaleOutRequest.Metadata["runtime.allowSharedFallback"]);

            Assert.Equal(
                "2",
                scaleOutRequest.Metadata["runtime.maxRuntimeInstances"]);

            Assert.Equal(
                HybridRuntimeInstanceIdPrefix,
                scaleOutRequest.Metadata["runtime.instanceIdPrefix"]);

            Assert.Equal(
                "5",
                scaleOutRequest.Metadata["runtime.workerCountPerInstance"]);

            Assert.Equal(
                "3",
                scaleOutRequest.Metadata["runtime.maxConcurrentRunsPerInstance"]);

            Assert.Equal(
                "250",
                scaleOutRequest.Metadata["runtime.localQueueCapacity"]);

            Assert.False(
                string.IsNullOrWhiteSpace(
                    scaleOutRequest.FulfilledRuntimeInstanceId));

            Assert.Contains(
                $":{HybridRuntimeInstanceIdPrefix}-1",
                scaleOutRequest.FulfilledRuntimeInstanceId,
                StringComparison.Ordinal);

            await WaitUntilAsync(
                    () => scaler.ActiveInstanceCount >= 1,
                    TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            Assert.True(
                scaler.ActiveInstanceCount >= 1,
                $"Expected the local scaler to create at least one hybrid tenant runtime instance. ActiveInstanceCount='{scaler.ActiveInstanceCount}'.");

            output.WriteLine(
                $"Hybrid tenant Redis local scale-out request fulfilled. ControlPlaneId='{controlPlaneId}', SharedRunId='{sharedRunId}', RequestId='{scaleOutRequest.RequestId}', TenantId='{scaleOutRequest.TenantId}', IsolationMode='{scaleOutRequest.IsolationMode}', RuntimeInstancePrefix='{scaleOutRequest.RuntimeInstanceIdPrefix}', RuntimeInstanceId='{scaleOutRequest.FulfilledRuntimeInstanceId}', PipelineKey='{pipelineName}', ActiveLocalInstances='{scaler.ActiveInstanceCount}'.");
        }

        /// <summary>
        /// Verifies that a real MCP control-plane host keeps the default tenant in shared mode
        /// and uses the shared tenant runtime settings for scale-out.
        /// </summary>
        [Fact]
        public async Task ControlPlaneWithLocalRuntimeInstances_With_Default_Tenant_Should_Create_Shared_ScaleOut_Request()
        {
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "default-tenant-local-scaleout-request");

            var controlPlaneSettings =
                GenericMcpServerTestSettings.CreateLocalScaleOutOnlyControlPlaneSettings(
                    controlPlaneId);

            await using var host =
                new GenericMcpServerTestHost(
                    controlPlaneSettings);

            using var client =
                host.CreateClient();

            AssertRedisStoresPublisherWatcherAndLocalScaler(
                host.Services);

            var tenantRuntimeSettingsProvider =
                host.Services.GetRequiredService<IAiTenantRuntimeSettingsProvider>();

            var tenantRuntimeSettings =
                tenantRuntimeSettingsProvider.GetSettings(
                    TenantId,
                    null);

            output.WriteLine(
                $"Default tenant settings resolved. TenantId='{tenantRuntimeSettings.TenantId}', IsolationMode='{tenantRuntimeSettings.IsolationMode}', RuntimeInstanceIdPrefix='{tenantRuntimeSettings.RuntimeInstanceIdPrefix}', MaxRuntimeInstances='{tenantRuntimeSettings.MaxRuntimeInstances}', AllowSharedFallback='{tenantRuntimeSettings.AllowSharedFallback}'.");

            Assert.Equal(
                TenantId,
                tenantRuntimeSettings.TenantId);

            Assert.Equal(
                AiRuntimeInstanceIsolationMode.Shared,
                tenantRuntimeSettings.IsolationMode);

            Assert.False(
                tenantRuntimeSettings.PreferDedicatedCapacity);

            Assert.True(
                tenantRuntimeSettings.AllowSharedFallback);

            Assert.Equal(
                1,
                tenantRuntimeSettings.MaxRuntimeInstances);

            Assert.Equal(
                "runtime-instance",
                tenantRuntimeSettings.RuntimeInstanceIdPrefix);

            Assert.Equal(
                10,
                tenantRuntimeSettings.WorkerCountPerInstance);

            Assert.Equal(
                3,
                tenantRuntimeSettings.MaxConcurrentRunsPerInstance);

            var mcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        host,
                        client,
                        RequestedBy,
                        tenantId: TenantId)
                    .ConfigureAwait(false);

            var scaleOutRequestStore =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var scaler =
                host.Services.GetRequiredService<IAiLocalRuntimeInstanceScaler>();

            Assert.Equal(
                0,
                scaler.ActiveInstanceCount);

            var pipelineName =
                $"mcp-default-tenant-local-scaleout-{Guid.NewGuid():N}";

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

            var sharedRunStore =
                host.Services.GetRequiredService<IAiSharedRunStore>();

            var sharedRun =
                await sharedRunStore
                    .GetAsync(
                        sharedRunId)
                    .ConfigureAwait(false);

            Assert.NotNull(
                sharedRun);

            output.WriteLine(
                $"Default tenant shared run after submit. SharedRunId='{sharedRun.SharedRunId}', Status='{sharedRun.Status}', ControlPlaneId='{sharedRun.ControlPlaneId}', PipelineKey='{sharedRun.PipelineKey}', TenantId='{sharedRun.ExecutionContextSnapshot.TenantId}'.");

            Assert.Equal(
                AiSharedRunStatus.ScaleOutRequested,
                sharedRun.Status);

            Assert.Equal(
                controlPlaneId,
                sharedRun.ControlPlaneId);

            Assert.Equal(
                pipelineName,
                sharedRun.PipelineKey);

            Assert.Equal(
                TenantId,
                sharedRun.ExecutionContextSnapshot.TenantId);

            Assert.NotNull(
                sharedRun.AdmissionDecision);

            Assert.Equal(
                TenantId,
                sharedRun.AdmissionDecision.TenantId);

            Assert.Equal(
                AiRuntimeInstanceIsolationMode.Shared,
                sharedRun.AdmissionDecision.TenantRuntimeSettings?.IsolationMode);

            output.WriteLine(
                $"Default tenant admission decision stored with shared run. DecisionType='{sharedRun.AdmissionDecision.DecisionType}', TenantId='{sharedRun.AdmissionDecision.TenantId}', TenantGroupId='{sharedRun.AdmissionDecision.TenantGroupId}', StoredIsolationMode='{sharedRun.AdmissionDecision.TenantRuntimeSettings?.IsolationMode.ToString() ?? "null"}'.");

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
                "runtime-instance",
                scaleOutRequest.RuntimeInstanceIdPrefix);

            Assert.Equal(
                10,
                scaleOutRequest.WorkerCountPerInstance);

            Assert.Equal(
                3,
                scaleOutRequest.MaxConcurrentRunsPerInstance);

            Assert.Equal(
                AiRuntimeScaleOutRequestStatus.Fulfilled,
                scaleOutRequest.Status);

            Assert.Equal(
                "local",
                scaleOutRequest.ProviderHint);

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
                "runtime-instance",
                scaleOutRequest.Metadata["runtime.instanceIdPrefix"]);

            Assert.Equal(
                "10",
                scaleOutRequest.Metadata["runtime.workerCountPerInstance"]);

            Assert.Equal(
                "3",
                scaleOutRequest.Metadata["runtime.maxConcurrentRunsPerInstance"]);

            Assert.False(
                string.IsNullOrWhiteSpace(
                    scaleOutRequest.FulfilledRuntimeInstanceId));

            Assert.Contains(
                ":runtime-instance-1",
                scaleOutRequest.FulfilledRuntimeInstanceId,
                StringComparison.Ordinal);

            await WaitUntilAsync(
                    () => scaler.ActiveInstanceCount >= 1,
                    TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            Assert.True(
                scaler.ActiveInstanceCount >= 1,
                $"Expected the local scaler to create at least one shared default runtime instance. ActiveInstanceCount='{scaler.ActiveInstanceCount}'.");

            output.WriteLine(
                $"Default tenant Redis local scale-out request fulfilled. ControlPlaneId='{controlPlaneId}', SharedRunId='{sharedRunId}', RequestId='{scaleOutRequest.RequestId}', TenantId='{scaleOutRequest.TenantId}', IsolationMode='{scaleOutRequest.IsolationMode}', RuntimeInstancePrefix='{scaleOutRequest.RuntimeInstanceIdPrefix}', RuntimeInstanceId='{scaleOutRequest.FulfilledRuntimeInstanceId}', PipelineKey='{pipelineName}', ActiveLocalInstances='{scaler.ActiveInstanceCount}'.");
        }

        /// <summary>
        /// Verifies that the Redis-backed scale-out publisher does not request more runtime
        /// instances than the tenant-specific maximum instance count.
        /// </summary>
        [Fact]
        public async Task ScaleOutPublisher_With_Dedicated_Tenant_At_Max_Instance_Count_Should_Not_Request_Above_Tenant_Max()
        {
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "tenant-max-scaleout-cap");

            var controlPlaneSettings =
                GenericMcpServerTestSettings.CreateLocalScaleOutOnlyControlPlaneSettings(
                    controlPlaneId);

            await using var host =
                new GenericMcpServerTestHost(
                    controlPlaneSettings);

            AssertRedisStoresPublisherWatcherAndLocalScaler(
                host.Services);

            var tenantRuntimeSettingsProvider =
                host.Services.GetRequiredService<IAiTenantRuntimeSettingsProvider>();

            var tenantRuntimeSettings =
                tenantRuntimeSettingsProvider.GetSettings(
                    TenantAwareTenantId,
                    null);

            output.WriteLine(
                $"Tenant max scale-out cap settings resolved. TenantId='{tenantRuntimeSettings.TenantId}', IsolationMode='{tenantRuntimeSettings.IsolationMode}', RuntimeInstanceIdPrefix='{tenantRuntimeSettings.RuntimeInstanceIdPrefix}', MaxRuntimeInstances='{tenantRuntimeSettings.MaxRuntimeInstances}'.");

            Assert.Equal(
                TenantAwareTenantId,
                tenantRuntimeSettings.TenantId);

            Assert.Equal(
                AiRuntimeInstanceIsolationMode.Dedicated,
                tenantRuntimeSettings.IsolationMode);

            Assert.Equal(
                3,
                tenantRuntimeSettings.MaxRuntimeInstances);

            var scaleOutPublisher =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestPublisher>();

            var scaleOutRequestStore =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var sharedRunId =
                $"tenant-max-shared-run-{Guid.NewGuid():N}";

            var pipelineName =
                $"tenant-max-pipeline-{Guid.NewGuid():N}";

            var now =
                DateTimeOffset.UtcNow;

            var effectiveProject =
                "tenant-max-scaleout-cap";

            var effectiveNamespace =
                TenantAwareTenantId;

            var executionContextSnapshot =
                new ExecutionContextSnapshot
                {
                    TenantId = TenantAwareTenantId,
                    TenantGroupId = tenantRuntimeSettings.TenantGroupId,
                    ContextKey = "ctx-tenant-max-scaleout-cap",
                    CurrentNamespace = effectiveNamespace,
                    Namespaces = new List<NamespaceEntry>
                    {
                new()
                {
                    Name = effectiveNamespace,
                    Trns = new HashSet<string>
                    {
                        $"trn:{effectiveProject}:shared-run:execution:submit",
                        $"trn:{effectiveProject}:shared-run:registry:read",
                        $"trn:{effectiveProject}:shared-run:registry:list",
                        $"trn:{effectiveProject}:shared-queue:queue:list",
                        $"trn:{effectiveProject}:shared-queue:status:read",
                        $"trn:{effectiveProject}:shared-queue:pump:drain"
                    }
                }
                    },
                    UserId = RequestedBy,
                    Project = effectiveProject
                };

            var runRequest =
                new AiRuntimePipelineRunRequest
                {
                    PipelineName = pipelineName,
                    Input = "tenant max scale-out cap test",
                    ExecutionContextSnapshot = executionContextSnapshot
                };

            var sharedRun =
                new AiSharedRunRecord
                {
                    SharedRunId = sharedRunId,
                    Status = AiSharedRunStatus.ScaleOutRequested,
                    RunRequest = runRequest,
                    ExecutionContextSnapshot = executionContextSnapshot,
                    AdmissionDecision = new AiRunAdmissionDecision
                    {
                        DecisionType = AiRunAdmissionDecisionType.RequestScaleOut,
                        Reason = "Tenant max instance count cap test.",
                        TenantId = TenantAwareTenantId,
                        TenantGroupId = tenantRuntimeSettings.TenantGroupId,
                        TenantRuntimeSettings = tenantRuntimeSettings,
                        VisibleInstanceCount = 3,
                        AvailableInstanceCount = 0,
                        CurrentInstanceCount = 3,
                        MaxInstanceCount = tenantRuntimeSettings.MaxRuntimeInstances
                    },
                    ControlPlaneId = controlPlaneId,
                    PipelineKey = pipelineName,
                    CorrelationId = $"tenant-max-correlation-{Guid.NewGuid():N}",
                    RequestedBy = RequestedBy,
                    Source = Source,
                    Reason = "Tenant max instance count cap test.",
                    SubmittedAtUtc = now,
                    UpdatedAtUtc = now,
                    Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["test"] = "tenant-max-scaleout-cap"
                    }
                };

            var publishResult =
                await scaleOutPublisher
                    .PublishAsync(
                        new AiRuntimeScaleOutRequest
                        {
                            SharedRunId = sharedRunId,
                            SharedRun = sharedRun,
                            TenantId = TenantAwareTenantId,
                            TenantGroupId = tenantRuntimeSettings.TenantGroupId,
                            PipelineKey = pipelineName,
                            Reason = "Tenant max instance count cap test.",
                            RequestedBy = RequestedBy,
                            Source = Source,
                            CorrelationId = sharedRun.CorrelationId,

                            IsolationMode = tenantRuntimeSettings.IsolationMode,
                            PreferDedicatedCapacity = tenantRuntimeSettings.PreferDedicatedCapacity,
                            AllowSharedFallback = tenantRuntimeSettings.AllowSharedFallback,
                            MaxRuntimeInstances = tenantRuntimeSettings.MaxRuntimeInstances,
                            RuntimeInstanceIdPrefix = tenantRuntimeSettings.RuntimeInstanceIdPrefix,
                            WorkerCountPerInstance = tenantRuntimeSettings.WorkerCountPerInstance,
                            MaxConcurrentRunsPerInstance = tenantRuntimeSettings.MaxConcurrentRunsPerInstance,
                            LocalQueueCapacity = tenantRuntimeSettings.LocalQueueCapacity,

                            VisibleInstanceCount = 3,
                            AvailableInstanceCount = 0,
                            CurrentInstanceCount = 3,
                            MaxInstanceCount = tenantRuntimeSettings.MaxRuntimeInstances,
                            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["test"] = "tenant-max-scaleout-cap"
                            }
                        })
                    .ConfigureAwait(false);

            Assert.True(
                publishResult.Success,
                publishResult.Message);

            Assert.Equal(
                sharedRunId,
                publishResult.SharedRunId);

            var requestId =
                $"scale-out-{sharedRunId}";

            Assert.Equal(
                requestId,
                publishResult.ScaleOutRequestId);

            Assert.Equal(
                3,
                publishResult.RequestedTargetInstanceCount);

            var persisted =
                await scaleOutRequestStore
                    .GetAsync(
                        requestId)
                    .ConfigureAwait(false);

            Assert.NotNull(
                persisted);

            Assert.Equal(
                requestId,
                persisted.RequestId);

            Assert.Equal(
                sharedRunId,
                persisted.SharedRunId);

            Assert.Equal(
                controlPlaneId,
                persisted.ControlPlaneId);

            Assert.Equal(
                TenantAwareTenantId,
                persisted.TenantId);

            Assert.Equal(
                tenantRuntimeSettings.TenantGroupId,
                persisted.TenantGroupId);

            Assert.Equal(
                pipelineName,
                persisted.PipelineKey);

            Assert.Equal(
                AiRuntimeScaleOutRequestStatus.Pending,
                persisted.Status);

            Assert.Equal(
                AiRuntimeInstanceIsolationMode.Dedicated,
                persisted.IsolationMode);

            Assert.True(
                persisted.PreferDedicatedCapacity);

            Assert.False(
                persisted.AllowSharedFallback);

            Assert.Equal(
                3,
                persisted.MaxRuntimeInstances);

            Assert.Equal(
                TenantAwareRuntimeInstanceIdPrefix,
                persisted.RuntimeInstanceIdPrefix);

            Assert.Equal(
                10,
                persisted.WorkerCountPerInstance);

            Assert.Equal(
                5,
                persisted.MaxConcurrentRunsPerInstance);

            Assert.Equal(
                500,
                persisted.LocalQueueCapacity);

            Assert.Equal(
                3,
                persisted.VisibleInstanceCount);

            Assert.Equal(
                0,
                persisted.AvailableInstanceCount);

            Assert.Equal(
                3,
                persisted.CurrentInstanceCount);

            Assert.Equal(
                3,
                persisted.MaxInstanceCount);

            Assert.Equal(
                3,
                persisted.RequestedTargetInstanceCount);

            Assert.Equal(
                "local",
                persisted.ProviderHint);

            Assert.Equal(
                "Dedicated",
                persisted.Metadata["runtime.isolationMode"]);

            Assert.Equal(
                "True",
                persisted.Metadata["runtime.preferDedicatedCapacity"]);

            Assert.Equal(
                "False",
                persisted.Metadata["runtime.allowSharedFallback"]);

            Assert.Equal(
                "3",
                persisted.Metadata["runtime.maxRuntimeInstances"]);

            Assert.Equal(
                TenantAwareRuntimeInstanceIdPrefix,
                persisted.Metadata["runtime.instanceIdPrefix"]);

            Assert.Equal(
                "10",
                persisted.Metadata["runtime.workerCountPerInstance"]);

            Assert.Equal(
                "5",
                persisted.Metadata["runtime.maxConcurrentRunsPerInstance"]);

            Assert.Equal(
                "500",
                persisted.Metadata["runtime.localQueueCapacity"]);

            output.WriteLine(
                $"Tenant max scale-out cap persisted. RequestId='{persisted.RequestId}', TenantId='{persisted.TenantId}', IsolationMode='{persisted.IsolationMode}', CurrentInstanceCount='{persisted.CurrentInstanceCount}', MaxInstanceCount='{persisted.MaxInstanceCount}', RequestedTargetInstanceCount='{persisted.RequestedTargetInstanceCount}'.");
        }

        /// <summary>
        /// Verifies that the Redis-backed scale-out publisher does not request more runtime
        /// instances than the hybrid tenant-specific maximum instance count.
        /// </summary>
        [Fact]
        public async Task ScaleOutPublisher_With_Hybrid_Tenant_At_Max_Instance_Count_Should_Not_Request_Above_Tenant_Max()
        {
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "hybrid-tenant-max-scaleout-cap");

            var controlPlaneSettings =
                GenericMcpServerTestSettings.CreateLocalScaleOutOnlyControlPlaneSettings(
                    controlPlaneId);

            await using var host =
                new GenericMcpServerTestHost(
                    controlPlaneSettings);

            AssertRedisStoresPublisherWatcherAndLocalScaler(
                host.Services);

            var tenantRuntimeSettingsProvider =
                host.Services.GetRequiredService<IAiTenantRuntimeSettingsProvider>();

            var tenantRuntimeSettings =
                tenantRuntimeSettingsProvider.GetSettings(
                    HybridTenantId,
                    null);

            output.WriteLine(
                $"Hybrid tenant max scale-out cap settings resolved. TenantId='{tenantRuntimeSettings.TenantId}', IsolationMode='{tenantRuntimeSettings.IsolationMode}', RuntimeInstanceIdPrefix='{tenantRuntimeSettings.RuntimeInstanceIdPrefix}', MaxRuntimeInstances='{tenantRuntimeSettings.MaxRuntimeInstances}'.");

            Assert.Equal(
                HybridTenantId,
                tenantRuntimeSettings.TenantId);

            Assert.Equal(
                AiRuntimeInstanceIsolationMode.Hybrid,
                tenantRuntimeSettings.IsolationMode);

            Assert.Equal(
                2,
                tenantRuntimeSettings.MaxRuntimeInstances);

            Assert.True(
                tenantRuntimeSettings.PreferDedicatedCapacity);

            Assert.True(
                tenantRuntimeSettings.AllowSharedFallback);

            var scaleOutPublisher =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestPublisher>();

            var scaleOutRequestStore =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var sharedRunId =
                $"hybrid-tenant-max-shared-run-{Guid.NewGuid():N}";

            var pipelineName =
                $"hybrid-tenant-max-pipeline-{Guid.NewGuid():N}";

            var now =
                DateTimeOffset.UtcNow;

            var effectiveProject =
                "hybrid-tenant-max-scaleout-cap";

            var effectiveNamespace =
                HybridTenantId;

            var executionContextSnapshot =
                new ExecutionContextSnapshot
                {
                    TenantId = HybridTenantId,
                    TenantGroupId = tenantRuntimeSettings.TenantGroupId,
                    ContextKey = "ctx-hybrid-tenant-max-scaleout-cap",
                    CurrentNamespace = effectiveNamespace,
                    Namespaces = new List<NamespaceEntry>
                    {
                new()
                {
                    Name = effectiveNamespace,
                    Trns = new HashSet<string>
                    {
                        $"trn:{effectiveProject}:shared-run:execution:submit",
                        $"trn:{effectiveProject}:shared-run:registry:read",
                        $"trn:{effectiveProject}:shared-run:registry:list",
                        $"trn:{effectiveProject}:shared-queue:queue:list",
                        $"trn:{effectiveProject}:shared-queue:status:read",
                        $"trn:{effectiveProject}:shared-queue:pump:drain"
                    }
                }
                    },
                    UserId = RequestedBy,
                    Project = effectiveProject
                };

            var runRequest =
                new AiRuntimePipelineRunRequest
                {
                    PipelineName = pipelineName,
                    Input = "hybrid tenant max scale-out cap test",
                    ExecutionContextSnapshot = executionContextSnapshot
                };

            var sharedRun =
                new AiSharedRunRecord
                {
                    SharedRunId = sharedRunId,
                    Status = AiSharedRunStatus.ScaleOutRequested,
                    RunRequest = runRequest,
                    ExecutionContextSnapshot = executionContextSnapshot,
                    AdmissionDecision = new AiRunAdmissionDecision
                    {
                        DecisionType = AiRunAdmissionDecisionType.RequestScaleOut,
                        Reason = "Hybrid tenant max instance count cap test.",
                        TenantId = HybridTenantId,
                        TenantGroupId = tenantRuntimeSettings.TenantGroupId,
                        TenantRuntimeSettings = tenantRuntimeSettings,
                        VisibleInstanceCount = 2,
                        AvailableInstanceCount = 0,
                        CurrentInstanceCount = 2,
                        MaxInstanceCount = tenantRuntimeSettings.MaxRuntimeInstances
                    },
                    ControlPlaneId = controlPlaneId,
                    PipelineKey = pipelineName,
                    CorrelationId = $"hybrid-tenant-max-correlation-{Guid.NewGuid():N}",
                    RequestedBy = RequestedBy,
                    Source = Source,
                    Reason = "Hybrid tenant max instance count cap test.",
                    SubmittedAtUtc = now,
                    UpdatedAtUtc = now,
                    Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["test"] = "hybrid-tenant-max-scaleout-cap"
                    }
                };

            var publishResult =
                await scaleOutPublisher
                    .PublishAsync(
                        new AiRuntimeScaleOutRequest
                        {
                            SharedRunId = sharedRunId,
                            SharedRun = sharedRun,
                            TenantId = HybridTenantId,
                            TenantGroupId = tenantRuntimeSettings.TenantGroupId,
                            PipelineKey = pipelineName,
                            Reason = "Hybrid tenant max instance count cap test.",
                            RequestedBy = RequestedBy,
                            Source = Source,
                            CorrelationId = sharedRun.CorrelationId,

                            IsolationMode = tenantRuntimeSettings.IsolationMode,
                            PreferDedicatedCapacity = tenantRuntimeSettings.PreferDedicatedCapacity,
                            AllowSharedFallback = tenantRuntimeSettings.AllowSharedFallback,
                            MaxRuntimeInstances = tenantRuntimeSettings.MaxRuntimeInstances,
                            RuntimeInstanceIdPrefix = tenantRuntimeSettings.RuntimeInstanceIdPrefix,
                            WorkerCountPerInstance = tenantRuntimeSettings.WorkerCountPerInstance,
                            MaxConcurrentRunsPerInstance = tenantRuntimeSettings.MaxConcurrentRunsPerInstance,
                            LocalQueueCapacity = tenantRuntimeSettings.LocalQueueCapacity,

                            VisibleInstanceCount = 2,
                            AvailableInstanceCount = 0,
                            CurrentInstanceCount = 2,
                            MaxInstanceCount = tenantRuntimeSettings.MaxRuntimeInstances,
                            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["test"] = "hybrid-tenant-max-scaleout-cap"
                            }
                        })
                    .ConfigureAwait(false);

            Assert.True(
                publishResult.Success,
                publishResult.Message);

            Assert.Equal(
                sharedRunId,
                publishResult.SharedRunId);

            var requestId =
                $"scale-out-{sharedRunId}";

            Assert.Equal(
                requestId,
                publishResult.ScaleOutRequestId);

            Assert.Equal(
                2,
                publishResult.RequestedTargetInstanceCount);

            var persisted =
                await scaleOutRequestStore
                    .GetAsync(
                        requestId)
                    .ConfigureAwait(false);

            Assert.NotNull(
                persisted);

            Assert.Equal(
                requestId,
                persisted.RequestId);

            Assert.Equal(
                sharedRunId,
                persisted.SharedRunId);

            Assert.Equal(
                controlPlaneId,
                persisted.ControlPlaneId);

            Assert.Equal(
                HybridTenantId,
                persisted.TenantId);

            Assert.Equal(
                tenantRuntimeSettings.TenantGroupId,
                persisted.TenantGroupId);

            Assert.Equal(
                pipelineName,
                persisted.PipelineKey);

            Assert.Equal(
                AiRuntimeScaleOutRequestStatus.Pending,
                persisted.Status);

            Assert.Equal(
                AiRuntimeInstanceIsolationMode.Hybrid,
                persisted.IsolationMode);

            Assert.True(
                persisted.PreferDedicatedCapacity);

            Assert.True(
                persisted.AllowSharedFallback);

            Assert.Equal(
                2,
                persisted.MaxRuntimeInstances);

            Assert.Equal(
                HybridRuntimeInstanceIdPrefix,
                persisted.RuntimeInstanceIdPrefix);

            Assert.Equal(
                5,
                persisted.WorkerCountPerInstance);

            Assert.Equal(
                3,
                persisted.MaxConcurrentRunsPerInstance);

            Assert.Equal(
                250,
                persisted.LocalQueueCapacity);

            Assert.Equal(
                2,
                persisted.VisibleInstanceCount);

            Assert.Equal(
                0,
                persisted.AvailableInstanceCount);

            Assert.Equal(
                2,
                persisted.CurrentInstanceCount);

            Assert.Equal(
                2,
                persisted.MaxInstanceCount);

            Assert.Equal(
                2,
                persisted.RequestedTargetInstanceCount);

            Assert.Equal(
                "local",
                persisted.ProviderHint);

            Assert.Equal(
                "Hybrid",
                persisted.Metadata["runtime.isolationMode"]);

            Assert.Equal(
                "True",
                persisted.Metadata["runtime.preferDedicatedCapacity"]);

            Assert.Equal(
                "True",
                persisted.Metadata["runtime.allowSharedFallback"]);

            Assert.Equal(
                "2",
                persisted.Metadata["runtime.maxRuntimeInstances"]);

            Assert.Equal(
                HybridRuntimeInstanceIdPrefix,
                persisted.Metadata["runtime.instanceIdPrefix"]);

            Assert.Equal(
                "5",
                persisted.Metadata["runtime.workerCountPerInstance"]);

            Assert.Equal(
                "3",
                persisted.Metadata["runtime.maxConcurrentRunsPerInstance"]);

            Assert.Equal(
                "250",
                persisted.Metadata["runtime.localQueueCapacity"]);

            output.WriteLine(
                $"Hybrid tenant max scale-out cap persisted. RequestId='{persisted.RequestId}', TenantId='{persisted.TenantId}', IsolationMode='{persisted.IsolationMode}', CurrentInstanceCount='{persisted.CurrentInstanceCount}', MaxInstanceCount='{persisted.MaxInstanceCount}', RequestedTargetInstanceCount='{persisted.RequestedTargetInstanceCount}'.");
        }

        /// <summary>
        /// Verifies that the Redis-backed scale-out watcher preserves tenant runtime settings
        /// when converting a persisted scale-out request into a provider scale-out request.
        /// </summary>
        [Fact]
        public async Task ScaleOutWatcher_With_Hybrid_Tenant_Request_Should_Preserve_Tenant_Runtime_Settings_When_Fulfilling()
        {
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "hybrid-watcher-provider-request");

            var controlPlaneSettings =
                GenericMcpServerTestSettings.CreateLocalScaleOutOnlyControlPlaneSettings(
                    controlPlaneId);

            await using var host =
                new GenericMcpServerTestHost(
                    controlPlaneSettings);

            AssertRedisStoresPublisherWatcherAndLocalScaler(
                host.Services);

            var tenantRuntimeSettingsProvider =
                host.Services.GetRequiredService<IAiTenantRuntimeSettingsProvider>();

            var tenantRuntimeSettings =
                tenantRuntimeSettingsProvider.GetSettings(
                    HybridTenantId,
                    null);

            output.WriteLine(
                $"Hybrid watcher settings resolved. TenantId='{tenantRuntimeSettings.TenantId}', IsolationMode='{tenantRuntimeSettings.IsolationMode}', RuntimeInstanceIdPrefix='{tenantRuntimeSettings.RuntimeInstanceIdPrefix}', MaxRuntimeInstances='{tenantRuntimeSettings.MaxRuntimeInstances}', AllowSharedFallback='{tenantRuntimeSettings.AllowSharedFallback}'.");

            Assert.Equal(
                HybridTenantId,
                tenantRuntimeSettings.TenantId);

            Assert.Equal(
                AiRuntimeInstanceIsolationMode.Hybrid,
                tenantRuntimeSettings.IsolationMode);

            Assert.Equal(
                HybridRuntimeInstanceIdPrefix,
                tenantRuntimeSettings.RuntimeInstanceIdPrefix);

            Assert.Equal(
                2,
                tenantRuntimeSettings.MaxRuntimeInstances);

            Assert.True(
                tenantRuntimeSettings.PreferDedicatedCapacity);

            Assert.True(
                tenantRuntimeSettings.AllowSharedFallback);

            var scaleOutPublisher =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestPublisher>();

            var scaleOutRequestStore =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var scaler =
                host.Services.GetRequiredService<IAiLocalRuntimeInstanceScaler>();

            Assert.Equal(
                0,
                scaler.ActiveInstanceCount);

            var sharedRunId =
                $"hybrid-watcher-shared-run-{Guid.NewGuid():N}";

            var pipelineName =
                $"hybrid-watcher-pipeline-{Guid.NewGuid():N}";

            var now =
                DateTimeOffset.UtcNow;

            var effectiveProject =
                "hybrid-watcher-provider-request";

            var effectiveNamespace =
                HybridTenantId;

            var executionContextSnapshot =
                new ExecutionContextSnapshot
                {
                    TenantId = HybridTenantId,
                    TenantGroupId = tenantRuntimeSettings.TenantGroupId,
                    ContextKey = "ctx-hybrid-watcher-provider-request",
                    CurrentNamespace = effectiveNamespace,
                    Namespaces = new List<NamespaceEntry>
                    {
                new()
                {
                    Name = effectiveNamespace,
                    Trns = new HashSet<string>
                    {
                        $"trn:{effectiveProject}:shared-run:execution:submit",
                        $"trn:{effectiveProject}:shared-run:registry:read",
                        $"trn:{effectiveProject}:shared-run:registry:list",
                        $"trn:{effectiveProject}:shared-queue:queue:list",
                        $"trn:{effectiveProject}:shared-queue:status:read",
                        $"trn:{effectiveProject}:shared-queue:pump:drain"
                    }
                }
                    },
                    UserId = RequestedBy,
                    Project = effectiveProject
                };

            var runRequest =
                new AiRuntimePipelineRunRequest
                {
                    PipelineName = pipelineName,
                    Input = "hybrid watcher provider request propagation test",
                    ExecutionContextSnapshot = executionContextSnapshot
                };

            var sharedRun =
                new AiSharedRunRecord
                {
                    SharedRunId = sharedRunId,
                    Status = AiSharedRunStatus.ScaleOutRequested,
                    RunRequest = runRequest,
                    ExecutionContextSnapshot = executionContextSnapshot,
                    AdmissionDecision = new AiRunAdmissionDecision
                    {
                        DecisionType = AiRunAdmissionDecisionType.RequestScaleOut,
                        Reason = "Hybrid watcher provider request propagation test.",
                        TenantId = HybridTenantId,
                        TenantGroupId = tenantRuntimeSettings.TenantGroupId,
                        TenantRuntimeSettings = tenantRuntimeSettings,
                        VisibleInstanceCount = 0,
                        AvailableInstanceCount = 0,
                        CurrentInstanceCount = 0,
                        MaxInstanceCount = tenantRuntimeSettings.MaxRuntimeInstances
                    },
                    ControlPlaneId = controlPlaneId,
                    PipelineKey = pipelineName,
                    CorrelationId = $"hybrid-watcher-correlation-{Guid.NewGuid():N}",
                    RequestedBy = RequestedBy,
                    Source = Source,
                    Reason = "Hybrid watcher provider request propagation test.",
                    SubmittedAtUtc = now,
                    UpdatedAtUtc = now,
                    Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["test"] = "hybrid-watcher-provider-request"
                    }
                };

            var publishResult =
                await scaleOutPublisher
                    .PublishAsync(
                        new AiRuntimeScaleOutRequest
                        {
                            SharedRunId = sharedRunId,
                            SharedRun = sharedRun,
                            TenantId = HybridTenantId,
                            TenantGroupId = tenantRuntimeSettings.TenantGroupId,
                            PipelineKey = pipelineName,
                            Reason = "Hybrid watcher provider request propagation test.",
                            RequestedBy = RequestedBy,
                            Source = Source,
                            CorrelationId = sharedRun.CorrelationId,

                            IsolationMode = tenantRuntimeSettings.IsolationMode,
                            PreferDedicatedCapacity = tenantRuntimeSettings.PreferDedicatedCapacity,
                            AllowSharedFallback = tenantRuntimeSettings.AllowSharedFallback,
                            MaxRuntimeInstances = tenantRuntimeSettings.MaxRuntimeInstances,
                            RuntimeInstanceIdPrefix = tenantRuntimeSettings.RuntimeInstanceIdPrefix,
                            WorkerCountPerInstance = tenantRuntimeSettings.WorkerCountPerInstance,
                            MaxConcurrentRunsPerInstance = tenantRuntimeSettings.MaxConcurrentRunsPerInstance,
                            LocalQueueCapacity = tenantRuntimeSettings.LocalQueueCapacity,

                            VisibleInstanceCount = 0,
                            AvailableInstanceCount = 0,
                            CurrentInstanceCount = 0,
                            MaxInstanceCount = tenantRuntimeSettings.MaxRuntimeInstances,
                            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["test"] = "hybrid-watcher-provider-request"
                            }
                        })
                    .ConfigureAwait(false);

            Assert.True(
                publishResult.Success,
                publishResult.Message);

            var requestId =
                $"scale-out-{sharedRunId}";

            Assert.Equal(
                requestId,
                publishResult.ScaleOutRequestId);

            var fulfilled =
                await WaitForScaleOutRequestStatusAsync(
                        scaleOutRequestStore,
                        requestId,
                        AiRuntimeScaleOutRequestStatus.Fulfilled,
                        TimeSpan.FromSeconds(15))
                    .ConfigureAwait(false);

            Assert.Equal(
                requestId,
                fulfilled.RequestId);

            Assert.Equal(
                sharedRunId,
                fulfilled.SharedRunId);

            Assert.Equal(
                controlPlaneId,
                fulfilled.ControlPlaneId);

            Assert.Equal(
                HybridTenantId,
                fulfilled.TenantId);

            Assert.Equal(
                tenantRuntimeSettings.TenantGroupId,
                fulfilled.TenantGroupId);

            Assert.Equal(
                pipelineName,
                fulfilled.PipelineKey);

            Assert.Equal(
                AiRuntimeScaleOutRequestStatus.Fulfilled,
                fulfilled.Status);

            Assert.Equal(
                AiRuntimeInstanceIsolationMode.Hybrid,
                fulfilled.IsolationMode);

            Assert.True(
                fulfilled.PreferDedicatedCapacity);

            Assert.True(
                fulfilled.AllowSharedFallback);

            Assert.Equal(
                2,
                fulfilled.MaxRuntimeInstances);

            Assert.Equal(
                HybridRuntimeInstanceIdPrefix,
                fulfilled.RuntimeInstanceIdPrefix);

            Assert.Equal(
                5,
                fulfilled.WorkerCountPerInstance);

            Assert.Equal(
                3,
                fulfilled.MaxConcurrentRunsPerInstance);

            Assert.Equal(
                250,
                fulfilled.LocalQueueCapacity);

            Assert.Equal(
                0,
                fulfilled.VisibleInstanceCount);

            Assert.Equal(
                0,
                fulfilled.AvailableInstanceCount);

            Assert.Equal(
                0,
                fulfilled.CurrentInstanceCount);

            Assert.Equal(
                2,
                fulfilled.MaxInstanceCount);

            Assert.Equal(
                1,
                fulfilled.RequestedTargetInstanceCount);

            Assert.Equal(
                "local",
                fulfilled.ProviderHint);

            Assert.Equal(
                "mcp-scaleout-watcher",
                fulfilled.ObservedBy);

            Assert.Equal(
                "mcp-scaleout-watcher",
                fulfilled.FulfilledBy);

            Assert.False(
                string.IsNullOrWhiteSpace(
                    fulfilled.FulfilledRuntimeInstanceId));

            Assert.Contains(
                $":{HybridRuntimeInstanceIdPrefix}-1",
                fulfilled.FulfilledRuntimeInstanceId,
                StringComparison.Ordinal);

            Assert.Equal(
                "Hybrid",
                fulfilled.Metadata["runtime.isolationMode"]);

            Assert.Equal(
                "True",
                fulfilled.Metadata["runtime.preferDedicatedCapacity"]);

            Assert.Equal(
                "True",
                fulfilled.Metadata["runtime.allowSharedFallback"]);

            Assert.Equal(
                "2",
                fulfilled.Metadata["runtime.maxRuntimeInstances"]);

            Assert.Equal(
                HybridRuntimeInstanceIdPrefix,
                fulfilled.Metadata["runtime.instanceIdPrefix"]);

            Assert.Equal(
                "5",
                fulfilled.Metadata["runtime.workerCountPerInstance"]);

            Assert.Equal(
                "3",
                fulfilled.Metadata["runtime.maxConcurrentRunsPerInstance"]);

            Assert.Equal(
                "250",
                fulfilled.Metadata["runtime.localQueueCapacity"]);

            await WaitUntilAsync(
                    () => scaler.ActiveInstanceCount >= 1,
                    TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            Assert.True(
                scaler.ActiveInstanceCount >= 1,
                $"Expected the watcher/provider flow to create at least one hybrid tenant runtime instance. ActiveInstanceCount='{scaler.ActiveInstanceCount}'.");

            output.WriteLine(
                $"Hybrid watcher provider request fulfilled. RequestId='{fulfilled.RequestId}', TenantId='{fulfilled.TenantId}', IsolationMode='{fulfilled.IsolationMode}', RuntimeInstancePrefix='{fulfilled.RuntimeInstanceIdPrefix}', FulfilledRuntimeInstanceId='{fulfilled.FulfilledRuntimeInstanceId}', RequestedTargetInstanceCount='{fulfilled.RequestedTargetInstanceCount}', ActiveLocalInstances='{scaler.ActiveInstanceCount}'.");
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
        /// Waits until a condition becomes true.
        /// </summary>
        /// <param name="condition">The condition to evaluate.</param>
        /// <param name="timeout">The maximum wait duration.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private static async Task WaitUntilAsync(
            Func<bool> condition,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(
                condition);

            var deadline =
                DateTimeOffset.UtcNow.Add(
                    timeout);

            while (DateTimeOffset.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                await Task
                    .Delay(
                        TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"Condition was not reached within '{timeout}'.");
        }

        /// <summary>
        /// Submits a number of shared runtime runs for the specified pipeline.
        /// </summary>
        /// <param name="mcp">The MCP test client.</param>
        /// <param name="pipelineName">The pipeline key.</param>
        /// <param name="count">The number of runs to submit.</param>
        /// <param name="stepCount">The number of pipeline steps.</param>
        /// <param name="flakyStepInterval">The flaky step interval.</param>
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
    }
}
