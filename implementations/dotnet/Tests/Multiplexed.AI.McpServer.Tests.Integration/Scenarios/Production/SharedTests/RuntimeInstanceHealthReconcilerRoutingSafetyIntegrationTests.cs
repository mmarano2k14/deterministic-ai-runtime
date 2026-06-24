using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Health;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Health;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.SharedTests
{
    /// <summary>
    /// Production-style routing safety tests for runtime instance health reconciliation.
    /// </summary>
    public sealed class RuntimeInstanceHealthReconcilerRoutingSafetyIntegrationTests
    {
        /// <summary>
        /// Verifies that the health reconciler marks a stale ready runtime instance as unhealthy
        /// and prevents it from accepting new routing decisions.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Mark_Stale_Ready_Runtime_Unhealthy_And_Stop_New_Routing()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId: "runtime-tenant-a-1",
                    tenantId: "tenant-a",
                    tenantGroupId: "tenant-group-a"));

            var reconciler = CreateReconciler(
                registry,
                new AiRuntimeInstanceHealthReconciliationOptions
                {
                    Enabled = true,
                    StaleHeartbeatThreshold = TimeSpan.Zero,
                    MarkStaleRuntimeUnhealthy = true,
                    IncludeReadyRuntimeInstances = true,
                    IncludeBusyRuntimeInstances = true,
                    IgnoreStoppedRuntimeInstances = true,
                    IgnorePausedRuntimeInstances = true,
                    IgnoreDrainingRuntimeInstances = true,
                    DryRun = false
                });

            var result = await reconciler.ReconcileAsync();

            var snapshot = await registry.GetAsync("runtime-tenant-a-1");
            var routableSnapshots = await registry.ListAsync();

            Assert.Equal(1, result.ScannedCount);
            Assert.Equal(1, result.MarkedUnhealthyCount);
            Assert.Equal(0, result.IgnoredCount);
            Assert.Single(result.Decisions);

            Assert.Equal("runtime-tenant-a-1", result.Decisions[0].RuntimeInstanceId);
            Assert.Equal(AiRuntimeInstanceStatus.Ready, result.Decisions[0].PreviousStatus);
            Assert.Equal(AiRuntimeInstanceStatus.Unhealthy, result.Decisions[0].NewStatus);
            Assert.Equal("heartbeat-stale", result.Decisions[0].Reason);
            Assert.True(result.Decisions[0].Changed);

            Assert.NotNull(snapshot);
            Assert.Equal(AiRuntimeInstanceStatus.Unhealthy, snapshot!.Status);
            Assert.False(snapshot.CanAcceptRun);
            Assert.Equal("tenant-a", snapshot.TenantId);
            Assert.Equal("tenant-group-a", snapshot.TenantGroupId);
            Assert.Equal(5, snapshot.AvailableRunSlots);

            var listed = Assert.Single(routableSnapshots);
            Assert.Equal("runtime-tenant-a-1", listed.RuntimeInstanceId);
            Assert.Equal(AiRuntimeInstanceStatus.Unhealthy, listed.Status);
            Assert.False(listed.CanAcceptRun);
        }

        /// <summary>
        /// Verifies that the health reconciler marks a stale busy runtime instance as unhealthy
        /// without losing runtime ownership, capacity, or tenant metadata.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Mark_Stale_Busy_Runtime_Unhealthy_And_Preserve_Runtime_Metadata()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId: "runtime-tenant-a-1",
                    tenantId: "tenant-a",
                    tenantGroupId: "tenant-group-a"));

            await registry.HeartbeatAsync(
                runtimeInstanceId: "runtime-tenant-a-1",
                queuedRunCount: 2,
                runningRunCount: 1,
                activeRunCount: 3,
                availableRunSlots: 1,
                activeWorkerCount: 4,
                availableWorkerCount: 1,
                maxLocalWorkersPerExecution: 2,
                isQueuePaused: false,
                canAcceptRun: true,
                status: AiRuntimeInstanceStatus.Busy);

            var reconciler = CreateReconciler(
                registry,
                new AiRuntimeInstanceHealthReconciliationOptions
                {
                    Enabled = true,
                    StaleHeartbeatThreshold = TimeSpan.Zero,
                    MarkStaleRuntimeUnhealthy = true,
                    IncludeReadyRuntimeInstances = true,
                    IncludeBusyRuntimeInstances = true
                });

            var result = await reconciler.ReconcileAsync();

            var snapshot = await registry.GetAsync("runtime-tenant-a-1");

            Assert.Equal(1, result.ScannedCount);
            Assert.Equal(1, result.MarkedUnhealthyCount);
            Assert.Equal(0, result.IgnoredCount);
            Assert.Single(result.Decisions);

            Assert.Equal(AiRuntimeInstanceStatus.Busy, result.Decisions[0].PreviousStatus);
            Assert.Equal(AiRuntimeInstanceStatus.Unhealthy, result.Decisions[0].NewStatus);
            Assert.True(result.Decisions[0].Changed);

            Assert.NotNull(snapshot);
            Assert.Equal(AiRuntimeInstanceStatus.Unhealthy, snapshot!.Status);
            Assert.False(snapshot.CanAcceptRun);
            Assert.Equal("tenant-a", snapshot.TenantId);
            Assert.Equal("tenant-group-a", snapshot.TenantGroupId);
            Assert.Equal(2, snapshot.QueuedRunCount);
            Assert.Equal(1, snapshot.RunningRunCount);
            Assert.Equal(3, snapshot.ActiveRunCount);
            Assert.Equal(1, snapshot.AvailableRunSlots);
            Assert.Equal(4, snapshot.ActiveWorkerCount);
            Assert.Equal(1, snapshot.AvailableWorkerCount);
            Assert.Equal(2, snapshot.MaxLocalWorkersPerExecution);
            Assert.Equal("production-routing-safety", snapshot.Metadata["scenario"]);
        }

        /// <summary>
        /// Verifies that the health reconciler does not mark fresh runtime instances as unhealthy.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Not_Mark_Fresh_Runtime_Unhealthy()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId: "runtime-tenant-a-1",
                    tenantId: "tenant-a",
                    tenantGroupId: "tenant-group-a"));

            var reconciler = CreateReconciler(
                registry,
                new AiRuntimeInstanceHealthReconciliationOptions
                {
                    Enabled = true,
                    StaleHeartbeatThreshold = TimeSpan.FromMinutes(5),
                    MarkStaleRuntimeUnhealthy = true
                });

            var result = await reconciler.ReconcileAsync();

            var snapshot = await registry.GetAsync("runtime-tenant-a-1");

            Assert.Equal(1, result.ScannedCount);
            Assert.Equal(0, result.MarkedUnhealthyCount);
            Assert.Equal(0, result.IgnoredCount);
            Assert.Single(result.Decisions);

            Assert.Equal(AiRuntimeInstanceStatus.Ready, result.Decisions[0].PreviousStatus);
            Assert.Equal(AiRuntimeInstanceStatus.Ready, result.Decisions[0].NewStatus);
            Assert.Equal("heartbeat-fresh", result.Decisions[0].Reason);
            Assert.False(result.Decisions[0].Changed);

            Assert.NotNull(snapshot);
            Assert.Equal(AiRuntimeInstanceStatus.Ready, snapshot!.Status);
            Assert.True(snapshot.CanAcceptRun);
            Assert.Equal("tenant-a", snapshot.TenantId);
            Assert.Equal("tenant-group-a", snapshot.TenantGroupId);
        }

        /// <summary>
        /// Verifies that the health reconciler ignores runtime instances that are already non-routable.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Ignore_Already_NonRoutable_Runtime_Statuses()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(CreateRegistration("runtime-paused", "tenant-a", "tenant-group-a"));
            await registry.RegisterAsync(CreateRegistration("runtime-draining", "tenant-a", "tenant-group-a"));
            await registry.RegisterAsync(CreateRegistration("runtime-unhealthy", "tenant-a", "tenant-group-a"));
            await registry.RegisterAsync(CreateRegistration("runtime-stopped", "tenant-a", "tenant-group-a"));

            await registry.HeartbeatAsync(
                runtimeInstanceId: "runtime-paused",
                queuedRunCount: 0,
                runningRunCount: 0,
                activeRunCount: 0,
                availableRunSlots: 5,
                activeWorkerCount: 0,
                availableWorkerCount: 5,
                maxLocalWorkersPerExecution: 1,
                isQueuePaused: true,
                canAcceptRun: true,
                status: AiRuntimeInstanceStatus.Paused);

            await registry.MarkDrainingAsync("runtime-draining");
            await registry.MarkUnhealthyAsync("runtime-unhealthy");
            await registry.UnregisterAsync("runtime-stopped");

            var reconciler = CreateReconciler(
                registry,
                new AiRuntimeInstanceHealthReconciliationOptions
                {
                    Enabled = true,
                    StaleHeartbeatThreshold = TimeSpan.Zero,
                    IgnorePausedRuntimeInstances = true,
                    IgnoreDrainingRuntimeInstances = true,
                    IgnoreStoppedRuntimeInstances = true
                });

            var result = await reconciler.ReconcileAsync();

            Assert.Equal(4, result.ScannedCount);
            Assert.Equal(0, result.MarkedUnhealthyCount);
            Assert.Equal(4, result.IgnoredCount);
            Assert.Equal(4, result.Decisions.Count);
            Assert.All(result.Decisions, decision =>
            {
                Assert.Equal("ignored-runtime-status", decision.Reason);
                Assert.False(decision.Changed);
            });

            var paused = await registry.GetAsync("runtime-paused");
            var draining = await registry.GetAsync("runtime-draining");
            var unhealthy = await registry.GetAsync("runtime-unhealthy");
            var stopped = await registry.GetAsync("runtime-stopped");

            Assert.Equal(AiRuntimeInstanceStatus.Paused, paused!.Status);
            Assert.Equal(AiRuntimeInstanceStatus.Draining, draining!.Status);
            Assert.Equal(AiRuntimeInstanceStatus.Unhealthy, unhealthy!.Status);
            Assert.Equal(AiRuntimeInstanceStatus.Stopped, stopped!.Status);

            Assert.False(paused.CanAcceptRun);
            Assert.False(draining.CanAcceptRun);
            Assert.False(unhealthy.CanAcceptRun);
            Assert.False(stopped.CanAcceptRun);
        }

        /// <summary>
        /// Verifies that health reconciliation only protects routing and does not perform execution recovery.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Not_Requeue_Or_Modify_Assigned_Run_Ownership()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();
            var sharedQueue = new InMemoryAiSharedQueue();
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

            await sharedQueue.EnqueueAsync(new AiSharedQueueItem
            {
                SharedRunId = sharedRunId,
                Status = AiSharedQueueItemStatus.Pending,
                ExecutionContextSnapshot = contextSnapshot,
                PipelineKey = "health-reconciler-test",
                Priority = 0,
                EnqueuedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>
                {
                    ["scenario"] = "health-not-recovery"
                }
            });

            var claimed = await sharedQueue.ClaimNextAsync(new AiSharedQueueClaimRequest
            {
                RuntimeInstanceId = runtimeInstanceId,
                WorkerId = "worker-1",
                TenantId = "tenant-a",
                PipelineKey = "health-reconciler-test",
                ClaimTtl = TimeSpan.FromMinutes(5),
                Reason = "test-claim"
            });

            Assert.NotNull(claimed);
            Assert.False(string.IsNullOrWhiteSpace(claimed!.ClaimToken));

            await sharedQueue.MarkDispatchedAsync(
                sharedRunId,
                claimed.ClaimToken!,
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
                    ["scenario"] = "health-not-recovery"
                }
            });

            await runExecutionIndex.MarkStartedAsync(
                localRunId,
                executionId);

            var reconciler = CreateReconciler(
                registry,
                new AiRuntimeInstanceHealthReconciliationOptions
                {
                    Enabled = true,
                    StaleHeartbeatThreshold = TimeSpan.Zero,
                    MarkStaleRuntimeUnhealthy = true,
                    IncludeReadyRuntimeInstances = true,
                    IncludeBusyRuntimeInstances = true
                });

            var result = await reconciler.ReconcileAsync();

            var runtime = await registry.GetAsync(runtimeInstanceId);
            var queueItem = await sharedQueue.GetAsync(sharedRunId);
            var activeQueueItems = await sharedQueue.ListAsync();
            var terminalQueueItems = await sharedQueue.ListAsync(includeTerminal: true);
            var unfinishedRuns = await runExecutionIndex.ListUnfinishedByRuntimeInstanceAsync(runtimeInstanceId);

            Assert.Equal(1, result.ScannedCount);
            Assert.Equal(1, result.MarkedUnhealthyCount);
            Assert.Equal(0, result.IgnoredCount);

            Assert.NotNull(runtime);
            Assert.Equal(AiRuntimeInstanceStatus.Unhealthy, runtime!.Status);
            Assert.False(runtime.CanAcceptRun);

            Assert.NotNull(queueItem);
            Assert.Equal(AiSharedQueueItemStatus.Dispatched, queueItem!.Status);
            Assert.Equal(runtimeInstanceId, queueItem.ClaimedByRuntimeInstanceId);
            Assert.Equal("worker-1", queueItem.ClaimedByWorkerId);
            Assert.Equal(claimed.ClaimToken, queueItem.ClaimToken);
            Assert.Equal("test-dispatch", queueItem.Reason);
            Assert.Equal("tenant-a", queueItem.ExecutionContextSnapshot.TenantId);
            Assert.Equal("tenant-group-a", queueItem.ExecutionContextSnapshot.TenantGroupId);

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
            Assert.Equal("tenant-a", unfinished.ExecutionContextSnapshot.TenantId);
            Assert.Equal("tenant-group-a", unfinished.ExecutionContextSnapshot.TenantGroupId);
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
        /// Creates a runtime instance health reconciler.
        /// </summary>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="options">The reconciliation options.</param>
        /// <returns>The runtime instance health reconciler.</returns>
        private static AiRuntimeInstanceHealthReconciler CreateReconciler(
            IAiRuntimeInstanceRegistry registry,
            AiRuntimeInstanceHealthReconciliationOptions options)
        {
            return new AiRuntimeInstanceHealthReconciler(
                registry,
                Options.Create(options));
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
                    ["scenario"] = "production-routing-safety"
                }
            };
        }
    }
}