using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;

namespace Multiplexed.AI.McpServer.Host.Bootstrap
{
    /// <summary>
    /// Activates a standalone process-host Runtime Pool after standard RuntimeInstanceOnly
    /// services have been registered.
    /// </summary>
    public static class ProcessRuntimePoolBootstrapRegistration
    {
        /// <summary>
        /// Configures the ProcessPool when explicitly enabled.
        /// </summary>
        public static void Configure(
            IServiceCollection services,
            IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);

            var poolOptions =
                configuration
                    .GetSection("AiRuntimeProcessPool")
                    .Get<AiRuntimeProcessPoolOptions>()
                ?? new AiRuntimeProcessPoolOptions();

            if (!poolOptions.Enabled)
            {
                return;
            }

            var kubernetesPoolOptions =
                configuration
                    .GetSection("AiKubernetesRuntimePoolInPod")
                    .Get<AiKubernetesRuntimePoolInPodOptions>();

            if (kubernetesPoolOptions?.Enabled == true)
            {
                throw new InvalidOperationException(
                    "AiRuntimeProcessPool and AiKubernetesRuntimePoolInPod cannot both be enabled in the same RuntimeInstanceOnly host.");
            }

            var runtimeOptions =
                configuration
                    .GetSection("AiRuntimeProcessPoolRuntimeInstance")
                    .Get<AiRuntimeProcessPoolRuntimeInstanceOptions>()
                ?? throw new InvalidOperationException(
                    "AiRuntimeProcessPoolRuntimeInstance configuration is required when AiRuntimeProcessPool is enabled.");

            runtimeOptions.EnvironmentVariables =
                runtimeOptions.EnvironmentVariables is null
                    ? new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(
                        runtimeOptions.EnvironmentVariables,
                        StringComparer.OrdinalIgnoreCase);

            AiRuntimeProcessPoolChildEnvironmentComposer
                .AddDurableRuntimeEnvironment(
                    runtimeOptions.EnvironmentVariables,
                    configuration.GetConnectionString("Redis"),
                    configuration.GetConnectionString("Mongo"),
                    configuration["Mongo:DatabaseName"],
                    configuration["OpenAI:ApiKey"]);

            AiRuntimeProcessPoolChildEnvironmentComposer
                .AddHostMetadata(
                    runtimeOptions.EnvironmentVariables,
                    hostProvider: "process",
                    hostCreationMode: AiRuntimeHostCreationModeNames.ProcessPool,
                    hostType: AiRuntimeHostTypeNames.ProcessPool,
                    deployment: AiRuntimeHostDeploymentNames.ProcessPool,
                    transportEndpointScope: "host-local");

            RuntimePoolBootstrapHostedServiceRegistration
                .RemoveSingleRuntimeHostedServices(services);

            services.AddAiRuntimeProcessPool(
                poolOptions,
                runtimeOptions);
        }
    }
}
