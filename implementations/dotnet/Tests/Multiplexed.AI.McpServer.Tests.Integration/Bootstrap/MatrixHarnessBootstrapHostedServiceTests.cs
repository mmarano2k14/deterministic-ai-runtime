using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.McpServer.Host.Bootstrap;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod;
using Multiplexed.Rbac.Core.ExecutionContext;
using RbacExecutionContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;

namespace Multiplexed.AI.McpServer.Tests.Integration.Bootstrap
{
    /// <summary>
    /// Exercises matrix context seeding through durable serialization, snapshot mapping and the
    /// production Kubernetes bootstrap argument/validation path without starting infrastructure.
    /// </summary>
    public sealed class MatrixHarnessBootstrapHostedServiceTests
    {
        [Theory]
        [InlineData("local")]
        [InlineData("docker")]
        [InlineData("kubernetes")]
        public async Task StartAsync_Should_Preserve_Default_Context_Ttl_Through_Kubernetes_Bootstrap(string topology)
        {
            using var scope = new BootstrapScope(topology);
            await scope.Service.StartAsync(CancellationToken.None);

            var snapshot = await ReadSnapshotAsync(scope, expectedTtlSeconds: 3600);
            var (arguments, options) = CreateInPodOptions(snapshot);

            Assert.Contains("--AiKubernetesRuntimePoolInPod:SnapshotTtlSeconds=3600", arguments);
            Assert.Equal(3600, options.SnapshotTtlSeconds);
            Assert.Equal(snapshot.ContextKey, options.ContextKey);
            Assert.Equal(snapshot.TenantId, options.TenantId);
            Assert.Equal(snapshot.TenantGroupId, options.TenantGroupId);
            Assert.Equal(snapshot.Project, options.Project);
            Assert.Equal(snapshot.UserId, options.UserId);
            Assert.Equal(snapshot.CurrentNamespace, options.CurrentNamespace);
            AiKubernetesRuntimePoolInPodOptionsValidator.Validate(options, requirePodUidFile: false);
        }

        [Theory]
        [InlineData(120)]
        [InlineData(1800)]
        public async Task StartAsync_Should_Preserve_Explicit_Positive_Context_Ttl(int ttlSeconds)
        {
            using var scope = new BootstrapScope("kubernetes", ttlSeconds);
            await scope.Service.StartAsync(CancellationToken.None);

            var snapshot = await ReadSnapshotAsync(scope, ttlSeconds);
            var (_, options) = CreateInPodOptions(snapshot);

            Assert.Equal(ttlSeconds, options.SnapshotTtlSeconds);
            AiKubernetesRuntimePoolInPodOptionsValidator.Validate(options, requirePodUidFile: false);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public async Task StartAsync_Should_Reject_Invalid_Context_Ttl_Before_Store_Or_Manifest(int ttlSeconds)
        {
            using var scope = new BootstrapScope("kubernetes", ttlSeconds);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => scope.Service.StartAsync(CancellationToken.None));

            Assert.Contains("AiMatrixHarness:ExecutionContextTtlSeconds", exception.Message);
            Assert.Equal(0, scope.Contexts.StoreCount);
            Assert.False(File.Exists(scope.ManifestPath));
            Assert.False(File.Exists(scope.ManifestPath + ".tmp"));
        }

        [Fact]
        public async Task StartAsync_Should_Not_Seed_Or_Validate_Disabled_Harness()
        {
            using var scope = new BootstrapScope("kubernetes", ttlSeconds: 0, enabled: false);
            await scope.Service.StartAsync(CancellationToken.None);

            Assert.Equal(0, scope.Contexts.StoreCount);
            Assert.False(File.Exists(scope.ManifestPath));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public async Task Kubernetes_Validator_Should_Still_Reject_Nonpositive_Snapshot_Ttl(int ttlSeconds)
        {
            using var scope = new BootstrapScope("kubernetes");
            await scope.Service.StartAsync(CancellationToken.None);
            var snapshot = await ReadSnapshotAsync(scope, expectedTtlSeconds: 3600);

            // All other bootstrap values are valid, so this reproduces only the observed defect.
            snapshot.TtlSeconds = ttlSeconds;
            var (arguments, options) = CreateInPodOptions(snapshot);
            Assert.Contains(
                "--AiKubernetesRuntimePoolInPod:SnapshotTtlSeconds=" + ttlSeconds.ToString(CultureInfo.InvariantCulture),
                arguments);
            var exception = Assert.Throws<ArgumentException>(
                () => AiKubernetesRuntimePoolInPodOptionsValidator.Validate(options, requirePodUidFile: false));

            Assert.Equal("options", exception.ParamName);
            Assert.Contains("snapshot values must be greater than zero", exception.Message);
        }

        private static async Task<ExecutionContextSnapshot> ReadSnapshotAsync(BootstrapScope scope, int expectedTtlSeconds)
        {
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(scope.ManifestPath));
            var key = manifest.RootElement.GetProperty("accessContext").GetString()!;
            Assert.Equal(expectedTtlSeconds, manifest.RootElement.GetProperty("executionContextTtlSeconds").GetInt32());
            Assert.Equal(1, scope.Contexts.StoreCount);

            var context = Assert.IsType<RbacExecutionContext>(await scope.Contexts.GetAsync(key));
            Assert.Equal(expectedTtlSeconds, context.TtlSeconds);
            Assert.Equal("matrix-tenant", context.TenantId);
            Assert.Equal("matrix-group", context.TenantGroupId);
            Assert.Equal("matrix-user", context.UserId);
            var namespaceEntry = Assert.Single(context.Namespaces);
            Assert.Equal("default", namespaceEntry.Name);
            Assert.Equal(7, namespaceEntry.Trns.Count);
            Assert.Contains("trn:matrix:default:code:publication:execute", namespaceEntry.Trns);
            Assert.Contains("trn:matrix:default:shared-run:execution:submit", namespaceEntry.Trns);

            var accessor = new McpRuntimeExecutionContextAccessor();
            accessor.Set(context);
            try
            {
                // Retain the actual mapping and JSON boundary used by durable execution state.
                var snapshot = accessor.MapToSnapshot();
                return JsonSerializer.Deserialize<ExecutionContextSnapshot>(JsonSerializer.Serialize(snapshot))!;
            }
            finally
            {
                accessor.Clear();
            }
        }

        private static (IReadOnlyList<string> Arguments, AiKubernetesRuntimePoolInPodOptions Options)
            CreateInPodOptions(ExecutionContextSnapshot snapshot)
        {
            var pool = new AiKubernetesRuntimePoolOptions
            {
                Enabled = true,
                PoolId = "matrix-bootstrap-pool",
                Namespace = "ai-runtime",
                ProviderName = "http",
                TransportName = "http",
                InitialRuntimeInstanceCount = 1,
                MinimumRuntimeInstanceCount = 1,
                MaximumRuntimeInstanceCount = 1,
                MaximumPodCount = 1,
                StartupParallelism = 1,
                StableTransportPort = 8080,
                ReadinessPort = 8081,
                FirstChildTransportPort = 18080
            };
            var host = new AiKubernetesRuntimePoolHostOptions
            {
                RuntimeImage = "runtime:matrix-test",
                RedisConnectionString = "redis:6379",
                MongoConnectionString = "mongodb://mongo:27017",
                MongoDatabaseName = "matrix-test",
                OpenAiApiKey = "matrix-test-not-used"
            };
            var plan = AiKubernetesRuntimePoolPodPlanFactory.Create(pool, "matrix-bootstrap-request", "matrix-runtime-1");
            var spec = new AiKubernetesRuntimePoolPodSpecBuilder(pool, host).Build(plan);
            var request = new AiRuntimeHostStartRequest
            {
                RequestId = "matrix-scale-out",
                ControlPlaneId = "matrix-control-plane",
                PoolId = pool.PoolId,
                RuntimeInstanceId = "matrix-runtime-1",
                ProviderName = "http",
                TransportName = "http",
                WorkerCountPerInstance = 2,
                MaxConcurrentRunsPerInstance = 2,
                LocalQueueCapacity = 10,
                ExecutionContextSnapshot = snapshot
            };
            var arguments = new AiKubernetesRuntimePoolInPodCommandLineFactory(host).Create(spec, request);
            var configuration = new ConfigurationBuilder().AddCommandLine(arguments.ToArray()).Build();
            var options = configuration.GetSection("AiKubernetesRuntimePoolInPod").Get<AiKubernetesRuntimePoolInPodOptions>()!;
            return (arguments, options);
        }

        private sealed class BootstrapScope : IDisposable
        {
            private readonly string _directory = Path.Combine(Path.GetTempPath(), "matrix-bootstrap-" + Guid.NewGuid().ToString("N"));
            public string ManifestPath => Path.Combine(_directory, "manifest.json");
            public RecordingContextStore Contexts { get; } = new();
            public MatrixHarnessBootstrapHostedService Service { get; }

            public BootstrapScope(string topology, int? ttlSeconds = null, bool enabled = true)
            {
                var settings = new Dictionary<string, string?>
                {
                    ["AiMatrixHarness:Enabled"] = enabled.ToString(),
                    ["AiMatrixHarness:BearerToken"] = "matrix-test-token",
                    ["AiMatrixHarness:ManifestPath"] = ManifestPath,
                    ["AiMatrixHarness:Topology"] = topology
                };
                if (ttlSeconds.HasValue)
                {
                    settings["AiMatrixHarness:ExecutionContextTtlSeconds"] = ttlSeconds.Value.ToString(CultureInfo.InvariantCulture);
                }
                Service = new MatrixHarnessBootstrapHostedService(
                    Contexts,
                    new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
                    new AiHostedInvocationEnvironmentSet(
                        "matrix-dotnet", "matrix-typescript", "matrix-python", Array.Empty<AiPublicationEnvironment>()));
            }

            public void Dispose()
            {
                if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
            }
        }

        /// <summary>Isolates storage I/O while retaining the serialized context contract; no Redis server is used.</summary>
        private sealed class RecordingContextStore : IContextStore
        {
            private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
            private readonly Dictionary<string, string> _contexts = new(StringComparer.Ordinal);
            public int StoreCount { get; private set; }

            public Task<string> StoreAsync(RbacExecutionContext context)
            {
                var key = "ctx_" + Guid.NewGuid().ToString("N");
                context.ContextKey = key;
                _contexts.Add(key, JsonSerializer.Serialize(context, JsonOptions));
                StoreCount++;
                return Task.FromResult(key);
            }

            public Task<RbacExecutionContext?> GetAsync(string key) => Task.FromResult(
                _contexts.TryGetValue(key, out var json)
                    ? JsonSerializer.Deserialize<RbacExecutionContext>(json, JsonOptions)
                    : null);

            public Task<string> SeedAsync(RbacExecutionContext context) => throw new NotSupportedException();
            public Task<bool> TryAcquireInFlightAsync(string key, int maxInFlight) => throw new NotSupportedException();
            public Task ReleaseInFlightAsync(string key) => throw new NotSupportedException();
            public Task<(string newKey, RbacExecutionContext context)> RotateAsync(string key, TimeSpan overlapWindow) => throw new NotSupportedException();
        }
    }
}
