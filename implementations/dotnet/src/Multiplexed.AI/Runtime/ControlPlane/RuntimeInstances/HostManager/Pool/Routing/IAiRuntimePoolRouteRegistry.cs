using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing
{
    /// <summary>
    /// Stores, resolves, and leases exact host-local routes for independently registered runtime
    /// instances.
    /// </summary>
    public interface IAiRuntimePoolRouteRegistry
    {
        /// <summary>
        /// Registers one exact route incarnation.
        /// </summary>
        Task<AiRuntimePoolRouteDescriptor> RegisterAsync(
            AiRuntimePoolRouteRegistration registration,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Resolves one exact runtime route for diagnostics without acquiring a forwarding lease.
        /// </summary>
        Task<AiRuntimePoolRouteResolutionResult> ResolveAsync(
            AiRuntimePoolRouteResolutionRequest request,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Atomically validates and acquires one exact ready route for forwarding.
        /// </summary>
        Task<AiRuntimePoolRouteLeaseAcquisitionResult>
            AcquireForwardingLeaseAsync(
                AiRuntimePoolRouteResolutionRequest request,
                CancellationToken cancellationToken = default);

        /// <summary>
        /// Prevents one exact route incarnation from accepting new forwarding leases.
        /// </summary>
        Task<AiRuntimePoolRouteMutationResult> BeginDrainAsync(
            AiRuntimePoolRouteMutationRequest request,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Waits until every active forwarding lease for one exact draining route is released.
        /// </summary>
        Task<AiRuntimePoolRouteMutationResult> WaitUntilDrainedAsync(
            AiRuntimePoolRouteMutationRequest request,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Removes one exact route incarnation.
        /// </summary>
        Task<AiRuntimePoolRouteMutationResult> RemoveAsync(
            AiRuntimePoolRouteMutationRequest request,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists route snapshots owned by one exact host incarnation.
        /// </summary>
        Task<IReadOnlyList<AiRuntimePoolRouteDescriptor>> ListByHostIdAsync(
            string hostId,
            CancellationToken cancellationToken = default);
    }
}
