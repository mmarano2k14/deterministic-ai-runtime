using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Pool;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Background;
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
    /// <para>
    /// The MCP host can run in several modes:
    /// </para>
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

            var hostOptions = configuration
                .GetSection("AiMcpHost")
                .Get<AiMcpHostOptions>()
                ?? new AiMcpHostOptions();

            var aiEngineOptions = new AiEngineOptions();

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
                        hostOptions);
                    break;

                case AiMcpHostMode.ControlPlaneWithLocalRuntimeInstances:
                    ConfigureControlPlaneWithLocalRuntimeInstances(
                        services,
                        hostOptions);
                    break;

                case AiMcpHostMode.ControlPlaneWithHttpRuntimeInstances:
                    ConfigureControlPlaneWithHttpRuntimeInstances(
                        services,
                        hostOptions);
                    break;

                case AiMcpHostMode.RuntimeInstanceOnly:
                    ConfigureRuntimeInstanceOnly(
                        services,
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
        /// <para>
        /// This mode exposes MCP tools and can pump the shared queue, but it does not
        /// host local runtime instances.
        /// </para>
        ///
        /// <para>
        /// It is useful when runtime instances are hosted by other processes, pods,
        /// or external workers.
        /// </para>
        /// </remarks>
        /// <param name="services">The service collection.</param>
        /// <param name="hostOptions">The MCP host options.</param>
        private static void ConfigureControlPlaneOnly(
            IServiceCollection services,
            AiMcpHostOptions hostOptions)
        {





            services.AddAiControlPlane(
                configureAdmission: options =>
                {
                    options.EnableScaleOutRequest = false;
                    options.EnableGlobalQueueFallback = true;
                    options.RejectWhenNoCapacity = false;
                });

            services.AddAiMcpServer();

            ConfigureSharedQueueBackgroundService(
                services,
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
        /// <para>
        /// This mode hosts runtime instances inside the same MCP host process.
        /// </para>
        ///
        /// <para>
        /// Dispatch uses <c>LocalAiRuntimeInstanceProvider</c> through the shared
        /// runtime instance registry. Runtime instances still own their own local
        /// queues, workers, and DAG execution engines.
        /// </para>
        /// </remarks>
        /// <param name="services">The service collection.</param>
        /// <param name="hostOptions">The MCP host options.</param>
        private static void ConfigureControlPlaneWithLocalRuntimeInstances(
            IServiceCollection services,
            AiMcpHostOptions hostOptions)
        {
            services.AddAiControlPlane(
                configureAdmission: options =>
                {
                    options.EnableScaleOutRequest = false;
                    options.EnableGlobalQueueFallback = true;
                    options.RejectWhenNoCapacity = false;
                });

            services.RemoveAll<IAiSharedRunDispatcher>();
            services.AddSingleton<IAiSharedRunDispatcher, RemoteAiSharedRunDispatcher>();

            services.AddAiMcpServer();

            ConfigureSharedQueueBackgroundService(
                services,
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
        /// <para>
        /// This mode is used to test or run MCP/control-plane separately from runtime
        /// instances that are addressable through HTTP.
        /// </para>
        ///
        /// <para>
        /// Dispatch uses <c>HttpAiRuntimeInstanceProvider</c> when runtime capacity
        /// descriptors publish:
        /// </para>
        ///
        /// <code>
        /// provider.name = http
        /// transport.endpoint = http://runtime-instance-1:8081
        /// </code>
        ///
        /// <para>
        /// The control-plane still uses <see cref="RemoteAiSharedRunDispatcher"/>.
        /// The dispatcher resolves the provider through the centralized provider
        /// capability resolver.
        /// </para>
        ///
        /// <para>
        /// This mode does not host local runtime instances. The target runtime
        /// instance process must expose the HTTP command endpoint:
        /// </para>
        ///
        /// <code>
        /// POST /runtime-instance/commands
        /// </code>
        /// </remarks>
        /// <param name="services">The service collection.</param>
        /// <param name="hostOptions">The MCP host options.</param>
        private static void ConfigureControlPlaneWithHttpRuntimeInstances(
            IServiceCollection services,
            AiMcpHostOptions hostOptions)
        {
            services.AddAiControlPlane(
                configureAdmission: options =>
                {
                    options.EnableScaleOutRequest = false;
                    options.EnableGlobalQueueFallback = true;
                    options.RejectWhenNoCapacity = false;
                });

            services.RemoveAll<IAiSharedRunDispatcher>();
            services.AddSingleton<IAiSharedRunDispatcher, RemoteAiSharedRunDispatcher>();

            services.AddAiHttpRuntimeInstanceProvider();

            services.AddAiMcpServer();

            ConfigureSharedQueueBackgroundService(
                services,
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
        /// <para>
        /// This mode hosts the local runtime execution engine and runtime queue
        /// without enabling MCP control-plane tools.
        /// </para>
        ///
        /// <para>
        /// When used with the HTTP provider, the runtime host must also map the HTTP
        /// runtime command endpoint in the web application pipeline:
        /// </para>
        ///
        /// <code>
        /// app.MapAiRuntimeInstanceHttpCommandEndpoint();
        /// </code>
        ///
        /// <para>
        /// Its runtime instance registration must publish HTTP provider metadata so
        /// the control-plane can route to it.
        /// </para>
        /// </remarks>
        /// <param name="services">The service collection.</param>
        /// <param name="hostOptions">The MCP host options.</param>
        private static void ConfigureRuntimeInstanceOnly(
            IServiceCollection services,
            AiMcpHostOptions hostOptions)
        {

            Console.WriteLine("[SERVICE REGISTRATION] ConfigureRuntimeInstanceOnly executed.");

            services.AddAiControlPlane();

            services.Configure<AiSharedQueueBackgroundServiceOptions>(options =>
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

            services.AddHostedService<AiRuntimePipelineBackgroundControllerHostedService>();

            Console.WriteLine(
                "[RUNTIME INSTANCE ONLY] Registered AiRuntimePipelineBackgroundControllerHostedService.");

            var hostedServices =
                services
                    .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
                    .Select(descriptor =>
                        descriptor.ImplementationType?.FullName
                        ?? descriptor.ImplementationInstance?.GetType().FullName
                        ?? descriptor.ImplementationFactory?.Method.ReturnType.FullName
                        ?? "factory")
                    .ToArray();

            Console.WriteLine(
                "[RUNTIME INSTANCE ONLY] IHostedService registrations: " + string.Join(" | ", hostedServices));

            services.AddAiRuntimeInstanceRegistrationHostedService();

            Console.WriteLine("[SERVICE REGISTRATION] AiRuntimePipelineBackgroundControllerHostedService registered.");
        }

        /// <summary>
        /// Configures the shared queue background service.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="hostOptions">The MCP host options.</param>
        private static void ConfigureSharedQueueBackgroundService(
            IServiceCollection services,
            AiMcpHostOptions hostOptions)
        {
            services.Configure<AiSharedQueueBackgroundServiceOptions>(options =>
            {
                options.Enabled = hostOptions.EnableSharedQueuePump;
            });
        }
    }
}