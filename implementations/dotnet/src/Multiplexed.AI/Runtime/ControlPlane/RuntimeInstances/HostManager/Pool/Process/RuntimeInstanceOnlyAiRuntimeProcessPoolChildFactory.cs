using System;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Readiness;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Starts a RuntimeInstanceOnly process child and returns it only after authoritative readiness.
    /// </summary>
    /// <remarks>
    /// When a route registry is supplied, the ready child is also registered under one exact route
    /// incarnation before it is returned to the pool manager.
    /// </remarks>
    public sealed class RuntimeInstanceOnlyAiRuntimeProcessPoolChildFactory :
        IAiRuntimeProcessPoolChildFactory
    {
        private readonly IAiRuntimeProcessPoolRuntimeInstanceStartPlanFactory
            planFactory;
        private readonly IAiRuntimeProcessPoolChildProcessLauncher
            processLauncher;
        private readonly IAiRuntimeInstanceReadinessWaiter readinessWaiter;
        private readonly IAiRuntimePoolRouteRegistry? routeRegistry;
        private readonly IAiRuntimePoolFailureObserver? failureObserver;

        /// <summary>
        /// Initializes a child factory without route lifecycle binding.
        /// </summary>
        /// <remarks>
        /// This constructor preserves focused readiness tests and custom isolated composition.
        /// Production process-pool composition supplies a route registry through the four-argument
        /// constructor.
        /// </remarks>
        public RuntimeInstanceOnlyAiRuntimeProcessPoolChildFactory(
            IAiRuntimeProcessPoolRuntimeInstanceStartPlanFactory planFactory,
            IAiRuntimeProcessPoolChildProcessLauncher processLauncher,
            IAiRuntimeInstanceReadinessWaiter readinessWaiter)
        {
            this.planFactory =
                planFactory
                ?? throw new ArgumentNullException(nameof(planFactory));

            this.processLauncher =
                processLauncher
                ?? throw new ArgumentNullException(nameof(processLauncher));

            this.readinessWaiter =
                readinessWaiter
                ?? throw new ArgumentNullException(nameof(readinessWaiter));
        }

        /// <summary>
        /// Initializes a child factory with exact route lifecycle binding.
        /// </summary>
        /// <param name="planFactory">The RuntimeInstanceOnly launch plan factory.</param>
        /// <param name="processLauncher">The operating-system child-process launcher.</param>
        /// <param name="readinessWaiter">The provider-neutral runtime readiness waiter.</param>
        /// <param name="routeRegistry">The exact local runtime route registry.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when any dependency is <see langword="null"/>.
        /// </exception>
        public RuntimeInstanceOnlyAiRuntimeProcessPoolChildFactory(
            IAiRuntimeProcessPoolRuntimeInstanceStartPlanFactory planFactory,
            IAiRuntimeProcessPoolChildProcessLauncher processLauncher,
            IAiRuntimeInstanceReadinessWaiter readinessWaiter,
            IAiRuntimePoolRouteRegistry routeRegistry)
            : this(
                planFactory,
                processLauncher,
                readinessWaiter,
                routeRegistry,
                failureObserver: null)
        {
        }

        /// <summary>
        /// Initializes a child factory with exact route and failure lifecycle binding.
        /// </summary>
        /// <param name="planFactory">The RuntimeInstanceOnly launch plan factory.</param>
        /// <param name="processLauncher">The operating-system child-process launcher.</param>
        /// <param name="readinessWaiter">The provider-neutral runtime readiness waiter.</param>
        /// <param name="routeRegistry">The exact local runtime route registry.</param>
        /// <param name="failureObserver">
        /// The observer that records exact unexpected child failures.
        /// </param>
        public RuntimeInstanceOnlyAiRuntimeProcessPoolChildFactory(
            IAiRuntimeProcessPoolRuntimeInstanceStartPlanFactory planFactory,
            IAiRuntimeProcessPoolChildProcessLauncher processLauncher,
            IAiRuntimeInstanceReadinessWaiter readinessWaiter,
            IAiRuntimePoolRouteRegistry routeRegistry,
            IAiRuntimePoolFailureObserver? failureObserver)
            : this(
                planFactory,
                processLauncher,
                readinessWaiter)
        {
            this.routeRegistry =
                routeRegistry
                ?? throw new ArgumentNullException(nameof(routeRegistry));

            this.failureObserver = failureObserver;
        }

        /// <inheritdoc />
        public async Task<IAiRuntimeProcessPoolChild> StartAsync(
            AiRuntimeProcessPoolChildStartRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var plan =
                await this.planFactory
                    .CreateAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);

            AiRuntimeProcessPoolPortLeasedChild? child = null;
            AiRuntimePoolRouteDescriptor? route = null;

            try
            {
                var startedChild =
                    await this.processLauncher
                        .StartAsync(
                            request,
                            plan.ProcessOptions,
                            cancellationToken)
                        .ConfigureAwait(false);

                child =
                    new AiRuntimeProcessPoolPortLeasedChild(
                        startedChild,
                        plan.PortLease);

                var readinessTask =
                    this.readinessWaiter.WaitUntilReadyAsync(
                        plan.ReadinessRequest,
                        cancellationToken);

                var completedTask =
                    await Task.WhenAny(
                            readinessTask,
                            child.Completion)
                        .ConfigureAwait(false);

                if (ReferenceEquals(
                        completedTask,
                        child.Completion))
                {
                    var childExit =
                        await child.Completion
                            .ConfigureAwait(false);

                    throw new InvalidOperationException(
                        $"Runtime pool child '{request.RuntimeInstanceId}' exited before readiness. Kind={childExit.Kind}, ExitCode={childExit.ExitCode}.");
                }

                var readiness =
                    await readinessTask.ConfigureAwait(false);

                if (!readiness.Success)
                {
                    throw new InvalidOperationException(
                        $"Runtime pool child '{request.RuntimeInstanceId}' did not become ready. Reason={readiness.FailureReason ?? "unknown"}, TimedOut={readiness.TimedOut}.");
                }

                if (!StringComparer.Ordinal.Equals(
                        request.RuntimeInstanceId,
                        readiness.RuntimeInstanceId))
                {
                    throw new InvalidOperationException(
                        "The runtime readiness result returned a different RuntimeInstanceId.");
                }

                if (child.Completion.IsCompleted)
                {
                    var childExit =
                        await child.Completion
                            .ConfigureAwait(false);

                    throw new InvalidOperationException(
                        $"Runtime pool child '{request.RuntimeInstanceId}' completed during readiness. Kind={childExit.Kind}, ExitCode={childExit.ExitCode}.");
                }

                if (this.routeRegistry is null)
                {
                    return child;
                }

                route =
                    await this.routeRegistry
                        .RegisterAsync(
                            new AiRuntimePoolRouteRegistration
                            {
                                RouteId =
                                    AiRuntimePoolRouteIdentityFactory
                                        .CreateRouteId(),
                                PoolId = request.PoolId,
                                HostId = request.HostId,
                                RuntimeInstanceId =
                                    request.RuntimeInstanceId,
                                TransportName =
                                    plan.ReadinessRequest
                                        .TransportName,
                                TransportEndpoint =
                                    plan.TransportEndpoint
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                return new AiRuntimeProcessPoolRoutedChild(
                    child,
                    this.routeRegistry,
                    route,
                    this.failureObserver);
            }
            catch
            {
                if (route is not null &&
                    this.routeRegistry is not null)
                {
                    await RemoveFailedRouteBestEffortAsync(
                            this.routeRegistry,
                            route)
                        .ConfigureAwait(false);
                }

                if (child is null)
                {
                    await plan.PortLease
                        .DisposeAsync()
                        .ConfigureAwait(false);
                }
                else
                {
                    await StopFailedStartBestEffortAsync(child)
                        .ConfigureAwait(false);
                }

                throw;
            }
        }

        /// <summary>
        /// Removes a route created for a child that could not be returned.
        /// </summary>
        private static async Task RemoveFailedRouteBestEffortAsync(
            IAiRuntimePoolRouteRegistry routeRegistry,
            AiRuntimePoolRouteDescriptor route)
        {
            try
            {
                await routeRegistry
                    .RemoveAsync(
                        new AiRuntimePoolRouteMutationRequest
                        {
                            RouteId = route.RouteId,
                            PoolId = route.PoolId,
                            HostId = route.HostId,
                            RuntimeInstanceId =
                                route.RuntimeInstanceId
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // The original start failure remains authoritative.
            }
        }

        /// <summary>
        /// Stops a child that failed readiness or route registration without masking the original
        /// failure.
        /// </summary>
        private static async Task StopFailedStartBestEffortAsync(
            AiRuntimeProcessPoolPortLeasedChild child)
        {
            try
            {
                await child
                    .StopAsync(CancellationToken.None)
                    .ConfigureAwait(false);

                await child.Completion
                    .ConfigureAwait(false);
            }
            catch
            {
                // The original launch, readiness, or route-registration exception remains
                // authoritative. A failed stop intentionally keeps the port lease reserved until
                // the child actually completes.
            }
        }
    }
}
