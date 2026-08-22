using System;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.AI.Runtime.ControlPlane.Observability;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Lifecycle
{
    /// <summary>
    /// Preserves direct-construction compatibility while routing lifecycle facts through
    /// the existing Event Manager and lifecycle projection sink.
    /// </summary>
    internal static class AiRuntimeLifecycleObservabilityCompatibility
    {
        public static IAiControlPlaneObserver Create(IAiRuntimeLifecycleJournal journal)
        {
            ArgumentNullException.ThrowIfNull(journal);

            return new CompositeAiControlPlaneObserver(
                [new RuntimeLifecycleJournalAiControlPlaneEventSink(journal)]);
        }

        public static IAiControlPlaneObserver Compose(
            IAiControlPlaneObserver observer,
            IAiRuntimeLifecycleJournal journal)
        {
            ArgumentNullException.ThrowIfNull(observer);
            ArgumentNullException.ThrowIfNull(journal);

            var projection = new RuntimeLifecycleJournalAiControlPlaneEventSink(journal);

            if (observer is CompositeAiControlPlaneObserver composite)
            {
                return composite.WithProjectionSink(projection);
            }

            if (observer is NoopAiControlPlaneObserver)
            {
                return new CompositeAiControlPlaneObserver([projection]);
            }

            return new CompatibilityObserver(observer, projection);
        }

        private sealed class CompatibilityObserver : IAiControlPlaneObserver
        {
            private readonly IAiControlPlaneObserver observer;
            private readonly CompositeAiControlPlaneObserver projectionObserver;

            public CompatibilityObserver(
                IAiControlPlaneObserver observer,
                IAiControlPlaneEventProjectionSink projection)
            {
                this.observer = observer;
                this.projectionObserver = new CompositeAiControlPlaneObserver([projection]);
            }

            public async Task RecordAsync(
                AiControlPlaneEvent controlPlaneEvent,
                CancellationToken cancellationToken = default)
            {
                var semanticEventType = controlPlaneEvent.SemanticEventType;
                if (!string.IsNullOrWhiteSpace(semanticEventType) &&
                    AiEngineEventProjectionCatalog.TryGet(semanticEventType, out var descriptor) &&
                    descriptor.GetRequirement(AiEngineEventProjectionTarget.LifecycleJournal) !=
                        AiEngineEventProjectionRequirement.None)
                {
                    await this.projectionObserver
                        .RecordAsync(controlPlaneEvent, cancellationToken)
                        .ConfigureAwait(false);
                }

                await this.observer
                    .RecordAsync(controlPlaneEvent, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}
