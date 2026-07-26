using System;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing
{
    /// <summary>
    /// Provides protocol-neutral exact forwarding with route lifecycle protection.
    /// </summary>
    public sealed class AiRuntimePoolRouteForwarder :
        IAiRuntimePoolRouteForwarder
    {
        private readonly IAiRuntimePoolRouteRegistry routeRegistry;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiRuntimePoolRouteForwarder"/> class.
        /// </summary>
        /// <param name="routeRegistry">The exact local route registry.</param>
        public AiRuntimePoolRouteForwarder(
            IAiRuntimePoolRouteRegistry routeRegistry)
        {
            this.routeRegistry =
                routeRegistry
                ?? throw new ArgumentNullException(nameof(routeRegistry));
        }

        /// <inheritdoc />
        public async Task<AiRuntimePoolRouteForwardingResult<TResponse>>
            ForwardAsync<TResponse>(
                AiRuntimePoolRouteResolutionRequest request,
                Func<
                    AiRuntimePoolRouteDescriptor,
                    CancellationToken,
                    Task<TResponse>> transportForwarder,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(transportForwarder);

            var acquisition =
                await this.routeRegistry
                    .AcquireForwardingLeaseAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (acquisition.Status !=
                    AiRuntimePoolRouteResolutionStatus.Resolved ||
                acquisition.Lease is null)
            {
                return new AiRuntimePoolRouteForwardingResult<TResponse>
                {
                    Status = acquisition.Status
                };
            }

            await using var routeLease =
                acquisition.Lease;

            var response =
                await transportForwarder(
                        routeLease.Route,
                        cancellationToken)
                    .ConfigureAwait(false);

            return new AiRuntimePoolRouteForwardingResult<TResponse>
            {
                Status =
                    AiRuntimePoolRouteResolutionStatus.Resolved,
                Response = response,
                RouteId = routeLease.Route.RouteId
            };
        }
    }
}
