using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Health;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Redis;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Health;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Ownership;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue.Redis;
using StackExchange.Redis;

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

            IAiRuntimeExecutionRecoveryTransitionService transitionService =
                new AiRuntimeExecutionRecoveryTransitionService(sharedQueue);

            var recoveryReconciler = new AiRuntimeExecutionRecoveryReconciler(
                registry,
                runExecutionIndex,
                ownershipResolver,
                transitionService,
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
            Assert.Equal("dry-run-requeue-shared-run", decision.Action);
            Assert.Equal("dry-run-runtime-execution-recovery", decision.Reason);
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
        /// Verifies that recovery reconciliation can requeue a dispatched shared queue item
        /// when recovery mutation is explicitly enabled.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Requeue_Dispatched_Shared_Run_When_Recovery_Mutation_Is_Enabled()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();
            var sharedQueue = new InMemoryAiSharedQueue();
            var sharedRunStore = new InMemoryAiSharedRunStore();
            var runExecutionIndex = new InMemoryAiRuntimeRunExecutionIndex();

            const string runtimeInstanceId = "runtime-tenant-a-1";
            const string sharedRunId = "shared-run-recovery-mutation-1";
            const string localRunId = "local-run-recovery-mutation-1";
            const string executionId = "execution-recovery-mutation-1";

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
                PipelineKey = "runtime-recovery-mutation-test",
                CorrelationId = "correlation-runtime-recovery-mutation",
                RequestedBy = "test",
                Source = "integration-test",
                Reason = "created-for-runtime-recovery-mutation",
                SubmittedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>
                {
                    ["scenario"] = "runtime-recovery-mutation"
                }
            });

            await sharedQueue.EnqueueAsync(new AiSharedQueueItem
            {
                SharedRunId = sharedRunId,
                Status = AiSharedQueueItemStatus.Pending,
                ExecutionContextSnapshot = contextSnapshot,
                PipelineKey = "runtime-recovery-mutation-test",
                Priority = 0,
                EnqueuedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>
                {
                    ["scenario"] = "runtime-recovery-mutation"
                }
            });

            var claimed = await sharedQueue.ClaimNextAsync(new AiSharedQueueClaimRequest
            {
                RuntimeInstanceId = runtimeInstanceId,
                WorkerId = "worker-1",
                TenantId = "tenant-a",
                PipelineKey = "runtime-recovery-mutation-test",
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
                    ["scenario"] = "runtime-recovery-mutation"
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

            IAiRuntimeExecutionRecoveryTransitionService transitionService =
                new AiRuntimeExecutionRecoveryTransitionService(sharedQueue);

            var recoveryReconciler = new AiRuntimeExecutionRecoveryReconciler(
                registry,
                runExecutionIndex,
                ownershipResolver,
                transitionService,
                Options.Create(new AiRuntimeExecutionRecoveryReconciliationOptions
                {
                    Enabled = true,
                    IncludeUnhealthyRuntimeInstances = true,
                    IncludeStoppedRuntimeInstances = false,
                    IncludeDrainingRuntimeInstances = false,
                    RequeueUnfinishedRuns = true,
                    DryRun = false
                }));

            var recoveryResult = await recoveryReconciler.ReconcileAsync();

            var runtime = await registry.GetAsync(runtimeInstanceId);
            var sharedRun = await sharedRunStore.GetAsync(sharedRunId);
            var queueItem = await sharedQueue.GetAsync(sharedRunId);
            var activeQueueItems = await sharedQueue.ListAsync();
            var allQueueItems = await sharedQueue.ListAsync(includeTerminal: true);
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
            Assert.Equal(1, recoveryResult.RecoveredRunCount);

            var decision = Assert.Single(recoveryResult.Decisions);
            Assert.Equal(runtimeInstanceId, decision.RuntimeInstanceId);
            Assert.Equal(localRunId, decision.LocalRunId);
            Assert.Equal(executionId, decision.ExecutionId);
            Assert.Equal(sharedRunId, decision.SharedRunId);
            Assert.Equal("tenant-a", decision.TenantId);
            Assert.Equal("tenant-group-a", decision.TenantGroupId);
            Assert.Equal("requeue-shared-run", decision.Action);
            Assert.Equal("runtime-execution-recovery-requeue", decision.Reason);
            Assert.True(decision.Changed);

            Assert.NotNull(sharedRun);
            Assert.Equal(AiSharedRunStatus.Dispatched, sharedRun!.Status);
            Assert.Equal(runtimeInstanceId, sharedRun.AssignedRuntimeInstanceId);
            Assert.Equal(localRunId, sharedRun.LocalRunId);
            Assert.Equal(executionId, sharedRun.ExecutionId);

            Assert.NotNull(queueItem);
            Assert.Equal(AiSharedQueueItemStatus.Pending, queueItem!.Status);
            Assert.Null(queueItem.ClaimedByRuntimeInstanceId);
            Assert.Null(queueItem.ClaimedByWorkerId);
            Assert.Null(queueItem.ClaimToken);
            Assert.Null(queueItem.ClaimedAtUtc);
            Assert.Null(queueItem.ClaimExpiresAtUtc);
            Assert.Equal("runtime-execution-recovery-requeue", queueItem.Reason);

            var activeItem = Assert.Single(activeQueueItems);
            Assert.Equal(sharedRunId, activeItem.SharedRunId);
            Assert.Equal(AiSharedQueueItemStatus.Pending, activeItem.Status);

            var allItem = Assert.Single(allQueueItems);
            Assert.Equal(sharedRunId, allItem.SharedRunId);
            Assert.Equal(AiSharedQueueItemStatus.Pending, allItem.Status);

            Assert.Empty(unfinishedRuns);

            Assert.NotNull(indexEntry);
            Assert.Equal(localRunId, indexEntry!.RunId);
            Assert.Equal(executionId, indexEntry.ExecutionId);
            Assert.Equal(runtimeInstanceId, indexEntry.RuntimeInstanceId);
            Assert.Equal("requeued-for-recovery", indexEntry.Status);
            Assert.Equal("runtime-execution-recovery-requeue", indexEntry.FailureReason);
            Assert.NotNull(indexEntry.CompletedAtUtc);
        }

        /// <summary>
        /// Verifies that recovery reconciliation does not requeue the same shared run twice.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Not_Requeue_Same_Shared_Run_Twice_When_Already_Recovered()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();
            var sharedQueue = new InMemoryAiSharedQueue();
            var sharedRunStore = new InMemoryAiSharedRunStore();
            var runExecutionIndex = new InMemoryAiRuntimeRunExecutionIndex();

            const string runtimeInstanceId = "runtime-tenant-a-1";
            const string sharedRunId = "shared-run-recovery-idempotence-1";
            const string localRunId = "local-run-recovery-idempotence-1";
            const string executionId = "execution-recovery-idempotence-1";

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
                PipelineKey = "runtime-recovery-idempotence-test",
                CorrelationId = "correlation-runtime-recovery-idempotence",
                RequestedBy = "test",
                Source = "integration-test",
                Reason = "created-for-runtime-recovery-idempotence",
                SubmittedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>
                {
                    ["scenario"] = "runtime-recovery-idempotence"
                }
            });

            await sharedQueue.EnqueueAsync(new AiSharedQueueItem
            {
                SharedRunId = sharedRunId,
                Status = AiSharedQueueItemStatus.Pending,
                ExecutionContextSnapshot = contextSnapshot,
                PipelineKey = "runtime-recovery-idempotence-test",
                Priority = 0,
                EnqueuedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>
                {
                    ["scenario"] = "runtime-recovery-idempotence"
                }
            });

            var claimed = await sharedQueue.ClaimNextAsync(new AiSharedQueueClaimRequest
            {
                RuntimeInstanceId = runtimeInstanceId,
                WorkerId = "worker-1",
                TenantId = "tenant-a",
                PipelineKey = "runtime-recovery-idempotence-test",
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
                    ["scenario"] = "runtime-recovery-idempotence"
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

            await healthReconciler.ReconcileAsync();

            IAiSharedRunOwnershipResolver ownershipResolver =
                new AiSharedRunOwnershipResolver(
                    sharedQueue,
                    sharedRunStore);

            IAiRuntimeExecutionRecoveryTransitionService transitionService =
                new AiRuntimeExecutionRecoveryTransitionService(sharedQueue);

            var recoveryReconciler = new AiRuntimeExecutionRecoveryReconciler(
                registry,
                runExecutionIndex,
                ownershipResolver,
                transitionService,
                Options.Create(new AiRuntimeExecutionRecoveryReconciliationOptions
                {
                    Enabled = true,
                    IncludeUnhealthyRuntimeInstances = true,
                    IncludeStoppedRuntimeInstances = false,
                    IncludeDrainingRuntimeInstances = false,
                    RequeueUnfinishedRuns = true,
                    DryRun = false
                }));

            var firstRecoveryResult = await recoveryReconciler.ReconcileAsync();
            var secondRecoveryResult = await recoveryReconciler.ReconcileAsync();

            var queueItem = await sharedQueue.GetAsync(sharedRunId);
            var activeQueueItems = await sharedQueue.ListAsync();
            var allQueueItems = await sharedQueue.ListAsync(includeTerminal: true);
            var unfinishedRuns = await runExecutionIndex.ListUnfinishedByRuntimeInstanceAsync(runtimeInstanceId);
            var indexEntry = await runExecutionIndex.GetAsync(localRunId);

            Assert.Equal(1, firstRecoveryResult.DiscoveredUnfinishedRunCount);
            Assert.Equal(1, firstRecoveryResult.RecoveredRunCount);

            var firstDecision = Assert.Single(firstRecoveryResult.Decisions);
            Assert.Equal(sharedRunId, firstDecision.SharedRunId);
            Assert.Equal("requeue-shared-run", firstDecision.Action);
            Assert.Equal("runtime-execution-recovery-requeue", firstDecision.Reason);
            Assert.True(firstDecision.Changed);

            Assert.Equal(0, secondRecoveryResult.DiscoveredUnfinishedRunCount);
            Assert.Equal(0, secondRecoveryResult.RecoveredRunCount);

            var secondDecision = Assert.Single(secondRecoveryResult.Decisions);
            Assert.Equal(runtimeInstanceId, secondDecision.RuntimeInstanceId);
            Assert.Null(secondDecision.LocalRunId);
            Assert.Null(secondDecision.ExecutionId);
            Assert.Null(secondDecision.SharedRunId);
            Assert.Equal("none", secondDecision.Action);
            Assert.Equal("no-unfinished-runtime-runs", secondDecision.Reason);
            Assert.False(secondDecision.Changed);

            Assert.NotNull(queueItem);
            Assert.Equal(AiSharedQueueItemStatus.Pending, queueItem!.Status);
            Assert.Null(queueItem.ClaimedByRuntimeInstanceId);
            Assert.Null(queueItem.ClaimedByWorkerId);
            Assert.Null(queueItem.ClaimToken);
            Assert.Equal("runtime-execution-recovery-requeue", queueItem.Reason);

            var activeItem = Assert.Single(activeQueueItems);
            Assert.Equal(sharedRunId, activeItem.SharedRunId);
            Assert.Equal(AiSharedQueueItemStatus.Pending, activeItem.Status);

            var allItem = Assert.Single(allQueueItems);
            Assert.Equal(sharedRunId, allItem.SharedRunId);
            Assert.Equal(AiSharedQueueItemStatus.Pending, allItem.Status);

            Assert.Empty(unfinishedRuns);

            Assert.NotNull(indexEntry);
            Assert.Equal(localRunId, indexEntry!.RunId);
            Assert.Equal(executionId, indexEntry.ExecutionId);
            Assert.Equal(runtimeInstanceId, indexEntry.RuntimeInstanceId);
            Assert.Equal("requeued-for-recovery", indexEntry.Status);
            Assert.Equal("runtime-execution-recovery-requeue", indexEntry.FailureReason);
            Assert.NotNull(indexEntry.CompletedAtUtc);
        }

        /// <summary>
        /// Verifies that recovery reconciliation can requeue a dispatched Redis shared queue item
        /// when recovery mutation is explicitly enabled.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Requeue_Dispatched_Redis_Shared_Run_When_Recovery_Mutation_Is_Enabled()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();
            var sharedRunStore = new InMemoryAiSharedRunStore();
            var runExecutionIndex = new InMemoryAiRuntimeRunExecutionIndex();

            var keyPrefix = $"test:runtime-recovery-redis:{Guid.NewGuid():N}";
            await using var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");

            try
            {
                var sharedQueue = new RedisAiSharedQueue(
                    redis,
                    Options.Create(new RedisAiSharedQueueOptions
                    {
                        KeyPrefix = keyPrefix,
                        ListScanLimit = 100
                    }),
                    new StaticAiControlPlaneIdResolver("test-control-plane"));

                const string runtimeInstanceId = "runtime-tenant-a-redis-1";
                const string sharedRunId = "shared-run-recovery-redis-mutation-1";
                const string localRunId = "local-run-recovery-redis-mutation-1";
                const string executionId = "execution-recovery-redis-mutation-1";

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
                    PipelineKey = "runtime-recovery-redis-mutation-test",
                    CorrelationId = "correlation-runtime-recovery-redis-mutation",
                    RequestedBy = "test",
                    Source = "integration-test",
                    Reason = "created-for-runtime-recovery-redis-mutation",
                    SubmittedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Metadata = new Dictionary<string, string>
                    {
                        ["scenario"] = "runtime-recovery-redis-mutation"
                    }
                });

                await sharedQueue.EnqueueAsync(new AiSharedQueueItem
                {
                    SharedRunId = sharedRunId,
                    Status = AiSharedQueueItemStatus.Pending,
                    ExecutionContextSnapshot = contextSnapshot,
                    PipelineKey = "runtime-recovery-redis-mutation-test",
                    Priority = 0,
                    EnqueuedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Metadata = new Dictionary<string, string>
                    {
                        ["scenario"] = "runtime-recovery-redis-mutation"
                    }
                });

                var claimed = await sharedQueue.ClaimNextAsync(new AiSharedQueueClaimRequest
                {
                    RuntimeInstanceId = runtimeInstanceId,
                    WorkerId = "worker-1",
                    TenantId = "tenant-a",
                    PipelineKey = "runtime-recovery-redis-mutation-test",
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
                        ["scenario"] = "runtime-recovery-redis-mutation"
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

                await healthReconciler.ReconcileAsync();

                IAiSharedRunOwnershipResolver ownershipResolver =
                    new AiSharedRunOwnershipResolver(
                        sharedQueue,
                        sharedRunStore);

                IAiRuntimeExecutionRecoveryTransitionService transitionService =
                    new AiRuntimeExecutionRecoveryTransitionService(sharedQueue);

                var recoveryReconciler = new AiRuntimeExecutionRecoveryReconciler(
                    registry,
                    runExecutionIndex,
                    ownershipResolver,
                    transitionService,
                    Options.Create(new AiRuntimeExecutionRecoveryReconciliationOptions
                    {
                        Enabled = true,
                        IncludeUnhealthyRuntimeInstances = true,
                        IncludeStoppedRuntimeInstances = false,
                        IncludeDrainingRuntimeInstances = false,
                        RequeueUnfinishedRuns = true,
                        DryRun = false
                    }));

                var recoveryResult = await recoveryReconciler.ReconcileAsync();

                var queueItem = await sharedQueue.GetAsync(sharedRunId);
                var activeQueueItems = await sharedQueue.ListAsync();
                var allQueueItems = await sharedQueue.ListAsync(includeTerminal: true);
                var unfinishedRuns = await runExecutionIndex.ListUnfinishedByRuntimeInstanceAsync(runtimeInstanceId);
                var indexEntry = await runExecutionIndex.GetAsync(localRunId);

                Assert.Equal(1, recoveryResult.ScannedRuntimeInstanceCount);
                Assert.Equal(0, recoveryResult.IgnoredRuntimeInstanceCount);
                Assert.Equal(1, recoveryResult.DiscoveredUnfinishedRunCount);
                Assert.Equal(1, recoveryResult.RecoveredRunCount);

                var decision = Assert.Single(recoveryResult.Decisions);
                Assert.Equal(runtimeInstanceId, decision.RuntimeInstanceId);
                Assert.Equal(localRunId, decision.LocalRunId);
                Assert.Equal(executionId, decision.ExecutionId);
                Assert.Equal(sharedRunId, decision.SharedRunId);
                Assert.Equal("tenant-a", decision.TenantId);
                Assert.Equal("tenant-group-a", decision.TenantGroupId);
                Assert.Equal("requeue-shared-run", decision.Action);
                Assert.Equal("runtime-execution-recovery-requeue", decision.Reason);
                Assert.True(decision.Changed);

                Assert.NotNull(queueItem);
                Assert.Equal(AiSharedQueueItemStatus.Pending, queueItem!.Status);
                Assert.Null(queueItem.ClaimedByRuntimeInstanceId);
                Assert.Null(queueItem.ClaimedByWorkerId);
                Assert.Null(queueItem.ClaimToken);
                Assert.Null(queueItem.ClaimedAtUtc);
                Assert.Null(queueItem.ClaimExpiresAtUtc);
                Assert.Equal("runtime-execution-recovery-requeue", queueItem.Reason);

                var activeItem = Assert.Single(activeQueueItems);
                Assert.Equal(sharedRunId, activeItem.SharedRunId);
                Assert.Equal(AiSharedQueueItemStatus.Pending, activeItem.Status);

                var allItem = Assert.Single(allQueueItems);
                Assert.Equal(sharedRunId, allItem.SharedRunId);
                Assert.Equal(AiSharedQueueItemStatus.Pending, allItem.Status);

                Assert.Empty(unfinishedRuns);

                Assert.NotNull(indexEntry);
                Assert.Equal(localRunId, indexEntry!.RunId);
                Assert.Equal(executionId, indexEntry.ExecutionId);
                Assert.Equal(runtimeInstanceId, indexEntry.RuntimeInstanceId);
                Assert.Equal("requeued-for-recovery", indexEntry.Status);
                Assert.Equal("runtime-execution-recovery-requeue", indexEntry.FailureReason);
                Assert.NotNull(indexEntry.CompletedAtUtc);
            }
            finally
            {
                var database = redis.GetDatabase();

                var server = redis.GetServer(
                    redis.GetEndPoints().First());

                var keys = server.Keys(
                        database: database.Database,
                        pattern: $"{keyPrefix}*")
                    .ToArray();

                if (keys.Length > 0)
                {
                    await database.KeyDeleteAsync(keys);
                }

                await redis.CloseAsync();
            }
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

        /// <summary>
        /// Static control plane identifier resolver for Redis integration tests.
        /// </summary>
        private sealed class StaticAiControlPlaneIdResolver : IAiControlPlaneIdResolver
        {
            private readonly string controlPlaneId;

            /// <summary>
            /// Initializes a new instance of the <see cref="StaticAiControlPlaneIdResolver"/> class.
            /// </summary>
            /// <param name="controlPlaneId">The control plane identifier.</param>
            public StaticAiControlPlaneIdResolver(string controlPlaneId)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);

                this.controlPlaneId = controlPlaneId;
            }

            /// <inheritdoc />
            public Task<string> ResolveAsync(
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(controlPlaneId);
            }
        }
    }
}