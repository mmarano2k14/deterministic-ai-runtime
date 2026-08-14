using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes
{
    /// <summary>
    /// Locks the additive compatibility and identity invariants that close Runtime Pool Step 5.
    /// </summary>
    public sealed class KubernetesRuntimePoolStep5ClosureTests
    {
        /// <summary>
        /// Verifies that the new Kubernetes Pool mode remains additive and opt-in.
        /// </summary>
        [Fact]
        public void HostCreationModes_Should_Preserve_LegacyValues_And_DisabledPoolDefault()
        {
            Assert.Equal(0, (int)AiRuntimeHostCreationMode.Fixture);
            Assert.Equal(1, (int)AiRuntimeHostCreationMode.Process);
            Assert.Equal(2, (int)AiRuntimeHostCreationMode.Kubernetes);
            Assert.Equal(3, (int)AiRuntimeHostCreationMode.Attach);
            Assert.Equal(4, (int)AiRuntimeHostCreationMode.KubernetesPool);

            var options = new AiKubernetesRuntimePoolOptions();

            Assert.False(options.Enabled);
        }

        /// <summary>
        /// Verifies that Runtime Pool registration remains explicit and uses only its dedicated strategy.
        /// </summary>
        [Fact]
        public void ServiceRegistration_Should_Remain_Explicit_And_Dedicated()
        {
            var services = new ServiceCollection();

            Assert.Empty(
                services.Where(
                    descriptor =>
                        descriptor.ServiceType ==
                        typeof(IAiRuntimeHostCreationStrategy)));

            services.AddLogging();
            services.AddAiKubernetesRuntimePoolHostProvider(
                configurePool: options =>
                {
                    options.Enabled = true;
                    options.PoolId = "pool-step-5-closure";
                    options.ProviderName = "http";
                    options.TransportName = "http";
                },
                configureHost: options =>
                {
                    options.RuntimeImage =
                        "multiplexed-ai-runtime:step-5-closure";
                });

            var strategyDescriptor =
                Assert.Single(
                    services.Where(
                        descriptor =>
                            descriptor.ServiceType ==
                            typeof(IAiRuntimeHostCreationStrategy)));

            Assert.Equal(
                typeof(KubernetesAiRuntimePoolHostCreationStrategy),
                strategyDescriptor.ImplementationType);
        }

        /// <summary>
        /// Verifies that Pod ownership never replaces independent dispatchable child identities.
        /// </summary>
        [Fact]
        public void Topology_Should_Keep_PodOwnership_Separate_From_ChildDispatchIdentity()
        {
            var options = CreateValidOptions();

            var plan =
                AiKubernetesRuntimePoolPodPlanFactory.Create(
                    options,
                    "step-5-closure-request");

            var identity =
                AiKubernetesRuntimePoolHostIdentityFactory.Create(
                    plan,
                    "step-5-closure-pod-uid");

            Assert.Equal(
                "step-5-closure-pod-uid",
                identity.HostId);
            Assert.Equal(options.PoolId, identity.PoolId);
            Assert.Equal(3, plan.RuntimeInstances.Count);
            Assert.Equal(
                3,
                plan.RuntimeInstances
                    .Select(item => item.RuntimeInstanceId)
                    .Distinct(StringComparer.Ordinal)
                    .Count());
            Assert.All(
                plan.RuntimeInstances,
                item =>
                {
                    Assert.NotEqual(
                        identity.HostId,
                        item.RuntimeInstanceId);
                    Assert.Equal(
                        plan.PoolId,
                        item.PoolId);
                    Assert.Equal(
                        plan.PodRequestId,
                        item.PodRequestId);
                });
        }

        /// <summary>
        /// Verifies that stable transport, readiness, and child endpoints remain non-overlapping.
        /// </summary>
        [Fact]
        public void EndpointContract_Should_Keep_Stable_Readiness_And_ChildPorts_Separate()
        {
            var plan =
                AiKubernetesRuntimePoolPodPlanFactory.Create(
                    CreateValidOptions(),
                    "step-5-endpoint-closure");

            Assert.NotEqual(
                plan.StableTransportPort,
                plan.ReadinessPort);
            Assert.DoesNotContain(
                plan.RuntimeInstances,
                item =>
                    item.TransportPort ==
                    plan.StableTransportPort);
            Assert.DoesNotContain(
                plan.RuntimeInstances,
                item =>
                    item.TransportPort ==
                    plan.ReadinessPort);
        }

        /// <summary>
        /// Creates valid fixed-size Kubernetes Runtime Pool options.
        /// </summary>
        private static AiKubernetesRuntimePoolOptions CreateValidOptions()
        {
            return new AiKubernetesRuntimePoolOptions
            {
                Enabled = true,
                PoolId = "pool-step-5-closure",
                Namespace = "runtime-tests",
                PodNamePrefix = "runtime-pool",
                RuntimeInstanceIdPrefix = "runtime-pool",
                ProviderName = "http",
                TransportName = "http",
                InitialRuntimeInstanceCount = 3,
                MinimumRuntimeInstanceCount = 3,
                MaximumRuntimeInstanceCount = 3,
                StartupParallelism = 1,
                StableTransportPort = 8080,
                ReadinessPort = 8081,
                FirstChildTransportPort = 18080,
                ChildTransportPortStride = 1,
                ShutdownTimeoutSeconds = 30
            };
        }
    }
}
