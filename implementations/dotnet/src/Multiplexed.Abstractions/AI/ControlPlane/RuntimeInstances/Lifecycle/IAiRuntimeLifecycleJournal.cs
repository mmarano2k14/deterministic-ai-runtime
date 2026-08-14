using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle
{
    /// <summary>
    /// Stores and queries append-only runtime infrastructure lifecycle events.
    /// </summary>
    public interface IAiRuntimeLifecycleJournal
    {
        /// <summary>
        /// Appends one lifecycle event.
        /// </summary>
        Task AppendAsync(
            AiRuntimeLifecycleEvent lifecycleEvent,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets one lifecycle event by its stable event identifier.
        /// </summary>
        Task<AiRuntimeLifecycleEvent?> GetByEventIdAsync(
            string eventId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists lifecycle events for one control plane in chronological order.
        /// </summary>
        Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByControlPlaneIdAsync(
            string controlPlaneId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists lifecycle events for one logical runtime pool in chronological order.
        /// </summary>
        Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByPoolIdAsync(
            string poolId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists lifecycle events for one physical host incarnation in chronological order.
        /// </summary>
        Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByHostIdAsync(
            string hostId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists lifecycle events for one Kubernetes Pod UID in chronological order.
        /// </summary>
        Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByKubernetesPodUidAsync(
            string kubernetesPodUid,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists lifecycle events for one runtime instance in chronological order.
        /// </summary>
        Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByRuntimeInstanceIdAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists lifecycle events for one infrastructure failure incident in chronological order.
        /// </summary>
        Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByRuntimeFailureIncidentIdAsync(
            string runtimeFailureIncidentId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists tenant-scoped lifecycle events for one shared run in chronological order.
        /// </summary>
        Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListBySharedRunIdAsync(
            string tenantId,
            string sharedRunId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists lifecycle events for one durable execution in chronological order.
        /// </summary>
        Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByExecutionIdAsync(
            string executionId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists lifecycle events for one correlation identifier in chronological order.
        /// </summary>
        Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByCorrelationIdAsync(
            string correlationId,
            CancellationToken cancellationToken = default);
    }
}
