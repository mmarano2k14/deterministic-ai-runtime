using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Pool;

namespace Multiplexed.AI.ControlPlane.RuntimeInstances.Pool
{
    /// <summary>
    /// Provides access to the root service collection used to build local runtime instances.
    /// </summary>
    public sealed class AiLocalRuntimeInstanceServiceCollectionProvider
        : IAiLocalRuntimeInstanceServiceCollectionProvider
    {
        public AiLocalRuntimeInstanceServiceCollectionProvider(
            IServiceCollection services)
        {
            Services = services ?? throw new ArgumentNullException(nameof(services));
        }

        /// <inheritdoc />
        public IServiceCollection Services { get; }
    }
}