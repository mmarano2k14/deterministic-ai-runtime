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
    public sealed class RedisAiRuntimeInstanceRegistryTenantVisibilityTests
    {
        [Fact]
        public async Task ListAsync_Should_Treat_Missing_Isolation_Metadata_As_Shared()
        {
            var controlPlaneId = CreateControlPlaneId();
            await using var fixture = await RedisFixture.CreateAsync(controlPlaneId);

            var registry = CreateRegistry(
                fixture.Redis,
                fixture.ControlPlaneIdResolver,
                tenantId: "tenant-x");

            await registry.RegisterAsync(new AiRuntimeInstanceRegistration
            {
                RuntimeInstanceId = "shared-runtime-1",
                WorkerCount = 10,
                QueueCapacity = 100,
                MaxConcurrentRuns = 5,
                Metadata = new Dictionary<string, string>()
            });

            var snapshots = await registry.ListAsync();

            Assert.Contains(
                snapshots,
                snapshot => snapshot.RuntimeInstanceId == "shared-runtime-1");
        }

        [Fact]
        public async Task ListAsync_Should_Return_Dedicated_Instance_When_Tenant_Matches()
        {
            var controlPlaneId = CreateControlPlaneId();
            await using var fixture = await RedisFixture.CreateAsync(controlPlaneId);

            var registry = CreateRegistry(
                fixture.Redis,
                fixture.ControlPlaneIdResolver,
                tenantId: "tenant-a");

            await registry.RegisterAsync(new AiRuntimeInstanceRegistration
            {
                RuntimeInstanceId = "tenant-a-runtime-1",
                WorkerCount = 10,
                QueueCapacity = 100,
                MaxConcurrentRuns = 5,
                Metadata = CreateDedicatedTenantMetadata("tenant-a")
            });

            var snapshots = await registry.ListAsync();

            Assert.Contains(
                snapshots,
                snapshot => snapshot.RuntimeInstanceId == "tenant-a-runtime-1");
        }

        [Fact]
        public async Task ListAsync_Should_Not_Return_Dedicated_Instance_When_Tenant_Does_Not_Match()
        {
            var controlPlaneId = CreateControlPlaneId();
            await using var fixture = await RedisFixture.CreateAsync(controlPlaneId);

            var tenantARegistry = CreateRegistry(
                fixture.Redis,
                fixture.ControlPlaneIdResolver,
                tenantId: "tenant-a");

            await tenantARegistry.RegisterAsync(new AiRuntimeInstanceRegistration
            {
                RuntimeInstanceId = "tenant-a-runtime-1",
                WorkerCount = 10,
                QueueCapacity = 100,
                MaxConcurrentRuns = 5,
                Metadata = CreateDedicatedTenantMetadata("tenant-a")
            });

            var tenantBRegistry = CreateRegistry(
                fixture.Redis,
                fixture.ControlPlaneIdResolver,
                tenantId: "tenant-b");

            var snapshots = await tenantBRegistry.ListAsync();

            Assert.DoesNotContain(
                snapshots,
                snapshot => snapshot.RuntimeInstanceId == "tenant-a-runtime-1");
        }

        [Fact]
        public async Task GetAsync_Should_Return_Null_For_Dedicated_Instance_When_Tenant_Does_Not_Match()
        {
            var controlPlaneId = CreateControlPlaneId();
            await using var fixture = await RedisFixture.CreateAsync(controlPlaneId);

            var tenantARegistry = CreateRegistry(
                fixture.Redis,
                fixture.ControlPlaneIdResolver,
                tenantId: "tenant-a");

            await tenantARegistry.RegisterAsync(new AiRuntimeInstanceRegistration
            {
                RuntimeInstanceId = "tenant-a-runtime-1",
                WorkerCount = 10,
                QueueCapacity = 100,
                MaxConcurrentRuns = 5,
                Metadata = CreateDedicatedTenantMetadata("tenant-a")
            });

            var tenantBRegistry = CreateRegistry(
                fixture.Redis,
                fixture.ControlPlaneIdResolver,
                tenantId: "tenant-b");

            var snapshot = await tenantBRegistry.GetAsync("tenant-a-runtime-1");

            Assert.Null(snapshot);
        }

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