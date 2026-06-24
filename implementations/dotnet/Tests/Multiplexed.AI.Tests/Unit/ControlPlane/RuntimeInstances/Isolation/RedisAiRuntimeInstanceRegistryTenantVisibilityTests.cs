using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.AI.Tests.Fixtures;
using StackExchange.Redis;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Isolation
{
    /// <summary>
    /// Tenant visibility tests for the Redis-backed runtime instance registry.
    /// </summary>
    public sealed class RedisAiRuntimeInstanceRegistryTenantVisibilityTests
    {
        /// <summary>
        /// Verifies that registry entries without isolation metadata are treated as shared.
        /// </summary>
        [Fact]
        public async Task ListAsync_Should_Treat_Missing_Isolation_Metadata_As_Shared()
        {
            var controlPlaneId = CreateControlPlaneId();
            await using var fixture = await RedisFixture.CreateAsync(controlPlaneId);

            var registry = CreateRegistry(
                fixture.Redis,
                fixture.ControlPlaneIdResolver,
                tenantId: "tenant-x");

            await registry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId: "shared-runtime-1",
                    metadata: new Dictionary<string, string>()));

            var snapshots = await registry.ListAsync();

            Assert.Contains(
                snapshots,
                snapshot => snapshot.RuntimeInstanceId == "shared-runtime-1");
        }

        /// <summary>
        /// Verifies that a shared registry entry can be loaded directly by a tenant-scoped context.
        /// </summary>
        [Fact]
        public async Task GetAsync_Should_Treat_Missing_Isolation_Metadata_As_Shared()
        {
            var controlPlaneId = CreateControlPlaneId();
            await using var fixture = await RedisFixture.CreateAsync(controlPlaneId);

            var registry = CreateRegistry(
                fixture.Redis,
                fixture.ControlPlaneIdResolver,
                tenantId: "tenant-x");

            await registry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId: "shared-runtime-1",
                    metadata: new Dictionary<string, string>()));

            var snapshot = await registry.GetAsync("shared-runtime-1");

            Assert.NotNull(snapshot);
            Assert.Equal("shared-runtime-1", snapshot!.RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that a metadata-owned dedicated runtime is visible to the matching tenant.
        /// </summary>
        [Fact]
        public async Task ListAsync_Should_Return_Dedicated_Instance_When_Tenant_Matches()
        {
            var controlPlaneId = CreateControlPlaneId();
            await using var fixture = await RedisFixture.CreateAsync(controlPlaneId);

            var registry = CreateRegistry(
                fixture.Redis,
                fixture.ControlPlaneIdResolver,
                tenantId: "tenant-a");

            await registry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId: "tenant-a-runtime-1",
                    metadata: CreateDedicatedTenantMetadata("tenant-a")));

            var snapshots = await registry.ListAsync();

            Assert.Contains(
                snapshots,
                snapshot => snapshot.RuntimeInstanceId == "tenant-a-runtime-1");
        }

        /// <summary>
        /// Verifies that a metadata-owned dedicated runtime can be loaded by the matching tenant.
        /// </summary>
        [Fact]
        public async Task GetAsync_Should_Return_Dedicated_Instance_When_Tenant_Matches()
        {
            var controlPlaneId = CreateControlPlaneId();
            await using var fixture = await RedisFixture.CreateAsync(controlPlaneId);

            var registry = CreateRegistry(
                fixture.Redis,
                fixture.ControlPlaneIdResolver,
                tenantId: "tenant-a");

            await registry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId: "tenant-a-runtime-1",
                    metadata: CreateDedicatedTenantMetadata("tenant-a")));

            var snapshot = await registry.GetAsync("tenant-a-runtime-1");

            Assert.NotNull(snapshot);
            Assert.Equal("tenant-a-runtime-1", snapshot!.RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that a metadata-owned dedicated runtime is hidden from non-matching tenants.
        /// </summary>
        [Fact]
        public async Task ListAsync_Should_Not_Return_Dedicated_Instance_When_Tenant_Does_Not_Match()
        {
            var controlPlaneId = CreateControlPlaneId();
            await using var fixture = await RedisFixture.CreateAsync(controlPlaneId);

            var tenantARegistry = CreateRegistry(
                fixture.Redis,
                fixture.ControlPlaneIdResolver,
                tenantId: "tenant-a");

            await tenantARegistry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId: "tenant-a-runtime-1",
                    metadata: CreateDedicatedTenantMetadata("tenant-a")));

            var tenantBRegistry = CreateRegistry(
                fixture.Redis,
                fixture.ControlPlaneIdResolver,
                tenantId: "tenant-b");

            var snapshots = await tenantBRegistry.ListAsync();

            Assert.DoesNotContain(
                snapshots,
                snapshot => snapshot.RuntimeInstanceId == "tenant-a-runtime-1");
        }

        /// <summary>
        /// Verifies that a metadata-owned dedicated runtime cannot be loaded by a non-matching tenant.
        /// </summary>
        [Fact]
        public async Task GetAsync_Should_Return_Null_For_Dedicated_Instance_When_Tenant_Does_Not_Match()
        {
            var controlPlaneId = CreateControlPlaneId();
            await using var fixture = await RedisFixture.CreateAsync(controlPlaneId);

            var tenantARegistry = CreateRegistry(
                fixture.Redis,
                fixture.ControlPlaneIdResolver,
                tenantId: "tenant-a");

            await tenantARegistry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId: "tenant-a-runtime-1",
                    metadata: CreateDedicatedTenantMetadata("tenant-a")));

            var tenantBRegistry = CreateRegistry(
                fixture.Redis,
                fixture.ControlPlaneIdResolver,
                tenantId: "tenant-b");

            var snapshot = await tenantBRegistry.GetAsync("tenant-a-runtime-1");

            Assert.Null(snapshot);
        }

        /// <summary>
        /// Verifies that first-class tenant ownership is used when metadata does not contain a tenant id.
        /// </summary>
        [Fact]
        public async Task ListAsync_Should_Return_Dedicated_Instance_When_FirstClass_Tenant_Matches()
        {
            var controlPlaneId = CreateControlPlaneId();
            await using var fixture = await RedisFixture.CreateAsync(controlPlaneId);

            var registry = CreateRegistry(
                fixture.Redis,
                fixture.ControlPlaneIdResolver,
                tenantId: "tenant-a",
                tenantGroupId: "tenant-group-a");

            await registry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId: "tenant-a-runtime-1",
                    tenantId: "tenant-a",
                    tenantGroupId: "tenant-group-a",
                    metadata: CreateDedicatedMetadataWithoutTenant()));

            var snapshots = await registry.ListAsync();

            var snapshot = Assert.Single(
                snapshots,
                item => item.RuntimeInstanceId == "tenant-a-runtime-1");

            Assert.Equal("tenant-a", snapshot.TenantId);
            Assert.Equal("tenant-group-a", snapshot.TenantGroupId);
        }

        /// <summary>
        /// Verifies that first-class tenant ownership is used by direct registry lookup.
        /// </summary>
        [Fact]
        public async Task GetAsync_Should_Return_Dedicated_Instance_When_FirstClass_Tenant_Matches()
        {
            var controlPlaneId = CreateControlPlaneId();
            await using var fixture = await RedisFixture.CreateAsync(controlPlaneId);

            var registry = CreateRegistry(
                fixture.Redis,
                fixture.ControlPlaneIdResolver,
                tenantId: "tenant-a",
                tenantGroupId: "tenant-group-a");

            await registry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId: "tenant-a-runtime-1",
                    tenantId: "tenant-a",
                    tenantGroupId: "tenant-group-a",
                    metadata: CreateDedicatedMetadataWithoutTenant()));

            var snapshot = await registry.GetAsync("tenant-a-runtime-1");

            Assert.NotNull(snapshot);
            Assert.Equal("tenant-a-runtime-1", snapshot!.RuntimeInstanceId);
            Assert.Equal("tenant-a", snapshot.TenantId);
            Assert.Equal("tenant-group-a", snapshot.TenantGroupId);
        }

        /// <summary>
        /// Verifies that first-class tenant ownership hides dedicated runtimes from non-matching tenants.
        /// </summary>
        [Fact]
        public async Task ListAsync_Should_Not_Return_Dedicated_Instance_When_FirstClass_Tenant_Does_Not_Match()
        {
            var controlPlaneId = CreateControlPlaneId();
            await using var fixture = await RedisFixture.CreateAsync(controlPlaneId);

            var tenantARegistry = CreateRegistry(
                fixture.Redis,
                fixture.ControlPlaneIdResolver,
                tenantId: "tenant-a",
                tenantGroupId: "tenant-group-a");

            await tenantARegistry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId: "tenant-a-runtime-1",
                    tenantId: "tenant-a",
                    tenantGroupId: "tenant-group-a",
                    metadata: CreateDedicatedMetadataWithoutTenant()));

            var tenantBRegistry = CreateRegistry(
                fixture.Redis,
                fixture.ControlPlaneIdResolver,
                tenantId: "tenant-b",
                tenantGroupId: "tenant-group-b");

            var snapshots = await tenantBRegistry.ListAsync();

            Assert.DoesNotContain(
                snapshots,
                snapshot => snapshot.RuntimeInstanceId == "tenant-a-runtime-1");
        }

        /// <summary>
        /// Verifies that first-class tenant ownership blocks direct lookup from non-matching tenants.
        /// </summary>
        [Fact]
        public async Task GetAsync_Should_Return_Null_For_Dedicated_Instance_When_FirstClass_Tenant_Does_Not_Match()
        {
            var controlPlaneId = CreateControlPlaneId();
            await using var fixture = await RedisFixture.CreateAsync(controlPlaneId);

            var tenantARegistry = CreateRegistry(
                fixture.Redis,
                fixture.ControlPlaneIdResolver,
                tenantId: "tenant-a",
                tenantGroupId: "tenant-group-a");

            await tenantARegistry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId: "tenant-a-runtime-1",
                    tenantId: "tenant-a",
                    tenantGroupId: "tenant-group-a",
                    metadata: CreateDedicatedMetadataWithoutTenant()));

            var tenantBRegistry = CreateRegistry(
                fixture.Redis,
                fixture.ControlPlaneIdResolver,
                tenantId: "tenant-b",
                tenantGroupId: "tenant-group-b");

            var snapshot = await tenantBRegistry.GetAsync("tenant-a-runtime-1");

            Assert.Null(snapshot);
        }

        /// <summary>
        /// Creates a Redis-backed runtime registry with an optional tenant execution context.
        /// </summary>
        private static RedisAiRuntimeInstanceRegistry CreateRegistry(
            IConnectionMultiplexer redis,
            IAiControlPlaneIdResolver controlPlaneIdResolver,
            string? tenantId = null,
            string? tenantGroupId = null)
        {
            var options = Options.Create(new AiRuntimeInstanceRegistrationOptions
            {
                RegistryTtl = TimeSpan.FromMinutes(30)
            });

            IExecutionContextSnapshotProvider? snapshotProvider = string.IsNullOrWhiteSpace(tenantId)
                ? null
                : new TestExecutionContextSnapshotProvider(
                    tenantId,
                    tenantGroupId);

            return new RedisAiRuntimeInstanceRegistry(
                redis,
                options,
                controlPlaneIdResolver,
                new AiRuntimeInstanceVisibilityEvaluator(new HardcodedAiTenantRuntimeSettingsProvider()),
                snapshotProvider);
        }

        /// <summary>
        /// Creates a runtime instance registration for registry visibility tests.
        /// </summary>
        private static AiRuntimeInstanceRegistration CreateRegistration(
            string runtimeInstanceId,
            string? tenantId = null,
            string? tenantGroupId = null,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            return new AiRuntimeInstanceRegistration
            {
                RuntimeInstanceId = runtimeInstanceId,
                TenantId = tenantId,
                TenantGroupId = tenantGroupId,
                WorkerCount = 10,
                QueueCapacity = 100,
                MaxConcurrentRuns = 5,
                Metadata = metadata ?? new Dictionary<string, string>()
            };
        }

        /// <summary>
        /// Creates dedicated runtime isolation metadata for the specified tenant.
        /// </summary>
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
        /// Creates dedicated runtime isolation metadata without tenant ownership.
        /// </summary>
        private static IReadOnlyDictionary<string, string> CreateDedicatedMetadataWithoutTenant()
        {
            return new Dictionary<string, string>
            {
                [AiRuntimeInstanceIsolationMetadataKeys.IsolationMode] = "Dedicated",
                [AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback] = "false",
                [AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity] = "true"
            };
        }

        /// <summary>
        /// Creates a unique control-plane identifier.
        /// </summary>
        private static string CreateControlPlaneId()
        {
            return $"test-control-plane-{Guid.NewGuid():N}";
        }

        /// <summary>
        /// Test execution context snapshot provider.
        /// </summary>
        private sealed class TestExecutionContextSnapshotProvider :
            IExecutionContextSnapshotProvider
        {
            private readonly ExecutionContextSnapshot snapshot;

            /// <summary>
            /// Initializes a new instance of the <see cref="TestExecutionContextSnapshotProvider"/> class.
            /// </summary>
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
        /// Static control-plane id resolver used by tests.
        /// </summary>
        private sealed class TestControlPlaneIdResolver : IAiControlPlaneIdResolver
        {
            private readonly string controlPlaneId;

            /// <summary>
            /// Initializes a new instance of the <see cref="TestControlPlaneIdResolver"/> class.
            /// </summary>
            public TestControlPlaneIdResolver(
                string controlPlaneId)
            {
                this.controlPlaneId = controlPlaneId;
            }

            /// <inheritdoc />
            public Task<string> ResolveAsync(
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(controlPlaneId);
            }
        }

        /// <summary>
        /// Redis fixture that isolates registry keys per control-plane identifier.
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
                ControlPlaneIdResolver = new TestControlPlaneIdResolver(controlPlaneId);
            }

            /// <summary>
            /// Gets the Redis connection multiplexer.
            /// </summary>
            public IConnectionMultiplexer Redis { get; }

            /// <summary>
            /// Gets the control-plane id resolver.
            /// </summary>
            public IAiControlPlaneIdResolver ControlPlaneIdResolver { get; }

            /// <summary>
            /// Creates a Redis fixture and removes stale keys for the requested control-plane.
            /// </summary>
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

            /// <inheritdoc />
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