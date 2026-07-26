using System;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing
{
    /// <summary>
    /// Coordinates exact protocol-neutral forwarding through active route leases.
    /// </summary>
    public interface IAiRuntimePoolRouteForwarder
    {
        /// <summary>
        /// Acquires the exact target route and invokes one transport adapter without sibling
        /// fallback.
        /// </summary>
        /// <typeparam name="TResponse">The transport-adapter response type.</typeparam>
        /// <param name="request">The exact route-resolution request.</param>
        /// <param name="transportForwarder">
        /// The transport callback invoked only for an exact ready route.
        /// </param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The forwarding result.</returns>
        Task<AiRuntimePoolRouteForwardingResult<TResponse>> ForwardAsync<TResponse>(
            AiRuntimePoolRouteResolutionRequest request,
            Func<
                AiRuntimePoolRouteDescriptor,
                CancellationToken,
                Task<TResponse>> transportForwarder,
            CancellationToken cancellationToken = default);
    }
}
