using System;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.AI.Runtime.ControlPlane.Observability;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Lifecycle
{
    /// <summary>
    /// Projects canonical runtime lifecycle engine events to the existing durable
    /// runtime lifecycle journal writer.
    /// </summary>
    /// <remarks>
    /// The sink is intentionally thin. Stable event ids, append-once behavior, journal
    /// storage, and persisted lifecycle semantics remain owned by
    /// <see cref="AiRuntimeLifecycleEventWriter"/> and <see cref="IAiRuntimeLifecycleJournal"/>.
    /// </remarks>
    public sealed class RuntimeLifecycleJournalAiControlPlaneEventSink : IAiControlPlaneEventProjectionSink
    {
        private readonly AiRuntimeLifecycleEventWriter writer;

        /// <summary>
        /// Initializes the lifecycle journal projection sink.
        /// </summary>
        /// <param name="journal">The existing lifecycle journal.</param>
        public RuntimeLifecycleJournalAiControlPlaneEventSink(IAiRuntimeLifecycleJournal journal)
        {
            this.writer = new AiRuntimeLifecycleEventWriter(
                journal ?? throw new ArgumentNullException(nameof(journal)));
        }

        /// <inheritdoc />
        public AiEngineEventProjectionTarget ProjectionTarget =>
            AiEngineEventProjectionTarget.LifecycleJournal;

        /// <inheritdoc />
        public async Task RecordAsync(
            AiControlPlaneEvent controlPlaneEvent,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(controlPlaneEvent);

            if (string.IsNullOrWhiteSpace(controlPlaneEvent.SemanticEventType))
            {
                return;
            }

            if (!controlPlaneEvent.Properties.TryGetValue(
                    AiRuntimeLifecycleEngineEventFactory.ProjectionPayloadProperty,
                    out var value) ||
                value is not AiRuntimeLifecycleEvent lifecycleEvent)
            {
                throw new InvalidOperationException(
                    $"Canonical runtime lifecycle event '{controlPlaneEvent.SemanticEventType}' does not contain the existing lifecycle journal projection payload.");
            }

            if (!string.Equals(
                    lifecycleEvent.EventId,
                    controlPlaneEvent.EventId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    lifecycleEvent.EventType,
                    controlPlaneEvent.SemanticEventType,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Canonical runtime lifecycle projection payload does not match the Event Manager envelope identity.");
            }

            await this.writer
                .AppendOnceAsync(lifecycleEvent, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
