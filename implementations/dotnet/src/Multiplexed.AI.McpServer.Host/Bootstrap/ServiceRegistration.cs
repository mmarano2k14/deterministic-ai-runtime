using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Pool;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Background;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.ControlPlane.RuntimeInstances.Pool;
using Multiplexed.AI.DI.Engine;
using Multiplexed.AI.McpServer.DependencyInjection;
using Multiplexed.AI.McpServer.Host.Configuration;
using Multiplexed.AI.Runtime;
using Multiplexed.AI.Runtime.ControlPlane.Admission.Reservations;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.Discovery;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Dispatch;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.Sample.External.Plugins.Steps.Steps;
using StackExchange.Redis;

namespace Multiplexed.AI.McpServer.Host.Bootstrap
{
    /// <summary>
    /// Registers application services for the MCP host.
    /// </summary>
    /// <remarks>
    /// The MCP host can run in several modes:
    ///
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// Control-plane only: exposes MCP/control-plane tools and shared queue pumping,
    /// but does not host local runtime instances.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// Control-plane with local runtime instances: hosts local runtime instances inside
    /// the same process and dispatches to them through the local provider.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// Control-plane with HTTP runtime instances: exposes MCP/control-plane tools and
    /// dispatches to remote runtime instances through the HTTP runtime instance provider.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// Runtime-instance only: hosts a runtime instance without enabling MCP control-plane tools.
    /// </description>
    /// </item>
    /// </list>
    /// </remarks>
    public static class ServiceRegistration
    {
        /// <summary>
        /// Configures all MCP host services.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configuration">The application configuration.</param>
        public static void Configure(
            IServiceCollection services,
            IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);

            services.AddHealthChecks();

            services.Configure<AiMcpHostOptions>(
                configuration.GetSection("AiMcpHost"));

            services.Configure<AiRuntimeInstanceRegistrationOptions>(
                configuration.GetSection("AiRuntimeInstanceRegistration"));

            services.Configure<AiLocalRuntimeInstancePoolOptions>(
                configuration.GetSection("AiLocalRuntimeInstancePool"));

            services.Configure<AiSharedRuntimeControllerOptions>(
                configuration.GetSection("AiSharedRuntimeController"));

            var hostOptions =
                configuration
                    .GetSection("AiMcpHost")
                    .Get<AiMcpHostOptions>()
                ?? new AiMcpHostOptions();

            var aiEngineOptions =
                new AiEngineOptions();

            configuration
                .GetSection("AiEngine")
                .Bind(aiEngineOptions);

            ApplyControlPlaneDiscoveryDefaults(
                aiEngineOptions,
                hostOptions);

            LogEffectiveHostConfiguration(
                configuration,
                hostOptions,
                aiEngineOptions,
                "[SERVICE REGISTRATION][INITIAL CONFIG]");

            LogHostedServiceRegistrations(
                services,
                "[SERVICE REGISTRATION][BEFORE AiRuntimeServiceRegistration.Register]");

            AiRuntimeServiceRegistration.Register(
                services,
                configuration,
                aiEngineOptions);

            LogHostedServiceRegistrations(
                services,
                "[SERVICE REGISTRATION][AFTER AiRuntimeServiceRegistration.Register]");

            switch (hostOptions.Mode)
            {
                case AiMcpHostMode.ControlPlaneOnly:
                    Console.WriteLine(
                        "[SERVICE REGISTRATION] Mode selected: ControlPlaneOnly.");

                    ConfigureControlPlaneOnly(
                        services,
                        configuration,
                        hostOptions);
                    break;

                case AiMcpHostMode.ControlPlaneWithLocalRuntimeInstances:
                    Console.WriteLine(
                        "[SERVICE REGISTRATION] Mode selected: ControlPlaneWithLocalRuntimeInstances.");

                    ConfigureControlPlaneWithLocalRuntimeInstances(
                        services,
                        configuration,
                        hostOptions);
                    break;

                case AiMcpHostMode.ControlPlaneWithHttpRuntimeInstances:
                    Console.WriteLine(
                        "[SERVICE REGISTRATION] Mode selected: ControlPlaneWithHttpRuntimeInstances.");

                    ConfigureControlPlaneWithHttpRuntimeInstances(
                        services,
                        configuration,
                        hostOptions);
                    break;

                case AiMcpHostMode.RuntimeInstanceOnly:
                    Console.WriteLine(
                        "[SERVICE REGISTRATION] Mode selected: RuntimeInstanceOnly.");

                    ConfigureRuntimeInstanceOnly(
                        services,
                        configuration,
                        hostOptions);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported host mode '{hostOptions.Mode}'.");
            }

            LogHostedServiceRegistrations(
                services,
                $"[SERVICE REGISTRATION][AFTER MODE {hostOptions.Mode}]");

            LogEffectiveHostConfiguration(
                configuration,
                hostOptions,
                aiEngineOptions,
                $"[SERVICE REGISTRATION][FINAL CONFIG {hostOptions.Mode}]");
        }

        /// <summary>
        /// Configures the host as a control-plane only MCP server.
        /// </summary>
        /// <remarks>
        /// This mode exposes MCP tools and can pump the shared queue, but it does not
        /// host local runtime instances.
        ///
        /// It is useful when runtime instances are hosted by other processes, pods,
        /// or external workers.
        /// </remarks>
        /// <param name="services">The service collection.</param>
        /// <param name="configuration">The application configuration.</param>
        /// <param name="hostOptions">The MCP host options.</param>
        private static void ConfigureControlPlaneOnly(
            IServiceCollection services,
            IConfiguration configuration,
            AiMcpHostOptions hostOptions)
        {
            Console.WriteLine(
                "[CONTROL PLANE ONLY] ConfigureControlPlaneOnly executed.");

            LogPoolConfiguration(
                configuration,
                "[CONTROL PLANE ONLY][BEFORE REGISTRATION]");

            services.AddAiControlPlane(
                configureRuntimeExecutionRecoveryReconciliation: options =>
                {
                    ConfigureRuntimeExecutionRecoveryReconciliationOptions(
                        configuration,
                        options);
                },
                configureAdmission: options =>
                {
                    ConfigureAdmissionOptions(
                        configuration,
                        options);
                });

            AddRedisControlPlaneStoresIfAvailable(
                services,
                configuration);

            services.AddAiMcpServer();

            services.AddAiControlPlaneDiscoveryCore();
            services.AddAiControlPlaneDiscoveryPublisher();

            ConfigureSharedQueueBackgroundService(
                services,
                configuration,
                hostOptions);

            services.AddAiMcpControlPlaneHost(options =>
            {
                options.Enabled = true;
                options.EnableSharedQueuePump = hostOptions.EnableSharedQueuePump;
                options.WorkerId = "mcp-background-pump";
            });

            services.AddAiRuntimeInstanceRegistrationHostedService(options =>
            {
                options.Enabled = true;
                options.RuntimeInstanceId = "mcp-control-plane";
                options.Role = AiRuntimeInstanceRole.ControlPlane;
            });

            ConfigureScaleOutRequestWatcher(
                services,
                configuration,
                hostOptions);

            LogHostedServiceRegistrations(
                services,
                "[CONTROL PLANE ONLY][AFTER REGISTRATION]");
        }

        /// <summary>
        /// Configures the host as a control-plane with local runtime instances.
        /// </summary>
        /// <remarks>
        /// This mode hosts runtime instances inside the same MCP host process.
        ///
        /// Dispatch uses LocalAiRuntimeInstanceProvider through the shared
        /// runtime instance registry. Runtime instances still own their own local
        /// queues, workers, and DAG execution engines.
        /// </remarks>
        /// <param name="services">The service collection.</param>
        /// <param name="configuration">The application configuration.</param>
        /// <param name="hostOptions">The MCP host options.</param>
        private static void ConfigureControlPlaneWithLocalRuntimeInstances(
            IServiceCollection services,
            IConfiguration configuration,
            AiMcpHostOptions hostOptions)
        {
            Console.WriteLine(
                "[CONTROL PLANE LOCAL] ConfigureControlPlaneWithLocalRuntimeInstances executed.");

            LogPoolConfiguration(
                configuration,
                "[CONTROL PLANE LOCAL][BEFORE REGISTRATION]");

            services.AddAiControlPlane(
                configureRuntimeExecutionRecoveryReconciliation: options =>
                {
                    ConfigureRuntimeExecutionRecoveryReconciliationOptions(
                        configuration,
                        options);
                },
                configureAdmission: options =>
                {
                    ConfigureAdmissionOptions(
                        configuration,
                        options);
                });

            AddRedisControlPlaneStoresIfAvailable(
                services,
                configuration);

            services.AddAiControlPlaneDiscoveryCore();
            services.AddAiControlPlaneDiscoveryPublisher();

            services.RemoveAll<IAiSharedRunDispatcher>();
            services.AddSingleton<IAiSharedRunDispatcher, RemoteAiSharedRunDispatcher>();

            services.AddAiMcpServer();

            ConfigureSharedQueueBackgroundService(
                services,
                configuration,
                hostOptions);

            services.AddAiMcpControlPlaneHost(options =>
            {
                options.Enabled = true;
                options.EnableSharedQueuePump = hostOptions.EnableSharedQueuePump;
                options.RuntimeInstanceId = "mcp-control-plane";
                options.WorkerId = "mcp-background-pump";
            });

            services.AddAiStepsFromAssemblies(
                typeof(AiRuntimeAssemblyMarker).Assembly,
                typeof(DistributedChaosFlakyProviderStep).Assembly);

            Console.WriteLine(
                "[CONTROL PLANE LOCAL] Registering AiLocalRuntimeInstancePoolHostedService through AddAiLocalRuntimeInstancePool.");

            services.AddAiLocalRuntimeInstancePool();

            services.AddAiRuntimeInstanceRegistrationHostedService(options =>
            {
                options.Enabled = true;
                options.RuntimeInstanceId = "mcp-control-plane";
                options.Role = AiRuntimeInstanceRole.ControlPlane;
            });

            ConfigureScaleOutRequestWatcher(
                services,
                configuration,
                hostOptions);

            LogHostedServiceRegistrations(
                services,
                "[CONTROL PLANE LOCAL][AFTER REGISTRATION]");
        }

        /// <summary>
        /// Configures the host as a control-plane that dispatches to HTTP runtime instances.
        /// </summary>
        /// <remarks>
        /// This mode is used to test or run MCP/control-plane separately from runtime
        /// instances that are addressable through HTTP.
        ///
        /// Dispatch uses HttpAiRuntimeInstanceProvider when runtime capacity
        /// descriptors publish:
        ///
        /// <code>
        /// provider.name = http
        /// transport.endpoint = http://runtime-instance-1:8081
        /// </code>
        ///
        /// The control-plane still uses <see cref="IAiSharedRunDispatcher"/>.
        /// The dispatcher resolves the provider through the centralized provider
        /// capability resolver.
        ///
        /// This mode does not host local runtime instances. The target runtime
        /// instance process must expose the HTTP command endpoint:
        ///
        /// <code>
        /// POST /runtime-instance/commands
        /// </code>
        /// </remarks>
        /// <param name="services">The service collection.</param>
        /// <param name="configuration">The application configuration.</param>
        /// <param name="hostOptions">The MCP host options.</param>
        private static void ConfigureControlPlaneWithHttpRuntimeInstances(
            IServiceCollection services,
            IConfiguration configuration,
            AiMcpHostOptions hostOptions)
        {
            Console.WriteLine(
                "[CONTROL PLANE HTTP] ConfigureControlPlaneWithHttpRuntimeInstances executed.");

            LogPoolConfiguration(
                configuration,
                "[CONTROL PLANE HTTP][BEFORE REGISTRATION]");

            LogHostedServiceRegistrations(
                services,
                "[CONTROL PLANE HTTP][ENTRY]");

            services.AddAiControlPlane(
                configureRuntimeExecutionRecoveryReconciliation: options =>
                {
                    ConfigureRuntimeExecutionRecoveryReconciliationOptions(
                        configuration,
                        options);
                },
                configureAdmission: options =>
                {
                    ConfigureAdmissionOptions(
                        configuration,
                        options);
                });

            LogHostedServiceRegistrations(
                services,
                "[CONTROL PLANE HTTP][AFTER AddAiControlPlane]");

            AddRedisControlPlaneStoresIfAvailable(
                services,
                configuration);

            services.AddAiControlPlaneDiscoveryCore();
            services.AddAiControlPlaneDiscoveryPublisher();

            LogHostedServiceRegistrations(
                services,
                "[CONTROL PLANE HTTP][AFTER AddRedisControlPlaneStoresIfAvailable]");

            services.RemoveAll<IAiSharedRunDispatcher>();
            services.AddSingleton<IAiSharedRunDispatcher, RemoteAiSharedRunDispatcher>();

            services.AddAiHttpRuntimeInstanceProvider();

            LogHostedServiceRegistrations(
                services,
                "[CONTROL PLANE HTTP][AFTER AddAiHttpRuntimeInstanceProvider]");

            services.AddAiMcpServer();

            LogHostedServiceRegistrations(
                services,
                "[CONTROL PLANE HTTP][AFTER AddAiMcpServer]");

            ConfigureSharedQueueBackgroundService(
                services,
                configuration,
                hostOptions);

            LogHostedServiceRegistrations(
                services,
                "[CONTROL PLANE HTTP][AFTER ConfigureSharedQueueBackgroundService]");

            services.AddAiMcpControlPlaneHost(options =>
            {
                options.Enabled = true;
                options.EnableSharedQueuePump = hostOptions.EnableSharedQueuePump;
                options.RuntimeInstanceId = "mcp-control-plane";
                options.WorkerId = "mcp-background-pump";
            });

            LogHostedServiceRegistrations(
                services,
                "[CONTROL PLANE HTTP][AFTER AddAiMcpControlPlaneHost]");

            services.AddAiRuntimeInstanceRegistrationHostedService(options =>
            {
                options.Enabled = true;
                options.RuntimeInstanceId = "mcp-control-plane";
                options.Role = AiRuntimeInstanceRole.ControlPlane;
            });

            LogHostedServiceRegistrations(
                services,
                "[CONTROL PLANE HTTP][AFTER AddAiRuntimeInstanceRegistrationHostedService]");

            ConfigureScaleOutRequestWatcher(
                services,
                configuration,
                hostOptions);

            LogHostedServiceRegistrations(
                services,
                "[CONTROL PLANE HTTP][AFTER ConfigureScaleOutRequestWatcher]");

            LogPoolConfiguration(
                configuration,
                "[CONTROL PLANE HTTP][AFTER REGISTRATION]");
        }

        /// <summary>
        /// Configures the host as a runtime-instance only process.
        /// </summary>
        /// <remarks>
        /// This mode hosts runtime execution capacity without enabling MCP control-plane tools.
        ///
        /// When <c>AiLocalRuntimeInstancePool:Enabled</c> is disabled, the host registers itself
        /// as a single runtime instance and starts one local runtime pipeline background controller.
        ///
        /// When <c>AiLocalRuntimeInstancePool:Enabled</c> is enabled, the host starts an internal
        /// runtime instance pool instead. In that mode, the parent host identity must not be
        /// registered as a dispatchable runtime instance; only the child runtime instances created
        /// by the pool should be visible to admission.
        /// </remarks>
        /// <param name="services">The service collection.</param>
        /// <param name="configuration">The application configuration.</param>
        /// <param name="hostOptions">The MCP host options.</param>
        private static void ConfigureRuntimeInstanceOnly(
            IServiceCollection services,
            IConfiguration configuration,
            AiMcpHostOptions hostOptions)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(hostOptions);

            Console.WriteLine(
                "[RUNTIME INSTANCE ONLY] ConfigureRuntimeInstanceOnly executed.");

            LogPoolConfiguration(
                configuration,
                "[RUNTIME INSTANCE ONLY][BEFORE REGISTRATION]");

            var poolOptions =
                configuration
                    .GetSection("AiLocalRuntimeInstancePool")
                    .Get<AiLocalRuntimeInstancePoolOptions>()
                ?? new AiLocalRuntimeInstancePoolOptions();

            services.AddAiControlPlane();

            AddRedisControlPlaneStoresIfAvailable(
                services,
                configuration);

            Console.WriteLine(
                "[RUNTIME INSTANCE ONLY] Redis control-plane stores registered when available.");

            services.AddAiControlPlaneDiscoveryCore();

            services.AddAiRuntimeInstanceHttpCommandHandling();

            Console.WriteLine(
                "[RUNTIME INSTANCE ONLY] Registered runtime HTTP command handling services.");

            LogHostedServiceRegistrations(
                services,
                "[RUNTIME INSTANCE ONLY][AFTER AddAiControlPlane]");

            services.Configure<AiSharedQueueBackgroundServiceOptions>(options =>
            {
                options.Enabled = false;
            });

            services.Configure<AiSharedQueuePumpOptions>(options =>
            {
                options.Enabled = false;
            });

            services.AddAiMcpControlPlaneHost(options =>
            {
                options.Enabled = false;
                options.EnableSharedQueuePump = false;
                options.RuntimeInstanceId = "runtime-instance";
                options.WorkerId = "runtime-instance-worker";
            });

            LogHostedServiceRegistrations(
                services,
                "[RUNTIME INSTANCE ONLY][AFTER AddAiMcpControlPlaneHost]");

            services.AddAiStepsFromAssemblies(
                typeof(AiRuntimeAssemblyMarker).Assembly,
                typeof(DistributedChaosFlakyProviderStep).Assembly);

            if (poolOptions.Enabled)
            {
                Console.WriteLine(
                    $"[RUNTIME INSTANCE ONLY] Local runtime instance pool enabled. InstanceCount='{poolOptions.InstanceCount}', RuntimeInstanceIdPrefix='{poolOptions.RuntimeInstanceIdPrefix}'.");

                services.AddAiLocalRuntimeInstancePool();

                Console.WriteLine(
                    "[RUNTIME INSTANCE ONLY] Registered AiLocalRuntimeInstancePoolHostedService.");

                LogHostedServiceRegistrations(
                    services,
                    "[RUNTIME INSTANCE ONLY][AFTER AddAiLocalRuntimeInstancePool]");

                return;
            }

            services.AddHostedService<AiRuntimePipelineBackgroundControllerHostedService>();

            Console.WriteLine(
                "[RUNTIME INSTANCE ONLY] Registered AiRuntimePipelineBackgroundControllerHostedService.");

            services.AddAiRuntimeInstanceRegistrationHostedService();

            Console.WriteLine(
                "[RUNTIME INSTANCE ONLY] Registered AiRuntimeInstanceRegistrationHostedService.");

            LogHostedServiceRegistrations(
                services,
                "[RUNTIME INSTANCE ONLY][AFTER SINGLE RUNTIME REGISTRATION]");
        }

        /// <summary>
        /// Registers Redis-backed control-plane stores when Redis is available.
        /// </summary>
        /// <remarks>
        /// This keeps <see cref="AddAiControlPlane"/> safe as the default in-memory baseline,
        /// while allowing MCP control-plane modes to switch selected control-plane stores
        /// to Redis when a Redis connection is configured.
        ///
        /// IMPORTANT:
        /// Redis-backed stores are intentionally enabled one by one for now.
        /// This makes it possible to validate each store independently:
        /// - shared run store
        /// - shared queue
        /// - admission reservation store
        /// - scale-out request store
        /// </remarks>
        /// <param name="services">The service collection.</param>
        /// <param name="configuration">The application configuration.</param>
        private static void AddRedisControlPlaneStoresIfAvailable(
            IServiceCollection services,
            IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);

            var redisConnectionString =
                GetRedisConnectionString(configuration);

            if (!string.IsNullOrWhiteSpace(redisConnectionString))
            {
                Console.WriteLine(
                    "[REDIS CONTROL PLANE] Redis connection string detected. Registering IConnectionMultiplexer if missing.");

                services.TryAddSingleton<IConnectionMultiplexer>(
                    _ => ConnectionMultiplexer.Connect(redisConnectionString));
            }
            else
            {
                Console.WriteLine(
                    "[REDIS CONTROL PLANE] No Redis connection string detected.");
            }

            if (!HasRedisConnectionMultiplexer(services))
            {
                Console.WriteLine(
                    "[REDIS CONTROL PLANE] Redis multiplexer not registered. Redis control-plane stores skipped.");

                return;
            }

            var keyPrefix =
                GetRedisKeyPrefix(configuration);

            Console.WriteLine(
                $"[REDIS CONTROL PLANE] Redis multiplexer available. KeyPrefix='{keyPrefix}'. Registering Redis shared stores.");

            services.AddRedisAiSharedRunStore(options =>
            {
                options.KeyPrefix = keyPrefix;
            });

            services.AddRedisAiSharedQueue(options =>
            {
                options.KeyPrefix = keyPrefix;
            });

            services.AddAiRedisRuntimeRunExecutionIndex(options =>
            {
                options.KeyPrefix = keyPrefix;
            });

            services.AddRedisAiRuntimeAdmissionReservationStore(options =>
            {
                options.KeyPrefix = keyPrefix;
                options.ReservationTtl = TimeSpan.FromMinutes(2);
                options.KeyTtl = TimeSpan.FromMinutes(10);
            });

            services.AddRedisAiRuntimeScaleOutRequestStore(options =>
            {
                options.KeyPrefix = keyPrefix;
                options.DefaultTtl = TimeSpan.FromMinutes(30);
                options.DeduplicationWindow = TimeSpan.FromSeconds(30);
                options.MaxListResults = 500;
                options.DefaultIndexScanLimit = 1_000;
                options.EnableDeduplication = true;
            });
        }

        /// <summary>
        /// Gets the Redis connection string from known configuration locations.
        /// </summary>
        /// <param name="configuration">The application configuration.</param>
        /// <returns>The configured Redis connection string, or null when no connection string exists.</returns>
        private static string? GetRedisConnectionString(
            IConfiguration configuration)
        {
            return configuration.GetConnectionString("Redis") ??
                   configuration["Redis:ConnectionString"] ??
                   configuration["AiRedis:ConnectionString"];
        }

        /// <summary>
        /// Gets the Redis key prefix used by control-plane stores.
        /// </summary>
        /// <param name="configuration">The application configuration.</param>
        /// <returns>The Redis key prefix.</returns>
        private static string GetRedisKeyPrefix(
            IConfiguration configuration)
        {
            return configuration["AiRedis:KeyPrefix"] ??
                   configuration["Redis:KeyPrefix"] ??
                   "multiplexed:ai";
        }

        /// <summary>
        /// Determines whether a Redis connection multiplexer is already registered.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <returns>True when Redis is available through dependency injection.</returns>
        private static bool HasRedisConnectionMultiplexer(
            IServiceCollection services)
        {
            return services.Any(descriptor =>
                descriptor.ServiceType == typeof(IConnectionMultiplexer));
        }

        /// <summary>
        /// Writes the currently registered hosted services to the console for integration-test diagnostics.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="prefix">The diagnostic log prefix.</param>
        private static void LogHostedServiceRegistrations(
            IServiceCollection services,
            string prefix)
        {
            var hostedServices =
                services
                    .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
                    .Select(descriptor =>
                        descriptor.ImplementationType?.FullName ??
                        descriptor.ImplementationInstance?.GetType().FullName ??
                        descriptor.ImplementationFactory?.Method.ReturnType.FullName ??
                        "factory")
                    .ToArray();

            Console.WriteLine(
                $"{prefix} IHostedService registrations: " +
                string.Join(" | ", hostedServices));
        }

        /// <summary>
        /// Writes the effective host and runtime configuration used during service registration.
        /// </summary>
        /// <param name="configuration">The application configuration.</param>
        /// <param name="hostOptions">The MCP host options.</param>
        /// <param name="aiEngineOptions">The AI engine options.</param>
        /// <param name="prefix">The diagnostic log prefix.</param>
        private static void LogEffectiveHostConfiguration(
            IConfiguration configuration,
            AiMcpHostOptions hostOptions,
            AiEngineOptions aiEngineOptions,
            string prefix)
        {
            var poolOptions =
                configuration
                    .GetSection("AiLocalRuntimeInstancePool")
                    .Get<AiLocalRuntimeInstancePoolOptions>()
                ?? new AiLocalRuntimeInstancePoolOptions();

            var registrationOptions =
                configuration
                    .GetSection("AiRuntimeInstanceRegistration")
                    .Get<AiRuntimeInstanceRegistrationOptions>()
                ?? new AiRuntimeInstanceRegistrationOptions();

            Console.WriteLine(
                $"{prefix} Effective host configuration. " +
                $"Mode='{hostOptions.Mode}', " +
                $"EnableSharedQueuePump='{hostOptions.EnableSharedQueuePump}', " +
                $"Registration.RuntimeInstanceId='{registrationOptions.RuntimeInstanceId}', " +
                $"Registration.Role='{registrationOptions.Role}', " +
                $"Registration.ProviderName='{registrationOptions.ProviderName}', " +
                $"Pool.Enabled='{poolOptions.Enabled}', " +
                $"Pool.InstanceCount='{poolOptions.InstanceCount}', " +
                $"Pool.WorkerCountPerInstance='{poolOptions.WorkerCountPerInstance}', " +
                $"Pool.MaxConcurrentRunsPerInstance='{poolOptions.MaxConcurrentRunsPerInstance}', " +
                $"Pool.RuntimeInstanceIdPrefix='{poolOptions.RuntimeInstanceIdPrefix}'.");
        }

        /// <summary>
        /// Writes the effective local runtime instance pool configuration.
        /// </summary>
        /// <param name="configuration">The application configuration.</param>
        /// <param name="prefix">The diagnostic log prefix.</param>
        private static void LogPoolConfiguration(
            IConfiguration configuration,
            string prefix)
        {
            var poolOptions =
                configuration
                    .GetSection("AiLocalRuntimeInstancePool")
                    .Get<AiLocalRuntimeInstancePoolOptions>()
                ?? new AiLocalRuntimeInstancePoolOptions();

            Console.WriteLine(
                $"{prefix} Local runtime instance pool configuration. " +
                $"Enabled='{poolOptions.Enabled}', " +
                $"InstanceCount='{poolOptions.InstanceCount}', " +
                $"WorkerCountPerInstance='{poolOptions.WorkerCountPerInstance}', " +
                $"MaxConcurrentRunsPerInstance='{poolOptions.MaxConcurrentRunsPerInstance}', " +
                $"LocalQueueCapacity='{poolOptions.LocalQueueCapacity?.ToString() ?? "unlimited"}', " +
                $"RuntimeInstanceIdPrefix='{poolOptions.RuntimeInstanceIdPrefix}'.");
        }

        /// <summary>
        /// Configures the shared queue background service and pump options.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configuration">The application configuration.</param>
        /// <param name="hostOptions">The MCP host options.</param>
        private static void ConfigureSharedQueueBackgroundService(
            IServiceCollection services,
            IConfiguration configuration,
            AiMcpHostOptions hostOptions)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(hostOptions);

            services.Configure<AiSharedQueueBackgroundServiceOptions>(options =>
            {
                configuration
                    .GetSection("AiSharedQueueBackgroundService")
                    .Bind(options);
            });

            services.Configure<AiSharedQueuePumpOptions>(options =>
            {
                configuration
                    .GetSection("AiSharedQueuePump")
                    .Bind(options);
            });
        }

        /// <summary>
        /// Configures run admission options for MCP control-plane modes.
        /// </summary>
        /// <remarks>
        /// Configuration values from the <c>AiRunAdmission</c> section are applied first.
        ///
        /// IMPORTANT:
        /// - Defaults are only applied when the corresponding configuration key is missing.
        /// - Tests and deployment configuration must be able to force scale-out behavior,
        ///   global queue fallback behavior, or rejection behavior explicitly.
        /// </remarks>
        /// <param name="configuration">The application configuration.</param>
        /// <param name="options">The run admission options.</param>
        private static void ConfigureAdmissionOptions(
            IConfiguration configuration,
            AiRunAdmissionOptions options)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(options);

            var section =
                configuration.GetSection("AiRunAdmission");

            section.Bind(options);

            if (section["EnableGlobalQueueFallback"] is null)
            {
                options.EnableGlobalQueueFallback = true;
            }

            if (section["RejectWhenNoCapacity"] is null)
            {
                options.RejectWhenNoCapacity = false;
            }
        }

        /// <summary>
        /// Configures runtime execution recovery reconciliation options for MCP control-plane modes.
        /// </summary>
        /// <remarks>
        /// Configuration values from the <c>AiRuntimeExecutionRecoveryReconciliation</c> section are applied first.
        ///
        /// Runtime execution recovery is intentionally separate from runtime instance health reconciliation.
        /// Health reconciliation prevents unsafe routing. Execution recovery requeues work that was already
        /// assigned to failed, stopped, draining, or unavailable runtime instances.
        /// </remarks>
        /// <param name="configuration">The application configuration.</param>
        /// <param name="options">The runtime execution recovery reconciliation options.</param>
        private static void ConfigureRuntimeExecutionRecoveryReconciliationOptions(
            IConfiguration configuration,
            AiRuntimeExecutionRecoveryReconciliationOptions options)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(options);

            var section =
                configuration.GetSection("AiRuntimeExecutionRecoveryReconciliation");

            section.Bind(options);

            if (section["Enabled"] is null)
            {
                options.Enabled = true;
            }

            if (section["IncludeUnhealthyRuntimeInstances"] is null)
            {
                options.IncludeUnhealthyRuntimeInstances = true;
            }

            if (section["IncludeStoppedRuntimeInstances"] is null)
            {
                options.IncludeStoppedRuntimeInstances = true;
            }

            if (section["IncludeDrainingRuntimeInstances"] is null)
            {
                options.IncludeDrainingRuntimeInstances = true;
            }

            if (section["RequeueUnfinishedRuns"] is null)
            {
                options.RequeueUnfinishedRuns = true;
            }

            if (section["EnableDagExecutionResume"] is null)
            {
                options.EnableDagExecutionResume = true;
            }

            if (section["DryRun"] is null)
            {
                options.DryRun = false;
            }
        }

        /// <summary>
        /// Applies MCP host mode defaults to control-plane discovery options.
        /// </summary>
        /// <param name="aiEngineOptions">The AI engine options.</param>
        /// <param name="hostOptions">The MCP host options.</param>
        private static void ApplyControlPlaneDiscoveryDefaults(
            AiEngineOptions aiEngineOptions,
            AiMcpHostOptions hostOptions)
        {
            ArgumentNullException.ThrowIfNull(aiEngineOptions);
            ArgumentNullException.ThrowIfNull(hostOptions);

            aiEngineOptions.ControlPlane.RedisDiscoveryKey =
                string.IsNullOrWhiteSpace(aiEngineOptions.ControlPlane.RedisDiscoveryKey)
                    ? AiControlPlaneOptions.DefaultRedisDiscoveryKey
                    : aiEngineOptions.ControlPlane.RedisDiscoveryKey;

            switch (hostOptions.Mode)
            {
                case AiMcpHostMode.ControlPlaneOnly:
                    aiEngineOptions.ControlPlane.EnableDiscovery = true;
                    aiEngineOptions.ControlPlane.PublishDiscovery = true;
                    aiEngineOptions.ControlPlane.RequireDiscovery = false;
                    break;

                case AiMcpHostMode.ControlPlaneWithLocalRuntimeInstances:
                case AiMcpHostMode.ControlPlaneWithHttpRuntimeInstances:
                    aiEngineOptions.ControlPlane.EnableDiscovery = true;
                    aiEngineOptions.ControlPlane.PublishDiscovery = true;
                    aiEngineOptions.ControlPlane.RequireDiscovery = true;
                    break;

                case AiMcpHostMode.RuntimeInstanceOnly:
                    aiEngineOptions.ControlPlane.EnableDiscovery = true;
                    aiEngineOptions.ControlPlane.PublishDiscovery = false;
                    aiEngineOptions.ControlPlane.RequireDiscovery = true;
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported MCP host mode '{hostOptions.Mode}'.");
            }
        }

        /// <summary>
        /// Configures the runtime scale-out request watcher hosted service.
        /// </summary>
        /// <remarks>
        /// The watcher is registered only for control-plane capable host modes.
        /// It remains inactive unless <c>AiRuntimeScaleOutRequestWatcher:Enabled</c>
        /// is set to <see langword="true" />.
        /// </remarks>
        /// <param name="services">The service collection.</param>
        /// <param name="configuration">The application configuration.</param>
        /// <param name="hostOptions">The MCP host options.</param>
        private static void ConfigureScaleOutRequestWatcher(
            IServiceCollection services,
            IConfiguration configuration,
            AiMcpHostOptions hostOptions)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(hostOptions);

            if (hostOptions.Mode == AiMcpHostMode.RuntimeInstanceOnly)
            {
                return;
            }

            services.AddAiRuntimeScaleOutRequestWatcher(options =>
            {
                configuration
                    .GetSection("AiRuntimeScaleOutRequestWatcher")
                    .Bind(options);
            });

            services.Configure<SimulatedAiRuntimeScaleOutProviderOptions>(options =>
            {
                configuration
                    .GetSection("SimulatedAiRuntimeScaleOutProvider")
                    .Bind(options);
            });
        }
    }
}