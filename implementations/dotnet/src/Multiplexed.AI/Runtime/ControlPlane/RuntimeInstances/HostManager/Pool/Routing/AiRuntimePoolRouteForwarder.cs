using System;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing
{
    /// <summary>
    /// Provides protocol-neutral exact forwarding with route lifecycle protection.
    /// </summary>
    public sealed class AiRuntimePoolRouteForwarder :
        IAiRuntimePoolRouteForwarder
    {
        private readonly IAiRuntimePoolRouteRegistry routeRegistry;
        private readonly IAiRuntimePoolCapacitySafetyReader? safetyReader;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiRuntimePoolRouteForwarder"/> class.
        /// </summary>
        /// <param name="routeRegistry">The exact local route registry.</param>
        public AiRuntimePoolRouteForwarder(
            IAiRuntimePoolRouteRegistry routeRegistry)
            : this(
                routeRegistry,
                safetyReader: null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiRuntimePoolRouteForwarder"/> class with exact capacity safety.
        /// </summary>
        /// <param name="routeRegistry">The exact local route registry.</param>
        /// <param name="safetyReader">The exact unsafe-capacity reader.</param>
        public AiRuntimePoolRouteForwarder(
            IAiRuntimePoolRouteRegistry routeRegistry,
            IAiRuntimePoolCapacitySafetyReader? safetyReader)
        {
            this.routeRegistry =
                routeRegistry
                ?? throw new ArgumentNullException(nameof(routeRegistry));

            this.safetyReader = safetyReader;
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

            if (await this
                    .IsSuppressedAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                return new AiRuntimePoolRouteForwardingResult<TResponse>
                {
                    Status =
                        AiRuntimePoolRouteResolutionStatus.Suppressed
                };
            }

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

            if (await this
                    .IsSuppressedAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                return new AiRuntimePoolRouteForwardingResult<TResponse>
                {
                    Status =
                        AiRuntimePoolRouteResolutionStatus.Suppressed,
                    RouteId = routeLease.Route.RouteId
                };
            }

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

        /// <summary>
        /// Determines whether the exact requested runtime instance has been suppressed.
        /// </summary>
        private async Task<bool> IsSuppressedAsync(
            AiRuntimePoolRouteResolutionRequest request,
            CancellationToken cancellationToken)
        {
            if (this.safetyReader is null)
            {
                return false;
            }

            var suppression =
                await this.safetyReader
                    .GetSuppressionAsync(
                        request.PoolId,
                        request.HostId,
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            return suppression is not null;
        }
    }
}
