using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.AI.Tests.Fixtures;
using StackExchange.Redis;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Capacity
{
    public sealed class RedisAiRuntimeInstanceCapacityStoreTenantVisibilityTests
    {
        [Fact]
        public async Task ListAsync_Should_Treat_Missing_Isolation_Metadata_As_Shared()
        {
            var controlPlaneId = CreateControlPlaneId();
            await using var fixture = await RedisFixture.CreateAsync(controlPlaneId);

            var store = CreateStore(
                fixture.Redis,
                fixture.ControlPlaneIdResolver,
                tenantId: "tenant-x");

            await store.PublishAsync(CreateDescriptor(
                runtimeInstanceId: "shared-runtime-1",
                metadata: new Dictionary<string, string>()));

            var descriptors = await store.ListAsync();

            Assert.Contains(
                descriptors,
                descriptor => descriptor.RuntimeInstanceId == "shared-runtime-1");
        }

        [Fact]
        public async Task GetAsync_Should_Treat_Missing_Isolation_Metadata_As_Shared()
        {
            var controlPlaneId = CreateControlPlaneId();
            await using var fixture = await RedisFixture.CreateAsync(controlPlaneId);

            var store = CreateStore(
                fixture.Redis,
                fixture.ControlPlaneIdResolver,
                tenantId: "tenant-x");

            await store.PublishAsync(CreateDescriptor(
                runtimeInstanceId: "shared-runtime-1",
                metadata: new Dictionary<string, string>()));

            var descriptor = await store.GetAsync("shared-runtime-1");

            Assert.NotNull(descriptor);
            Assert.Equal("shared-runtime-1", descriptor.RuntimeInstanceId);
        }

        [Fact]
        public async Task ListAsync_Should_Return_Dedicated_Capacity_When_Tenant_Matches()
        {
            var controlPlaneId = CreateControlPlaneId();
            await using var fixture = await RedisFixture.CreateAsync(controlPlaneId);

            var store = CreateStore(
                fixture.Redis,
                fixture.ControlPlaneIdResolver,
                tenantId: "tenant-a");

            await store.PublishAsync(CreateDescriptor(
                runtimeInstanceId: "tenant-a-runtime-1",
                metadata: CreateDedicatedTenantMetadata("tenant-a")));

            var descriptors = await store.ListAsync();

            Assert.Contains(
                descriptors,
                descriptor => descriptor.RuntimeInstanceId == "tenant-a-runtime-1");
        }

        [Fact]
        public async Task GetAsync_Should_Return_Dedicated_Capacity_When_Tenant_Matches()
        {
            var controlPlaneId = CreateControlPlaneId();
            await using var fixture = await RedisFixture.CreateAsync(controlPlaneId);

            var store = CreateStore(
                fixture.Redis,
                fixture.ControlPlaneIdResolver,
                tenantId: "tenant-a");

            await store.PublishAsync(CreateDescriptor(
                runtimeInstanceId: "tenant-a-runtime-1",
                metadata: CreateDedicatedTenantMetadata("tenant-a")));

            var descriptor = await store.GetAsync("tenant-a-runtime-1");

            Assert.NotNull(descriptor);
            Assert.Equal("tenant-a-runtime-1", descriptor.RuntimeInstanceId);
        }

        [Fact]
        public async Task ListAsync_Should_Not_Return_Dedicated_Capacity_When_Tenant_Does_Not_Match()
        {
            var controlPlaneId = CreateControlPlaneId();
            await using var fixture = await RedisFixture.CreateAsync(controlPlaneId);

            var tenantAStore = CreateStore(
                fixture.Redis,
                fixture.ControlPlaneIdResolver,
                tenantId: "tenant-a");

            await tenantAStore.PublishAsync(CreateDescriptor(
                runtimeInstanceId: "tenant-a-runtime-1",
                metadata: CreateDedicatedTenantMetadata("tenant-a")));

            var tenantBStore = CreateStore(
                fixture.Redis,
                fixture.ControlPlaneIdResolver,
                tenantId: "tenant-b");

            var descriptors = await tenantBStore.ListAsync();

            Assert.DoesNotContain(
                descriptors,
                descriptor => descriptor.RuntimeInstanceId == "tenant-a-runtime-1");
        }

        [Fact]
        public async Task GetAsync_Should_Return_Null_For_Dedicated_Capacity_When_Tenant_Does_Not_Match()
        {
            var controlPlaneId = CreateControlPlaneId();
            await using var fixture = await RedisFixture.CreateAsync(controlPlaneId);

            var tenantAStore = CreateStore(
                fixture.Redis,
                fixture.ControlPlaneIdResolver,
                tenantId: "tenant-a");

            await tenantAStore.PublishAsync(CreateDescriptor(
                runtimeInstanceId: "tenant-a-runtime-1",
                metadata: CreateDedicatedTenantMetadata("tenant-a")));

            var tenantBStore = CreateStore(
                fixture.Redis,
                fixture.ControlPlaneIdResolver,
                tenantId: "tenant-b");

            var descriptor = await tenantBStore.GetAsync("tenant-a-runtime-1");

            Assert.Null(descriptor);
        }

        private static RedisAiRuntimeInstanceCapacityStore CreateStore(
            IConnectionMultiplexer redis,
            IAiControlPlaneIdResolver controlPlaneIdResolver,
            string? tenantId = null,
            string? tenantGroupId = null)
        {
            var options = Options.Create(new AiRuntimeInstanceRegistrationOptions
            {
                RegistryTtl = TimeSpan.FromMinutes(30),
                CapacityTtl = TimeSpan.FromMinutes(30)
            });

            IExecutionContextSnapshotProvider? snapshotProvider = string.IsNullOrWhiteSpace(tenantId)
                ? null
                : new TestExecutionContextSnapshotProvider(
                    tenantId,
                    tenantGroupId);

            return new RedisAiRuntimeInstanceCapacityStore(
                redis,
                options,
                controlPlaneIdResolver,
                new AiRuntimeInstanceVisibilityEvaluator(new HardcodedAiTenantRuntimeSettingsProvider()),
                snapshotProvider);
        }

        private static AiRuntimeInstanceCapacityDescriptor CreateDescriptor(
            string runtimeInstanceId,
            IReadOnlyDictionary<string, string> metadata)
        {
            return new AiRuntimeInstanceCapacityDescriptor
            {
                RuntimeInstanceId = runtimeInstanceId,
                Role = AiRuntimeInstanceRole.Runtime,
                Status = AiRuntimeInstanceStatus.Ready,
                WorkerCount = 10,
                ActiveWorkerCount = 0,
                AvailableWorkerCount = 10,
                MaxWorkersPerRun = 10,
                MinWorkersRequiredPerRun = 1,
                QueuedRunCount = 0,
                RunningRunCount = 0,
                ActiveRunCount = 0,
                MaxConcurrentRuns = 5,
                MaxRunSlots = 5,
                AvailableRunSlots = 5,
                ReservedRunSlots = 0,
                EffectiveAvailableRunSlots = 5,
                IsQueuePaused = false,
                CanAcceptRun = true,
                LastHeartbeatAtUtc = DateTimeOffset.UtcNow,
                Metadata = metadata
            };
        }

        private static IReadOnlyDictionary<string, string> CreateDedicatedTenantMetadata(
            string tenantId)
        {
            return new Dictionary<string, string>
            {
                [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = tenantId,
                [AiRuntimeInstanceIsolationMetadataKeys.IsolationMode] = "Dedicated",
                [AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback] = "false",
                [AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity] = "true"
            };
        }

        private static string CreateControlPlaneId()
        {
            return $"test-control-plane-{Guid.NewGuid():N}";
        }

        private sealed class TestExecutionContextSnapshotProvider :
            IExecutionContextSnapshotProvider
        {
            private readonly ExecutionContextSnapshot snapshot;

            public TestExecutionContextSnapshotProvider(
                string tenantId,
                string? tenantGroupId)
            {
                snapshot = AiExecutionContextSnapshotTestFactory.Create(
                    tenantId: tenantId,
                    tenantGroupId: tenantGroupId);
            }

            public ExecutionContextSnapshot MapToSnapshot()
            {
                return snapshot;
            }
        }

        private sealed class TestControlPlaneIdResolver : IAiControlPlaneIdResolver
        {
            private readonly string controlPlaneId;

            public TestControlPlaneIdResolver(
                string controlPlaneId)
            {
                this.controlPlaneId = controlPlaneId;
            }

            public Task<string> ResolveAsync(
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(controlPlaneId);
            }
        }

        private sealed class RedisFixture : IAsyncDisposable
        {
            private readonly string controlPlaneId;

            private RedisFixture(
                IConnectionMultiplexer redis,
                string controlPlaneId)
            {
                Redis = redis;
                this.controlPlaneId = controlPlaneId;
                ControlPlaneIdResolver = new TestControlPlaneIdResolver(controlPlaneId);
            }

            public IConnectionMultiplexer Redis { get; }

            public IAiControlPlaneIdResolver ControlPlaneIdResolver { get; }

            public static async Task<RedisFixture> CreateAsync(
                string controlPlaneId)
            {
                var connectionString =
                    Environment.GetEnvironmentVariable("MULTIPLEXED_TEST_REDIS") ??
                    Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING") ??
                    "localhost:6379";

                var redis = await ConnectionMultiplexer
                    .ConnectAsync(connectionString)
                    .ConfigureAwait(false);

                var fixture = new RedisFixture(
                    redis,
                    controlPlaneId);

                await fixture.CleanupAsync().ConfigureAwait(false);

                return fixture;
            }

            public async ValueTask DisposeAsync()
            {
                await CleanupAsync().ConfigureAwait(false);
                await Redis.CloseAsync().ConfigureAwait(false);
                Redis.Dispose();
            }

            private async Task CleanupAsync()
            {
                var database = Redis.GetDatabase();
                var server = GetServer();

                var pattern = $"ai:control-plane:{controlPlaneId}:*";

                foreach (var key in server.Keys(pattern: pattern))
                {
                    await database.KeyDeleteAsync(key).ConfigureAwait(false);
                }
            }

            private IServer GetServer()
            {
                var endpoint = Redis.GetEndPoints().First();

                return Redis.GetServer(endpoint);
            }
        }
    }
}