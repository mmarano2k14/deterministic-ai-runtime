using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Lifecycle
{
    /// <summary>
    /// Provides a compatibility runtime lifecycle journal that intentionally stores no events.
    /// </summary>
    /// <remarks>
    /// Existing compositions keep their current behavior until an in-memory or MongoDB lifecycle
    /// journal is explicitly registered. Durable scenarios must register a real implementation.
    /// </remarks>
    public sealed class NoopAiRuntimeLifecycleJournal : IAiRuntimeLifecycleJournal
    {
        /// <summary>
        /// Gets the shared no-op lifecycle journal instance.
        /// </summary>
        public static NoopAiRuntimeLifecycleJournal Instance { get; } = new();

        private NoopAiRuntimeLifecycleJournal()
        {
        }

        /// <inheritdoc />
        public Task AppendAsync(
            AiRuntimeLifecycleEvent lifecycleEvent,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(lifecycleEvent);
            cancellationToken.ThrowIfCancellationRequested();

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<AiRuntimeLifecycleEvent?> GetByEventIdAsync(
            string eventId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult<AiRuntimeLifecycleEvent?>(null);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByControlPlaneIdAsync(
            string controlPlaneId,
            CancellationToken cancellationToken = default)
        {
            return EmptyAsync(cancellationToken);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByPoolIdAsync(
            string poolId,
            CancellationToken cancellationToken = default)
        {
            return EmptyAsync(cancellationToken);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByHostIdAsync(
            string hostId,
            CancellationToken cancellationToken = default)
        {
            return EmptyAsync(cancellationToken);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByKubernetesPodUidAsync(
            string kubernetesPodUid,
            CancellationToken cancellationToken = default)
        {
            return EmptyAsync(cancellationToken);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByRuntimeInstanceIdAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            return EmptyAsync(cancellationToken);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByRuntimeFailureIncidentIdAsync(
            string runtimeFailureIncidentId,
            CancellationToken cancellationToken = default)
        {
            return EmptyAsync(cancellationToken);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListBySharedRunIdAsync(
            string tenantId,
            string sharedRunId,
            CancellationToken cancellationToken = default)
        {
            return EmptyAsync(cancellationToken);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByExecutionIdAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            return EmptyAsync(cancellationToken);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByCorrelationIdAsync(
            string correlationId,
            CancellationToken cancellationToken = default)
        {
            return EmptyAsync(cancellationToken);
        }

        private static Task<IReadOnlyList<AiRuntimeLifecycleEvent>> EmptyAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult<IReadOnlyList<AiRuntimeLifecycleEvent>>(
                Array.Empty<AiRuntimeLifecycleEvent>());
        }
    }
}
