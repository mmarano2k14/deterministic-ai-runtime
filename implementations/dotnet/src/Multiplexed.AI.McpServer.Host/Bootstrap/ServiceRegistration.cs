using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Pool;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Background;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.ControlPlane.RuntimeInstances.Pool;
using Multiplexed.AI.DI.Engine;
using Multiplexed.AI.McpServer.DependencyInjection;
using Multiplexed.AI.McpServer.Host.Configuration;
using Multiplexed.AI.Runtime;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.Sample.External.Plugins.Steps.Steps;

namespace Multiplexed.AI.McpServer.Host.Bootstrap
{
    /// <summary>
    /// Registers application services.
    /// </summary>
    public static class ServiceRegistration
    {
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
                options.RuntimeInstanceId = "mcp-control-plane";
                options.WorkerId = "mcp-background-pump";
            });
        }

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

            /*
            services.AddHostedService<
                AiRuntimePipelineBackgroundControllerHostedService>();
            */

            services.AddAiLocalRuntimeInstancePool();

            services.AddAiRuntimeInstanceRegistrationHostedService();
        }

        private static void ConfigureRuntimeInstanceOnly(
            IServiceCollection services,
            AiMcpHostOptions hostOptions)
        {
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

            services.AddHostedService<
                AiRuntimePipelineBackgroundControllerHostedService>();

            services.AddAiRuntimeInstanceRegistrationHostedService();
        }

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