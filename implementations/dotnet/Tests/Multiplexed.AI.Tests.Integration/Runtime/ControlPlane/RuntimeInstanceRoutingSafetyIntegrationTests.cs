using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue.Redis;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Redis;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue.Redis;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue.Redis;
using Multiplexed.AI.Tests.Fixtures;
using StackExchange.Redis;
using Xunit;

namespace Multiplexed.AI.Tests.Integration.Runtime.ControlPlane
{
    /// <summary>
    /// Cross-store integration tests for runtime routing safety.
    /// </summary>
    public sealed class RuntimeInstanceRoutingSafetyIntegrationTests :
        IAsyncLifetime
    {
        private readonly string keyPrefix =
            $"test:ai:routing-safety:{Guid.NewGuid():N}";

        private readonly string controlPlaneId =
            $"test-control-plane-{Guid.NewGuid():N}";

        private IConnectionMultiplexer? connection;

        /// <inheritdoc />
        public async Task InitializeAsync()
        {
            connection = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
        }

        /// <inheritdoc />
        public async Task DisposeAsync()
        {
            if (connection is null)
            {
                return;
            }

            await DeleteTestKeysAsync(connection).ConfigureAwait(false);
            await connection.CloseAsync().ConfigureAwait(false);
            await connection.DisposeAsync();
        }

        /// <summary>
        /// Verifies that marking a runtime unhealthy stops new routing while preserving
        /// already assigned run ownership across shared queue, shared run store, and runtime run index.
        /// </summary>
        [Fact]
        public async Task Marking_Runtime_Unhealthy_Should_Stop_New_Routing_While_Preserving_Assigned_Run_Ownership()
        {
            var registry = CreateRegistry();
            var sharedQueue = CreateSharedQueue();
            var sharedRunStore = CreateSharedRunStore();
            var runtimeRunIndex = CreateRuntimeRunExecutionIndex();

            var tenantId = "tenant-a";
            var tenantGroupId = "tenant-group-a";
            var runtimeInstanceId = $"runtime-a-{Guid.NewGuid():N}";
            var workerId = $"worker-{Guid.NewGuid():N}";
            var sharedRunId = $"shared-run-{Guid.NewGuid():N}";
            var localRunId = $"local-run-{Guid.NewGuid():N}";
            var executionId = $"execution-{Guid.NewGuid():N}";

            var registeredRuntime = await registry.RegisterAsync(
                new AiRuntimeInstanceRegistration
                {
                    RuntimeInstanceId = runtimeInstanceId,
                    TenantId = tenantId,
                    TenantGroupId = tenantGroupId,
                    ControlPlaneId = controlPlaneId,
                    Role = AiRuntimeInstanceRole.Runtime,
                    HostId = "test-host",
                    RuntimeId = "test-runtime",
                    ControlPlaneHostId = "test-control-plane-host",
                    HostName = "test-host-name",
                    ProcessId = 123,
                    WorkerCount = 10,
                    QueueCapacity = 100,
                    MaxConcurrentRuns = 5,
                    RuntimeVersion = "test",
                    Metadata = new Dictionary<string, string>
                    {
                        ["tenantId"] = tenantId,
                        ["tenantGroupId"] = tenantGroupId
                    },
                    RegisteredAtUtc = DateTimeOffset.UtcNow
                });

            Assert.Equal(AiRuntimeInstanceStatus.Ready, registeredRuntime.Status);
            Assert.True(registeredRuntime.CanAcceptRun);
            Assert.Equal(5, registeredRuntime.AvailableRunSlots);

            await sharedRunStore.CreateAsync(
                CreateSharedRunRecord(
                    sharedRunId,
                    tenantId,
                    AiSharedRunStatus.AssignedToInstance));

            await sharedQueue.EnqueueAsync(
                CreateSharedQueueItem(
                    sharedRunId,
                    tenantId));

            var claimed = await sharedQueue.ClaimNextAsync(
                new AiSharedQueueClaimRequest
                {
                    RuntimeInstanceId = runtimeInstanceId,
                    WorkerId = workerId,
                    TenantId = tenantId,
                    ClaimTtl = TimeSpan.FromSeconds(30),
                    Reason = "claim for runtime routing safety test"
                });

            Assert.NotNull(claimed);
            Assert.Equal(sharedRunId, claimed!.SharedRunId);
            Assert.Equal(AiSharedQueueItemStatus.Claimed, claimed.Status);
            Assert.Equal(runtimeInstanceId, claimed.ClaimedByRuntimeInstanceId);

            var dispatchedQueueItem = await sharedQueue.MarkDispatchedAsync(
                sharedRunId,
                claimed.ClaimToken!,
                reason: "sent to runtime local queue");

            Assert.NotNull(dispatchedQueueItem);
            Assert.Equal(AiSharedQueueItemStatus.Dispatched, dispatchedQueueItem!.Status);
            Assert.Equal(runtimeInstanceId, dispatchedQueueItem.ClaimedByRuntimeInstanceId);

            var dispatchedRun = await sharedRunStore.MarkDispatchedAsync(
                sharedRunId,
                runtimeInstanceId,
                localRunId,
                executionId,
                reason: "dispatch succeeded");

            Assert.NotNull(dispatchedRun);
            Assert.Equal(AiSharedRunStatus.Dispatched, dispatchedRun!.Status);
            Assert.Equal(runtimeInstanceId, dispatchedRun.AssignedRuntimeInstanceId);
            Assert.Equal(localRunId, dispatchedRun.LocalRunId);
            Assert.Equal(executionId, dispatchedRun.ExecutionId);

            await runtimeRunIndex.RegisterQueuedAsync(
                CreateRuntimeRunIndexEntry(
                    sharedRunId,
                    tenantId,
                    runtimeInstanceId));

            await runtimeRunIndex.MarkStartedAsync(
                sharedRunId,
                executionId);

            var unhealthyRuntime = await registry.HeartbeatAsync(
                runtimeInstanceId,
                queuedRunCount: 0,
                runningRunCount: 1,
                activeRunCount: 1,
                availableRunSlots: 5,
                activeWorkerCount: 1,
                availableWorkerCount: 9,
                maxLocalWorkersPerExecution: 2,
                isQueuePaused: false,
                canAcceptRun: true,
                status: AiRuntimeInstanceStatus.Unhealthy);

            Assert.NotNull(unhealthyRuntime);
            Assert.Equal(AiRuntimeInstanceStatus.Unhealthy, unhealthyRuntime!.Status);
            Assert.False(unhealthyRuntime.CanAcceptRun);
            Assert.Equal(tenantId, unhealthyRuntime.TenantId);
            Assert.Equal(tenantGroupId, unhealthyRuntime.TenantGroupId);

            var loadedRuntime = await registry.GetAsync(runtimeInstanceId);

            Assert.NotNull(loadedRuntime);
            Assert.Equal(AiRuntimeInstanceStatus.Unhealthy, loadedRuntime!.Status);
            Assert.False(loadedRuntime.CanAcceptRun);

            var loadedQueueItem = await sharedQueue.GetAsync(sharedRunId);
            var activeQueueItems = await sharedQueue.ListAsync();
            var allQueueItems = await sharedQueue.ListAsync(includeTerminal: true);

            Assert.NotNull(loadedQueueItem);
            Assert.Equal(AiSharedQueueItemStatus.Dispatched, loadedQueueItem!.Status);
            Assert.Equal(runtimeInstanceId, loadedQueueItem.ClaimedByRuntimeInstanceId);
            Assert.DoesNotContain(activeQueueItems, item => item.SharedRunId == sharedRunId);
            Assert.Contains(allQueueItems, item => item.SharedRunId == sharedRunId && item.Status == AiSharedQueueItemStatus.Dispatched);

            var loadedSharedRun = await sharedRunStore.GetAsync(sharedRunId);

            Assert.NotNull(loadedSharedRun);
            Assert.Equal(AiSharedRunStatus.Dispatched, loadedSharedRun!.Status);
            Assert.Equal(runtimeInstanceId, loadedSharedRun.AssignedRuntimeInstanceId);
            Assert.Equal(localRunId, loadedSharedRun.LocalRunId);
            Assert.Equal(executionId, loadedSharedRun.ExecutionId);

            var unfinishedRuns = await runtimeRunIndex.ListUnfinishedByRuntimeInstanceAsync(runtimeInstanceId);

            var unfinishedRun = Assert.Single(
                unfinishedRuns,
                item => item.RunId == sharedRunId);

            Assert.Equal("running", unfinishedRun.Status);
            Assert.Equal(runtimeInstanceId, unfinishedRun.RuntimeInstanceId);
            Assert.Equal(executionId, unfinishedRun.ExecutionId);
            Assert.Equal(tenantId, unfinishedRun.ExecutionContextSnapshot?.TenantId);
        }

        /// <summary>
        /// Creates the Redis-backed runtime instance registry.
        /// </summary>
        private RedisAiRuntimeInstanceRegistry CreateRegistry()
        {
            return new RedisAiRuntimeInstanceRegistry(
                RequireConnection(),
                Options.Create(new AiRuntimeInstanceRegistrationOptions
                {
                    RegistryTtl = TimeSpan.FromMinutes(30)
                }),
                new StaticAiControlPlaneIdResolver(controlPlaneId));
        }

        /// <summary>
        /// Creates the Redis-backed shared queue.
        /// </summary>
        private RedisAiSharedQueue CreateSharedQueue()
        {
            return new RedisAiSharedQueue(
                RequireConnection(),
                Options.Create(new RedisAiSharedQueueOptions
                {
                    KeyPrefix = $"{keyPrefix}:shared-queue",
                    ListScanLimit = 100
                }),
                new StaticAiControlPlaneIdResolver(controlPlaneId));
        }

        /// <summary>
        /// Creates the Redis-backed shared run store.
        /// </summary>
        private RedisAiSharedRunStore CreateSharedRunStore()
        {
            return new RedisAiSharedRunStore(
                RequireConnection(),
                Options.Create(new RedisAiSharedRunStoreOptions
                {
                    KeyPrefix = $"{keyPrefix}:shared-runs",
                    ListScanLimit = 100
                }),
                new StaticAiControlPlaneIdResolver(controlPlaneId));
        }

        /// <summary>
        /// Creates the Redis-backed runtime run execution index.
        /// </summary>
        private RedisAiRuntimeRunExecutionIndex CreateRuntimeRunExecutionIndex()
        {
            return new RedisAiRuntimeRunExecutionIndex(
                RequireConnection(),
                Options.Create(new RedisAiRuntimeRunExecutionIndexOptions
                {
                    KeyPrefix = $"{keyPrefix}:runtime-run-index",
                    EnableRecordExpiration = true,
                    RecordExpiration = TimeSpan.FromMinutes(30)
                }),
                new StaticAiControlPlaneIdResolver(controlPlaneId),
                new StaticExecutionContextSnapshotProvider(CreateSnapshot("tenant-a")));
        }

        /// <summary>
        /// Creates a shared run record.
        /// </summary>
        private AiSharedRunRecord CreateSharedRunRecord(
            string sharedRunId,
            string tenantId,
            AiSharedRunStatus status)
        {
            var now = DateTimeOffset.UtcNow;

            return new AiSharedRunRecord
            {
                SharedRunId = sharedRunId,
                ControlPlaneId = controlPlaneId,
                Status = status,
                RunRequest = new AiRuntimePipelineRunRequest
                {
                    PipelineName = "pipeline-1"
                },
                ExecutionContextSnapshot = CreateSnapshot(tenantId),
                SubmittedAtUtc = now,
                UpdatedAtUtc = now,
                Metadata = new Dictionary<string, string>
                {
                    ["controlPlaneId"] = controlPlaneId,
                    ["tenantId"] = tenantId,
                    ["source"] = "routing-safety-test"
                }
            };
        }

        /// <summary>
        /// Creates a shared queue item.
        /// </summary>
        private static AiSharedQueueItem CreateSharedQueueItem(
            string sharedRunId,
            string tenantId)
        {
            var now = DateTimeOffset.UtcNow;

            return new AiSharedQueueItem
            {
                SharedRunId = sharedRunId,
                Status = AiSharedQueueItemStatus.Pending,
                ExecutionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create(tenantId: tenantId),
                PipelineKey = "pipeline-1",
                Priority = 0,
                EnqueuedAtUtc = now,
                UpdatedAtUtc = now,
                Metadata = new Dictionary<string, string>
                {
                    ["tenantId"] = tenantId,
                    ["source"] = "routing-safety-test"
                }
            };
        }

        /// <summary>
        /// Creates a runtime run index entry.
        /// </summary>
        private static AiRuntimeRunExecutionIndexEntry CreateRuntimeRunIndexEntry(
            string runId,
            string tenantId,
            string runtimeInstanceId)
        {
            return new AiRuntimeRunExecutionIndexEntry
            {
                RunId = runId,
                RuntimeInstanceId = runtimeInstanceId,
                Status = "queued",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ExecutionContextSnapshot = CreateSnapshot(tenantId),
                Metadata = new Dictionary<string, string>
                {
                    ["tenantId"] = tenantId,
                    ["source"] = "routing-safety-test"
                }
            };
        }

        /// <summary>
        /// Creates an execution context snapshot.
        /// </summary>
        private static ExecutionContextSnapshot CreateSnapshot(
            string tenantId)
        {
            return new ExecutionContextSnapshot
            {
                ContextKey = $"test-context-{Guid.NewGuid():N}",
                TenantId = tenantId,
                TenantGroupId = "tenant-group-a",
                Project = "deterministic-ai-runtime-tests",
                UserId = $"user-{tenantId}",
                CurrentNamespace = "default",
                Namespaces = new List<NamespaceEntry>
                {
                    new()
                    {
                        Name = "default",
                        Trns = new HashSet<string>
                        {
                            "trn:deterministic-ai-runtime-tests:runtime:run:read",
                            "trn:deterministic-ai-runtime-tests:runtime:run:write",
                            "trn:deterministic-ai-runtime-tests:runtime:execution:read"
                        }
                    }
                },
                InFlightCount = 0,
                TtlSeconds = 300
            };
        }

        /// <summary>
        /// Gets the initialized Redis connection.
        /// </summary>
        private IConnectionMultiplexer RequireConnection()
        {
            return connection ?? throw new InvalidOperationException("Redis connection was not initialized.");
        }

        /// <summary>
        /// Deletes keys created by this integration test.
        /// </summary>
        private async Task DeleteTestKeysAsync(
            IConnectionMultiplexer redis)
        {
            var database = redis.GetDatabase();

            foreach (var endpoint in redis.GetEndPoints())
            {
                var server = redis.GetServer(endpoint);

                if (!server.IsConnected)
                {
                    continue;
                }

                var prefixedKeys = server.Keys(
                        database: database.Database,
                        pattern: $"{keyPrefix}:*")
                    .ToArray();

                var registryKeys = server.Keys(
                        database: database.Database,
                        pattern: $"ai:control-plane:{controlPlaneId}:*")
                    .ToArray();

                var keys = prefixedKeys
                    .Concat(registryKeys)
                    .Distinct()
                    .ToArray();

                if (keys.Length > 0)
                {
                    await database.KeyDeleteAsync(keys).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Static execution context snapshot provider for runtime run index tests.
        /// </summary>
        private sealed class StaticExecutionContextSnapshotProvider :
            IExecutionContextSnapshotProvider
        {
            private readonly ExecutionContextSnapshot snapshot;

            /// <summary>
            /// Initializes a new instance of the <see cref="StaticExecutionContextSnapshotProvider"/> class.
            /// </summary>
            public StaticExecutionContextSnapshotProvider(
                ExecutionContextSnapshot snapshot)
            {
                this.snapshot = snapshot;
            }

            /// <inheritdoc />
            public ExecutionContextSnapshot MapToSnapshot()
            {
                return snapshot;
            }
        }
    }
}