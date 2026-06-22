using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Development
{
    /// <summary>
    /// Contains end-to-end Redis-backed MCP tenant-isolation scenarios for shared runs.
    /// </summary>
    /// <remarks>
    /// This test exercises the real MCP path:
    /// MCP RBAC headers, execution-context snapshot mapping, shared runtime controller,
    /// Redis shared run store, Redis indexes, list/get/cancel visibility, and tenant isolation.
    /// </remarks>
    public sealed class SharedRunTenantIsolationScenarioTests
    {
        private const string TenantA = "tenant-a";
        private const string TenantB = "tenant-b";
        private const string RequestedByA = "mcp-tenant-a-test";
        private const string RequestedByB = "mcp-tenant-b-test";
        private const string Source = "mcp-tenant-isolation-test";

        private readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance of the <see cref="SharedRunTenantIsolationScenarioTests" /> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public SharedRunTenantIsolationScenarioTests(
            ITestOutputHelper output)
        {
            this.output =
                output;
        }

        /// <summary>
        /// Verifies that Redis-backed shared run operations are isolated by tenant when
        /// they are executed through MCP with different RBAC execution contexts.
        /// </summary>
        [Fact]
        public async Task Shared_Run_Mcp_Redis_Store_Should_Isolate_List_Get_And_Cancel_By_Tenant()
        {
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "shared-run-tenant-isolation");

            var settings =
                CreateTenantIsolationControlPlaneSettings(
                    controlPlaneId);

            await using var host =
                new GenericMcpServerTestHost(
                    settings);

            using var tenantAClient =
                host.CreateClient();

            using var tenantBClient =
                host.CreateClient();

            var tenantAMcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        host,
                        tenantAClient,
                        RequestedByA,
                        tenantId: TenantA)
                    .ConfigureAwait(false);

            var tenantBMcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        host,
                        tenantBClient,
                        RequestedByB,
                        tenantId: TenantB)
                    .ConfigureAwait(false);

            var tenantAPipelineName =
                $"tenant-a-pipeline-{Guid.NewGuid():N}";

            var tenantBPipelineName =
                $"tenant-b-pipeline-{Guid.NewGuid():N}";

            var tenantASharedRunId =
                $"tenant-a-run-{Guid.NewGuid():N}";

            var tenantBSharedRunId =
                $"tenant-b-run-{Guid.NewGuid():N}";

            var tenantASubmit =
                await tenantAMcp
                    .SubmitRunAsync(
                        CreateSubmitRequest(
                            tenantASharedRunId,
                            tenantAPipelineName,
                            TenantA,
                            RequestedByA))
                    .ConfigureAwait(false);

            var tenantBSubmit =
                await tenantBMcp
                    .SubmitRunAsync(
                        CreateSubmitRequest(
                            tenantBSharedRunId,
                            tenantBPipelineName,
                            TenantB,
                            RequestedByB))
                    .ConfigureAwait(false);

            Assert.True(
                tenantASubmit.Success,
                tenantASubmit.FailureReason ?? tenantASubmit.Message);

            Assert.True(
                tenantBSubmit.Success,
                tenantBSubmit.FailureReason ?? tenantBSubmit.Message);

            Assert.Equal(
                tenantASharedRunId,
                tenantASubmit.SharedRunId);

            Assert.Equal(
                tenantBSharedRunId,
                tenantBSubmit.SharedRunId);

            var tenantAList =
                await tenantAMcp
                    .ListSharedRunsAsync(
                        CreateListRequest(
                            TenantA,
                            RequestedByA))
                    .ConfigureAwait(false);

            var tenantBList =
                await tenantBMcp
                    .ListSharedRunsAsync(
                        CreateListRequest(
                            TenantB,
                            RequestedByB))
                    .ConfigureAwait(false);

            Assert.True(
                tenantAList.Success,
                tenantAList.FailureReason ?? tenantAList.Message);

            Assert.True(
                tenantBList.Success,
                tenantBList.FailureReason ?? tenantBList.Message);

            Assert.NotNull(
                tenantAList.Runs);

            Assert.NotNull(
                tenantBList.Runs);

            Assert.Contains(
                tenantAList.Runs,
                run => run.SharedRunId == tenantASharedRunId);

            Assert.DoesNotContain(
                tenantAList.Runs,
                run => run.SharedRunId == tenantBSharedRunId);

            Assert.Contains(
                tenantBList.Runs,
                run => run.SharedRunId == tenantBSharedRunId);

            Assert.DoesNotContain(
                tenantBList.Runs,
                run => run.SharedRunId == tenantASharedRunId);

            var tenantAGetOwn =
                await tenantAMcp
                    .GetSharedRunAsync(
                        CreateGetRequest(
                            tenantASharedRunId,
                            TenantA,
                            RequestedByA))
                    .ConfigureAwait(false);

            var tenantAGetForeign =
                await tenantAMcp
                    .GetSharedRunAsync(
                        CreateGetRequest(
                            tenantBSharedRunId,
                            TenantA,
                            RequestedByA))
                    .ConfigureAwait(false);

            var tenantBGetOwn =
                await tenantBMcp
                    .GetSharedRunAsync(
                        CreateGetRequest(
                            tenantBSharedRunId,
                            TenantB,
                            RequestedByB))
                    .ConfigureAwait(false);

            Assert.True(
                tenantAGetOwn.Success,
                tenantAGetOwn.FailureReason ?? tenantAGetOwn.Message);

            Assert.True(
                tenantAGetForeign.Success,
                tenantAGetForeign.FailureReason ?? tenantAGetForeign.Message);

            Assert.True(
                tenantBGetOwn.Success,
                tenantBGetOwn.FailureReason ?? tenantBGetOwn.Message);

            Assert.NotNull(
                tenantAGetOwn.Run);

            Assert.Null(
                tenantAGetForeign.Run);

            Assert.NotNull(
                tenantBGetOwn.Run);

            Assert.Equal(
                tenantASharedRunId,
                tenantAGetOwn.Run!.SharedRunId);

            Assert.Equal(
                tenantBSharedRunId,
                tenantBGetOwn.Run!.SharedRunId);

            var tenantACancelForeign =
                await tenantAMcp
                    .CancelSharedRunAsync(
                        CreateCancelRequest(
                            tenantBSharedRunId,
                            TenantA,
                            RequestedByA,
                            "tenant-a-must-not-cancel-tenant-b-run"))
                    .ConfigureAwait(false);

            Assert.True(
                tenantACancelForeign.Success,
                tenantACancelForeign.FailureReason ?? tenantACancelForeign.Message);

            Assert.Null(
                tenantACancelForeign.Run);

            var tenantBGetAfterForeignCancelAttempt =
                await tenantBMcp
                    .GetSharedRunAsync(
                        CreateGetRequest(
                            tenantBSharedRunId,
                            TenantB,
                            RequestedByB))
                    .ConfigureAwait(false);

            Assert.True(
                tenantBGetAfterForeignCancelAttempt.Success,
                tenantBGetAfterForeignCancelAttempt.FailureReason ?? tenantBGetAfterForeignCancelAttempt.Message);

            Assert.NotNull(
                tenantBGetAfterForeignCancelAttempt.Run);

            Assert.NotEqual(
                AiSharedRunStatus.Cancelled,
                tenantBGetAfterForeignCancelAttempt.Run!.Status);

            var tenantBCancelOwn =
                await tenantBMcp
                    .CancelSharedRunAsync(
                        CreateCancelRequest(
                            tenantBSharedRunId,
                            TenantB,
                            RequestedByB,
                            "tenant-b-cancels-own-run"))
                    .ConfigureAwait(false);

            Assert.True(
                tenantBCancelOwn.Success,
                tenantBCancelOwn.FailureReason ?? tenantBCancelOwn.Message);

            Assert.NotNull(
                tenantBCancelOwn.Run);

            Assert.Equal(
                tenantBSharedRunId,
                tenantBCancelOwn.Run!.SharedRunId);

            Assert.Equal(
                AiSharedRunStatus.Cancelled,
                tenantBCancelOwn.Run.Status);

            output.WriteLine(
                $"Tenant isolation verified. ControlPlaneId='{controlPlaneId}', TenantARun='{tenantASharedRunId}', TenantBRun='{tenantBSharedRunId}'.");
        }

        /// <summary>
        /// Creates one shared run submit request for a specific tenant.
        /// </summary>
        private static AiSharedRuntimeControllerRequest CreateSubmitRequest(
            string sharedRunId,
            string pipelineName,
            string tenantId,
            string requestedBy)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                sharedRunId);

            ArgumentException.ThrowIfNullOrWhiteSpace(
                pipelineName);

            ArgumentException.ThrowIfNullOrWhiteSpace(
                tenantId);

            ArgumentException.ThrowIfNullOrWhiteSpace(
                requestedBy);

            return new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                RequestedSharedRunId = sharedRunId,
                PipelineKey = pipelineName,
                TenantId = tenantId,
                CorrelationId = $"tenant-isolation-{Guid.NewGuid():N}",
                RequestedBy = requestedBy,
                Source = Source,
                RunRequest = McpTestPipelineFactory.CreateRunRequest(
                    pipelineName: pipelineName,
                    stepCount: 3,
                    input: new
                    {
                        source = Source,
                        tenantId,
                        scenario = "shared-run-tenant-isolation"
                    },
                    enableRetention: false,
                    flakyStepInterval: 0)
            };
        }

        /// <summary>
        /// Creates a list request for a tenant-scoped shared run view.
        /// </summary>
        private static AiSharedRuntimeControllerRequest CreateListRequest(
            string tenantId,
            string requestedBy)
        {
            return new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.ListRuns,
                TenantId = tenantId,
                IncludeCancelled = true,
                IncludeCompleted = true,
                IncludeFailed = true,
                RequestedBy = requestedBy,
                Source = Source
            };
        }

        /// <summary>
        /// Creates a get request for a shared run.
        /// </summary>
        private static AiSharedRuntimeControllerRequest CreateGetRequest(
            string sharedRunId,
            string tenantId,
            string requestedBy)
        {
            return new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.GetRun,
                SharedRunId = sharedRunId,
                TenantId = tenantId,
                RequestedBy = requestedBy,
                Source = Source
            };
        }

        /// <summary>
        /// Creates a cancel request for a shared run.
        /// </summary>
        private static AiSharedRuntimeControllerRequest CreateCancelRequest(
            string sharedRunId,
            string tenantId,
            string requestedBy,
            string reason)
        {
            return new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.CancelRun,
                SharedRunId = sharedRunId,
                TenantId = tenantId,
                RequestedBy = requestedBy,
                Source = Source,
                Reason = reason
            };
        }

        /// <summary>
        /// Creates local Redis-backed MCP control-plane settings for tenant isolation tests.
        /// </summary>
        private static Dictionary<string, string?> CreateTenantIsolationControlPlaneSettings(
            string controlPlaneId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                controlPlaneId);

            var controlPlaneRuntimeInstanceId =
                $"mcp-control-plane-tenant-isolation-{Guid.NewGuid():N}";

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
                    ["AiRuntimeInstanceRegistration:Metadata:deployment"] = "test-shared-run-tenant-isolation",

                    ["AiLocalRuntimeInstancePool:Enabled"] = "true",
                    ["AiLocalRuntimeInstancePool:InstanceCount"] = "3",
                    ["AiLocalRuntimeInstancePool:WorkerCountPerInstance"] = "10",
                    ["AiLocalRuntimeInstancePool:MaxConcurrentRunsPerInstance"] = "5",
                    ["AiLocalRuntimeInstancePool:RuntimeInstanceIdPrefix"] = "mcp-tenant-isolation-runtime",

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
