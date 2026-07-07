using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client.Factory;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Publisher;
using System;

namespace Multiplexed.AI.Runtime.ControlPlane.DI
{
    /// <summary>
    /// Provides dependency injection extensions for Kubernetes runtime host lifecycle support.
    /// </summary>
    public static class AiKubernetesRuntimeHostServiceCollectionExtensions
    {
        /// <summary>
        /// Adds Kubernetes runtime host lifecycle services.
        /// </summary>
        /// <remarks>
        /// Kubernetes is registered as a runtime host lifecycle provider.
        /// It does not replace HTTP or gRPC runtime transport providers.
        /// </remarks>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">The optional options configuration delegate.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddAiKubernetesRuntimeHostProvider(
            this IServiceCollection services,
            Action<AiKubernetesRuntimeHostOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            if (configure is not null)
            {
                services.Configure(configure);
            }

            services.TryAddSingleton(
                serviceProvider =>
                    new AiKubernetesRuntimePodMetadataBuilder(
                        serviceProvider
                            .GetRequiredService<IOptions<AiKubernetesRuntimeHostOptions>>()
                            .Value));

            services.TryAddSingleton(
                serviceProvider =>
                    new AiKubernetesRuntimePodSpecBuilder(
                        serviceProvider
                            .GetRequiredService<IOptions<AiKubernetesRuntimeHostOptions>>()
                            .Value,
                        serviceProvider.GetRequiredService<AiKubernetesRuntimePodMetadataBuilder>()));

            services.TryAddSingleton<AiKubernetesSdkResourceFactory>();
            services.TryAddSingleton<IKubernetesClientFactory, DefaultKubernetesClientFactory>();
            services.TryAddSingleton<KubernetesSdkAiKubernetesRuntimeHostClient>();
            services.TryAddSingleton<IAiKubernetesRuntimeInstancePublisher, KubernetesAiRuntimeInstancePublisher>();

            services.TryAddSingleton<IAiKubernetesRuntimeHostClient>(
                serviceProvider =>
                {
                    var options =
                        serviceProvider
                            .GetRequiredService<IOptions<AiKubernetesRuntimeHostOptions>>()
                            .Value;

                    return options.ClientMode switch
                    {
                        AiKubernetesRuntimeHostClientMode.Fake =>
                            new FakeAiKubernetesRuntimeHostClient(),

                        AiKubernetesRuntimeHostClientMode.KubernetesSdk =>
                            serviceProvider.GetRequiredService<KubernetesSdkAiKubernetesRuntimeHostClient>(),

                        _ =>
                            throw new InvalidOperationException(
                                $"Unsupported Kubernetes runtime host client mode '{options.ClientMode}'.")
                    };
                });

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IAiRuntimeHostCreationStrategy, KubernetesAiRuntimeHostCreationStrategy>());

            return services;
        }

        /// <summary>
        /// Adds Kubernetes runtime host lifecycle services using configuration binding.
        /// </summary>
        /// <remarks>
        /// The expected configuration section is normally <c>AiKubernetesRuntimeHost</c>.
        /// </remarks>
        /// <param name="services">The service collection.</param>
        /// <param name="configuration">The configuration.</param>
        /// <param name="sectionName">The configuration section name.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddAiKubernetesRuntimeHostProvider(
            this IServiceCollection services,
            IConfiguration configuration,
            string sectionName = "AiKubernetesRuntimeHost")
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);

            services.Configure<AiKubernetesRuntimeHostOptions>(
                configuration.GetSection(sectionName));

            return services.AddAiKubernetesRuntimeHostProvider();
        }
    }
}