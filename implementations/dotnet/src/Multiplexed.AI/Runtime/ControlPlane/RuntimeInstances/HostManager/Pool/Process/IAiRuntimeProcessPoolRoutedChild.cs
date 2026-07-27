using System.Threading;
using System.Threading.Tasks;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Represents a runtime child whose exact transport route is bound to its lifecycle.
    /// </summary>
    public interface IAiRuntimeProcessPoolRoutedChild :
        IAiRuntimeProcessPoolChild
    {
        /// <summary>
        /// Gets the immutable route-incarnation identifier.
        /// </summary>
        string RouteId { get; }

        /// <summary>
        /// Gets the registered child transport name.
        /// </summary>
        string TransportName { get; }

        /// <summary>
        /// Gets the exact child transport endpoint.
        /// </summary>
        string TransportEndpoint { get; }

        /// <summary>
        /// Prevents the exact route from accepting new requests without stopping the child.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The exact route mutation result.</returns>
        Task<AiRuntimePoolRouteMutationResult> BeginDrainAsync(
            CancellationToken cancellationToken = default);
    }
}
