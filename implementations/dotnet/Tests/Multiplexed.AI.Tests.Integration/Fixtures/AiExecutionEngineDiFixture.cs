using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.AI.ControlPlane.Signals;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Execution.Payloads.Mongo;
using Multiplexed.Abstractions.AI.Execution.Payloads.Redis;
using Multiplexed.Abstractions.AI.Execution.Payloads.Resolvers;
using Multiplexed.Abstractions.AI.Execution.Payloads.Stores;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.DI;
using Multiplexed.AI.DI.AI;
using Multiplexed.AI.DI.Cleanup;
using Multiplexed.AI.DI.Engine;
using Multiplexed.AI.DI.Persistence;
using Multiplexed.AI.DI.Persistence.Mongo;
using Multiplexed.AI.Runtime;
using Multiplexed.AI.Runtime.AI.Providers.Llm.OpenAI.DI;
using Multiplexed.AI.Runtime.Configuration;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.Discovery;
using Multiplexed.AI.Runtime.ControlPlane.Signals;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Retention.Policies;
using Multiplexed.AI.Runtime.Pipeline.Steps.Prompt;
using Multiplexed.AI.Tests.Fakes;
using Multiplexed.AI.Tests.Models;
using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.Rbac.Core.Runtime.DI;
using Multiplexed.Rbac.Core.Runtime.Messaging.NServiceBus.DI;
using Multiplexed.Realtime.DI;
using StackExchange.Redis;
using ExecutionContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;

namespace Multiplexed.AI.Tests.Integration.Runtime.Execution.Fixtures
{
    /// <summary>
    /// Creates fully wired DAG execution engines using the production dependency injection graph.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Build production-like runtime hosts for DAG execution integration tests.
    /// - Keep test wiring aligned with the real runtime dependency graph.
    /// - Provide optional test-specific service overrides without weakening the default graph.
    ///
    /// RETENTION:
    /// - Retention is now policy-driven and config-driven.
    /// - Legacy options-driven retention policies and services are no longer registered here.
    /// - Retention policies are discovered through the shared AI policy registration path.
    /// - Pipeline-level retention configuration is expected to be provided through pipeline config.
    /// </remarks>
    public static class AiDagExecutionEngineFixture
    {
        /// <summary>
        /// Creates a fully wired DAG execution engine integration test host.
        /// </summary>
        /// <param name="options">The AI engine options.</param>
        /// <param name="mongoConnectionString">Optional MongoDB connection string override.</param>
        /// <param name="mongoDatabaseName">Optional MongoDB database name override.</param>
        /// <param name="redisConnectionString">Optional Redis connection string override.</param>
        /// <returns>The created DAG execution engine test host.</returns>
        public static async Task<AiDagExecutionEngineTestHost> CreateAsync(
            AiEngineOptions options,
            string? mongoConnectionString = null,
            string? mongoDatabaseName = null,
            string? redisConnectionString = null)
        {
            return await CreateInternalAsync(
                options,
                configureServices: null,
                mongoConnectionString,
                mongoDatabaseName,
                redisConnectionString);
        }

        /// <summary>
        /// Creates a fully wired DAG execution engine integration test host and allows
        /// additional service registrations for specialized test scenarios.
        /// </summary>
        /// <remarks>
        /// PURPOSE:
        /// - Preserves the existing fixture entry point without breaking callers.
        /// - Enables targeted test-only registrations such as RAG providers,
        ///   retrievals, composers, or custom registries.
        ///
        /// IMPORTANT:
        /// - This overload is additive and does not change existing behavior.
        /// - The base production graph is always registered first.
        /// - The caller hook runs after production registration and before the
        ///   service provider is built.
        /// </remarks>
        /// <param name="options">The AI engine options.</param>
        /// <param name="configureServices">Additional test-specific service registrations.</param>
        /// <param name="mongoConnectionString">Optional MongoDB connection string override.</param>
        /// <param name="mongoDatabaseName">Optional MongoDB database name override.</param>
        /// <param name="redisConnectionString">Optional Redis connection string override.</param>
        /// <returns>The created DAG execution engine test host.</returns>
        public static async Task<AiDagExecutionEngineTestHost> CreateAsync(
            AiEngineOptions options,
            Action<IServiceCollection> configureServices,
            string? mongoConnectionString = null,
            string? mongoDatabaseName = null,
            string? redisConnectionString = null)
        {
            ArgumentNullException.ThrowIfNull(configureServices);

            return await CreateInternalAsync(
                options,
                configureServices,
                mongoConnectionString,
                mongoDatabaseName,
                redisConnectionString);
        }

        /// <summary>
        /// Dumps the current execution record and state for diagnostics.
        /// </summary>
        /// <param name="record">The execution record.</param>
        /// <param name="state">The execution state.</param>
        /// <returns>A human-readable diagnostic string.</returns>
        public static string DumpState(AiExecutionRecord? record, AiExecutionState? state)
        {
            if (record is null && state is null)
            {
                return "Record=null, State=null";
            }

            var lines = new List<string>
            {
                $"RecordStatus={record?.Status}",
                $"CurrentStep={record?.CurrentStep}",
                $"ExecutionStepKey={record?.ExecutionStepKey}",
                $"CompletedSteps=[{string.Join(", ", record?.CompletedSteps ?? new List<string>())}]"
            };

            if (state?.Steps is not null)
            {
                foreach (var step in state.Steps.Values.OrderBy(x => x.StepName, StringComparer.Ordinal))
                {
                    lines.Add(
                        $"Step={step.StepName}, " +
                        $"Status={step.Status}, " +
                        $"RetryCount={step.RetryState?.RetryCount}, " +
                        $"MaxRetries={step.Retry?.MaxRetries}, " +
                        $"NextRetryAtUtc={step.RetryState?.NextRetryAtUtc}, " +
                        $"ClaimedBy={step.ClaimedBy}, " +
                        $"ClaimToken={step.ClaimToken}, " +
                        $"DependsOn=[{string.Join(", ", step.DependsOn ?? new List<string>())}]");
                }
            }

            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>
        /// Creates a runtime RBAC context suitable for integration tests.
        /// </summary>
        /// <returns>The created RBAC execution context.</returns>
        private static ExecutionContext CreateRuntimeContext()
        {
            return new ExecutionContext
            {
                ContextKey = string.Empty,
                Project = "Project",
                TenantId = "tenant-id-xxxx",
                TenantGroupId = "tenant-group-id-xxx",
                CurrentNamespace = "Namespace",
                UserId = "userId",
                Namespaces = new List<NamespaceEntry>
                {
                    new NamespaceEntry
                    {
                        Name = "Namespace",
                        Trns = new HashSet<string>
                        {
                            "trn:Project:crm:billing:invoice:read",
                            "trn:Project:crm:billing:invoice:refund"
                        }
                    }
                },
                TtlSeconds = 300
            };
        }

        /// <summary>
        /// Shared fixture creation pipeline used by all public overloads.
        /// </summary>
        /// <remarks>
        /// PURPOSE:
        /// - Avoids duplication between the standard and extended <c>CreateAsync</c> overloads.
        /// - Preserves the existing host construction behavior.
        /// - Adds an optional post-registration hook for test-specific services.
        /// </remarks>
        /// <param name="options">The AI engine options.</param>
        /// <param name="configureServices">Optional additional service registration hook.</param>
        /// <param name="mongoConnectionString">Optional MongoDB connection string override.</param>
        /// <param name="mongoDatabaseName">Optional MongoDB database name override.</param>
        /// <param name="redisConnectionString">Optional Redis connection string override.</param>
        /// <returns>The created test host.</returns>
        private static async Task<AiDagExecutionEngineTestHost> CreateInternalAsync(
            AiEngineOptions options,
            Action<IServiceCollection>? configureServices,
            string? mongoConnectionString,
            string? mongoDatabaseName,
            string? redisConnectionString)
        {
            ArgumentNullException.ThrowIfNull(options);

            var services = new ServiceCollection();

            RegisterProductionServices(
                services,
                options,
                mongoConnectionString,
                mongoDatabaseName,
                redisConnectionString);

            configureServices?.Invoke(services);

            var rootProvider = services.BuildServiceProvider();
            var scope = rootProvider.CreateScope();
            var serviceProvider = scope.ServiceProvider;

            var accessor = serviceProvider.GetRequiredService<IExecutionContextAccessor>();

            if (accessor is not FakeInMemoryContextAccessor fakeAccessor)
            {
                throw new InvalidOperationException(
                    $"Resolved IExecutionContextAccessor is '{accessor.GetType().FullName}', expected '{typeof(FakeInMemoryContextAccessor).FullName}'.");
            }

            fakeAccessor.Set(CreateRuntimeContext());

            if (options.Snapshots.Enabled && options.Snapshots.Mongo.Enabled)
            {
                await serviceProvider.EnsureMongoAiExecutionSnapshotIndexesAsync<ExecutionContextSnapshot>(
                    options.Snapshots.Mongo);
            }

            var engine = serviceProvider.GetRequiredService<AiDagExecutionEngine>();

            return new AiDagExecutionEngineTestHost(
                rootProvider,
                scope,
                engine);
        }

        /// <summary>
        /// Registers the production runtime services required by the DAG execution engine.
        /// </summary>
        /// <remarks>
        /// IMPORTANT:
        /// - This fixture now registers retention through the policy engine path.
        /// - Legacy retention services, mode-based policies, triggers, and decision services are not registered here.
        /// - Tests that require retention must provide retention configuration through pipeline config.
        /// </remarks>
        /// <param name="services">The service collection to configure.</param>
        /// <param name="options">The AI engine options.</param>
        /// <param name="mongoConnectionString">Optional MongoDB connection string override.</param>
        /// <param name="mongoDatabaseName">Optional MongoDB database name override.</param>
        /// <param name="redisConnectionString">Optional Redis connection string override.</param>
        private static void RegisterProductionServices(
            IServiceCollection services,
            AiEngineOptions options,
            string? mongoConnectionString = null,
            string? mongoDatabaseName = null,
            string? redisConnectionString = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(options);

            services.AddLogging();
            services.AddMemoryCache();

            var configuration = new ConfigurationManager();

            services.TryAddSingleton<IConfiguration>(
                configuration);

            var resolvedRedisConnectionString =
                redisConnectionString ??
                "localhost:6379";

            services.AddSingleton<IConnectionMultiplexer>(_ =>
                ConnectionMultiplexer.Connect(resolvedRedisConnectionString));

            services.AddMultiplexRealtime()
                .AddSignalRRealtimeTransport(realtimeOptions =>
                {
                    realtimeOptions.CorsPolicy = "SignalRcors";
                    realtimeOptions.AllowedOrigins =
                    [
                        "http://localhost:3000"
                    ];
                });

            EnsureDefaultPayloadStoreOptions(
                options,
                mongoConnectionString,
                mongoDatabaseName);

            services.AddMultiplexAI(options);

            services.AddAiControlPlaneDiscoveryCore();
            services.AddAiRuntimeSignals();

            services.AddAiPoliciesFromAssemblies(
                typeof(AiRuntimeAssemblyMarker).Assembly,
                typeof(CompactAiRetentionPolicy).Assembly);

            services.AddAiExecutionCleanup(cleanup =>
            {
                cleanup.AutoCleanupOnCompleted = options.Cleanup.AutoCleanupOnCompleted;
                cleanup.AutoCleanupOnFailed = options.Cleanup.AutoCleanupOnFailed;
                cleanup.SuppressSnapshotIfExist = options.Cleanup.SuppressSnapshotIfExist;
                cleanup.SuppressCleanupExceptions = options.Cleanup.SuppressCleanupExceptions;
            });

            services.AddMultiplexedRbacRuntime(
                    configuration,
                    rbacOptions =>
                    {
                        rbacOptions.MaxInFlightPerContextKey = 10;
                        rbacOptions.AllowClientMaxInFlightOverride = true;
                        rbacOptions.DemoMaxInFlightHeader = "X-Demo-Max-InFlight";
                        rbacOptions.InFlightCounterTtl = TimeSpan.FromSeconds(30);
                        rbacOptions.LogConcurrencyViolations = true;
                        rbacOptions.UseRedisLuaScriptShaCaching = true;
                        rbacOptions.AllowClientRotationOverlapOverride = true;
                        rbacOptions.RotationOverlapWindowHeader = "X-Demo-Rotation-Overlap-Ms";
                        rbacOptions.RotationOverlapWindow = TimeSpan.FromMilliseconds(10000);
                    })
                .AddMultiplexedRbacHttp()
                .AddMultiplexedRbacNServiceBus()
                .AddAiPromptRuntime(typeof(AiRuntimeAssemblyMarker).Assembly)
                .AddOpenAiPromptProvider(openAiOptions =>
                {
                    openAiOptions.ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                        ?? throw new InvalidOperationException("OPENAI_API_KEY is required.");
                });

            services.Replace(ServiceDescriptor.Singleton<IExecutionContextAccessor, FakeInMemoryContextAccessor>());

            if (options.Snapshots.Enabled && options.Snapshots.Mongo.Enabled)
            {
                var connectionString =
                    mongoConnectionString ??
                    options.Snapshots.Mongo.ConnectionString ??
                    throw new InvalidOperationException(
                        "Mongo snapshot persistence is enabled but no connection string was provided.");

                var databaseName =
                    mongoDatabaseName ??
                    options.Snapshots.Mongo.DatabaseName ??
                    throw new InvalidOperationException(
                        "Mongo snapshot persistence is enabled but no database name was provided.");

                options.Snapshots.Mongo.ConnectionString = connectionString;
                options.Snapshots.Mongo.DatabaseName = databaseName;

                services.AddAiExecutionSnapshots(options);

                services.AddAiStepsFromAssemblies(typeof(AiRuntimeAssemblyMarker).Assembly);
            }

            services.AddAiExecutionReplay();
        }

        /// <summary>
        /// Ensures deterministic payload store defaults for integration tests.
        /// </summary>
        /// <param name="options">The AI engine options to update.</param>
        /// <param name="mongoConnectionString">Optional MongoDB connection string override.</param>
        /// <param name="mongoDatabaseName">Optional MongoDB database name override.</param>
        private static void EnsureDefaultPayloadStoreOptions(
            AiEngineOptions options,
            string? mongoConnectionString,
            string? mongoDatabaseName)
        {
            options.PayloadStore ??= new AiPayloadStoreOptions();

            options.PayloadStore.Enabled = true;

            if (string.IsNullOrWhiteSpace(options.PayloadStore.Provider) ||
                string.Equals(options.PayloadStore.Provider, "inmemory", StringComparison.OrdinalIgnoreCase))
            {
                options.PayloadStore.Provider = "mongo-redis";
            }

            options.PayloadStore.RequireReplaySafePayloads = true;

            if (options.PayloadStore.MaxInlineSizeBytes <= 0)
            {
                options.PayloadStore.MaxInlineSizeBytes = 512;
            }

            options.PayloadStore.Mongo ??= new MongoAiPayloadStoreOptions();
            options.PayloadStore.Mongo.Enabled = true;
            options.PayloadStore.Mongo.ConnectionString ??=
                mongoConnectionString ?? "mongodb://localhost:27017";
            options.PayloadStore.Mongo.DatabaseName ??=
                mongoDatabaseName ?? "multiplexed_ai_tests";
            options.PayloadStore.Mongo.CollectionName ??=
                $"payloads_tests_{Guid.NewGuid():N}";

            options.PayloadStore.RedisCache ??= new RedisAiPayloadCacheOptions();
            options.PayloadStore.RedisCache.Enabled = true;
            options.PayloadStore.RedisCache.KeyPrefix ??=
                $"test:ai:payload:{Guid.NewGuid():N}";

            if (options.PayloadStore.RedisCache.ExpirationSeconds <= 0)
            {
                options.PayloadStore.RedisCache.ExpirationSeconds = 120;
            }

            if (options.PayloadStore.RedisCache.MaxCacheablePayloadBytes <= 0)
            {
                options.PayloadStore.RedisCache.MaxCacheablePayloadBytes = 1024 * 1024;
            }

            options.PayloadStore.StepIndexCache ??= new RedisAiStepPayloadIndexCacheOptions();
            options.PayloadStore.StepIndexCache.Enabled = true;
            options.PayloadStore.StepIndexCache.KeyPrefix ??=
                $"test:ai:step-index:{Guid.NewGuid():N}";

            if (options.PayloadStore.StepIndexCache.ExpirationSeconds <= 0)
            {
                options.PayloadStore.StepIndexCache.ExpirationSeconds = 120;
            }

            options.PayloadStore.StepIndexCache.RefreshTtlOnRead = true;
        }
    }

    /// <summary>
    /// Represents a fully wired DAG execution engine test host.
    /// </summary>
    public sealed class AiDagExecutionEngineTestHost : IAsyncDisposable, IDisposable
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AiDagExecutionEngineTestHost"/> class.
        /// </summary>
        /// <param name="rootProvider">The root service provider.</param>
        /// <param name="scope">The active service scope.</param>
        /// <param name="engine">The DAG execution engine.</param>
        public AiDagExecutionEngineTestHost(
            ServiceProvider rootProvider,
            IServiceScope scope,
            AiDagExecutionEngine engine)
        {
            RootProvider = rootProvider ?? throw new ArgumentNullException(nameof(rootProvider));
            Scope = scope ?? throw new ArgumentNullException(nameof(scope));
            Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        /// <summary>
        /// Gets the root service provider.
        /// </summary>
        public ServiceProvider RootProvider { get; }

        /// <summary>
        /// Gets the active service scope.
        /// </summary>
        public IServiceScope Scope { get; }

        /// <summary>
        /// Gets the scoped service provider.
        /// </summary>
        public IServiceProvider ServiceProvider => Scope.ServiceProvider;

        /// <summary>
        /// Gets the DAG execution engine.
        /// </summary>
        public AiDagExecutionEngine Engine { get; }

        /// <inheritdoc />
        public void Dispose()
        {
            if (RootProvider.GetService<IConnectionMultiplexer>() is IDisposable multiplexer)
            {
                multiplexer.Dispose();
            }

            Scope.Dispose();
            RootProvider.Dispose();
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (RootProvider.GetService<IConnectionMultiplexer>() is IAsyncDisposable asyncMultiplexer)
            {
                await asyncMultiplexer.DisposeAsync();
            }
            else if (RootProvider.GetService<IConnectionMultiplexer>() is IDisposable multiplexer)
            {
                multiplexer.Dispose();
            }

            Scope.Dispose();

            if (RootProvider is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
                return;
            }

            RootProvider.Dispose();
        }

        /// <summary>
        /// Payload resolver used by tests where payload resolution is not expected.
        /// </summary>
        public sealed class NoopPayloadResolver : IAiExecutionPayloadResolver
        {
            /// <inheritdoc />
            public Task<object?> ResolveAsync(
                AiStoredPayload payload,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException(
                    "Payload resolution is not expected in this test.");
            }
        }
    }
}