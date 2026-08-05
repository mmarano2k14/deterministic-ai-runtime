using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Execution;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client.Factory;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;

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

            services.TryAddSingleton<
                IAiKubernetesRuntimePoolPodInventory>(
                serviceProvider =>
                    serviceProvider
                        .GetRequiredService<
                            IAiKubernetesRuntimePoolHostClient>()
                        as IAiKubernetesRuntimePoolPodInventory
                    ?? throw new InvalidOperationException(
                        "The configured Kubernetes Runtime Pool host client must expose the physical Pod inventory authority."));

            services.TryAddSingleton<
                InMemoryAiRuntimePoolFailureJournal>();

            services.TryAddSingleton<
                IAiRuntimePoolFailureReader>(
                serviceProvider =>
                    serviceProvider.GetRequiredService<
                        InMemoryAiRuntimePoolFailureJournal>());

            services.TryAddSingleton<
                IAiKubernetesRuntimePoolPodMembershipEnumerator,
                AiKubernetesRuntimePoolPodMembershipEnumerator>();

            services.TryAddSingleton<
                IAiRuntimePoolCapacitySafetyRegistry,
                InMemoryAiRuntimePoolCapacitySafetyRegistry>();

            services.TryAddSingleton<
                IAiRuntimePoolCapacitySafetyWriter>(
                serviceProvider =>
                    serviceProvider.GetRequiredService<
                        IAiRuntimePoolCapacitySafetyRegistry>());

            services.TryAddSingleton<
                IAiRuntimePoolCapacitySafetyReader>(
                serviceProvider =>
                    serviceProvider.GetRequiredService<
                        IAiRuntimePoolCapacitySafetyRegistry>());

            services.TryAddSingleton<
                IAiRuntimePoolFailureObserver>(
                serviceProvider =>
                    new AiRuntimePoolFailureSafetyObserver(
                        serviceProvider.GetRequiredService<
                            InMemoryAiRuntimePoolFailureJournal>(),
                        serviceProvider.GetRequiredService<
                            IAiRuntimePoolCapacitySafetyWriter>()));

            services.TryAddSingleton<
                IAiRuntimePoolCapacitySafetyBatchWriter>(
                serviceProvider =>
                {
                    var registry =
                        serviceProvider.GetRequiredService<
                            IAiRuntimePoolCapacitySafetyRegistry>();

                    return registry as
                        IAiRuntimePoolCapacitySafetyBatchWriter
                        ?? throw new InvalidOperationException(
                            "The configured Runtime Pool capacity safety registry must support atomic batch suppression for Kubernetes Pod failure handling.");
                });

            services.TryAddSingleton<
                IAiKubernetesRuntimePoolPodCapacitySuppressor,
                AiKubernetesRuntimePoolPodCapacitySuppressor>();

            services.TryAddSingleton<
                IAiRuntimePoolSuppressedAssignedWorkEnumerator,
                AiRuntimePoolSuppressedAssignedWorkEnumerator>();

            services.TryAddSingleton<
                IAiKubernetesRuntimePoolPodAssignedWorkEnumerator,
                AiKubernetesRuntimePoolPodAssignedWorkEnumerator>();

            services.TryAddSingleton<
                IAiRuntimePoolRecoveryClaimStore,
                InMemoryAiRuntimePoolRecoveryClaimStore>();

            services.TryAddSingleton<
                IAiRuntimePoolRecoveryMembershipClaimStore>(
                serviceProvider =>
                {
                    var store =
                        serviceProvider.GetRequiredService<
                            IAiRuntimePoolRecoveryClaimStore>();

                    return store as
                        IAiRuntimePoolRecoveryMembershipClaimStore
                        ?? throw new InvalidOperationException(
                            "The configured Runtime Pool recovery claim store must support exact membership claims for Kubernetes Pod failure recovery.");
                });

            services.TryAddSingleton<
                IAiKubernetesRuntimePoolPodRecoveryClaimCoordinator,
                AiKubernetesRuntimePoolPodRecoveryClaimCoordinator>();

            services.TryAddSingleton<
                IAiKubernetesRuntimePoolPodReplacementCoordinator,
                AiKubernetesRuntimePoolPodReplacementCoordinator>();

            services.TryAddSingleton<
                IAiRuntimePoolRecoveryCandidateTransitionExecutor,
                AiRuntimePoolRecoveryCandidateTransitionExecutor>();

            services.TryAddSingleton<
                IAiKubernetesRuntimePoolPodClaimedRecoveryExecutor,
                AiKubernetesRuntimePoolPodClaimedRecoveryExecutor>();

            services.TryAddSingleton<
                IAiKubernetesRuntimePoolPodFailureRecoveryCoordinator,
                AiKubernetesRuntimePoolPodFailureRecoveryCoordinator>();

            services.TryAddSingleton<
                IAiRuntimePoolPodCreationReservationStore,
                InMemoryAiRuntimePoolPodCreationReservationStore>();

            services.TryAddSingleton<
                IAiRuntimePoolPodCreationExecutor,
                AiRuntimePoolPodCreationExecutor>();

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
