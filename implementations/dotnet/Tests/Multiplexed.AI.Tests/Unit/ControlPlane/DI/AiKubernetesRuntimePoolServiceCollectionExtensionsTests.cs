using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy;
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
    }
}
