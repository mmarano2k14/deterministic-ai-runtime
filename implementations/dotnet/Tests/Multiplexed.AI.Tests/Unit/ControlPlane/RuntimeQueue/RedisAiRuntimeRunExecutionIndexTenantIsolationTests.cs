using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue.Redis;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue.Redis;
using Multiplexed.AI.Tests.Fixtures;
using StackExchange.Redis;
using Xunit;

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
            if (redis is not null)
            {
                await DeleteTestKeysAsync(redis)
                    .ConfigureAwait(false);

                await redis
                    .CloseAsync()
                    .ConfigureAwait(false);

                redis.Dispose();
            }
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