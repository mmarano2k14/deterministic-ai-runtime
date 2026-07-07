using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.Execution.Persistence.Replay.Metadata;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.DI;
using Multiplexed.AI.DI.AI;
using Multiplexed.AI.DI.Cleanup;
using Multiplexed.AI.DI.Engine;
using Multiplexed.AI.DI.Persistence;
using Multiplexed.AI.McpServer.Tools;
using Multiplexed.AI.Observability.Ledger;
using Multiplexed.AI.Runtime;
using Multiplexed.AI.Runtime.AI.Providers.Llm.OpenAI.DI;
using Multiplexed.AI.Runtime.AI.Rag.DI;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.AI.Runtime.Execution.Persistence.Replay.Metadata;
using Multiplexed.AI.Runtime.Execution.Retention.Policies;
using Multiplexed.AI.Runtime.Observability.Ledger.DI;
using Multiplexed.AI.Runtime.Pipeline.Steps.Test;
using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.Rbac.Core.Runtime.DI;
using Multiplexed.Rbac.Core.Runtime.Messaging.NServiceBus.DI;
using Multiplexed.Realtime.DI;
using StackExchange.Redis;

namespace Multiplexed.AI.McpServer.Host.Bootstrap
{
    /// <summary>
    /// Registers production AI runtime services required by the MCP host.
    /// </summary>
    public static class AiRuntimeServiceRegistration
    {
        /// <summary>
        /// Registers the production runtime services required by the DAG execution engine.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configuration">The application configuration.</param>
        /// <param name="options">The AI engine options.</param>
        public static void Register(
            IServiceCollection services,
            IConfiguration configuration,
            AiEngineOptions options)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(options);

            options.DefaultPipelineDefinitionSource = "Runtime";

            services.AddLogging();
            services.AddMemoryCache();

            var redisConnectionString =
                configuration.GetConnectionString("Redis")
                ?? "localhost:6379";

            services.AddSingleton<IConnectionMultiplexer>(_ =>
                ConnectionMultiplexer.Connect(redisConnectionString));

            services.AddMultiplexRealtime()
                .AddSignalRRealtimeTransport(realtimeOptions =>
                {
                    realtimeOptions.CorsPolicy = "SignalRcors";
                    realtimeOptions.AllowedOrigins =
                    [
                        "http://localhost:3000"
                    ];
                });

            EnsureSnapshotOptions(configuration, options);
            EnsurePayloadStoreOptions(configuration, options);

            services.AddMultiplexAI(options);

            ConfigureTenantRuntimeSettingsProvider(
                services,
                configuration);

            ConfigureDecisionLedger(
                services,
                configuration);

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
                .AddMultiplexedRbacAuthorizedServices(typeof(ReplayMcpTools).Assembly)
                .AddAiPromptRuntime(typeof(AiRuntimeAssemblyMarker).Assembly)
                .AddOpenAiPromptProvider(openAiOptions =>
                {
                    openAiOptions.ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                        ?? throw new InvalidOperationException("OPENAI_API_KEY is required.");
                });

            services.AddSingleton<McpRuntimeExecutionContextAccessor>();

            services.Replace(
                ServiceDescriptor.Singleton<IExecutionContextAccessor>(
                    serviceProvider =>
                        serviceProvider.GetRequiredService<McpRuntimeExecutionContextAccessor>()));

            services.AddSingleton<IExecutionContextSnapshotProvider>(
                serviceProvider =>
                    serviceProvider.GetRequiredService<McpRuntimeExecutionContextAccessor>());

            services.AddSingleton<TestStepAttemptTracker>();

            services.AddRagCore();

            if (options.Snapshots.Enabled && options.Snapshots.Mongo.Enabled)
            {
                services.AddAiExecutionSnapshots(options);
            }

            services.AddAiExecutionReplay();

            ConfigureReplayMetadataStore(
                services,
                configuration);

            ConfigureRuntimeRecoveryForensics(
                services,
                configuration);
        }

        /// <summary>
        /// Configures the tenant runtime settings provider used by admission, scale-out,
        /// host creation, registration metadata, capacity metadata, and visibility logic.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configuration">The application configuration.</param>
        /// <remarks>
        /// The production scenario framework can provide tenant runtime settings through
        /// configuration. This override must run after <c>AddMultiplexAI</c> because the
        /// default runtime registration may already have registered the hardcoded provider.
        /// </remarks>
        private static void ConfigureTenantRuntimeSettingsProvider(
            IServiceCollection services,
            IConfiguration configuration)
        {
            var provider =
                configuration["AiTenantRuntimeSettings:Provider"];

            if (!string.Equals(provider, "Configuration", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            services.RemoveAll<IAiTenantRuntimeSettingsProvider>();
            services.AddSingleton<IAiTenantRuntimeSettingsProvider, ConfigurationAiTenantRuntimeSettingsProvider>();
        }

        /// <summary>
        /// Configures the decision ledger provider.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configuration">The application configuration.</param>
        private static void ConfigureDecisionLedger(
            IServiceCollection services,
            IConfiguration configuration)
        {
            var provider =
                configuration["AiDecisionLedger:Provider"]
                ?? configuration["AiObservability:Ledger:Provider"]
                ?? "inmemory";

            services.RemoveAll<IAiDecisionLedger>();

            if (!string.Equals(provider, "mongo", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(provider, "mongodb", StringComparison.OrdinalIgnoreCase))
            {
                services.AddInMemoryAiDecisionLedger();
                return;
            }

            var connectionString =
                configuration.GetConnectionString("Mongo")
                ?? configuration["Mongo:ConnectionString"]
                ?? "mongodb://localhost:27017";

            var databaseName =
                configuration["Mongo:DatabaseName"]
                ?? "multiplexed-ai";

            services.TryAddSingleton<IMongoClient>(
                _ => new MongoClient(connectionString));

            services.AddMongoAiDecisionLedger(options =>
            {
                options.DatabaseName = databaseName;
                options.CollectionName = "ai_decision_ledger_entries";
                options.SequenceCollectionName = "ai_decision_ledger_sequences";
                options.CreateIndexes = true;
            });
        }

        /// <summary>
        /// Configures the replay metadata store provider.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configuration">The application configuration.</param>
        private static void ConfigureReplayMetadataStore(
            IServiceCollection services,
            IConfiguration configuration)
        {
            var provider =
                configuration["AiExecutionReplay:MetadataStore:Provider"]
                ?? configuration["AiReplay:MetadataStore:Provider"]
                ?? "inmemory";

            if (!string.Equals(provider, "mongo", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(provider, "mongodb", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var connectionString =
                configuration.GetConnectionString("Mongo")
                ?? configuration["Mongo:ConnectionString"]
                ?? "mongodb://localhost:27017";

            var databaseName =
                configuration["Mongo:DatabaseName"]
                ?? "multiplexed-ai";

            var collectionName =
                configuration["AiExecutionReplay:MetadataStore:Mongo:CollectionName"]
                ?? "ai_execution_replay_metadata";

            services.RemoveAll<IAiExecutionReplayMetadataStore>();

            services.TryAddSingleton<IMongoClient>(
                _ => new MongoClient(connectionString));

            services.AddSingleton<IAiExecutionReplayMetadataStore>(
                serviceProvider =>
                    new MongoAiExecutionReplayMetadataStore(
                        serviceProvider.GetRequiredService<IMongoClient>(),
                        databaseName,
                        collectionName));
        }

        /// <summary>
        /// Ensures Mongo snapshot persistence options are configured when Mongo snapshots are enabled.
        /// </summary>
        /// <param name="configuration">The application configuration.</param>
        /// <param name="options">The AI engine options.</param>
        private static void EnsureSnapshotOptions(
            IConfiguration configuration,
            AiEngineOptions options)
        {
            if (!options.Snapshots.Enabled || !options.Snapshots.Mongo.Enabled)
            {
                return;
            }

            options.Snapshots.Mongo.ConnectionString =
                options.Snapshots.Mongo.ConnectionString
                ?? configuration.GetConnectionString("Mongo")
                ?? throw new InvalidOperationException(
                    "Mongo snapshot persistence is enabled but no connection string was provided.");

            options.Snapshots.Mongo.DatabaseName =
                options.Snapshots.Mongo.DatabaseName
                ?? configuration["Mongo:DatabaseName"]
                ?? "multiplexed-ai";
        }

        /// <summary>
        /// Ensures Mongo payload store options are configured when payload externalization uses Mongo.
        /// </summary>
        /// <param name="configuration">The application configuration.</param>
        /// <param name="options">The AI engine options.</param>
        private static void EnsurePayloadStoreOptions(
            IConfiguration configuration,
            AiEngineOptions options)
        {
            var connectionString =
                configuration.GetConnectionString("Mongo")
                ?? configuration["Mongo:ConnectionString"]
                ?? "mongodb://localhost:27017";

            var databaseName =
                configuration["Mongo:DatabaseName"]
                ?? "multiplexed-ai";

            options.PayloadStore.Mongo.Enabled = true;
            options.PayloadStore.Mongo.ConnectionString ??= connectionString;
            options.PayloadStore.Mongo.DatabaseName ??= databaseName;
        }

        /// <summary>
        /// Configures the runtime recovery forensics store and read model provider.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configuration">The application configuration.</param>
        private static void ConfigureRuntimeRecoveryForensics(
            IServiceCollection services,
            IConfiguration configuration)
        {
            var provider =
                configuration["AiRuntimeRecoveryForensics:Provider"]
                ?? configuration["AiRuntimeRecoveryForensics:Store:Provider"]
                ?? "mongo";

            if (!string.Equals(provider, "mongo", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(provider, "mongodb", StringComparison.OrdinalIgnoreCase))
            {
                services.AddInMemoryAiRuntimeRecoveryForensics();
                return;
            }

            var connectionString =
                configuration.GetConnectionString("Mongo")
                ?? configuration["Mongo:ConnectionString"]
                ?? "mongodb://localhost:27017";

            var databaseName =
                configuration["Mongo:DatabaseName"]
                ?? "multiplexed-ai";

            var collectionName =
                configuration["AiRuntimeRecoveryForensics:Mongo:CollectionName"]
                ?? configuration["AiRuntimeRecoveryForensics:Store:Mongo:CollectionName"]
                ?? "ai_runtime_recovery_forensics";

            services.TryAddSingleton<IMongoClient>(
                _ => new MongoClient(connectionString));

            services.AddMongoAiRuntimeRecoveryForensics(
                configureMongo: options =>
                {
                    options.ConnectionString = connectionString;
                    options.DatabaseName = databaseName;
                    options.CollectionName = collectionName;
                    options.EnsureIndexes = true;
                });
        }
    }
}