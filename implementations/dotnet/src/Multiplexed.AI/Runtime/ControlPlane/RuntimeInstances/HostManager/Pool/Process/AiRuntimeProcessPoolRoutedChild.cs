using System;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Binds one ready runtime child to one exact first-class route incarnation.
    /// </summary>
    public sealed class AiRuntimeProcessPoolRoutedChild :
        IAiRuntimeProcessPoolRoutedChild
    {
        private readonly IAiRuntimeProcessPoolChild inner;
        private readonly IAiRuntimePoolRouteRegistry routeRegistry;
        private readonly AiRuntimePoolRouteDescriptor route;
        private readonly Task<AiRuntimeProcessPoolChildExit> completion;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiRuntimeProcessPoolRoutedChild"/> class.
        /// </summary>
        /// <param name="inner">The ready underlying runtime child.</param>
        /// <param name="routeRegistry">The exact local route registry.</param>
        /// <param name="route">The registered route incarnation.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when any argument is <see langword="null"/>.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the child and route identities differ.
        /// </exception>
        public AiRuntimeProcessPoolRoutedChild(
            IAiRuntimeProcessPoolChild inner,
            IAiRuntimePoolRouteRegistry routeRegistry,
            AiRuntimePoolRouteDescriptor route)
        {
            this.inner =
                inner ?? throw new ArgumentNullException(nameof(inner));

            this.routeRegistry =
                routeRegistry
                ?? throw new ArgumentNullException(nameof(routeRegistry));

            this.route =
                route ?? throw new ArgumentNullException(nameof(route));

            ValidateIdentity(this.inner, this.route);
            this.completion = this.ObserveCompletionAsync();
        }

        /// <inheritdoc />
        public string PoolId => this.inner.PoolId;

        /// <inheritdoc />
        public string HostId => this.inner.HostId;

        /// <inheritdoc />
        public string RuntimeInstanceId =>
            this.inner.RuntimeInstanceId;

        /// <inheritdoc />
        public int Ordinal => this.inner.Ordinal;

        /// <inheritdoc />
        public AiRuntimeProcessPoolChildStatus Status =>
            this.inner.Status;

        /// <inheritdoc />
        public Task<AiRuntimeProcessPoolChildExit> Completion =>
            this.completion;

        /// <inheritdoc />
        public string RouteId => this.route.RouteId;

        /// <inheritdoc />
        public string TransportName =>
            this.route.TransportName;

        /// <inheritdoc />
        public string TransportEndpoint =>
            this.route.TransportEndpoint;

        /// <inheritdoc />
        public Task<AiRuntimePoolRouteMutationResult> BeginDrainAsync(
            CancellationToken cancellationToken = default)
        {
            return this.routeRegistry.BeginDrainAsync(
                CreateMutationRequest(this.route),
                cancellationToken);
        }

        /// <inheritdoc />
        public async Task StopAsync(
            CancellationToken cancellationToken = default)
        {
            var drain =
                await this.BeginDrainAsync(cancellationToken)
                    .ConfigureAwait(false);

            if (drain.Status ==
                AiRuntimePoolRouteMutationStatus.IdentityMismatch)
            {
                throw new InvalidOperationException(
                    "The exact runtime route changed before graceful drain could begin.");
            }

            if (drain.Status !=
                AiRuntimePoolRouteMutationStatus.NotFound)
            {
                var drained =
                    await this.routeRegistry
                        .WaitUntilDrainedAsync(
                            CreateMutationRequest(this.route),
                            cancellationToken)
                        .ConfigureAwait(false);

                if (drained.Status is
                    AiRuntimePoolRouteMutationStatus.IdentityMismatch or
                    AiRuntimePoolRouteMutationStatus.NotDraining)
                {
                    throw new InvalidOperationException(
                        $"The exact runtime route could not complete graceful drain. Status={drained.Status}.");
                }
            }

            await this.inner
                .StopAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Removes the exact route before exposing child completion to the pool manager.
        /// </summary>
        /// <returns>The underlying child completion.</returns>
        private async Task<AiRuntimeProcessPoolChildExit>
            ObserveCompletionAsync()
        {
            AiRuntimeProcessPoolChildExit exit;

            try
            {
                exit =
                    await this.inner.Completion
                        .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                exit =
                    new AiRuntimeProcessPoolChildExit
                    {
                        Kind =
                            AiRuntimeProcessPoolChildExitKind.Faulted,
                        FailureMessage = exception.Message
                    };
            }

            var removal =
                await this.routeRegistry
                    .RemoveAsync(
                        CreateMutationRequest(this.route),
                        CancellationToken.None)
                    .ConfigureAwait(false);

            if (removal.Status is
                AiRuntimePoolRouteMutationStatus.Applied or
                AiRuntimePoolRouteMutationStatus.NotFound or
                AiRuntimePoolRouteMutationStatus.IdentityMismatch)
            {
                return exit;
            }

            return new AiRuntimeProcessPoolChildExit
            {
                Kind = AiRuntimeProcessPoolChildExitKind.Faulted,
                ExitCode = exit.ExitCode,
                FailureMessage =
                    $"Runtime route cleanup returned unexpected status '{removal.Status}'."
            };
        }

        /// <summary>
        /// Creates an exact route mutation request.
        /// </summary>
        private static AiRuntimePoolRouteMutationRequest
            CreateMutationRequest(
                AiRuntimePoolRouteDescriptor route)
        {
            return new AiRuntimePoolRouteMutationRequest
            {
                RouteId = route.RouteId,
                PoolId = route.PoolId,
                HostId = route.HostId,
                RuntimeInstanceId =
                    route.RuntimeInstanceId
            };
        }

        /// <summary>
        /// Validates that the route belongs to the exact child identity.
        /// </summary>
        private static void ValidateIdentity(
            IAiRuntimeProcessPoolChild child,
            AiRuntimePoolRouteDescriptor route)
        {
            if (!StringComparer.Ordinal.Equals(
                    child.PoolId,
                    route.PoolId) ||
                !StringComparer.Ordinal.Equals(
                    child.HostId,
                    route.HostId) ||
                !StringComparer.Ordinal.Equals(
                    child.RuntimeInstanceId,
                    route.RuntimeInstanceId))
            {
                throw new InvalidOperationException(
                    "The runtime child and route descriptor do not share the same authoritative identity.");
            }

            if (route.Status != AiRuntimePoolRouteStatus.Ready)
            {
                throw new InvalidOperationException(
                    "A routed runtime child requires a ready route.");
            }
        }
    }
}
