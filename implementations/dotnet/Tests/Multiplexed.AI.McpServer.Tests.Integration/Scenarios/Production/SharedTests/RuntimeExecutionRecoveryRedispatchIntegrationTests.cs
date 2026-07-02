using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
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
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Ownership;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue;
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue.Redis;
using StackExchange.Redis;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.SharedTests
{
    /// <summary>
    /// Production-style tests proving that an in-flight execution assigned to a failed runtime
    /// can be recovered, requeued, and redispatched to a healthy runtime.
    /// </summary>
    public sealed class RuntimeExecutionRecoveryRedispatchIntegrationTests
    {
        /// <summary>
        /// Verifies that an in-flight execution owned by a failed runtime can be recovered
        /// from durable shared state and redispatched to another healthy runtime.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Recover_InFlight_Execution_And_Redispatch_To_Healthy_Runtime()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();
            var sharedQueue = new InMemoryAiSharedQueue();
            var sharedRunStore = new InMemoryAiSharedRunStore();
            var runExecutionIndex = new InMemoryAiRuntimeRunExecutionIndex();

            await ExecuteRecoveryRedispatchScenarioAsync(
                registry,
                sharedQueue,
                sharedRunStore,
                runExecutionIndex);
        }

        /// <summary>
        /// Verifies that an in-flight execution owned by a failed runtime can be recovered
        /// and redispatched when the shared queue is backed by Redis.
        /// </summary>
        [Fact]
        public async Task ReconcileAsync_Should_Recover_InFlight_Redis_Execution_And_Redispatch_To_Healthy_Runtime()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();
            var sharedRunStore = new InMemoryAiSharedRunStore();
            var runExecutionIndex = new InMemoryAiRuntimeRunExecutionIndex();

            var keyPrefix = $"test:runtime-recovery-redispatch:{Guid.NewGuid():N}";
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

                await ExecuteRecoveryRedispatchScenarioAsync(
                    registry,
                    sharedQueue,
                    sharedRunStore,
                    runExecutionIndex);
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
        /// Executes the shared recovery redispatch scenario against the supplied queue implementation.
        /// </summary>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="sharedQueue">The shared queue.</param>
        /// <param name="sharedRunStore">The shared run store.</param>
        /// <param name="runExecutionIndex">The runtime run execution index.</param>
        private static async Task ExecuteRecoveryRedispatchScenarioAsync(
            IAiRuntimeInstanceRegistry registry,
            IAiSharedQueue sharedQueue,
            IAiSharedRunStore sharedRunStore,
            IAiRuntimeRunExecutionIndex runExecutionIndex)
        {
            const string runtimeAId = "runtime-a";
            const string runtimeBId = "runtime-b";
            const string sharedRunId = "shared-run-inflight-recovery-1";
            const string runtimeALocalRunId = "local-run-runtime-a-1";
            const string runtimeAExecutionId = "execution-runtime-a-1";
            const string runtimeBLocalRunId = "local-run-runtime-b-1";
            const string runtimeBExecutionId = "execution-runtime-b-1";
            const string pipelineKey = "runtime-recovery-redispatch-test";

            var contextSnapshot = CreateExecutionContextSnapshot(
                tenantId: "tenant-a",
                tenantGroupId: "tenant-group-a");

            await registry.RegisterAsync(
                CreateRegistration(
                    runtimeAId,
                    tenantId: "tenant-a",
                    tenantGroupId: "tenant-group-a"));

            await registry.RegisterAsync(
                CreateRegistration(
                    runtimeBId,
                    tenantId: "tenant-a",
                    tenantGroupId: "tenant-group-a"));

            await sharedRunStore.CreateAsync(new AiSharedRunRecord
            {
                SharedRunId = sharedRunId,
                Status = AiSharedRunStatus.QueuedGlobally,
                RunRequest = CreateRunRequest(
                    contextSnapshot,
                    pipelineKey),
                ExecutionContextSnapshot = contextSnapshot,
                PipelineKey = pipelineKey,
                CorrelationId = "correlation-runtime-recovery-redispatch",
                RequestedBy = "test",
                Source = "integration-test",
                Reason = "created-for-runtime-recovery-redispatch",
                SubmittedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>
                {
                    ["scenario"] = "runtime-recovery-redispatch"
                }
            });

            await sharedQueue.EnqueueAsync(new AiSharedQueueItem
            {
                SharedRunId = sharedRunId,
                Status = AiSharedQueueItemStatus.Pending,
                ExecutionContextSnapshot = contextSnapshot,
                PipelineKey = pipelineKey,
                Priority = 0,
                EnqueuedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>
                {
                    ["scenario"] = "runtime-recovery-redispatch"
                }
            });

            var runtimeAClaim = await sharedQueue.ClaimNextAsync(new AiSharedQueueClaimRequest
            {
                RuntimeInstanceId = runtimeAId,
                WorkerId = "worker-runtime-a-1",
                TenantId = "tenant-a",
                PipelineKey = pipelineKey,
                ClaimTtl = TimeSpan.FromMinutes(5),
                Reason = "runtime-a-claim"
            });

            Assert.NotNull(runtimeAClaim);
            Assert.Equal(sharedRunId, runtimeAClaim!.SharedRunId);
            Assert.False(string.IsNullOrWhiteSpace(runtimeAClaim.ClaimToken));

            await sharedQueue.MarkDispatchedAsync(
                sharedRunId,
                runtimeAClaim.ClaimToken!,
                reason: "runtime-a-dispatch");

            await sharedRunStore.MarkDispatchedAsync(
                sharedRunId,
                runtimeAId,
                runtimeALocalRunId,
                runtimeAExecutionId,
                reason: "runtime-a-dispatch");

            await runExecutionIndex.RegisterQueuedAsync(new AiRuntimeRunExecutionIndexEntry
            {
                RunId = runtimeALocalRunId,
                ExecutionId = runtimeAExecutionId,
                RuntimeInstanceId = runtimeAId,
                Status = "queued",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ExecutionContextSnapshot = contextSnapshot,
                Metadata = new Dictionary<string, string>
                {
                    ["scenario"] = "runtime-a-inflight-execution"
                }
            });

            await runExecutionIndex.MarkStartedAsync(
                runtimeALocalRunId,
                runtimeAExecutionId);

            // Simulates runtime-a crashing while execution-a is already running.
            await registry.MarkUnhealthyAsync(runtimeAId);

            IAiSharedRunOwnershipResolver ownershipResolver =
                new AiSharedRunOwnershipResolver(
                    sharedQueue,
                    sharedRunStore);

            IAiRuntimeExecutionRecoveryTransitionService transitionService =
                new AiRuntimeExecutionRecoveryTransitionService(
                    sharedQueue,
                    runExecutionIndex);

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

            var runtimeAAfterRecovery = await registry.GetAsync(runtimeAId);
            var runtimeBBeforeRedispatch = await registry.GetAsync(runtimeBId);
            var queueItemAfterRecovery = await sharedQueue.GetAsync(sharedRunId);
            var sharedRunAfterRecovery = await sharedRunStore.GetAsync(sharedRunId);
            var runtimeAIndexEntryAfterRecovery = await runExecutionIndex.GetAsync(runtimeALocalRunId);
            var runtimeAUnfinishedAfterRecovery = await runExecutionIndex.ListUnfinishedByRuntimeInstanceAsync(runtimeAId);

            Assert.NotNull(runtimeAAfterRecovery);
            Assert.Equal(AiRuntimeInstanceStatus.Unhealthy, runtimeAAfterRecovery!.Status);
            Assert.False(runtimeAAfterRecovery.CanAcceptRun);

            Assert.NotNull(runtimeBBeforeRedispatch);
            Assert.NotEqual(AiRuntimeInstanceStatus.Unhealthy, runtimeBBeforeRedispatch!.Status);
            Assert.True(runtimeBBeforeRedispatch.CanAcceptRun);

            Assert.Equal(1, recoveryResult.ScannedRuntimeInstanceCount);
            Assert.Equal(1, recoveryResult.IgnoredRuntimeInstanceCount);
            Assert.Equal(1, recoveryResult.DiscoveredUnfinishedRunCount);
            Assert.Equal(1, recoveryResult.RecoveredRunCount);

            Assert.Contains(
                recoveryResult.Decisions,
                decision =>
                    decision.RuntimeInstanceId == runtimeAId &&
                    decision.LocalRunId == runtimeALocalRunId &&
                    decision.ExecutionId == runtimeAExecutionId &&
                    decision.SharedRunId == sharedRunId &&
                    decision.Action == "requeue-shared-run" &&
                    decision.Reason.StartsWith(
                        "transitionReason=runtime-execution-recovery-requeue",
                        StringComparison.Ordinal) &&
                    decision.Changed);

            Assert.Contains(
                recoveryResult.Decisions,
                decision =>
                    decision.RuntimeInstanceId == runtimeBId &&
                    decision.Action == "ignore-runtime-instance" &&
                    decision.Reason == "runtime-status-not-included" &&
                    !decision.Changed);

            Assert.NotNull(queueItemAfterRecovery);
            Assert.Equal(AiSharedQueueItemStatus.Pending, queueItemAfterRecovery!.Status);
            Assert.Null(queueItemAfterRecovery.ClaimToken);
            Assert.Null(queueItemAfterRecovery.ClaimedByRuntimeInstanceId);
            Assert.Null(queueItemAfterRecovery.ClaimedByWorkerId);
            Assert.Null(queueItemAfterRecovery.ClaimedAtUtc);
            Assert.Null(queueItemAfterRecovery.ClaimExpiresAtUtc);
            Assert.Equal("runtime-execution-recovery-requeue", queueItemAfterRecovery.Reason);

            Assert.NotNull(sharedRunAfterRecovery);
            Assert.Equal(AiSharedRunStatus.Dispatched, sharedRunAfterRecovery!.Status);
            Assert.Equal(runtimeAId, sharedRunAfterRecovery.AssignedRuntimeInstanceId);
            Assert.Equal(runtimeALocalRunId, sharedRunAfterRecovery.LocalRunId);
            Assert.Equal(runtimeAExecutionId, sharedRunAfterRecovery.ExecutionId);

            Assert.NotNull(runtimeAIndexEntryAfterRecovery);
            Assert.Equal(runtimeALocalRunId, runtimeAIndexEntryAfterRecovery!.RunId);
            Assert.Equal(runtimeAExecutionId, runtimeAIndexEntryAfterRecovery.ExecutionId);
            Assert.Equal(runtimeAId, runtimeAIndexEntryAfterRecovery.RuntimeInstanceId);
            Assert.Equal("requeued-for-recovery", runtimeAIndexEntryAfterRecovery.Status);
            Assert.Equal("runtime-execution-recovery-requeue", runtimeAIndexEntryAfterRecovery.FailureReason);
            Assert.NotNull(runtimeAIndexEntryAfterRecovery.CompletedAtUtc);

            Assert.Empty(runtimeAUnfinishedAfterRecovery);

            var runtimeBClaim = await sharedQueue.ClaimNextAsync(new AiSharedQueueClaimRequest
            {
                RuntimeInstanceId = runtimeBId,
                WorkerId = "worker-runtime-b-1",
                TenantId = "tenant-a",
                PipelineKey = pipelineKey,
                ClaimTtl = TimeSpan.FromMinutes(5),
                Reason = "runtime-b-claim-after-recovery"
            });

            Assert.NotNull(runtimeBClaim);
            Assert.Equal(sharedRunId, runtimeBClaim!.SharedRunId);
            Assert.False(string.IsNullOrWhiteSpace(runtimeBClaim.ClaimToken));

            await sharedQueue.MarkDispatchedAsync(
                sharedRunId,
                runtimeBClaim.ClaimToken!,
                reason: "runtime-b-dispatch-after-recovery");

            await sharedRunStore.MarkDispatchedAsync(
                sharedRunId,
                runtimeBId,
                runtimeBLocalRunId,
                runtimeBExecutionId,
                reason: "runtime-b-dispatch-after-recovery");

            await runExecutionIndex.RegisterQueuedAsync(new AiRuntimeRunExecutionIndexEntry
            {
                RunId = runtimeBLocalRunId,
                ExecutionId = runtimeBExecutionId,
                RuntimeInstanceId = runtimeBId,
                Status = "queued",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ExecutionContextSnapshot = contextSnapshot,
                Metadata = new Dictionary<string, string>
                {
                    ["scenario"] = "runtime-b-recovered-execution"
                }
            });

            await runExecutionIndex.MarkStartedAsync(
                runtimeBLocalRunId,
                runtimeBExecutionId);

            await runExecutionIndex.MarkCompletedAsync(
                runtimeBLocalRunId,
                runtimeBExecutionId);

            var queueItemAfterRedispatch = await sharedQueue.GetAsync(sharedRunId);
            var sharedRunAfterRedispatch = await sharedRunStore.GetAsync(sharedRunId);
            var runtimeAIndexEntryFinal = await runExecutionIndex.GetAsync(runtimeALocalRunId);
            var runtimeBIndexEntryFinal = await runExecutionIndex.GetAsync(runtimeBLocalRunId);
            var runtimeAUnfinishedFinal = await runExecutionIndex.ListUnfinishedByRuntimeInstanceAsync(runtimeAId);
            var runtimeBUnfinishedFinal = await runExecutionIndex.ListUnfinishedByRuntimeInstanceAsync(runtimeBId);

            Assert.NotNull(queueItemAfterRedispatch);
            Assert.Equal(AiSharedQueueItemStatus.Dispatched, queueItemAfterRedispatch!.Status);
            Assert.Equal(runtimeBId, queueItemAfterRedispatch.ClaimedByRuntimeInstanceId);
            Assert.Equal("worker-runtime-b-1", queueItemAfterRedispatch.ClaimedByWorkerId);
            Assert.Equal(runtimeBClaim.ClaimToken, queueItemAfterRedispatch.ClaimToken);
            Assert.Equal("runtime-b-dispatch-after-recovery", queueItemAfterRedispatch.Reason);

            Assert.NotNull(sharedRunAfterRedispatch);
            Assert.Equal(AiSharedRunStatus.Dispatched, sharedRunAfterRedispatch!.Status);
            Assert.Equal(runtimeBId, sharedRunAfterRedispatch.AssignedRuntimeInstanceId);
            Assert.Equal(runtimeBLocalRunId, sharedRunAfterRedispatch.LocalRunId);
            Assert.Equal(runtimeBExecutionId, sharedRunAfterRedispatch.ExecutionId);
            Assert.Equal("runtime-b-dispatch-after-recovery", sharedRunAfterRedispatch.Reason);

            Assert.NotNull(runtimeAIndexEntryFinal);
            Assert.Equal("requeued-for-recovery", runtimeAIndexEntryFinal!.Status);
            Assert.Equal("runtime-execution-recovery-requeue", runtimeAIndexEntryFinal.FailureReason);

            Assert.NotNull(runtimeBIndexEntryFinal);
            Assert.Equal(runtimeBLocalRunId, runtimeBIndexEntryFinal!.RunId);
            Assert.Equal(runtimeBExecutionId, runtimeBIndexEntryFinal.ExecutionId);
            Assert.Equal(runtimeBId, runtimeBIndexEntryFinal.RuntimeInstanceId);
            Assert.Equal("completed", runtimeBIndexEntryFinal.Status);
            Assert.NotNull(runtimeBIndexEntryFinal.StartedAtUtc);
            Assert.NotNull(runtimeBIndexEntryFinal.CompletedAtUtc);

            Assert.Empty(runtimeAUnfinishedFinal);
            Assert.Empty(runtimeBUnfinishedFinal);
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
                ProcessId = Random.Shared.Next(10000, 99999),
                WorkerCount = 5,
                QueueCapacity = 20,
                MaxConcurrentRuns = 5,
                RuntimeVersion = "production-test",
                Metadata = new Dictionary<string, string>
                {
                    ["scenario"] = "runtime-recovery-redispatch"
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
        /// <param name="pipelineKey">The pipeline key.</param>
        /// <returns>The pipeline run request.</returns>
        private static AiRuntimePipelineRunRequest CreateRunRequest(
            ExecutionContextSnapshot contextSnapshot,
            string pipelineKey)
        {
            return new AiRuntimePipelineRunRequest
            {
                PipelineName = pipelineKey,
                ExecutionContextSnapshot = contextSnapshot,
                Input = new Dictionary<string, object?>
                {
                    ["scenario"] = "runtime-recovery-redispatch"
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
            public StaticAiControlPlaneIdResolver(
                string controlPlaneId)
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