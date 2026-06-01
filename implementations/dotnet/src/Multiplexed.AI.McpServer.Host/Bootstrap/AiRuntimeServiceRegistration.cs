using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.DI;
using Multiplexed.AI.DI.AI;
using Multiplexed.AI.DI.Cleanup;
using Multiplexed.AI.DI.Engine;
using Multiplexed.AI.DI.Persistence;
using Multiplexed.AI.Runtime;
using Multiplexed.AI.Runtime.AI.Providers.Llm.OpenAI.DI;
using Multiplexed.AI.Runtime.AI.Rag.DI;
using Multiplexed.AI.Runtime.DependencyInjection;
using Multiplexed.AI.Runtime.Execution.Retention.Policies;
using Multiplexed.AI.Runtime.Pipeline.Steps.Test;
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

            services.AddSingleton<TestStepAttemptTracker>();

            services.AddRagCore();



            if (options.Snapshots.Enabled && options.Snapshots.Mongo.Enabled)
            {
                services.AddAiExecutionSnapshots(options);

                
            }

            services.AddAiExecutionReplay();
        }

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
    }

}