using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue.Redis;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue.Redis;
using Multiplexed.AI.Tests.Fixtures;
using StackExchange.Redis;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeQueue
{
    /// <summary>
    /// Integration tests for the Redis-backed runtime run execution index.
    /// </summary>
    public sealed class RedisAiRuntimeRunExecutionIndexTenantIsolationTests :
        IAsyncLifetime
    {
        private readonly string keyPrefix =
            $"multiplexed:ai:test:runtime-run-index:{Guid.NewGuid():N}";

        private IConnectionMultiplexer? redis;
        private RedisAiRuntimeRunExecutionIndex? tenantAStore;
        private RedisAiRuntimeRunExecutionIndex? tenantBStore;

        public async Task InitializeAsync()
        {
            var connectionString =
                Environment.GetEnvironmentVariable("MULTIPLEXED_TEST_REDIS") ??
                "localhost:6379";

            redis =
                await ConnectionMultiplexer
                    .ConnectAsync(connectionString)
                    .ConfigureAwait(false);

            var options =
                Options.Create(
                    new RedisAiRuntimeRunExecutionIndexOptions
                    {
                        KeyPrefix = keyPrefix,
                        EnableRecordExpiration = true,
                        RecordExpiration = TimeSpan.FromSeconds(10)
                    });

            var controlPlaneIdResolver =
                new StaticAiControlPlaneIdResolver("test-control-plane");

            tenantAStore =
                new RedisAiRuntimeRunExecutionIndex(
                    redis,
                    options,
                    controlPlaneIdResolver,
                    new StaticExecutionContextSnapshotProvider(
                        CreateSnapshot("tenant-a")));

            tenantBStore =
                new RedisAiRuntimeRunExecutionIndex(
                    redis,
                    options,
                    controlPlaneIdResolver,
                    new StaticExecutionContextSnapshotProvider(
                        CreateSnapshot("tenant-b")));
        }

        public async Task DisposeAsync()
        {
            if (redis is null)
            {
                return;
            }

            await DeleteTestKeysAsync(redis)
                .ConfigureAwait(false);

            await redis
                .CloseAsync()
                .ConfigureAwait(false);

            redis.Dispose();
        }

        [Fact]
        public async Task GetAsync_ShouldReturnOnlyRunsForCurrentTenant()
        {
            var tenantARunId =
                $"run-a-{Guid.NewGuid():N}";

            var tenantBRunId =
                $"run-b-{Guid.NewGuid():N}";

            await tenantAStore!
                .RegisterQueuedAsync(
                    CreateEntry(
                        tenantARunId,
                        "tenant-a",
                        runtimeInstanceId: "runtime-a"))
                .ConfigureAwait(false);

            await tenantBStore!
                .RegisterQueuedAsync(
                    CreateEntry(
                        tenantBRunId,
                        "tenant-b",
                        runtimeInstanceId: "runtime-b"))
                .ConfigureAwait(false);

            var tenantAOwnRun =
                await tenantAStore
                    .GetAsync(tenantARunId)
                    .ConfigureAwait(false);

            var tenantACrossTenantRun =
                await tenantAStore
                    .GetAsync(tenantBRunId)
                    .ConfigureAwait(false);

            var tenantBOwnRun =
                await tenantBStore
                    .GetAsync(tenantBRunId)
                    .ConfigureAwait(false);

            var tenantBCrossTenantRun =
                await tenantBStore
                    .GetAsync(tenantARunId)
                    .ConfigureAwait(false);

            Assert.NotNull(tenantAOwnRun);
            Assert.Equal(tenantARunId, tenantAOwnRun!.RunId);
            Assert.Equal("tenant-a", tenantAOwnRun.ExecutionContextSnapshot?.TenantId);

            Assert.Null(tenantACrossTenantRun);

            Assert.NotNull(tenantBOwnRun);
            Assert.Equal(tenantBRunId, tenantBOwnRun!.RunId);
            Assert.Equal("tenant-b", tenantBOwnRun.ExecutionContextSnapshot?.TenantId);

            Assert.Null(tenantBCrossTenantRun);
        }

        [Fact]
        public async Task MarkStartedAndCompleted_ShouldPreserveTenantIsolation()
        {
            var tenantBRunId =
                $"run-b-{Guid.NewGuid():N}";

            var tenantBExecutionId =
                $"execution-b-{Guid.NewGuid():N}";

            await tenantBStore!
                .RegisterQueuedAsync(
                    CreateEntry(
                        tenantBRunId,
                        "tenant-b",
                        runtimeInstanceId: "runtime-b"))
                .ConfigureAwait(false);

            await tenantBStore
                .MarkStartedAsync(
                    tenantBRunId,
                    tenantBExecutionId)
                .ConfigureAwait(false);

            await tenantBStore
                .MarkCompletedAsync(
                    tenantBRunId,
                    tenantBExecutionId)
                .ConfigureAwait(false);

            var visibleToTenantB =
                await tenantBStore
                    .GetAsync(tenantBRunId)
                    .ConfigureAwait(false);

            var visibleToTenantA =
                await tenantAStore!
                    .GetAsync(tenantBRunId)
                    .ConfigureAwait(false);

            Assert.NotNull(visibleToTenantB);
            Assert.Equal(tenantBRunId, visibleToTenantB!.RunId);
            Assert.Equal(tenantBExecutionId, visibleToTenantB.ExecutionId);
            Assert.Equal("completed", visibleToTenantB.Status);
            Assert.Equal("tenant-b", visibleToTenantB.ExecutionContextSnapshot?.TenantId);

            Assert.Null(visibleToTenantA);
        }

        [Fact]
        public async Task MarkFailed_ShouldPreserveTenantIsolation()
        {
            var tenantARunId =
                $"run-a-{Guid.NewGuid():N}";

            var tenantAExecutionId =
                $"execution-a-{Guid.NewGuid():N}";

            await tenantAStore!
                .RegisterQueuedAsync(
                    CreateEntry(
                        tenantARunId,
                        "tenant-a",
                        runtimeInstanceId: "runtime-a"))
                .ConfigureAwait(false);

            await tenantAStore
                .MarkFailedAsync(
                    tenantARunId,
                    tenantAExecutionId,
                    "runtime failure")
                .ConfigureAwait(false);

            var visibleToTenantA =
                await tenantAStore
                    .GetAsync(tenantARunId)
                    .ConfigureAwait(false);

            var visibleToTenantB =
                await tenantBStore!
                    .GetAsync(tenantARunId)
                    .ConfigureAwait(false);

            Assert.NotNull(visibleToTenantA);
            Assert.Equal(tenantARunId, visibleToTenantA!.RunId);
            Assert.Equal(tenantAExecutionId, visibleToTenantA.ExecutionId);
            Assert.Equal("failed", visibleToTenantA.Status);
            Assert.Equal("runtime failure", visibleToTenantA.FailureReason);
            Assert.Equal("tenant-a", visibleToTenantA.ExecutionContextSnapshot?.TenantId);

            Assert.Null(visibleToTenantB);
        }

        [Fact]
        public async Task MarkCancelled_ShouldPreserveTenantIsolation()
        {
            var tenantARunId =
                $"run-a-{Guid.NewGuid():N}";

            var tenantAExecutionId =
                $"execution-a-{Guid.NewGuid():N}";

            await tenantAStore!
                .RegisterQueuedAsync(
                    CreateEntry(
                        tenantARunId,
                        "tenant-a",
                        runtimeInstanceId: "runtime-a"))
                .ConfigureAwait(false);

            await tenantAStore
                .MarkCancelledAsync(
                    tenantARunId,
                    tenantAExecutionId,
                    "cancelled by test")
                .ConfigureAwait(false);

            var visibleToTenantA =
                await tenantAStore
                    .GetAsync(tenantARunId)
                    .ConfigureAwait(false);

            var visibleToTenantB =
                await tenantBStore!
                    .GetAsync(tenantARunId)
                    .ConfigureAwait(false);

            Assert.NotNull(visibleToTenantA);
            Assert.Equal(tenantARunId, visibleToTenantA!.RunId);
            Assert.Equal(tenantAExecutionId, visibleToTenantA.ExecutionId);
            Assert.Equal("cancelled", visibleToTenantA.Status);
            Assert.Equal("cancelled by test", visibleToTenantA.FailureReason);
            Assert.Equal("tenant-a", visibleToTenantA.ExecutionContextSnapshot?.TenantId);

            Assert.Null(visibleToTenantB);
        }

        /// <summary>
        /// Verifies that unfinished runtime run entries can be listed by runtime instance id.
        /// </summary>
        [Fact]
        public async Task ListUnfinishedByRuntimeInstanceAsync_Should_Return_Only_Unfinished_Runs_For_RuntimeInstance()
        {
            var queuedRunId = $"run-a-queued-{Guid.NewGuid():N}";
            var runningRunId = $"run-a-running-{Guid.NewGuid():N}";
            var completedRunId = $"run-a-completed-{Guid.NewGuid():N}";
            var failedRunId = $"run-a-failed-{Guid.NewGuid():N}";
            var cancelledRunId = $"run-a-cancelled-{Guid.NewGuid():N}";
            var otherRuntimeRunId = $"run-a-other-runtime-{Guid.NewGuid():N}";

            await tenantAStore!.RegisterQueuedAsync(CreateEntry(queuedRunId, "tenant-a", runtimeInstanceId: "runtime-a")).ConfigureAwait(false);
            await tenantAStore.RegisterQueuedAsync(CreateEntry(runningRunId, "tenant-a", runtimeInstanceId: "runtime-a")).ConfigureAwait(false);
            await tenantAStore.MarkStartedAsync(runningRunId, $"execution-{Guid.NewGuid():N}").ConfigureAwait(false);
            await tenantAStore.RegisterQueuedAsync(CreateEntry(completedRunId, "tenant-a", runtimeInstanceId: "runtime-a")).ConfigureAwait(false);
            await tenantAStore.MarkCompletedAsync(completedRunId, $"execution-{Guid.NewGuid():N}").ConfigureAwait(false);
            await tenantAStore.RegisterQueuedAsync(CreateEntry(failedRunId, "tenant-a", runtimeInstanceId: "runtime-a")).ConfigureAwait(false);
            await tenantAStore.MarkFailedAsync(failedRunId, $"execution-{Guid.NewGuid():N}", "runtime failure").ConfigureAwait(false);
            await tenantAStore.RegisterQueuedAsync(CreateEntry(cancelledRunId, "tenant-a", runtimeInstanceId: "runtime-a")).ConfigureAwait(false);
            await tenantAStore.MarkCancelledAsync(cancelledRunId, $"execution-{Guid.NewGuid():N}", "cancelled").ConfigureAwait(false);
            await tenantAStore.RegisterQueuedAsync(CreateEntry(otherRuntimeRunId, "tenant-a", runtimeInstanceId: "runtime-other")).ConfigureAwait(false);

            var entries = await tenantAStore.ListUnfinishedByRuntimeInstanceAsync("runtime-a").ConfigureAwait(false);

            Assert.Equal(2, entries.Count);
            Assert.Contains(entries, entry => entry.RunId == queuedRunId && entry.Status == "queued");
            Assert.Contains(entries, entry => entry.RunId == runningRunId && entry.Status == "running");
            Assert.DoesNotContain(entries, entry => entry.RunId == completedRunId);
            Assert.DoesNotContain(entries, entry => entry.RunId == failedRunId);
            Assert.DoesNotContain(entries, entry => entry.RunId == cancelledRunId);
            Assert.DoesNotContain(entries, entry => entry.RuntimeInstanceId == "runtime-other");
        }

        /// <summary>
        /// Verifies that unfinished runtime run lookup preserves tenant isolation.
        /// </summary>
        [Fact]
        public async Task ListUnfinishedByRuntimeInstanceAsync_Should_Preserve_Tenant_Isolation()
        {
            var tenantARunId = $"run-a-{Guid.NewGuid():N}";
            var tenantBRunId = $"run-b-{Guid.NewGuid():N}";

            await tenantAStore!.RegisterQueuedAsync(CreateEntry(tenantARunId, "tenant-a", runtimeInstanceId: "runtime-shared")).ConfigureAwait(false);
            await tenantBStore!.RegisterQueuedAsync(CreateEntry(tenantBRunId, "tenant-b", runtimeInstanceId: "runtime-shared")).ConfigureAwait(false);

            var visibleToTenantA = await tenantAStore.ListUnfinishedByRuntimeInstanceAsync("runtime-shared").ConfigureAwait(false);
            var visibleToTenantB = await tenantBStore.ListUnfinishedByRuntimeInstanceAsync("runtime-shared").ConfigureAwait(false);

            Assert.Single(visibleToTenantA);
            Assert.Equal(tenantARunId, visibleToTenantA[0].RunId);
            Assert.Equal("tenant-a", visibleToTenantA[0].ExecutionContextSnapshot?.TenantId);

            Assert.Single(visibleToTenantB);
            Assert.Equal(tenantBRunId, visibleToTenantB[0].RunId);
            Assert.Equal("tenant-b", visibleToTenantB[0].ExecutionContextSnapshot?.TenantId);
        }

        /// <summary>
        /// Verifies that runtime assignment metadata is persisted on queued index entries.
        /// </summary>
        [Fact]
        public async Task RegisterQueuedAsync_Should_Persist_Runtime_Assignment_Metadata()
        {
            var tenantARunId =
                $"run-a-{Guid.NewGuid():N}";

            await tenantAStore!
                .RegisterQueuedAsync(
                    CreateEntry(
                        tenantARunId,
                        "tenant-a",
                        runtimeInstanceId: "runtime-a"))
                .ConfigureAwait(false);

            var entry =
                await tenantAStore
                    .GetAsync(tenantARunId)
                    .ConfigureAwait(false);

            Assert.NotNull(entry);
            Assert.Equal(tenantARunId, entry!.RunId);
            Assert.Equal("runtime-a", entry.RuntimeInstanceId);
            Assert.Equal("queued", entry.Status);
            Assert.Equal("tenant-a", entry.ExecutionContextSnapshot?.TenantId);
            Assert.Equal("tenant-a", entry.Metadata["tenantId"]);
            Assert.Equal("redis-runtime-run-index-test", entry.Metadata["source"]);
        }

        /// <summary>
        /// Verifies that a running Redis runtime run can be marked as requeued for recovery.
        /// </summary>
        [Fact]
        public async Task MarkRequeuedForRecoveryAsync_Should_Mark_Running_Run_As_Requeued_For_Recovery()
        {
            var runId =
                $"run-requeued-for-recovery-{Guid.NewGuid():N}";

            await tenantAStore!
                .RegisterQueuedAsync(
                    CreateEntry(
                        runId,
                        "tenant-a",
                        runtimeInstanceId: "runtime-a"))
                .ConfigureAwait(false);

            await tenantAStore
                .MarkStartedAsync(
                    runId,
                    "execution-1")
                .ConfigureAwait(false);

            var changed =
                await tenantAStore
                    .MarkRequeuedForRecoveryAsync(
                        runId,
                        "execution-1",
                        "runtime-execution-recovery-requeue")
                    .ConfigureAwait(false);

            var entry =
                await tenantAStore
                    .GetAsync(runId)
                    .ConfigureAwait(false);

            var unfinished =
                await tenantAStore
                    .ListUnfinishedByRuntimeInstanceAsync("runtime-a")
                    .ConfigureAwait(false);

            Assert.True(changed);
            Assert.NotNull(entry);
            Assert.Equal("requeued-for-recovery", entry!.Status);
            Assert.Equal("runtime-execution-recovery-requeue", entry.FailureReason);
            Assert.NotNull(entry.CompletedAtUtc);
            Assert.Empty(unfinished);
        }

        /// <summary>
        /// Verifies that Redis requeued-for-recovery is idempotent.
        /// </summary>
        [Fact]
        public async Task MarkRequeuedForRecoveryAsync_Should_Return_False_When_Already_Requeued_For_Recovery()
        {
            var runId =
                $"run-requeued-for-recovery-idempotent-{Guid.NewGuid():N}";

            await tenantAStore!
                .RegisterQueuedAsync(
                    CreateEntry(
                        runId,
                        "tenant-a",
                        runtimeInstanceId: "runtime-a"))
                .ConfigureAwait(false);

            await tenantAStore
                .MarkStartedAsync(
                    runId,
                    "execution-1")
                .ConfigureAwait(false);

            var first =
                await tenantAStore
                    .MarkRequeuedForRecoveryAsync(
                        runId,
                        "execution-1",
                        "first-recovery")
                    .ConfigureAwait(false);

            var second =
                await tenantAStore
                    .MarkRequeuedForRecoveryAsync(
                        runId,
                        "execution-1",
                        "second-recovery")
                    .ConfigureAwait(false);

            var entry =
                await tenantAStore
                    .GetAsync(runId)
                    .ConfigureAwait(false);

            Assert.True(first);
            Assert.False(second);
            Assert.NotNull(entry);
            Assert.Equal("requeued-for-recovery", entry!.Status);
            Assert.Equal("first-recovery", entry.FailureReason);
        }

        /// <summary>
        /// Verifies that terminal Redis runtime run entries cannot be marked as requeued for recovery.
        /// </summary>
        [Theory]
        [InlineData("completed")]
        [InlineData("failed")]
        [InlineData("cancelled")]
        [InlineData("requeued-for-recovery")]
        public async Task MarkRequeuedForRecoveryAsync_Should_Return_False_When_Run_Is_Terminal(
            string terminalStatus)
        {
            var runId =
                $"run-requeued-for-recovery-terminal-{terminalStatus}-{Guid.NewGuid():N}";

            await tenantAStore!
                .RegisterQueuedAsync(
                    CreateEntry(
                        runId,
                        "tenant-a",
                        runtimeInstanceId: "runtime-a"))
                .ConfigureAwait(false);

            if (terminalStatus == "completed")
            {
                await tenantAStore
                    .MarkCompletedAsync(
                        runId,
                        "execution-1")
                    .ConfigureAwait(false);
            }
            else if (terminalStatus == "failed")
            {
                await tenantAStore
                    .MarkFailedAsync(
                        runId,
                        "execution-1",
                        "terminal")
                    .ConfigureAwait(false);
            }
            else if (terminalStatus == "cancelled")
            {
                await tenantAStore
                    .MarkCancelledAsync(
                        runId,
                        "execution-1",
                        "terminal")
                    .ConfigureAwait(false);
            }
            else
            {
                await tenantAStore
                    .MarkStartedAsync(
                        runId,
                        "execution-1")
                    .ConfigureAwait(false);

                await tenantAStore
                    .MarkRequeuedForRecoveryAsync(
                        runId,
                        "execution-1",
                        "terminal")
                    .ConfigureAwait(false);
            }

            var changed =
                await tenantAStore
                    .MarkRequeuedForRecoveryAsync(
                        runId,
                        "execution-1",
                        "runtime-execution-recovery-requeue")
                    .ConfigureAwait(false);

            var entry =
                await tenantAStore
                    .GetAsync(runId)
                    .ConfigureAwait(false);

            Assert.False(changed);
            Assert.NotNull(entry);
            Assert.Equal(terminalStatus, entry!.Status);

            if (terminalStatus == "completed")
            {
                Assert.Null(entry.FailureReason);
            }
            else
            {
                Assert.Equal("terminal", entry.FailureReason);
            }
        }

        private static AiRuntimeRunExecutionIndexEntry CreateEntry(
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
                    ["source"] = "redis-runtime-run-index-test"
                }
            };
        }

        private static ExecutionContextSnapshot CreateSnapshot(
            string tenantId)
        {
            return new ExecutionContextSnapshot
            {
                ContextKey = $"test-context-{Guid.NewGuid():N}",
                TenantId = tenantId,
                TenantGroupId = "test-tenant-group",
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

        private async Task DeleteTestKeysAsync(
            IConnectionMultiplexer connection)
        {
            var database =
                connection.GetDatabase();

            foreach (var endpoint in connection.GetEndPoints())
            {
                var server =
                    connection.GetServer(endpoint);

                if (!server.IsConnected)
                {
                    continue;
                }

                var keys =
                    server.Keys(
                            pattern: $"{keyPrefix}:*")
                        .ToArray();

                if (keys.Length > 0)
                {
                    await database
                        .KeyDeleteAsync(keys)
                        .ConfigureAwait(false);
                }
            }
        }

        private sealed class StaticExecutionContextSnapshotProvider :
            IExecutionContextSnapshotProvider
        {
            private readonly ExecutionContextSnapshot snapshot;

            public StaticExecutionContextSnapshotProvider(
                ExecutionContextSnapshot snapshot)
            {
                this.snapshot =
                    snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            }

            public ExecutionContextSnapshot MapToSnapshot()
            {
                return snapshot;
            }
        }
    }
}