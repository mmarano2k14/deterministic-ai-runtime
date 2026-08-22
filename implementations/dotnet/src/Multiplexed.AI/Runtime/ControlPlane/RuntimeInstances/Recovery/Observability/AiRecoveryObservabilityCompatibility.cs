using System;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery.Observability
{
    /// <summary>
    /// Preserves legacy constructor composition while routing recovery facts through the Event Manager.
    /// </summary>
    /// <remarks>
    /// This adapter exists only for callers that still construct production components directly with a
    /// recovery-forensics recorder. Dependency-injection composition uses the registered Event Manager and
    /// its registered recovery-forensics projection sink.
    /// </remarks>
    internal static class AiRecoveryObservabilityCompatibility
    {
        /// <summary>
        /// Creates an Event Manager backed by the supplied existing recovery-forensics recorder.
        /// </summary>
        /// <param name="forensicsRecorder">The existing recovery-forensics recorder.</param>
        /// <returns>An observer that centrally projects canonical recovery events to that recorder.</returns>
        public static IAiControlPlaneObserver Create(
            IAiRuntimeRecoveryForensicsRecorder forensicsRecorder)
        {
            ArgumentNullException.ThrowIfNull(forensicsRecorder);

            return new CompositeAiControlPlaneObserver(
                [new RecoveryForensicsAiControlPlaneEventSink(forensicsRecorder)]);
        }

        /// <summary>
        /// Combines a legacy/custom observer with recovery-forensics projection when required.
        /// </summary>
        /// <param name="observer">The supplied control-plane observer.</param>
        /// <param name="forensicsRecorder">The existing recovery-forensics recorder.</param>
        /// <returns>The observer to use for all production emissions.</returns>
        public static IAiControlPlaneObserver Compose(
            IAiControlPlaneObserver observer,
            IAiRuntimeRecoveryForensicsRecorder forensicsRecorder)
        {
            ArgumentNullException.ThrowIfNull(observer);
            ArgumentNullException.ThrowIfNull(forensicsRecorder);

            if (observer is CompositeAiControlPlaneObserver compositeObserver)
            {
                return compositeObserver.WithProjectionSink(
                    new RecoveryForensicsAiControlPlaneEventSink(forensicsRecorder));
            }

            var recoveryProjection = Create(forensicsRecorder);

            if (observer is NoopAiControlPlaneObserver)
            {
                return recoveryProjection;
            }

            const bool forwardCanonicalEvents = true;

            return new CompatibilityFanOutObserver(
                observer,
                recoveryProjection,
                forwardCanonicalEvents);
        }

        /// <summary>
        /// Transitional fan-out used only by direct-construction compatibility overloads.
        /// </summary>
        private sealed class CompatibilityFanOutObserver : IAiControlPlaneObserver
        {
            private readonly IAiControlPlaneObserver observer;
            private readonly IAiControlPlaneObserver recoveryProjection;
            private readonly bool forwardCanonicalEvents;

            public CompatibilityFanOutObserver(
                IAiControlPlaneObserver observer,
                IAiControlPlaneObserver recoveryProjection,
                bool forwardCanonicalEvents)
            {
                this.observer = observer;
                this.recoveryProjection = recoveryProjection;
                this.forwardCanonicalEvents = forwardCanonicalEvents;
            }

            /// <inheritdoc />
            public async Task RecordAsync(
                AiControlPlaneEvent controlPlaneEvent,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(controlPlaneEvent);

                var semanticEventType = controlPlaneEvent.SemanticEventType;
                if (!string.IsNullOrWhiteSpace(semanticEventType) &&
                    AiEngineEventProjectionCatalog.TryGet(semanticEventType, out var descriptor) &&
                    descriptor.GetRequirement(AiEngineEventProjectionTarget.RecoveryForensics) !=
                        AiEngineEventProjectionRequirement.None)
                {
                    await this.recoveryProjection
                        .RecordAsync(controlPlaneEvent, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (this.forwardCanonicalEvents ||
                    string.IsNullOrWhiteSpace(controlPlaneEvent.SemanticEventType))
                {
                    await this.observer
                        .RecordAsync(controlPlaneEvent, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
    }
}
