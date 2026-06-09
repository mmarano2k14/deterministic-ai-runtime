using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Pool;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Background;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.ControlPlane.RuntimeInstances.Pool;
using Multiplexed.AI.DI.Engine;
using Multiplexed.AI.McpServer.DependencyInjection;
using Multiplexed.AI.McpServer.Host.Configuration;
using Multiplexed.AI.Runtime;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Dispatch;
using Multiplexed.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.Sample.External.Plugins.Steps.Steps;

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

            AiRuntimeServiceRegistration.Register(
                services,
                configuration,
                aiEngineOptions);

            switch (hostOptions.Mode)
            {
                case AiMcpHostMode.ControlPlaneOnly:
                    ConfigureControlPlaneOnly(
                        services,
                        configuration,
                        hostOptions);
                    break;

                case AiMcpHostMode.ControlPlaneWithLocalRuntimeInstances:
                    ConfigureControlPlaneWithLocalRuntimeInstances(
                        services,
                        configuration,
                        hostOptions);
                    break;

                case AiMcpHostMode.ControlPlaneWithHttpRuntimeInstances:
                    ConfigureControlPlaneWithHttpRuntimeInstances(
                        services,
                        configuration,
                        hostOptions);
                    break;

                case AiMcpHostMode.RuntimeInstanceOnly:
                    ConfigureRuntimeInstanceOnly(
                        services,
                        configuration,
                        hostOptions);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported host mode '{hostOptions.Mode}'.");
            }
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
            services.AddAiControlPlane(
                configureAdmission: options =>
                {
                    ConfigureAdmissionOptions(
                        configuration,
                        options);
                });

            services.AddAiMcpServer();

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
            services.AddAiControlPlane(
                configureAdmission: options =>
                {
                    ConfigureAdmissionOptions(
                        configuration,
                        options);
                });

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

            services.AddAiLocalRuntimeInstancePool();

            services.AddAiRuntimeInstanceRegistrationHostedService(options =>
            {
                options.Enabled = true;
                options.RuntimeInstanceId = "mcp-control-plane";
                options.Role = AiRuntimeInstanceRole.ControlPlane;
            });
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
            services.AddAiControlPlane(
                configureAdmission: options =>
                {
                    ConfigureAdmissionOptions(
                        configuration,
                        options);
                });

            services.RemoveAll<IAiSharedRunDispatcher>();
            services.AddSingleton<IAiSharedRunDispatcher, RemoteAiSharedRunDispatcher>();

            services.AddAiHttpRuntimeInstanceProvider();

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

            services.AddAiRuntimeInstanceRegistrationHostedService(options =>
            {
                options.Enabled = true;
                options.RuntimeInstanceId = "mcp-control-plane";
                options.Role = AiRuntimeInstanceRole.ControlPlane;
            });
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

            Console.WriteLine("[SERVICE REGISTRATION] ConfigureRuntimeInstanceOnly executed.");

            var poolOptions =
                configuration
                    .GetSection("AiLocalRuntimeInstancePool")
                    .Get<AiLocalRuntimeInstancePoolOptions>()
                ?? new AiLocalRuntimeInstancePoolOptions();

            services.AddAiControlPlane();

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
                    "[RUNTIME INSTANCE ONLY]");

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
                "[RUNTIME INSTANCE ONLY]");
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
        /// Configuration values from the <c>AiRunAdmission</c> section are applied
        /// first, then MCP-safe defaults are applied only when they are part of the
        /// MCP host policy.
        /// </remarks>
        /// <param name="configuration">The application configuration.</param>
        /// <param name="options">The run admission options.</param>
        private static void ConfigureAdmissionOptions(
            IConfiguration configuration,
            AiRunAdmissionOptions options)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(options);

            configuration
                .GetSection("AiRunAdmission")
                .Bind(options);

            options.EnableGlobalQueueFallback = true;
            options.RejectWhenNoCapacity = false;
        }
    }
}
