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
    /// <summary>
    /// Tests tenant-aware visibility for the Redis-backed runtime instance capacity store.
    /// </summary>
    public sealed class RedisAiRuntimeInstanceCapacityStoreTenantVisibilityTests
    {
        /// <summary>
        /// Verifies that capacity without isolation metadata is treated as shared capacity.
        /// </summary>
        [Fact]
        public async Task ListAsync_Should_Treat_Missing_Isolation_Metadata_As_Shared()
        {
            var controlPlaneId = CreateControlPlaneId();
            await using var fixture = await RedisFixture.CreateAsync(controlPlaneId);

            var store = CreateStore(fixture.Redis, fixture.ControlPlaneIdResolver, tenantId: "tenant-x");

            await store.PublishAsync(CreateDescriptor(
                runtimeInstanceId: "shared-runtime-1",
                metadata: new Dictionary<string, string>()));

            var descriptors = await store.ListAsync();

            Assert.Contains(descriptors, descriptor => descriptor.RuntimeInstanceId == "shared-runtime-1");
        }

        /// <summary>
        /// Verifies that direct capacity lookup treats capacity without isolation metadata as shared capacity.
        /// </summary>
        [Fact]
        public async Task GetAsync_Should_Treat_Missing_Isolation_Metadata_As_Shared()
        {
            var controlPlaneId = CreateControlPlaneId();
            await using var fixture = await RedisFixture.CreateAsync(controlPlaneId);

            var store = CreateStore(fixture.Redis, fixture.ControlPlaneIdResolver, tenantId: "tenant-x");

            await store.PublishAsync(CreateDescriptor(
                runtimeInstanceId: "shared-runtime-1",
                metadata: new Dictionary<string, string>()));

            var descriptor = await store.GetAsync("shared-runtime-1");

            Assert.NotNull(descriptor);
            Assert.Equal("shared-runtime-1", descriptor!.RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that dedicated capacity is visible when tenant ownership is provided through metadata.
        /// </summary>
        [Fact]
        public async Task ListAsync_Should_Return_Dedicated_Capacity_When_Tenant_Matches()
        {
            var controlPlaneId = CreateControlPlaneId();
            await using var fixture = await RedisFixture.CreateAsync(controlPlaneId);

            var store = CreateStore(fixture.Redis, fixture.ControlPlaneIdResolver, tenantId: "tenant-a");

            await store.PublishAsync(CreateDescriptor(
                runtimeInstanceId: "tenant-a-runtime-1",
                metadata: CreateDedicatedTenantMetadata("tenant-a")));

            var descriptors = await store.ListAsync();

            Assert.Contains(descriptors, descriptor => descriptor.RuntimeInstanceId == "tenant-a-runtime-1");
        }

        /// <summary>
        /// Verifies that dedicated capacity visibility can use first-class tenant fields,
        /// without depending only on metadata tenant ownership.
        /// </summary>
        [Fact]
        public async Task ListAsync_Should_Return_Dedicated_Capacity_When_FirstClass_Tenant_Matches()
        {
            var controlPlaneId = CreateControlPlaneId();
            await using var fixture = await RedisFixture.CreateAsync(controlPlaneId);

            var store = CreateStore(fixture.Redis, fixture.ControlPlaneIdResolver, tenantId: "tenant-a");

            await store.PublishAsync(CreateDescriptor(
                runtimeInstanceId: "tenant-a-runtime-1",
                metadata: CreateDedicatedIsolationMetadata(),
                tenantId: "tenant-a",
                tenantGroupId: "tenant-group-a"));

            var descriptors = await store.ListAsync();

            var descriptor = Assert.Single(descriptors.Where(item => item.RuntimeInstanceId == "tenant-a-runtime-1"));

            Assert.Equal("tenant-a", descriptor.TenantId);
            Assert.Equal("tenant-group-a", descriptor.TenantGroupId);
        }

        /// <summary>
        /// Verifies that direct dedicated capacity lookup is visible when tenant ownership matches.
        /// </summary>
        [Fact]
        public async Task GetAsync_Should_Return_Dedicated_Capacity_When_Tenant_Matches()
        {
            var controlPlaneId = CreateControlPlaneId();
            await using var fixture = await RedisFixture.CreateAsync(controlPlaneId);

            var store = CreateStore(fixture.Redis, fixture.ControlPlaneIdResolver, tenantId: "tenant-a");

            await store.PublishAsync(CreateDescriptor(
                runtimeInstanceId: "tenant-a-runtime-1",
                metadata: CreateDedicatedTenantMetadata("tenant-a")));

            var descriptor = await store.GetAsync("tenant-a-runtime-1");

            Assert.NotNull(descriptor);
            Assert.Equal("tenant-a-runtime-1", descriptor!.RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that dedicated capacity is hidden from non-owning tenants.
        /// </summary>
        [Fact]
        public async Task ListAsync_Should_Not_Return_Dedicated_Capacity_When_Tenant_Does_Not_Match()
        {
            var controlPlaneId = CreateControlPlaneId();
            await using var fixture = await RedisFixture.CreateAsync(controlPlaneId);

            var tenantAStore = CreateStore(fixture.Redis, fixture.ControlPlaneIdResolver, tenantId: "tenant-a");

            await tenantAStore.PublishAsync(CreateDescriptor(
                runtimeInstanceId: "tenant-a-runtime-1",
                metadata: CreateDedicatedTenantMetadata("tenant-a")));

            var tenantBStore = CreateStore(fixture.Redis, fixture.ControlPlaneIdResolver, tenantId: "tenant-b");

            var descriptors = await tenantBStore.ListAsync();

            Assert.DoesNotContain(descriptors, descriptor => descriptor.RuntimeInstanceId == "tenant-a-runtime-1");
        }

        /// <summary>
        /// Verifies that direct dedicated capacity lookup returns null for non-owning tenants.
        /// </summary>
        [Fact]
        public async Task GetAsync_Should_Return_Null_For_Dedicated_Capacity_When_Tenant_Does_Not_Match()
        {
            var controlPlaneId = CreateControlPlaneId();
            await using var fixture = await RedisFixture.CreateAsync(controlPlaneId);

            var tenantAStore = CreateStore(fixture.Redis, fixture.ControlPlaneIdResolver, tenantId: "tenant-a");

            await tenantAStore.PublishAsync(CreateDescriptor(
                runtimeInstanceId: "tenant-a-runtime-1",
                metadata: CreateDedicatedTenantMetadata("tenant-a")));

            var tenantBStore = CreateStore(fixture.Redis, fixture.ControlPlaneIdResolver, tenantId: "tenant-b");

            var descriptor = await tenantBStore.GetAsync("tenant-a-runtime-1");

            Assert.Null(descriptor);
        }

        /// <summary>
        /// Creates a Redis-backed runtime instance capacity store for a tenant-scoped visibility test.
        /// </summary>
        /// <param name="redis">The Redis connection multiplexer.</param>
        /// <param name="controlPlaneIdResolver">The control-plane id resolver.</param>
        /// <param name="tenantId">The optional tenant id for the current execution context.</param>
        /// <param name="tenantGroupId">The optional tenant group id for the current execution context.</param>
        /// <returns>The Redis-backed runtime instance capacity store.</returns>
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
                : new TestExecutionContextSnapshotProvider(tenantId, tenantGroupId);

            return new RedisAiRuntimeInstanceCapacityStore(
                redis,
                options,
                controlPlaneIdResolver,
                new AiRuntimeInstanceVisibilityEvaluator(new HardcodedAiTenantRuntimeSettingsProvider()),
                snapshotProvider);
        }

        /// <summary>
        /// Creates a runtime instance capacity descriptor for tenant visibility tests.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="metadata">The runtime isolation metadata.</param>
        /// <param name="tenantId">The optional first-class tenant identifier.</param>
        /// <param name="tenantGroupId">The optional first-class tenant group identifier.</param>
        /// <returns>The capacity descriptor.</returns>
        private static AiRuntimeInstanceCapacityDescriptor CreateDescriptor(
            string runtimeInstanceId,
            IReadOnlyDictionary<string, string> metadata,
            string? tenantId = null,
            string? tenantGroupId = null)
        {
            return new AiRuntimeInstanceCapacityDescriptor
            {
                RuntimeInstanceId = runtimeInstanceId,
                TenantId = tenantId,
                TenantGroupId = tenantGroupId,
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

        /// <summary>
        /// Creates dedicated isolation metadata including metadata-based tenant ownership.
        /// </summary>
        /// <param name="tenantId">The tenant identifier.</param>
        /// <returns>The dedicated runtime isolation metadata.</returns>
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

        /// <summary>
        /// Creates dedicated isolation metadata without duplicating first-class tenant ownership fields.
        /// </summary>
        /// <returns>The dedicated runtime isolation metadata.</returns>
        private static IReadOnlyDictionary<string, string> CreateDedicatedIsolationMetadata()
        {
            return new Dictionary<string, string>
            {
                [AiRuntimeInstanceIsolationMetadataKeys.IsolationMode] = "Dedicated",
                [AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback] = "false",
                [AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity] = "true"
            };
        }

        /// <summary>
        /// Creates a unique control-plane identifier for Redis test isolation.
        /// </summary>
        /// <returns>The control-plane identifier.</returns>
        private static string CreateControlPlaneId()
        {
            return $"test-control-plane-{Guid.NewGuid():N}";
        }

        /// <summary>
        /// Static execution context snapshot provider used by capacity visibility tests.
        /// </summary>
        private sealed class TestExecutionContextSnapshotProvider : IExecutionContextSnapshotProvider
        {
            private readonly ExecutionContextSnapshot snapshot;

            /// <summary>
            /// Initializes a new instance of the <see cref="TestExecutionContextSnapshotProvider"/> class.
            /// </summary>
            /// <param name="tenantId">The tenant identifier.</param>
            /// <param name="tenantGroupId">The optional tenant group identifier.</param>
            public TestExecutionContextSnapshotProvider(
                string tenantId,
                string? tenantGroupId)
            {
                snapshot = AiExecutionContextSnapshotTestFactory.Create(
                    tenantId: tenantId,
                    tenantGroupId: tenantGroupId);
            }

            /// <inheritdoc />
            public ExecutionContextSnapshot MapToSnapshot()
            {
                return snapshot;
            }
        }

       

        /// <summary>
        /// Redis fixture that isolates test keys by control-plane id and cleans them up.
        /// </summary>
        private sealed class RedisFixture : IAsyncDisposable
        {
            private readonly string controlPlaneId;

            private RedisFixture(
                IConnectionMultiplexer redis,
                string controlPlaneId)
            {
                Redis = redis;
                this.controlPlaneId = controlPlaneId;
                ControlPlaneIdResolver = new StaticAiControlPlaneIdResolver(controlPlaneId);
            }

            /// <summary>
            /// Gets the Redis connection multiplexer.
            /// </summary>
            public IConnectionMultiplexer Redis { get; }

            /// <summary>
            /// Gets the static control-plane id resolver.
            /// </summary>
            public IAiControlPlaneIdResolver ControlPlaneIdResolver { get; }

            /// <summary>
            /// Creates and cleans a Redis fixture for the specified control plane.
            /// </summary>
            /// <param name="controlPlaneId">The control-plane identifier.</param>
            /// <returns>The Redis fixture.</returns>
            public static async Task<RedisFixture> CreateAsync(
                string controlPlaneId)
            {
                var connectionString =
                    Environment.GetEnvironmentVariable("MULTIPLEXED_TEST_REDIS") ??
                    Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING") ??
                    "localhost:6379";

                var redis = await ConnectionMultiplexer.ConnectAsync(connectionString).ConfigureAwait(false);
                var fixture = new RedisFixture(redis, controlPlaneId);

                await fixture.CleanupAsync().ConfigureAwait(false);

                return fixture;
            }

            /// <inheritdoc />
            public async ValueTask DisposeAsync()
            {
                await CleanupAsync().ConfigureAwait(false);
                await Redis.CloseAsync().ConfigureAwait(false);
                Redis.Dispose();
            }

            /// <summary>
            /// Deletes all Redis keys owned by this test fixture control plane.
            /// </summary>
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

            /// <summary>
            /// Gets the Redis server used by the fixture connection.
            /// </summary>
            /// <returns>The Redis server.</returns>
            private IServer GetServer()
            {
                var endpoint = Redis.GetEndPoints().First();

                return Redis.GetServer(endpoint);
            }
        }
    }
}