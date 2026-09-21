using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Execution.Payloads.Resolvers;
using Multiplexed.Abstractions.AI.Execution.Payloads.Stores;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.McpServer.Host.Bootstrap;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod;
using Multiplexed.AI.Runtime.Publication;
using Multiplexed.Rbac.Core.Authorization.Engine;
using Multiplexed.Rbac.Core.Authorization.Trn;
using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.Rbac.Core.Runtime.DI;
using Multiplexed.Rbac.Core.Stores.Memory;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.KubernetesPool
{
    /// <summary>
    /// Exercises the matrix project settings, snapshot restoration and real publication RBAC guard.
    /// No Redis connection, Kubernetes cluster, worker process or publication artifact is created.
    /// </summary>
    public sealed class HttpKubernetesPoolExternalSdkRbacTests
    {
        private const string ProjectKey = "Multiplexed.Rbac.Core:Project";
        private const string ChildEnvironmentKey = "Multiplexed.Rbac.Core__Project";
        private const string ChildSettingKey = "AiKubernetesRuntimePoolHost:ChildEnvironmentVariables:" + ChildEnvironmentKey;
        private const string ExecuteTrn = "trn:matrix:default:code:publication:execute";
        private const string Denied = "The active RBAC context does not authorize this publication operation.";
        private static readonly AiDurableInvocationScope InvocationScope = new("matrix-tenant", "matrix-group", "matrix-control-plane");

        [Fact]
        public void Profile_Aligns_Three_Projects_Without_Adding_Grants_Or_Enabling_Child_Harness()
        {
            var settings = Settings();

            Assert.Equal(3, settings.Count);
            Assert.Equal("matrix", settings["AiMatrixHarness:Project"]);
            Assert.Equal("matrix", settings[ProjectKey]);
            Assert.Equal("matrix", settings[ChildSettingKey]);
            Assert.DoesNotContain(settings.Keys, key => key.Contains("Trns", StringComparison.Ordinal));
            Assert.DoesNotContain(settings.Keys, key => key.EndsWith("__Enabled", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Child_Project_Survives_Production_InPod_Arguments_And_Environment_Binding()
        {
            var snapshot = await SeedSnapshotAsync();
            var configuration = ChildConfiguration(Settings(), snapshot);

            Assert.Equal("matrix", configuration[ProjectKey]);
            Assert.Null(configuration["AiMatrixHarness:Enabled"]);
            Assert.Equal("matrix", snapshot.Project);
            Assert.Contains(ExecuteTrn, Assert.Single(snapshot.Namespaces).Trns);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Restored_Owner_Passes_Execute_Guard_But_Still_Requires_An_Immutable_Pin(bool child)
        {
            var snapshot = await SeedSnapshotAsync();
            var settings = Settings();
            var configuration = child
                ? ChildConfiguration(settings, snapshot)
                : new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            using var host = new AuthorizationHost(configuration);
            host.Accessor.Set(ExecutionContextSnapshotMapper.ToExecutionContext(snapshot));

            Assert.Equal(ExecuteTrn, host.Builder.Build("default", "code", "publication", "execute"));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Targets.ResolveAsync(Request()));

            Assert.Equal("No immutable publication binding was pinned before execution.", error.Message);
            Assert.Equal(2, host.Payloads.ReadCount); // Root pin and child binding, both deliberately absent.
            Assert.Equal(7, Assert.Single(host.Accessor.Current!.Namespaces).Trns.Count);
            Assert.Equal(snapshot.UserId, host.Accessor.Current.UserId);
            Assert.Equal(snapshot.ContextKey, host.Accessor.Current.ContextKey);
        }

        [Fact]
        public async Task Missing_Child_Project_Reproduces_Observed_Denial_Despite_Matrix_Snapshot()
        {
            var snapshot = await SeedSnapshotAsync();
            var settings = Settings();
            settings.Remove(ChildSettingKey);
            var configuration = ChildConfiguration(settings, snapshot);
            using var host = new AuthorizationHost(configuration);
            host.Accessor.Set(ExecutionContextSnapshotMapper.ToExecutionContext(snapshot));

            Assert.Null(configuration[ProjectKey]);
            Assert.Equal("matrix", host.Accessor.Current!.Project);
            Assert.Contains(ExecuteTrn, Assert.Single(host.Accessor.Current.Namespaces).Trns);
            Assert.Equal("trn:rbac-demo:default:code:publication:execute",
                host.Builder.Build("default", "code", "publication", "execute"));

            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Targets.ResolveAsync(Request()));
            Assert.Equal(Denied, error.Message);
            Assert.Equal(0, host.Payloads.ReadCount);
        }

        [Theory]
        [InlineData("rbac-demo")]
        [InlineData("other-project")]
        public async Task Explicit_Foreign_Host_Project_Does_Not_Authorize_Matrix_Grant(string project)
        {
            var snapshot = await SeedSnapshotAsync();
            var settings = Settings();
            settings[ChildSettingKey] = project;
            using var host = new AuthorizationHost(ChildConfiguration(settings, snapshot));
            host.Accessor.Set(ExecutionContextSnapshotMapper.ToExecutionContext(snapshot));

            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Targets.ResolveAsync(Request()));
            Assert.Equal(Denied, error.Message);
            Assert.Equal(0, host.Payloads.ReadCount);
        }

        [Fact]
        public async Task Aligned_Project_Does_Not_Replace_A_Missing_Execute_Grant()
        {
            var snapshot = await SeedSnapshotAsync();
            var restored = ExecutionContextSnapshotMapper.ToExecutionContext(snapshot);
            Assert.True(Assert.Single(restored.Namespaces).Trns.Remove(ExecuteTrn));
            Assert.Contains(ExecuteTrn, Assert.Single(snapshot.Namespaces).Trns);
            using var host = new AuthorizationHost(ChildConfiguration(Settings(), snapshot));
            host.Accessor.Set(restored);

            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Targets.ResolveAsync(Request()));
            Assert.Equal(Denied, error.Message);
            Assert.Equal(0, host.Payloads.ReadCount);
            Assert.Equal(6, Assert.Single(restored.Namespaces).Trns.Count);
        }

        [Theory]
        [InlineData("tenant")]
        [InlineData("group")]
        [InlineData("control-plane")]
        public async Task Aligned_Project_Does_Not_Bypass_Publication_Scope_Ownership(string component)
        {
            var snapshot = await SeedSnapshotAsync();
            using var host = new AuthorizationHost(ChildConfiguration(Settings(), snapshot));
            host.Accessor.Set(ExecutionContextSnapshotMapper.ToExecutionContext(snapshot));
            var foreign = component switch
            {
                "tenant" => InvocationScope with { TenantId = "other-tenant" },
                "group" => InvocationScope with { TenantGroupId = "other-group" },
                _ => InvocationScope with { ControlPlaneId = "other-control-plane" }
            };

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Targets.ResolveAsync(Request(foreign)));
            Assert.Equal(0, host.Payloads.ReadCount);
        }

        [Fact]
        public async Task Aligned_Project_Does_Not_Create_A_Trusted_Execution_Context()
        {
            var snapshot = await SeedSnapshotAsync();
            using var host = new AuthorizationHost(ChildConfiguration(Settings(), snapshot));
            host.Accessor.Clear();

            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Targets.ResolveAsync(Request()));
            Assert.Equal("No trusted publication context is active.", error.Message);
            Assert.Equal(0, host.Payloads.ReadCount);
        }

        [Fact]
        public async Task Seeded_Exact_Grants_Do_Not_Authorize_Unrelated_Actions_Or_Resources()
        {
            var snapshot = await SeedSnapshotAsync();
            using var host = new AuthorizationHost(ChildConfiguration(Settings(), snapshot));
            host.Accessor.Set(ExecutionContextSnapshotMapper.ToExecutionContext(snapshot));
            var authorization = host.Scope.ServiceProvider.GetRequiredService<IAuthorizationEngine>();

            Assert.True(authorization.IsAllowed("code", "publication", "execute"));
            Assert.False(authorization.IsAllowed("code", "publication", "delete"));
            Assert.False(authorization.IsAllowed("other-resource", "publication", "execute"));
            Assert.DoesNotContain(Assert.Single(host.Accessor.Current!.Namespaces).Trns, trn => trn.Contains('*'));
        }

        private static Dictionary<string, string?> Settings()
        {
            var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            HttpKubernetesPoolExternalSdkMatrixProfileTests.ConfigureRbacProject(settings);
            return settings;
        }

        private static AiDurableInvocationTargetRequest Request(AiDurableInvocationScope? scope = null) => new(
            scope ?? InvocationScope, "matrix-published-run", new string('a', 64), "matrix-pipeline", "1",
            "work", "published-work", "publication/work/v1", "python");

        private static async Task<ExecutionContextSnapshot> SeedSnapshotAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "matrix-rbac-" + Guid.NewGuid().ToString("N"));
            var manifestPath = Path.Combine(directory, "manifest.json");
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var contexts = new MemoryContextStore(cache, TimeSpan.FromHours(1));
            var settings = Settings();
            settings["AiMatrixHarness:Enabled"] = "true";
            settings["AiMatrixHarness:BearerToken"] = "matrix-rbac-test-only";
            settings["AiMatrixHarness:ManifestPath"] = manifestPath;
            settings["AiMatrixHarness:Topology"] = "kubernetes";
            var bootstrap = new MatrixHarnessBootstrapHostedService(contexts,
                new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
                new AiHostedInvocationEnvironmentSet("matrix-dotnet", "matrix-typescript", "matrix-python",
                    Array.Empty<AiPublicationEnvironment>()));
            var accessor = new McpRuntimeExecutionContextAccessor();
            var previous = accessor.Current;
            try
            {
                await bootstrap.StartAsync(CancellationToken.None);
                using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
                var context = await contexts.GetAsync(manifest.RootElement.GetProperty("accessContext").GetString()!);
                Assert.NotNull(context);
                accessor.Set(context);
                var snapshot = JsonSerializer.Deserialize<ExecutionContextSnapshot>(
                    JsonSerializer.Serialize(accessor.MapToSnapshot()))!;
                Assert.Equal(7, Assert.Single(snapshot.Namespaces).Trns.Count);
                Assert.Contains(ExecuteTrn, Assert.Single(snapshot.Namespaces).Trns);
                return snapshot;
            }
            finally
            {
                if (previous is null) accessor.Clear(); else accessor.Set(previous);
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
        }

        /// <summary>Uses the production command-line factory and the actual .NET environment provider.</summary>
        private static IConfigurationRoot ChildConfiguration(
            IDictionary<string, string?> settings, ExecutionContextSnapshot snapshot)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var host = new AiKubernetesRuntimePoolHostOptions
            {
                RuntimeImage = "runtime:matrix-rbac-test",
                RedisConnectionString = "redis:6379",
                MongoConnectionString = "mongodb://mongo:27017",
                MongoDatabaseName = "matrix-rbac-test",
                OpenAiApiKey = "matrix-rbac-test-not-used"
            };
            configuration.GetSection("AiKubernetesRuntimePoolHost").Bind(host);
            var pool = new AiKubernetesRuntimePoolOptions
            {
                Enabled = true, PoolId = "matrix-rbac-pool", Namespace = "ai-runtime",
                ProviderName = "http", TransportName = "http", InitialRuntimeInstanceCount = 1,
                MinimumRuntimeInstanceCount = 1, MaximumRuntimeInstanceCount = 1, MaximumPodCount = 1,
                StartupParallelism = 1, StableTransportPort = 8080, ReadinessPort = 8081,
                FirstChildTransportPort = 18080
            };
            var plan = AiKubernetesRuntimePoolPodPlanFactory.Create(pool, "matrix-rbac-request", "matrix-runtime-1");
            var spec = new AiKubernetesRuntimePoolPodSpecBuilder(pool, host).Build(plan);
            var request = new AiRuntimeHostStartRequest
            {
                RequestId = "matrix-scale-out", ControlPlaneId = InvocationScope.ControlPlaneId,
                PoolId = pool.PoolId, RuntimeInstanceId = "matrix-runtime-1", ProviderName = "http",
                TransportName = "http", WorkerCountPerInstance = 2, MaxConcurrentRunsPerInstance = 2,
                LocalQueueCapacity = 10, ExecutionContextSnapshot = snapshot
            };
            var arguments = new AiKubernetesRuntimePoolInPodCommandLineFactory(host).Create(spec, request);
            var inPod = new ConfigurationBuilder().AddCommandLine(arguments.ToArray()).Build()
                .GetSection("AiKubernetesRuntimePoolInPod").Get<AiKubernetesRuntimePoolInPodOptions>()!;
            Assert.Equal(snapshot.Project, inPod.Project);
            Assert.Equal(host.ChildEnvironmentVariables.Count, inPod.ChildEnvironmentVariables.Count);
            foreach (var pair in host.ChildEnvironmentVariables)
                Assert.Equal(pair.Value, inPod.ChildEnvironmentVariables[pair.Key]);

            // Unique prefixes prevent parallel tests from changing real application configuration.
            var prefix = "MATRIX_RBAC_TEST_" + Guid.NewGuid().ToString("N") + "_";
            try
            {
                foreach (var pair in inPod.ChildEnvironmentVariables)
                    Environment.SetEnvironmentVariable(prefix + pair.Key, pair.Value);
                return new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
            }
            finally
            {
                foreach (var pair in inPod.ChildEnvironmentVariables)
                    Environment.SetEnvironmentVariable(prefix + pair.Key, null);
            }
        }

        private sealed class AuthorizationHost : IDisposable
        {
            private readonly ServiceProvider _provider;
            private readonly Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext? _previous;
            internal McpRuntimeExecutionContextAccessor Accessor { get; } = new();
            internal MissingBindingsPayloadStore Payloads { get; } = new();
            internal IServiceScope Scope { get; }
            internal TrnBuilder Builder => _provider.GetRequiredService<TrnBuilder>();
            internal AiPublicationInvocationTargetResolver Targets { get; }

            internal AuthorizationHost(IConfiguration configuration)
            {
                _previous = Accessor.Current;
                var services = new ServiceCollection();
                services.AddLogging();
                // Real host registration, including the configured project and its default.
                // No context store or Redis multiplexer is resolved by these authorization checks.
                services.AddMultiplexedRbacRuntime(configuration);
                services.AddSingleton<IExecutionContextAccessor>(Accessor);
                _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
                Scope = _provider.CreateScope();
                var options = new AiPublicationOptions(
                    new AiPublicationCapability("code", "publication", "publish"),
                    new AiPublicationCapability("code", "publication", "read"),
                    new AiPublicationCapability("code", "publication", "execute"));
                var identity = new AiPublicationIdentity(Scope.ServiceProvider, Accessor, new FixedControlPlane());
                var publications = new AiImmutablePublicationStore(Payloads,
                    new AiConfiguredPublicationEnvironmentCatalog(Array.Empty<AiPublicationEnvironment>()), options);
                Targets = new AiPublicationInvocationTargetResolver(identity, options, publications);
            }

            public void Dispose()
            {
                if (_previous is null) Accessor.Clear(); else Accessor.Set(_previous);
                Scope.Dispose();
                _provider.Dispose();
            }
        }

        /// <summary>Counts reads after authorization; deliberately provides no publication run binding.</summary>
        private sealed class MissingBindingsPayloadStore : IAiPayloadStoreResolver, IAiImmutablePayloadStore
        {
            internal int ReadCount { get; private set; }
            public IAiPayloadStore Resolve() => this;
            public Task<string?> LoadAsync(string key, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadCount++;
                return Task.FromResult<string?>(null);
            }
            public Task<string> SaveAsync(string content, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("Authorization regression tests must not write payloads.");
            public Task<string> SaveImmutableAsync(string key, string content, AiPayloadMetadata metadata,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("Authorization regression tests must not write publication bindings.");
            public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("Authorization regression tests must not delete payloads.");
        }

        private sealed class FixedControlPlane : IAiControlPlaneIdResolver
        {
            public Task<string> ResolveAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(InvocationScope.ControlPlaneId);
            }
            public Task<string> ResolveAsync(AiControlPlaneIdResolutionRequest request,
                CancellationToken cancellationToken = default) => ResolveAsync(cancellationToken);
            public Task<IReadOnlyDictionary<string, string>> ResolveMetadataAsync(AiControlPlaneIdResolutionRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyDictionary<string, string>>(
                    new Dictionary<string, string> { ["controlPlaneId"] = InvocationScope.ControlPlaneId });
            }
        }
    }
}
