

using Microsoft.Extensions.DependencyInjection;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Pool
{
    /// <summary>
    /// Provides access to the root service collection used to build local runtime instances.
    /// </summary>
    public interface IAiLocalRuntimeInstanceServiceCollectionProvider
    {
        /// <summary>
        /// Gets the root service collection.
        /// </summary>
        IServiceCollection Services { get; }
    }
}