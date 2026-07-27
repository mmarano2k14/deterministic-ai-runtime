using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing
{
    /// <summary>
    /// Provides a deterministic in-memory registry for exact host-local runtime routes and active
    /// forwarding leases.
    /// </summary>
    public sealed class InMemoryAiRuntimePoolRouteRegistry :
        IAiRuntimePoolRouteRegistry
    {
        private readonly object syncRoot = new();
        private readonly Dictionary<string, RouteEntry>
            routesByRuntimeInstanceId =
            new(StringComparer.Ordinal);

        /// <inheritdoc />
        public Task<AiRuntimePoolRouteDescriptor> RegisterAsync(
            AiRuntimePoolRouteRegistration registration,
            CancellationToken cancellationToken = default)
        {
            ValidateRegistration(registration);
            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                if (this.routesByRuntimeInstanceId.TryGetValue(
                        registration.RuntimeInstanceId,
                        out var existing))
                {
                    if (RegistrationMatches(
                            existing.Route,
                            registration))
                    {
                        return Task.FromResult(existing.Route);
                    }

                    throw new AiRuntimePoolRouteConflictException(
                        registration.RuntimeInstanceId);
                }

                var route =
                    new AiRuntimePoolRouteDescriptor
                    {
                        RouteId = registration.RouteId.Trim(),
                        PoolId = registration.PoolId.Trim(),
                        HostId = registration.HostId.Trim(),
                        RuntimeInstanceId =
                            registration.RuntimeInstanceId.Trim(),
                        TransportName =
                            NormalizeTransportName(
                                registration.TransportName),
                        TransportEndpoint =
                            registration.TransportEndpoint.Trim(),
                        Status = AiRuntimePoolRouteStatus.Ready
                    };

                this.routesByRuntimeInstanceId.Add(
                    route.RuntimeInstanceId,
                    new RouteEntry(route));

                return Task.FromResult(route);
            }
        }

        /// <inheritdoc />
        public Task<AiRuntimePoolRouteResolutionResult> ResolveAsync(
            AiRuntimePoolRouteResolutionRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateResolutionRequest(request);
            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                if (!this.routesByRuntimeInstanceId.TryGetValue(
                        request.RuntimeInstanceId,
                        out var entry))
                {
                    return Task.FromResult(
                        CreateResolutionResult(
                            AiRuntimePoolRouteResolutionStatus.NotFound));
                }

                var status =
                    ResolveStatus(
                        entry.Route,
                        request);

                return Task.FromResult(
                    status ==
                        AiRuntimePoolRouteResolutionStatus.Resolved
                        ? new AiRuntimePoolRouteResolutionResult
                        {
                            Status = status,
                            Route = entry.Route
                        }
                        : CreateResolutionResult(status));
            }
        }

        /// <inheritdoc />
        public Task<AiRuntimePoolRouteLeaseAcquisitionResult>
            AcquireForwardingLeaseAsync(
                AiRuntimePoolRouteResolutionRequest request,
                CancellationToken cancellationToken = default)
        {
            ValidateResolutionRequest(request);
            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                if (!this.routesByRuntimeInstanceId.TryGetValue(
                        request.RuntimeInstanceId,
                        out var entry))
                {
                    return Task.FromResult(
                        CreateLeaseResult(
                            AiRuntimePoolRouteResolutionStatus.NotFound));
                }

                var status =
                    ResolveStatus(
                        entry.Route,
                        request);

                if (status !=
                    AiRuntimePoolRouteResolutionStatus.Resolved)
                {
                    return Task.FromResult(
                        CreateLeaseResult(status));
                }

                entry.AcquireForwarding();

                return Task.FromResult(
                    new AiRuntimePoolRouteLeaseAcquisitionResult
                    {
                        Status =
                            AiRuntimePoolRouteResolutionStatus.Resolved,
                        Lease =
                            new RouteLease(
                                this,
                                entry)
                    });
            }
        }

        /// <inheritdoc />
        public Task<AiRuntimePoolRouteMutationResult> BeginDrainAsync(
            AiRuntimePoolRouteMutationRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateMutationRequest(request);
            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                if (!this.routesByRuntimeInstanceId.TryGetValue(
                        request.RuntimeInstanceId,
                        out var entry))
                {
                    return Task.FromResult(
                        CreateMutationResult(
                            AiRuntimePoolRouteMutationStatus.NotFound));
                }

                if (!MutationIdentityMatches(
                        entry.Route,
                        request))
                {
                    return Task.FromResult(
                        CreateMutationResult(
                            AiRuntimePoolRouteMutationStatus.IdentityMismatch));
                }

                if (entry.Route.Status ==
                    AiRuntimePoolRouteStatus.Draining)
                {
                    return Task.FromResult(
                        new AiRuntimePoolRouteMutationResult
                        {
                            Status =
                                AiRuntimePoolRouteMutationStatus.AlreadyApplied,
                            Route = entry.Route
                        });
                }

                entry.Route =
                    entry.Route with
                    {
                        Status =
                            AiRuntimePoolRouteStatus.Draining
                    };

                return Task.FromResult(
                    new AiRuntimePoolRouteMutationResult
                    {
                        Status =
                            AiRuntimePoolRouteMutationStatus.Applied,
                        Route = entry.Route
                    });
            }
        }

        /// <inheritdoc />
        public async Task<AiRuntimePoolRouteMutationResult>
            WaitUntilDrainedAsync(
                AiRuntimePoolRouteMutationRequest request,
                CancellationToken cancellationToken = default)
        {
            ValidateMutationRequest(request);

            RouteEntry entry;
            Task drainTask;

            lock (this.syncRoot)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!this.routesByRuntimeInstanceId.TryGetValue(
                        request.RuntimeInstanceId,
                        out entry!))
                {
                    return CreateMutationResult(
                        AiRuntimePoolRouteMutationStatus.NotFound);
                }

                if (!MutationIdentityMatches(
                        entry.Route,
                        request))
                {
                    return CreateMutationResult(
                        AiRuntimePoolRouteMutationStatus.IdentityMismatch);
                }

                if (entry.Route.Status !=
                    AiRuntimePoolRouteStatus.Draining)
                {
                    return CreateMutationResult(
                        AiRuntimePoolRouteMutationStatus.NotDraining);
                }

                drainTask =
                    entry.GetDrainTask();
            }

            await drainTask
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            lock (this.syncRoot)
            {
                if (!this.routesByRuntimeInstanceId.TryGetValue(
                        request.RuntimeInstanceId,
                        out var current))
                {
                    return CreateMutationResult(
                        AiRuntimePoolRouteMutationStatus.NotFound);
                }

                if (!ReferenceEquals(
                        current,
                        entry) ||
                    !MutationIdentityMatches(
                        current.Route,
                        request))
                {
                    return CreateMutationResult(
                        AiRuntimePoolRouteMutationStatus.IdentityMismatch);
                }

                return new AiRuntimePoolRouteMutationResult
                {
                    Status =
                        AiRuntimePoolRouteMutationStatus.Applied,
                    Route = current.Route
                };
            }
        }

        /// <inheritdoc />
        public Task<AiRuntimePoolRouteMutationResult> RemoveAsync(
            AiRuntimePoolRouteMutationRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateMutationRequest(request);
            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                if (!this.routesByRuntimeInstanceId.TryGetValue(
                        request.RuntimeInstanceId,
                        out var entry))
                {
                    return Task.FromResult(
                        CreateMutationResult(
                            AiRuntimePoolRouteMutationStatus.NotFound));
                }

                if (!MutationIdentityMatches(
                        entry.Route,
                        request))
                {
                    return Task.FromResult(
                        CreateMutationResult(
                            AiRuntimePoolRouteMutationStatus.IdentityMismatch));
                }

                this.routesByRuntimeInstanceId.Remove(
                    entry.Route.RuntimeInstanceId);

                return Task.FromResult(
                    new AiRuntimePoolRouteMutationResult
                    {
                        Status =
                            AiRuntimePoolRouteMutationStatus.Applied
                    });
            }
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimePoolRouteDescriptor>>
            ListByHostIdAsync(
                string hostId,
                CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                IReadOnlyList<AiRuntimePoolRouteDescriptor> routes =
                    this.routesByRuntimeInstanceId
                        .Values
                        .Select(entry => entry.Route)
                        .Where(
                            route =>
                                StringComparer.Ordinal.Equals(
                                    route.HostId,
                                    hostId.Trim()))
                        .OrderBy(
                            route => route.RuntimeInstanceId,
                            StringComparer.Ordinal)
                        .ToArray();

                return Task.FromResult(routes);
            }
        }

        /// <summary>
        /// Releases one active forwarding lease without consulting the current route dictionary.
        /// </summary>
        private void ReleaseForwarding(
            RouteEntry entry)
        {
            lock (this.syncRoot)
            {
                entry.ReleaseForwarding();
            }
        }

        /// <summary>
        /// Resolves every first-class route authority and lifecycle boundary.
        /// </summary>
        private static AiRuntimePoolRouteResolutionStatus ResolveStatus(
            AiRuntimePoolRouteDescriptor route,
            AiRuntimePoolRouteResolutionRequest request)
        {
            if (!StringComparer.Ordinal.Equals(
                    route.PoolId,
                    request.PoolId.Trim()))
            {
                return AiRuntimePoolRouteResolutionStatus.PoolMismatch;
            }

            if (!StringComparer.Ordinal.Equals(
                    route.HostId,
                    request.HostId.Trim()))
            {
                return AiRuntimePoolRouteResolutionStatus.HostMismatch;
            }

            if (!StringComparer.OrdinalIgnoreCase.Equals(
                    route.TransportName,
                    request.TransportName.Trim()))
            {
                return AiRuntimePoolRouteResolutionStatus.TransportMismatch;
            }

            return route.Status ==
                AiRuntimePoolRouteStatus.Draining
                ? AiRuntimePoolRouteResolutionStatus.Draining
                : AiRuntimePoolRouteResolutionStatus.Resolved;
        }

        /// <summary>
        /// Validates one route registration.
        /// </summary>
        private static void ValidateRegistration(
            AiRuntimePoolRouteRegistration registration)
        {
            ArgumentNullException.ThrowIfNull(registration);
            ArgumentException.ThrowIfNullOrWhiteSpace(registration.RouteId);
            ArgumentException.ThrowIfNullOrWhiteSpace(registration.PoolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(registration.HostId);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                registration.RuntimeInstanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                registration.TransportName);
            ValidateTransportEndpoint(
                registration.TransportEndpoint);
        }

        /// <summary>
        /// Validates one exact route-resolution request.
        /// </summary>
        private static void ValidateResolutionRequest(
            AiRuntimePoolRouteResolutionRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.PoolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.HostId);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                request.RuntimeInstanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                request.TransportName);
        }

        /// <summary>
        /// Validates one exact route lifecycle mutation.
        /// </summary>
        private static void ValidateMutationRequest(
            AiRuntimePoolRouteMutationRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.RouteId);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.PoolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.HostId);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                request.RuntimeInstanceId);
        }

        /// <summary>
        /// Validates an absolute child transport endpoint.
        /// </summary>
        private static void ValidateTransportEndpoint(
            string transportEndpoint)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                transportEndpoint);

            if (!Uri.TryCreate(
                    transportEndpoint.Trim(),
                    UriKind.Absolute,
                    out _))
            {
                throw new ArgumentException(
                    "TransportEndpoint must be an absolute URI.",
                    nameof(transportEndpoint));
            }
        }

        /// <summary>
        /// Determines whether an existing route is the same idempotent registration.
        /// </summary>
        private static bool RegistrationMatches(
            AiRuntimePoolRouteDescriptor route,
            AiRuntimePoolRouteRegistration registration)
        {
            return
                StringComparer.Ordinal.Equals(
                    route.RouteId,
                    registration.RouteId.Trim()) &&
                StringComparer.Ordinal.Equals(
                    route.PoolId,
                    registration.PoolId.Trim()) &&
                StringComparer.Ordinal.Equals(
                    route.HostId,
                    registration.HostId.Trim()) &&
                StringComparer.Ordinal.Equals(
                    route.RuntimeInstanceId,
                    registration.RuntimeInstanceId.Trim()) &&
                StringComparer.OrdinalIgnoreCase.Equals(
                    route.TransportName,
                    registration.TransportName.Trim()) &&
                StringComparer.Ordinal.Equals(
                    route.TransportEndpoint,
                    registration.TransportEndpoint.Trim());
        }

        /// <summary>
        /// Determines whether a mutation targets the exact current route incarnation.
        /// </summary>
        private static bool MutationIdentityMatches(
            AiRuntimePoolRouteDescriptor route,
            AiRuntimePoolRouteMutationRequest request)
        {
            return
                StringComparer.Ordinal.Equals(
                    route.RouteId,
                    request.RouteId.Trim()) &&
                StringComparer.Ordinal.Equals(
                    route.PoolId,
                    request.PoolId.Trim()) &&
                StringComparer.Ordinal.Equals(
                    route.HostId,
                    request.HostId.Trim()) &&
                StringComparer.Ordinal.Equals(
                    route.RuntimeInstanceId,
                    request.RuntimeInstanceId.Trim());
        }

        /// <summary>
        /// Normalizes a transport name while preserving protocol neutrality.
        /// </summary>
        private static string NormalizeTransportName(
            string transportName)
        {
            return transportName
                .Trim()
                .ToLowerInvariant();
        }

        /// <summary>
        /// Creates a route-resolution result without exposing a mismatched route.
        /// </summary>
        private static AiRuntimePoolRouteResolutionResult
            CreateResolutionResult(
                AiRuntimePoolRouteResolutionStatus status)
        {
            return new AiRuntimePoolRouteResolutionResult
            {
                Status = status
            };
        }

        /// <summary>
        /// Creates a route-lease result without exposing a mismatched route.
        /// </summary>
        private static AiRuntimePoolRouteLeaseAcquisitionResult
            CreateLeaseResult(
                AiRuntimePoolRouteResolutionStatus status)
        {
            return new AiRuntimePoolRouteLeaseAcquisitionResult
            {
                Status = status
            };
        }

        /// <summary>
        /// Creates a route-mutation result without exposing a mismatched route.
        /// </summary>
        private static AiRuntimePoolRouteMutationResult
            CreateMutationResult(
                AiRuntimePoolRouteMutationStatus status)
        {
            return new AiRuntimePoolRouteMutationResult
            {
                Status = status
            };
        }

        /// <summary>
        /// Stores one mutable route lifecycle and its active forwarding count.
        /// </summary>
        private sealed class RouteEntry
        {
            private TaskCompletionSource<bool>? drainCompletion;

            /// <summary>
            /// Initializes a new instance of the <see cref="RouteEntry"/> class.
            /// </summary>
            public RouteEntry(
                AiRuntimePoolRouteDescriptor route)
            {
                this.Route = route;
            }

            /// <summary>
            /// Gets or sets the current immutable route snapshot.
            /// </summary>
            public AiRuntimePoolRouteDescriptor Route { get; set; }

            /// <summary>
            /// Gets the active forwarding count.
            /// </summary>
            public int ActiveForwardingCount { get; private set; }

            /// <summary>
            /// Acquires one active forwarding lease.
            /// </summary>
            public void AcquireForwarding()
            {
                if (this.ActiveForwardingCount == 0)
                {
                    this.drainCompletion =
                        new TaskCompletionSource<bool>(
                            TaskCreationOptions
                                .RunContinuationsAsynchronously);
                }

                this.ActiveForwardingCount++;
            }

            /// <summary>
            /// Releases one active forwarding lease.
            /// </summary>
            public void ReleaseForwarding()
            {
                if (this.ActiveForwardingCount <= 0)
                {
                    return;
                }

                this.ActiveForwardingCount--;

                if (this.ActiveForwardingCount == 0)
                {
                    this.drainCompletion?.TrySetResult(true);
                }
            }

            /// <summary>
            /// Gets a task that completes when the active forwarding count reaches zero.
            /// </summary>
            public Task GetDrainTask()
            {
                return this.ActiveForwardingCount == 0
                    ? Task.CompletedTask
                    : this.drainCompletion!.Task;
            }
        }

        /// <summary>
        /// Releases one active forwarding count exactly once.
        /// </summary>
        private sealed class RouteLease :
            IAiRuntimePoolRouteLease
        {
            private readonly InMemoryAiRuntimePoolRouteRegistry owner;
            private readonly RouteEntry entry;
            private int disposed;

            /// <summary>
            /// Initializes a new instance of the <see cref="RouteLease"/> class.
            /// </summary>
            public RouteLease(
                InMemoryAiRuntimePoolRouteRegistry owner,
                RouteEntry entry)
            {
                this.owner = owner;
                this.entry = entry;
            }

            /// <inheritdoc />
            public AiRuntimePoolRouteDescriptor Route =>
                this.entry.Route;

            /// <inheritdoc />
            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(
                        ref this.disposed,
                        1) == 0)
                {
                    this.owner.ReleaseForwarding(
                        this.entry);
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
