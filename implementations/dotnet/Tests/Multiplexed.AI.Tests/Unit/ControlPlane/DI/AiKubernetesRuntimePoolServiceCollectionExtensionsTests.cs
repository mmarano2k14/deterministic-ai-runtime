using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Execution;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.DI
{
    /// <summary>
    /// Validates opt-in Kubernetes Runtime Pool dependency registration.
    /// </summary>
    public sealed class AiKubernetesRuntimePoolServiceCollectionExtensionsTests
    {
        /// <summary>
        /// Verifies that KubernetesPool registers separately from the existing Kubernetes strategy.
        /// </summary>
        [Fact]
        public void Add_Should_Register_Dedicated_KubernetesPool_Strategy()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiKubernetesRuntimePoolHostProvider(
                configurePool: options =>
                {
                    options.Enabled = true;
                    options.PoolId = "pool-shared-01";
                    options.ProviderName = "http";
                    options.TransportName = "http";
                },
                configureHost: options =>
                {
                    options.RuntimeImage =
                        "multiplexed-ai-runtime:test";
                });

            var strategyDescriptor =
                Assert.Single(
                    services.Where(
                        descriptor =>
                            descriptor.ServiceType
                            == typeof(IAiRuntimeHostCreationStrategy)));

            Assert.Equal(
                typeof(
                    KubernetesAiRuntimePoolHostCreationStrategy),
                strategyDescriptor.ImplementationType);

            Assert.Equal(
                4,
                (int)AiRuntimeHostCreationMode.KubernetesPool);
            Assert.Equal(
                2,
                (int)AiRuntimeHostCreationMode.Kubernetes);
        }

        /// <summary>
        /// Verifies that Step 7E reuses the existing KubernetesPool host strategy through
        /// one dedicated Pod creation executor.
        /// </summary>
        [Fact]
        public void Add_Should_Register_RuntimePool_PodCreation_Executor()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiKubernetesRuntimePoolHostProvider();

            var descriptor =
                Assert.Single(
                    services.Where(
                        item =>
                            item.ServiceType ==
                            typeof(
                                IAiRuntimePoolPodCreationExecutor)));

            Assert.Equal(
                typeof(AiRuntimePoolPodCreationExecutor),
                descriptor.ImplementationType);
            Assert.Equal(
                ServiceLifetime.Singleton,
                descriptor.Lifetime);
        }

        /// <summary>
        /// Verifies that the selected Kubernetes Runtime Pool host client also owns
        /// the physical Pod inventory authority.
        /// </summary>
        [Fact]
        public void Add_Should_Register_Physical_RuntimePool_PodInventory()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiKubernetesRuntimePoolHostProvider();

            var descriptor =
                Assert.Single(
                    services.Where(
                        item =>
                            item.ServiceType ==
                            typeof(
                                IAiKubernetesRuntimePoolPodInventory)));

            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        }

        /// <summary>
        /// Verifies that KubernetesPool owns a single-process reservation authority
        /// that can be replaced by Redis in distributed control-plane composition.
        /// </summary>
        [Fact]
        public void Add_Should_Register_RuntimePool_PodCreation_Reservation_Store()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiKubernetesRuntimePoolHostProvider();

            var descriptor =
                Assert.Single(
                    services.Where(
                        item =>
                            item.ServiceType ==
                            typeof(
                                IAiRuntimePoolPodCreationReservationStore)));

            Assert.Equal(
                typeof(InMemoryAiRuntimePoolPodCreationReservationStore),
                descriptor.ImplementationType);
            Assert.Equal(
                ServiceLifetime.Singleton,
                descriptor.Lifetime);
        }

        /// <summary>
        /// Verifies that Redis composition replaces the local Pod creation reservation
        /// authority with the distributed atomic implementation.
        /// </summary>
        [Fact]
        public void AddRedis_Should_Replace_RuntimePool_PodCreation_Reservation_Store()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiKubernetesRuntimePoolHostProvider();
            services.AddRedisAiRuntimeScaleOutRequestStore();

            var descriptor =
                Assert.Single(
                    services.Where(
                        item =>
                            item.ServiceType ==
                            typeof(
                                IAiRuntimePoolPodCreationReservationStore)));

            Assert.Equal(
                typeof(RedisAiRuntimePoolPodCreationReservationStore),
                descriptor.ImplementationType);
            Assert.Equal(
                ServiceLifetime.Singleton,
                descriptor.Lifetime);
        }

        /// <summary>
        /// Verifies that atomic Pod-wide capacity suppression is registered separately.
        /// </summary>
        [Fact]
        public void Add_Should_Register_Atomic_PodCapacity_Suppressor()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddSingleton<IAiRuntimePoolMembershipReader>(
                new EmptyMembershipReader());
            services.AddAiKubernetesRuntimePoolHostProvider();

            var registryDescriptor =
                Assert.Single(
                    services.Where(
                        item =>
                            item.ServiceType ==
                            typeof(
                                IAiRuntimePoolCapacitySafetyRegistry)));

            Assert.Equal(
                typeof(
                    InMemoryAiRuntimePoolCapacitySafetyRegistry),
                registryDescriptor.ImplementationType);
            Assert.Equal(
                ServiceLifetime.Singleton,
                registryDescriptor.Lifetime);

            var batchDescriptor =
                Assert.Single(
                    services.Where(
                        item =>
                            item.ServiceType ==
                            typeof(
                                IAiRuntimePoolCapacitySafetyBatchWriter)));

            Assert.Equal(
                ServiceLifetime.Singleton,
                batchDescriptor.Lifetime);

            var suppressorDescriptor =
                Assert.Single(
                    services.Where(
                        item =>
                            item.ServiceType ==
                            typeof(
                                IAiKubernetesRuntimePoolPodCapacitySuppressor)));

            Assert.Equal(
                typeof(
                    AiKubernetesRuntimePoolPodCapacitySuppressor),
                suppressorDescriptor.ImplementationType);
            Assert.Equal(
                ServiceLifetime.Singleton,
                suppressorDescriptor.Lifetime);

            using var serviceProvider =
                services.BuildServiceProvider();

            var registry =
                serviceProvider.GetRequiredService<
                    IAiRuntimePoolCapacitySafetyRegistry>();

            Assert.Same(
                registry,
                serviceProvider.GetRequiredService<
                    IAiRuntimePoolCapacitySafetyWriter>());
            Assert.Same(
                registry,
                serviceProvider.GetRequiredService<
                    IAiRuntimePoolCapacitySafetyReader>());
            Assert.Same(
                registry,
                serviceProvider.GetRequiredService<
                    IAiRuntimePoolCapacitySafetyBatchWriter>());
            Assert.NotNull(
                serviceProvider.GetRequiredService<
                    IAiKubernetesRuntimePoolPodCapacitySuppressor>());
        }

        /// <summary>
        /// Verifies that exact Pod UID membership enumeration is available to failure handling.
        /// </summary>
        [Fact]
        public void Add_Should_Register_Exact_PodMembership_Enumerator()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiKubernetesRuntimePoolHostProvider();

            var descriptor =
                Assert.Single(
                    services.Where(
                        item =>
                            item.ServiceType ==
                            typeof(
                                IAiKubernetesRuntimePoolPodMembershipEnumerator)));

            Assert.Equal(
                typeof(
                    AiKubernetesRuntimePoolPodMembershipEnumerator),
                descriptor.ImplementationType);
            Assert.Equal(
                ServiceLifetime.Singleton,
                descriptor.Lifetime);
        }
        /// <summary>
        /// Verifies that Pod-wide work enumeration reuses the generic suppressed-runtime core.
        /// </summary>
        [Fact]
        public void Add_Should_Register_PodAssignedWork_Enumeration()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiKubernetesRuntimePoolHostProvider();

            var exactRuntimeDescriptor =
                Assert.Single(
                    services.Where(
                        item =>
                            item.ServiceType ==
                            typeof(
                                IAiRuntimePoolSuppressedAssignedWorkEnumerator)));

            Assert.Equal(
                typeof(
                    AiRuntimePoolSuppressedAssignedWorkEnumerator),
                exactRuntimeDescriptor.ImplementationType);
            Assert.Equal(
                ServiceLifetime.Singleton,
                exactRuntimeDescriptor.Lifetime);

            var podDescriptor =
                Assert.Single(
                    services.Where(
                        item =>
                            item.ServiceType ==
                            typeof(
                                IAiKubernetesRuntimePoolPodAssignedWorkEnumerator)));

            Assert.Equal(
                typeof(
                    AiKubernetesRuntimePoolPodAssignedWorkEnumerator),
                podDescriptor.ImplementationType);
            Assert.Equal(
                ServiceLifetime.Singleton,
                podDescriptor.Lifetime);
        }

        /// <summary>
        /// Verifies Pod-wide claim coordination reuses the existing atomic recovery claim store.
        /// </summary>
        [Fact]
        public void Add_Should_Register_PodRecovery_Claim_Coordination()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiKubernetesRuntimePoolHostProvider();

            var runtimeStoreDescriptor =
                Assert.Single(
                    services.Where(
                        item =>
                            item.ServiceType ==
                            typeof(IAiRuntimePoolRecoveryClaimStore)));

            Assert.Equal(
                typeof(InMemoryAiRuntimePoolRecoveryClaimStore),
                runtimeStoreDescriptor.ImplementationType);
            Assert.Equal(
                ServiceLifetime.Singleton,
                runtimeStoreDescriptor.Lifetime);

            var membershipStoreDescriptor =
                Assert.Single(
                    services.Where(
                        item =>
                            item.ServiceType ==
                            typeof(
                                IAiRuntimePoolRecoveryMembershipClaimStore)));

            Assert.Equal(
                ServiceLifetime.Singleton,
                membershipStoreDescriptor.Lifetime);

            var coordinatorDescriptor =
                Assert.Single(
                    services.Where(
                        item =>
                            item.ServiceType ==
                            typeof(
                                IAiKubernetesRuntimePoolPodRecoveryClaimCoordinator)));

            Assert.Equal(
                typeof(
                    AiKubernetesRuntimePoolPodRecoveryClaimCoordinator),
                coordinatorDescriptor.ImplementationType);
            Assert.Equal(
                ServiceLifetime.Singleton,
                coordinatorDescriptor.Lifetime);

            using var serviceProvider =
                services.BuildServiceProvider();

            var runtimeStore =
                serviceProvider.GetRequiredService<
                    IAiRuntimePoolRecoveryClaimStore>();

            Assert.Same(
                runtimeStore,
                serviceProvider.GetRequiredService<
                    IAiRuntimePoolRecoveryMembershipClaimStore>());
        }

        /// <summary>
        /// Verifies fresh Pod replacement coordination is registered over the existing
        /// KubernetesPool host strategy and exact membership authority.
        /// </summary>
        [Fact]
        public void Add_Should_Register_PodReplacement_Coordination()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddSingleton<IAiRuntimePoolMembershipReader>(
                new EmptyMembershipReader());
            services.AddAiKubernetesRuntimePoolHostProvider(
                configurePool: options =>
                {
                    options.Enabled = true;
                    options.PoolId = "pool-01";
                    options.ProviderName = "http";
                    options.TransportName = "http";
                },
                configureHost: options =>
                {
                    options.RuntimeImage =
                        "multiplexed-ai-runtime:test";
                });

            var descriptor =
                Assert.Single(
                    services.Where(
                        item =>
                            item.ServiceType ==
                            typeof(
                                IAiKubernetesRuntimePoolPodReplacementCoordinator)));

            Assert.Equal(
                typeof(
                    AiKubernetesRuntimePoolPodReplacementCoordinator),
                descriptor.ImplementationType);
            Assert.Equal(
                ServiceLifetime.Singleton,
                descriptor.Lifetime);

            using var serviceProvider =
                services.BuildServiceProvider();

            Assert.NotNull(
                serviceProvider.GetRequiredService<
                    IAiKubernetesRuntimePoolPodReplacementCoordinator>());
        }

        [Fact]
        public void Add_Should_Register_Shared_Transition_Core_And_Full_PodFailure_Coordinator()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiKubernetesRuntimePoolHostProvider();

            var transitionDescriptor =
                Assert.Single(
                    services.Where(
                        item =>
                            item.ServiceType ==
                            typeof(
                                IAiRuntimePoolRecoveryCandidateTransitionExecutor)));

            Assert.Equal(
                typeof(AiRuntimePoolRecoveryCandidateTransitionExecutor),
                transitionDescriptor.ImplementationType);
            Assert.Equal(
                ServiceLifetime.Singleton,
                transitionDescriptor.Lifetime);

            var podExecutorDescriptor =
                Assert.Single(
                    services.Where(
                        item =>
                            item.ServiceType ==
                            typeof(
                                IAiKubernetesRuntimePoolPodClaimedRecoveryExecutor)));

            Assert.Equal(
                typeof(AiKubernetesRuntimePoolPodClaimedRecoveryExecutor),
                podExecutorDescriptor.ImplementationType);

            var fullCoordinatorDescriptor =
                Assert.Single(
                    services.Where(
                        item =>
                            item.ServiceType ==
                            typeof(
                                IAiKubernetesRuntimePoolPodFailureRecoveryCoordinator)));

            Assert.Equal(
                typeof(AiKubernetesRuntimePoolPodFailureRecoveryCoordinator),
                fullCoordinatorDescriptor.ImplementationType);
            Assert.Equal(
                ServiceLifetime.Singleton,
                fullCoordinatorDescriptor.Lifetime);
        }

        private sealed class EmptyMembershipReader :
            IAiRuntimePoolMembershipReader
        {
            public Task<IReadOnlyList<AiRuntimeInstanceSnapshot>>
                ListByPoolIdAsync(
                    string poolId,
                    CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<AiRuntimeInstanceSnapshot>>(
                    Array.Empty<AiRuntimeInstanceSnapshot>());
            }

            public Task<IReadOnlyList<AiRuntimeInstanceSnapshot>>
                ListByHostIdAsync(
                    string hostId,
                    CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<AiRuntimeInstanceSnapshot>>(
                    Array.Empty<AiRuntimeInstanceSnapshot>());
            }

            public Task<IReadOnlyList<string>> ListHostIdsByPoolIdAsync(
                string poolId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<string>>(
                    Array.Empty<string>());
            }
        }
    }
}
