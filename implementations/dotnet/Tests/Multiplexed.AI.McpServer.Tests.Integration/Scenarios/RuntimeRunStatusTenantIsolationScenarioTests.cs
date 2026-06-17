using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue.Redis;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios
{
    /// <summary>
    /// Contains end-to-end Redis-backed MCP tenant-isolation scenarios for runtime run status.
    /// </summary>
    /// <remarks>
    /// This test exercises the real MCP path:
    /// MCP RBAC headers, execution-context snapshot mapping, shared runtime controller,
    /// shared queue dispatch, local runtime queue control-plane, Redis runtime run execution index,
    /// and tenant-isolated runtime run status visibility.
    /// </remarks>
    public sealed class RuntimeRunStatusTenantIsolationScenarioTests
    {
        private const string TenantA = "tenant-a";
        private const string TenantB = "tenant-b";
        private const string RequestedByA = "mcp-runtime-status-tenant-a-test";
        private const string RequestedByB = "mcp-runtime-status-tenant-b-test";
        private const string Source = "mcp-runtime-status-tenant-isolation-test";
        private const string WorkerId = "mcp-runtime-status-worker";
        private const string PumpRuntimeInstanceId = "mcp-runtime-status-pump";
        private const string RuntimeInstancePrefix = "mcp-runtime-status-runtime";

        private readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance of the <see cref="RuntimeRunStatusTenantIsolationScenarioTests"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public RuntimeRunStatusTenantIsolationScenarioTests(
            ITestOutputHelper output)
        {
            this.output =
                output ?? throw new ArgumentNullException(nameof(output));
        }

        /// <summary>
        /// Verifies that a runtime run status created for one tenant cannot be read
        /// by another tenant through MCP.
        /// </summary>
        [Fact]
        public async Task Runtime_Run_Status_Mcp_Redis_Index_Should_Be_Isolated_By_Tenant()
        {
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "runtime-run-status-tenant-isolation");

            var settings =
                CreateTenantIsolationControlPlaneSettings(
                    controlPlaneId);

            await using var host =
                new GenericMcpServerTestHost(
                    settings);

            using var serviceScope =
                host.Services.CreateScope();

            var runtimeRunExecutionIndex =
                serviceScope.ServiceProvider
                    .GetRequiredService<IAiRuntimeRunExecutionIndex>();

            Assert.IsType<RedisAiRuntimeRunExecutionIndex>(
                runtimeRunExecutionIndex);

            using var tenantAHttpClient =
                host.CreateClient();

            using var tenantBHttpClient =
                host.CreateClient();

            var tenantAMcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        host,
                        tenantAHttpClient,
                        RequestedByA,
                        tenantId: TenantA)
                    .ConfigureAwait(false);

            var tenantBMcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        host,
                        tenantBHttpClient,
                        RequestedByB,
                        tenantId: TenantB)
                    .ConfigureAwait(false);

            var pipelineName =
                $"tenant-a-runtime-status-pipeline-{Guid.NewGuid():N}";

            var sharedRunId =
                $"tenant-a-runtime-status-run-{Guid.NewGuid():N}";

            var submit =
                await tenantAMcp
                    .SubmitRunAsync(
                        CreateSubmitRequest(
                            sharedRunId,
                            pipelineName,
                            TenantA,
                            RequestedByA))
                    .ConfigureAwait(false);

            Assert.True(
                submit.Success,
                submit.FailureReason ?? submit.Message);

            Assert.Equal(
                sharedRunId,
                submit.SharedRunId);

            var drainResult =
                await tenantAMcp
                    .DrainQueueAsync(
                        new AiSharedQueuePumpRequest
                        {
                            PumpRuntimeInstanceId = PumpRuntimeInstanceId,
                            PumpWorkerId = WorkerId,
                            MaxDispatches = 1,
                            RequestedBy = RequestedByA,
                            Source = Source
                        })
                    .ConfigureAwait(false);

            Assert.True(
                drainResult.Success,
                drainResult.FailureReason);

            var dispatchedRuns =
                await McpTestWaitHelpers
                    .WaitForDispatchedRunsAsync(
                        tenantAMcp,
                        pipelineName,
                        expectedCount: 1,
                        timeout: TimeSpan.FromSeconds(30))
                    .ConfigureAwait(false);

            var dispatchedRun =
                dispatchedRuns.Single();

            AssertAssignedToLocalRuntimePool(
                dispatchedRun);

            var redis =
                serviceScope.ServiceProvider
                    .GetRequiredService<IConnectionMultiplexer>();

            var database =
                redis.GetDatabase();

            var keyPrefix =
                settings.TryGetValue("AiRedis:KeyPrefix", out var configuredKeyPrefix) &&
                !string.IsNullOrWhiteSpace(configuredKeyPrefix)
                    ? configuredKeyPrefix
                    : "multiplexed:ai";

            var redisRuntimeRunIndexItemKey =
                $"{keyPrefix}:control-plane:{controlPlaneId}:runtime-run-index:item:{dispatchedRun.LocalRunId}";

            var redisRuntimeRunIndexItemExists =
                await database
                    .KeyExistsAsync(redisRuntimeRunIndexItemKey)
                    .ConfigureAwait(false);

            Assert.True(
                redisRuntimeRunIndexItemExists,
                $"Expected Redis runtime run execution index item key to exist: '{redisRuntimeRunIndexItemKey}'. This means the test is not using RedisAiRuntimeRunExecutionIndex or the run was not registered in Redis.");

            var finalStatuses =
                await McpTestWaitHelpers
                    .WaitForTerminalRuntimeRunStatusesAsync(
                        tenantAMcp,
                        dispatchedRuns,
                        timeout: TimeSpan.FromMinutes(1))
                    .ConfigureAwait(false);

            var tenantAFinalStatus =
                finalStatuses.Single();

            Assert.True(
                tenantAFinalStatus.Success,
                tenantAFinalStatus.FailureReason ?? tenantAFinalStatus.Message);

            Assert.Equal(
                "completed",
                tenantAFinalStatus.RunState?.Status);

            Assert.False(
                string.IsNullOrWhiteSpace(
                    tenantAFinalStatus.ExecutionId ??
                    tenantAFinalStatus.RunState?.ExecutionId));

            var tenantAVisibleStatus =
                await tenantAMcp
                    .GetRuntimeQueueRunStatusAsync(
                        CreateRuntimeRunStatusRequest(
                            dispatchedRun,
                            TenantA,
                            RequestedByA))
                    .ConfigureAwait(false);

            Assert.True(
                tenantAVisibleStatus.Success,
                tenantAVisibleStatus.FailureReason ?? tenantAVisibleStatus.Message);

            Assert.NotNull(
                tenantAVisibleStatus.RunState);

            Assert.Equal(
                dispatchedRun.LocalRunId,
                tenantAVisibleStatus.RunState!.RunId);

            Assert.Equal(
                "completed",
                tenantAVisibleStatus.RunState.Status);

            var tenantBCrossTenantStatus =
                await tenantBMcp
                    .GetRuntimeQueueRunStatusAsync(
                        CreateRuntimeRunStatusRequest(
                            dispatchedRun,
                            TenantB,
                            RequestedByB))
                    .ConfigureAwait(false);

            Assert.True(
                tenantBCrossTenantStatus.Success,
                tenantBCrossTenantStatus.FailureReason ?? tenantBCrossTenantStatus.Message);

            Assert.Null(
                tenantBCrossTenantStatus.RunState);

            Assert.True(
                string.IsNullOrWhiteSpace(
                    tenantBCrossTenantStatus.ExecutionId),
                $"Tenant B must not see tenant A execution id. ExecutionId='{tenantBCrossTenantStatus.ExecutionId}'.");

            output.WriteLine(
                $"Runtime run status tenant isolation verified. ControlPlaneId='{controlPlaneId}', SharedRunId='{sharedRunId}', RuntimeInstanceId='{dispatchedRun.AssignedRuntimeInstanceId}', LocalRunId='{dispatchedRun.LocalRunId}'.");
        }

        /// <summary>
        /// Verifies that a runtime run created for one tenant cannot be cancelled
        /// by another tenant through MCP.
        /// </summary>
        [Fact]
        public async Task Runtime_Run_Cancel_Mcp_Redis_Index_Should_Be_Isolated_By_Tenant()
        {
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "runtime-run-cancel-tenant-isolation");

            var settings =
                CreateTenantIsolationControlPlaneSettings(
                    controlPlaneId);

            await using var host =
                new GenericMcpServerTestHost(
                    settings);

            using var tenantAHttpClient =
                host.CreateClient();

            using var tenantBHttpClient =
                host.CreateClient();

            var tenantAMcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        host,
                        tenantAHttpClient,
                        RequestedByA,
                        tenantId: TenantA)
                    .ConfigureAwait(false);

            var tenantBMcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        host,
                        tenantBHttpClient,
                        RequestedByB,
                        tenantId: TenantB)
                    .ConfigureAwait(false);

            var pipelineName =
                $"tenant-a-runtime-cancel-pipeline-{Guid.NewGuid():N}";

            var sharedRunId =
                $"tenant-a-runtime-cancel-run-{Guid.NewGuid():N}";

            var submit =
                await tenantAMcp
                    .SubmitRunAsync(
                        CreateSubmitRequest(
                            sharedRunId,
                            pipelineName,
                            TenantA,
                            RequestedByA))
                    .ConfigureAwait(false);

            Assert.True(
                submit.Success,
                submit.FailureReason ?? submit.Message);

            Assert.Equal(
                sharedRunId,
                submit.SharedRunId);

            var drainResult =
                await tenantAMcp
                    .DrainQueueAsync(
                        new AiSharedQueuePumpRequest
                        {
                            PumpRuntimeInstanceId = PumpRuntimeInstanceId,
                            PumpWorkerId = WorkerId,
                            MaxDispatches = 1,
                            RequestedBy = RequestedByA,
                            Source = Source
                        })
                    .ConfigureAwait(false);

            Assert.True(
                drainResult.Success,
                drainResult.FailureReason);

            var dispatchedRuns =
                await McpTestWaitHelpers
                    .WaitForDispatchedRunsAsync(
                        tenantAMcp,
                        pipelineName,
                        expectedCount: 1,
                        timeout: TimeSpan.FromSeconds(30))
                    .ConfigureAwait(false);

            var dispatchedRun =
                dispatchedRuns.Single();

            AssertAssignedToLocalRuntimePool(
                dispatchedRun);

            var tenantBCancelForeignRun =
                await tenantBMcp
                    .CancelRuntimeQueueRunAsync(
                        CreateCancelRuntimeRunRequest(
                            dispatchedRun,
                            TenantB,
                            RequestedByB,
                            "tenant-b-must-not-cancel-tenant-a-runtime-run"))
                    .ConfigureAwait(false);

            Assert.True(
                tenantBCancelForeignRun.Success,
                tenantBCancelForeignRun.FailureReason ?? tenantBCancelForeignRun.Message);

            Assert.Null(
                tenantBCancelForeignRun.RunState);

            Assert.True(
                string.IsNullOrWhiteSpace(
                    tenantBCancelForeignRun.ExecutionId),
                $"Tenant B must not receive tenant A execution id. ExecutionId='{tenantBCancelForeignRun.ExecutionId}'.");

            var tenantBStatusAfterForeignCancel =
                await tenantBMcp
                    .GetRuntimeQueueRunStatusAsync(
                        CreateRuntimeRunStatusRequest(
                            dispatchedRun,
                            TenantB,
                            RequestedByB))
                    .ConfigureAwait(false);

            Assert.True(
                tenantBStatusAfterForeignCancel.Success,
                tenantBStatusAfterForeignCancel.FailureReason ?? tenantBStatusAfterForeignCancel.Message);

            Assert.Null(
                tenantBStatusAfterForeignCancel.RunState);

            Assert.True(
                string.IsNullOrWhiteSpace(
                    tenantBStatusAfterForeignCancel.ExecutionId),
                $"Tenant B must not see tenant A execution id after foreign cancel attempt. ExecutionId='{tenantBStatusAfterForeignCancel.ExecutionId}'.");

            var tenantAStatusAfterForeignCancel =
                await tenantAMcp
                    .GetRuntimeQueueRunStatusAsync(
                        CreateRuntimeRunStatusRequest(
                            dispatchedRun,
                            TenantA,
                            RequestedByA))
                    .ConfigureAwait(false);

            Assert.True(
                tenantAStatusAfterForeignCancel.Success,
                tenantAStatusAfterForeignCancel.FailureReason ?? tenantAStatusAfterForeignCancel.Message);

            Assert.NotNull(
                tenantAStatusAfterForeignCancel.RunState);

            Assert.Equal(
                dispatchedRun.LocalRunId,
                tenantAStatusAfterForeignCancel.RunState!.RunId);

            Assert.NotEqual(
                "cancelled",
                tenantAStatusAfterForeignCancel.RunState.Status,
                StringComparer.OrdinalIgnoreCase);

            output.WriteLine(
                $"Runtime run cancel tenant isolation verified. ControlPlaneId='{controlPlaneId}', SharedRunId='{sharedRunId}', RuntimeInstanceId='{dispatchedRun.AssignedRuntimeInstanceId}', LocalRunId='{dispatchedRun.LocalRunId}', TenantAStatus='{tenantAStatusAfterForeignCancel.RunState.Status}'.");
        }

        /// <summary>
        /// Creates a runtime queue cancel request for one local runtime run.
        /// </summary>
        private static AiRuntimeQueueControlPlaneRequest CreateCancelRuntimeRunRequest(
            AiSharedRunRecord dispatchedRun,
            string tenantId,
            string requestedBy,
            string reason)
        {
            ArgumentNullException.ThrowIfNull(dispatchedRun);
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
            ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);

            return new AiRuntimeQueueControlPlaneRequest
            {
                Operation = AiRuntimeQueueControlPlaneOperation.CancelRun,
                RuntimeInstanceId = dispatchedRun.AssignedRuntimeInstanceId,
                RunId = dispatchedRun.LocalRunId,
                Reason = reason,
                RequestedBy = requestedBy,
                Source = Source,
                IncludeRunState = true,
                IncludeDiagnostics = true,
                Metadata = new Dictionary<string, string>
                {
                    ["tenantId"] = tenantId,
                    ["scenario"] = "runtime-run-cancel-tenant-isolation"
                }
            };
        }

        /// <summary>
        /// Creates a shared run submit request for one tenant.
        /// </summary>
        private static AiSharedRuntimeControllerRequest CreateSubmitRequest(
            string sharedRunId,
            string pipelineName,
            string tenantId,
            string requestedBy)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
            ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);

            return new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                RequestedSharedRunId = sharedRunId,
                PipelineKey = pipelineName,
                TenantId = tenantId,
                CorrelationId = $"runtime-status-tenant-isolation-{Guid.NewGuid():N}",
                RequestedBy = requestedBy,
                Source = Source,
                RunRequest = McpTestPipelineFactory.CreateRunRequest(
                    pipelineName: pipelineName,
                    stepCount: 10,
                    input: new
                    {
                        source = Source,
                        tenantId,
                        scenario = "runtime-run-status-tenant-isolation"
                    },
                    enableRetention: false,
                    flakyStepInterval: 0)
            };
        }

        /// <summary>
        /// Creates a runtime queue run-status request for one local runtime run.
        /// </summary>
        private static AiRuntimeQueueControlPlaneRequest CreateRuntimeRunStatusRequest(
            AiSharedRunRecord dispatchedRun,
            string tenantId,
            string requestedBy)
        {
            ArgumentNullException.ThrowIfNull(dispatchedRun);
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
            ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);

            return new AiRuntimeQueueControlPlaneRequest
            {
                Operation = AiRuntimeQueueControlPlaneOperation.GetRunStatus,
                RuntimeInstanceId = dispatchedRun.AssignedRuntimeInstanceId,
                RunId = dispatchedRun.LocalRunId,
                RequestedBy = requestedBy,
                Source = Source,
                IncludeRunState = true,
                IncludeDiagnostics = true,
                Metadata = new Dictionary<string, string>
                {
                    ["tenantId"] = tenantId,
                    ["scenario"] = "runtime-run-status-tenant-isolation"
                }
            };
        }

        /// <summary>
        /// Asserts that a shared run was assigned to the local runtime pool.
        /// </summary>
        private static void AssertAssignedToLocalRuntimePool(
            AiSharedRunRecord run)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(run.AssignedRuntimeInstanceId));

            Assert.Contains(
                RuntimeInstancePrefix,
                run.AssignedRuntimeInstanceId,
                StringComparison.Ordinal);

            Assert.False(
                string.IsNullOrWhiteSpace(run.LocalRunId));
        }

        /// <summary>
        /// Creates Redis-backed MCP control-plane settings for runtime run status tenant isolation.
        /// </summary>
        private static Dictionary<string, string?> CreateTenantIsolationControlPlaneSettings(
            string controlPlaneId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);

            var controlPlaneRuntimeInstanceId =
                $"mcp-control-plane-runtime-status-{Guid.NewGuid():N}";

            return GenericMcpServerTestSettings.CreateMcpSettings(
                controlPlaneId,
                new Dictionary<string, string?>
                {
                    ["AiMcpHost:Mode"] = "ControlPlaneWithLocalRuntimeInstances",
                    ["AiMcpHost:EnableSharedQueuePump"] = "false",

                    ["AiSharedQueueBackgroundService:Enabled"] = "false",
                    ["AiSharedQueueBackgroundService:WaitForRuntimeReadiness"] = "false",
                    ["AiSharedQueuePump:Enabled"] = "true",
                    ["AiSharedRuntimeController:SubmitMode"] = "QueueFirst",

                    ["AiRuntimeInstanceRegistration:ControlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:ProviderName"] = "local",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:controlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"] = "local",
                    ["AiRuntimeInstanceRegistration:Metadata:controlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:Metadata:provider.name"] = "local",
                    ["AiRuntimeInstanceRegistration:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,
                    ["AiRuntimeInstanceRegistration:Metadata:hostType"] = "control-plane-with-local-runtime",
                    ["AiRuntimeInstanceRegistration:Metadata:deployment"] = "test-runtime-run-status-tenant-isolation",

                    ["AiLocalRuntimeInstancePool:Enabled"] = "true",
                    ["AiLocalRuntimeInstancePool:InstanceCount"] = "3",
                    ["AiLocalRuntimeInstancePool:WorkerCountPerInstance"] = "10",
                    ["AiLocalRuntimeInstancePool:MaxConcurrentRunsPerInstance"] = "5",
                    ["AiLocalRuntimeInstancePool:RuntimeInstanceIdPrefix"] = RuntimeInstancePrefix,

                    ["AiEngine:ControlPlane:ControlPlaneId"] = controlPlaneId,
                    ["AiEngine:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,
                    ["AiEngine:PipelineBackgroundController:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,
                    ["AiEngine:PipelineBackgroundController:MaxConcurrentRuns"] = "5",
                    ["AiEngine:PipelineBackgroundController:QueueCapacity"] = "500",
                    ["AiEngine:PipelineBackgroundController:Distributed:Enabled"] = "true",
                    ["AiEngine:PipelineBackgroundController:Distributed:WorkerCount"] = "10",
                    ["AiEngine:PipelineBackgroundController:MaxLocalWorkersPerExecution"] = "5",
                    ["AiEngine:RuntimeInstanceWorker:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,

                    ["AiRuntimeInstanceRegistration:HeartbeatInterval"] = "00:00:02",
                    ["AiRuntimeInstanceRegistration:RegistryTtl"] = "00:00:30",
                    ["AiRuntimeInstanceRegistration:CapacityTtl"] = "00:00:30"
                });
        }
    }
}