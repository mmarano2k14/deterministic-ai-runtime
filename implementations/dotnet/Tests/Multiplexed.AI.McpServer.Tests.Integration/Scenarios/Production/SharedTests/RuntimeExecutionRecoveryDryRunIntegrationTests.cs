using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Health;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Health;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Ownership;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.SharedTests
{
    /// <summary>
    /// Production-style dry-run recovery tests for runtime execution recovery reconciliation.
    /// </summary>
    public sealed class RuntimeExecutionRecoveryDryRunIntegrationTests
    {
        /// <summary>
        /// Verifies that health reconciliation can mark a runtime unhealthy and recovery reconciliation
        /// can discover assigned unfinished runs without requeueing or mutating ownership.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Discover_Unfinished_Run_After_Runtime_Becomes_Unhealthy_Without_Requeue()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();
            var sharedQueue = new InMemoryAiSharedQueue();
            var sharedRunStore = new InMemoryAiSharedRunStore();
            var runExecutionIndex = new InMemoryAiRuntimeRunExecutionIndex();

            const string runtimeInstanceId = "runtime-tenant-a-1";
            const string sharedRunId = "shared-run-1";
            const string localRunId = "local-run-1";
            const string executionId = "execution-1";

            var contextSnapshot = CreateExecutionContextSnapshot(
                tenantId: "tenant-a",
                tenantGroupId: "tenant-group-a");

            await registry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId,
                    tenantId: "tenant-a",
                    tenantGroupId: "tenant-group-a"));

            await sharedRunStore.CreateAsync(new AiSharedRunRecord
            {
                SharedRunId = sharedRunId,
                Status = AiSharedRunStatus.QueuedGlobally,
                RunRequest = CreateRunRequest(contextSnapshot),
                ExecutionContextSnapshot = contextSnapshot,
                PipelineKey = "runtime-recovery-dry-run-test",
                CorrelationId = "correlation-runtime-recovery-dry-run",
                RequestedBy = "test",
                Source = "integration-test",
                Reason = "created-for-runtime-recovery-dry-run",
                SubmittedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>
                {
                    ["scenario"] = "runtime-recovery-dry-run"
                }
            });

            await sharedQueue.EnqueueAsync(new AiSharedQueueItem
            {
                SharedRunId = sharedRunId,
                Status = AiSharedQueueItemStatus.Pending,
                ExecutionContextSnapshot = contextSnapshot,
                PipelineKey = "runtime-recovery-dry-run-test",
                Priority = 0,
                EnqueuedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>
                {
                    ["scenario"] = "runtime-recovery-dry-run"
                }
            });

            var claimed = await sharedQueue.ClaimNextAsync(new AiSharedQueueClaimRequest
            {
                RuntimeInstanceId = runtimeInstanceId,
                WorkerId = "worker-1",
                TenantId = "tenant-a",
                PipelineKey = "runtime-recovery-dry-run-test",
                ClaimTtl = TimeSpan.FromMinutes(5),
                Reason = "test-claim"
            });

            Assert.NotNull(claimed);
            Assert.False(string.IsNullOrWhiteSpace(claimed!.ClaimToken));

            await sharedQueue.MarkDispatchedAsync(
                sharedRunId,
                claimed.ClaimToken!,
                reason: "test-dispatch");

            await sharedRunStore.MarkDispatchedAsync(
                sharedRunId,
                runtimeInstanceId,
                localRunId,
                executionId,
                reason: "test-dispatch");

            await runExecutionIndex.RegisterQueuedAsync(new AiRuntimeRunExecutionIndexEntry
            {
                RunId = localRunId,
                ExecutionId = executionId,
                RuntimeInstanceId = runtimeInstanceId,
                Status = "queued",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ExecutionContextSnapshot = contextSnapshot,
                Metadata = new Dictionary<string, string>
                {
                    ["scenario"] = "runtime-recovery-dry-run"
                }
            });

            await runExecutionIndex.MarkStartedAsync(
                localRunId,
                executionId);

            var healthReconciler = new AiRuntimeInstanceHealthReconciler(
                registry,
                Options.Create(new AiRuntimeInstanceHealthReconciliationOptions
                {
                    Enabled = true,
                    StaleHeartbeatThreshold = TimeSpan.Zero,
                    MarkStaleRuntimeUnhealthy = true,
                    IncludeReadyRuntimeInstances = true,
                    IncludeBusyRuntimeInstances = true
                }));

            var healthResult = await healthReconciler.ReconcileAsync();

            IAiSharedRunOwnershipResolver ownershipResolver =
                new AiSharedRunOwnershipResolver(
                    sharedQueue,
                    sharedRunStore);

            var recoveryReconciler = new AiRuntimeExecutionRecoveryReconciler(
                registry,
                runExecutionIndex,
                ownershipResolver,
                Options.Create(new AiRuntimeExecutionRecoveryReconciliationOptions
                {
                    Enabled = true,
                    IncludeUnhealthyRuntimeInstances = true,
                    IncludeStoppedRuntimeInstances = false,
                    IncludeDrainingRuntimeInstances = false,
                    RequeueUnfinishedRuns = false,
                    DryRun = true
                }));

            var recoveryResult = await recoveryReconciler.ReconcileAsync();

            var runtime = await registry.GetAsync(runtimeInstanceId);
            var sharedRun = await sharedRunStore.GetAsync(sharedRunId);
            var queueItem = await sharedQueue.GetAsync(sharedRunId);
            var activeQueueItems = await sharedQueue.ListAsync();
            var terminalQueueItems = await sharedQueue.ListAsync(includeTerminal: true);
            var unfinishedRuns = await runExecutionIndex.ListUnfinishedByRuntimeInstanceAsync(runtimeInstanceId);
            var indexEntry = await runExecutionIndex.GetAsync(localRunId);

            Assert.Equal(1, healthResult.ScannedCount);
            Assert.Equal(1, healthResult.MarkedUnhealthyCount);
            Assert.Equal(0, healthResult.IgnoredCount);

            Assert.NotNull(runtime);
            Assert.Equal(AiRuntimeInstanceStatus.Unhealthy, runtime!.Status);
            Assert.False(runtime.CanAcceptRun);

            Assert.Equal(1, recoveryResult.ScannedRuntimeInstanceCount);
            Assert.Equal(0, recoveryResult.IgnoredRuntimeInstanceCount);
            Assert.Equal(1, recoveryResult.DiscoveredUnfinishedRunCount);
            Assert.Equal(0, recoveryResult.RecoveredRunCount);

            var decision = Assert.Single(recoveryResult.Decisions);
            Assert.Equal(runtimeInstanceId, decision.RuntimeInstanceId);
            Assert.Equal(localRunId, decision.LocalRunId);
            Assert.Equal(executionId, decision.ExecutionId);
            Assert.Equal(sharedRunId, decision.SharedRunId);
            Assert.Equal("tenant-a", decision.TenantId);
            Assert.Equal("tenant-group-a", decision.TenantGroupId);
            Assert.Equal("report-recoverable-unfinished-run", decision.Action);
            Assert.Equal("dry-run-discovered-recoverable-shared-run", decision.Reason);
            Assert.False(decision.Changed);

            Assert.NotNull(sharedRun);
            Assert.Equal(AiSharedRunStatus.Dispatched, sharedRun!.Status);
            Assert.Equal(runtimeInstanceId, sharedRun.AssignedRuntimeInstanceId);
            Assert.Equal(localRunId, sharedRun.LocalRunId);
            Assert.Equal(executionId, sharedRun.ExecutionId);
            Assert.Equal("test-dispatch", sharedRun.Reason);

            Assert.NotNull(queueItem);
            Assert.Equal(AiSharedQueueItemStatus.Dispatched, queueItem!.Status);
            Assert.Equal(runtimeInstanceId, queueItem.ClaimedByRuntimeInstanceId);
            Assert.Equal("worker-1", queueItem.ClaimedByWorkerId);
            Assert.Equal(claimed.ClaimToken, queueItem.ClaimToken);
            Assert.Equal("test-dispatch", queueItem.Reason);

            Assert.Empty(activeQueueItems);

            var terminalItem = Assert.Single(terminalQueueItems);
            Assert.Equal(sharedRunId, terminalItem.SharedRunId);
            Assert.Equal(AiSharedQueueItemStatus.Dispatched, terminalItem.Status);
            Assert.Equal(runtimeInstanceId, terminalItem.ClaimedByRuntimeInstanceId);
            Assert.Equal(claimed.ClaimToken, terminalItem.ClaimToken);

            var unfinished = Assert.Single(unfinishedRuns);
            Assert.Equal(runtimeInstanceId, unfinished.RuntimeInstanceId);
            Assert.Equal(localRunId, unfinished.RunId);
            Assert.Equal(executionId, unfinished.ExecutionId);
            Assert.Equal("running", unfinished.Status);

            Assert.NotNull(indexEntry);
            Assert.Equal(localRunId, indexEntry!.RunId);
            Assert.Equal(executionId, indexEntry.ExecutionId);
            Assert.Equal(runtimeInstanceId, indexEntry.RuntimeInstanceId);
            Assert.Equal("running", indexEntry.Status);
            Assert.Null(indexEntry.CompletedAtUtc);
        }

        /// <summary>
        /// Creates a runtime instance registration.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="tenantId">The tenant identifier.</param>
        /// <param name="tenantGroupId">The tenant group identifier.</param>
        /// <returns>The runtime instance registration.</returns>
        private static AiRuntimeInstanceRegistration CreateRegistration(
            string runtimeInstanceId,
            string tenantId,
            string tenantGroupId)
        {
            return new AiRuntimeInstanceRegistration
            {
                RuntimeInstanceId = runtimeInstanceId,
                TenantId = tenantId,
                TenantGroupId = tenantGroupId,
                Role = AiRuntimeInstanceRole.Runtime,
                HostName = "production-test-host",
                ProcessId = 12345,
                WorkerCount = 5,
                QueueCapacity = 20,
                MaxConcurrentRuns = 5,
                RuntimeVersion = "production-test",
                Metadata = new Dictionary<string, string>
                {
                    ["scenario"] = "runtime-recovery-dry-run"
                }
            };
        }

        /// <summary>
        /// Creates an execution context snapshot for tenant-aware test records.
        /// </summary>
        /// <param name="tenantId">The tenant identifier.</param>
        /// <param name="tenantGroupId">The tenant group identifier.</param>
        /// <returns>The execution context snapshot.</returns>
        private static ExecutionContextSnapshot CreateExecutionContextSnapshot(
            string tenantId,
            string tenantGroupId)
        {
            return new ExecutionContextSnapshot
            {
                ContextKey = $"ctx-{tenantId}",
                Project = "deterministic-ai-runtime-tests",
                UserId = "test-user",
                TenantId = tenantId,
                TenantGroupId = tenantGroupId,
                CurrentNamespace = "default",
                Namespaces = new List<NamespaceEntry>(),
                InFlightCount = 0,
                TtlSeconds = 300,
                CreatedAtUtc = DateTime.Now
            };
        }

        /// <summary>
        /// Creates a pipeline run request for shared-run test records.
        /// </summary>
        /// <param name="contextSnapshot">The execution context snapshot.</param>
        /// <returns>The pipeline run request.</returns>
        private static AiRuntimePipelineRunRequest CreateRunRequest(
            ExecutionContextSnapshot contextSnapshot)
        {
            return new AiRuntimePipelineRunRequest
            {
                PipelineName = "runtime-recovery-dry-run-test",
                ExecutionContextSnapshot = contextSnapshot,
                Input = new Dictionary<string, object?>
                {
                    ["scenario"] = "runtime-recovery-dry-run"
                }
            };
        }
    }
}