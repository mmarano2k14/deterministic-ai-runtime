using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client.Factory;

namespace Multiplexed.AI.Runtime.ControlPlane.DI
{
    /// <summary>
    /// Provides opt-in dependency injection for Kubernetes Runtime Pool lifecycle support.
    /// </summary>
    public static class AiKubernetesRuntimePoolServiceCollectionExtensions
    {
        /// <summary>
        /// Adds the opt-in Kubernetes Runtime Pool host creation strategy.
        /// </summary>
        public static IServiceCollection AddAiKubernetesRuntimePoolHostProvider(
            this IServiceCollection services,
            Action<AiKubernetesRuntimePoolOptions>? configurePool = null,
            Action<AiKubernetesRuntimePoolHostOptions>? configureHost = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            if (configurePool is not null)
            {
                services.Configure(configurePool);
            }

            if (configureHost is not null)
            {
                services.Configure(configureHost);
            }

            services.TryAddSingleton(
                serviceProvider =>
                {
                    var poolOptions =
                        serviceProvider
                            .GetRequiredService<
                                IOptions<AiKubernetesRuntimePoolOptions>>()
                            .Value;

                    var hostOptions =
                        serviceProvider
                            .GetRequiredService<
                                IOptions<AiKubernetesRuntimePoolHostOptions>>()
                            .Value;

                    return new AiKubernetesRuntimePoolPodSpecBuilder(
                        poolOptions,
                        hostOptions);
                });

            services.TryAddSingleton(
                serviceProvider =>
                    new AiKubernetesRuntimePoolInPodCommandLineFactory(
                        serviceProvider
                            .GetRequiredService<
                                IOptions<AiKubernetesRuntimePoolHostOptions>>()
                            .Value));

            services.TryAddSingleton(
                serviceProvider =>
                    new AiKubernetesRuntimePoolSdkResourceFactory(
                        serviceProvider
                            .GetRequiredService<
                                IOptions<AiKubernetesRuntimePoolHostOptions>>()
                            .Value));

            services.TryAddSingleton<
                IKubernetesClientFactory,
                DefaultKubernetesClientFactory>();

            services.TryAddSingleton(
                serviceProvider =>
                    new KubernetesSdkAiKubernetesRuntimePoolHostClient(
                        serviceProvider.GetRequiredService<
                            IKubernetesClientFactory>(),
                        serviceProvider.GetRequiredService<
                            AiKubernetesRuntimePoolSdkResourceFactory>(),
                        serviceProvider
                            .GetRequiredService<
                                IOptions<AiKubernetesRuntimePoolHostOptions>>()
                            .Value));

            services.TryAddSingleton<IAiKubernetesRuntimePoolHostClient>(
                serviceProvider =>
                {
                    var hostOptions =
                        serviceProvider
                            .GetRequiredService<
                                IOptions<AiKubernetesRuntimePoolHostOptions>>()
                            .Value;

                    return hostOptions.ClientMode switch
                    {
                        AiKubernetesRuntimeHostClientMode.Fake =>
                            new FakeAiKubernetesRuntimePoolHostClient(),

                        AiKubernetesRuntimeHostClientMode.KubernetesSdk =>
                            serviceProvider.GetRequiredService<
                                KubernetesSdkAiKubernetesRuntimePoolHostClient>(),

                        _ =>
                            throw new InvalidOperationException(
                                string.Concat(
                                    "Unsupported Kubernetes Runtime Pool client mode '",
                                    hostOptions.ClientMode,
                                    "'."))
                    };
                });

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<
                    IAiRuntimeHostCreationStrategy,
                    KubernetesAiRuntimePoolHostCreationStrategy>());

            return services;
        }

        /// <summary>
        /// Adds Kubernetes Runtime Pool services using configuration binding.
        /// </summary>
        public static IServiceCollection AddAiKubernetesRuntimePoolHostProvider(
            this IServiceCollection services,
            IConfiguration configuration,
            string poolSectionName = "AiKubernetesRuntimePool",
            string hostSectionName = "AiKubernetesRuntimePoolHost")
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);

            services.Configure<AiKubernetesRuntimePoolOptions>(
                configuration.GetSection(poolSectionName));

            services.Configure<AiKubernetesRuntimePoolHostOptions>(
                configuration.GetSection(hostSectionName));

            return services.AddAiKubernetesRuntimePoolHostProvider();
        }
    }
}
