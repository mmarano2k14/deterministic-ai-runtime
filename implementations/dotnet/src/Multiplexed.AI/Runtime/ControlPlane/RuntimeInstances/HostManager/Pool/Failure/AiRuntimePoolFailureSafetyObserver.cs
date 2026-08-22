using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.Abstractions.AI.Observability.Events;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure
{
    /// <summary>
    /// Records exact runtime failures and projects them into capacity suppressions.
    /// </summary>
    public sealed class AiRuntimePoolFailureSafetyObserver :
        IAiRuntimePoolFailureObserver
    {
        private readonly IAiRuntimePoolFailureObserver journalObserver;
        private readonly IAiRuntimePoolCapacitySafetyWriter safetyWriter;
        private readonly AiRuntimeLifecycleEventWriter lifecycleWriter;
        private readonly IAiControlPlaneObserver observer;

        /// <summary>
        /// Preserves existing composition with a no-op lifecycle journal.
        /// </summary>
        public AiRuntimePoolFailureSafetyObserver(
            IAiRuntimePoolFailureObserver journalObserver,
            IAiRuntimePoolCapacitySafetyWriter safetyWriter)
            : this(
                journalObserver,
                safetyWriter,
                NoopAiRuntimeLifecycleJournal.Instance)
        {
        }

        /// <summary>
        /// Initializes the exact failure observer with durable lifecycle journaling.
        /// </summary>
        public AiRuntimePoolFailureSafetyObserver(
            IAiRuntimePoolFailureObserver journalObserver,
            IAiRuntimePoolCapacitySafetyWriter safetyWriter,
            IAiRuntimeLifecycleJournal lifecycleJournal,
            IAiControlPlaneObserver? observer = null)
        {
            this.journalObserver = journalObserver
                ?? throw new ArgumentNullException(nameof(journalObserver));
            this.safetyWriter = safetyWriter
                ?? throw new ArgumentNullException(nameof(safetyWriter));
            this.lifecycleWriter = new AiRuntimeLifecycleEventWriter(
                lifecycleJournal
                ?? throw new ArgumentNullException(nameof(lifecycleJournal)));
            this.observer = AiRuntimeLifecycleObservabilityCompatibility.Compose(
                observer ?? new NoopAiControlPlaneObserver(),
                lifecycleJournal);
        }

        /// <inheritdoc />
        public async Task<AiRuntimePoolFailureObservation> RecordAsync(
            AiRuntimePoolFailureObservation observation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(observation);

            var storedObservation = await this.journalObserver
                .RecordAsync(observation, cancellationToken)
                .ConfigureAwait(false);

            if (storedObservation.Scope ==
                AiRuntimePoolFailureScope.RuntimeInstance)
            {
                await this.safetyWriter
                    .SuppressAsync(
                        new AiRuntimePoolCapacitySuppression
                        {
                            FailureId = storedObservation.FailureId,
                            PoolId = storedObservation.PoolId,
                            HostId = storedObservation.HostId,
                            Scope = AiRuntimePoolCapacitySuppressionScope
                                .RuntimeInstanceRoute,
                            RuntimeInstanceId = storedObservation.RuntimeInstanceId!,
                            RouteId = storedObservation.RouteId,
                            SuppressedAtUtc = storedObservation.ObservedAtUtc
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await this.RecordLifecycleFailureAsync(
                    storedObservation,
                    cancellationToken)
                .ConfigureAwait(false);

            return storedObservation;
        }

        private async Task RecordLifecycleFailureAsync(
            AiRuntimePoolFailureObservation observation,
            CancellationToken cancellationToken)
        {
            var context = await this.lifecycleWriter
                .ResolveContextAsync(
                    observation.RuntimeInstanceId,
                    observation.HostId,
                    observation.PoolId,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var isHostFailure = observation.Scope ==
                AiRuntimePoolFailureScope.Host;
            var eventType = isHostFailure
                ? AiRuntimeLifecycleEvents.HostDisappeared
                : AiRuntimeLifecycleEvents.RuntimeSuppressed;
            var subjectId = isHostFailure
                ? observation.HostId
                : observation.RuntimeInstanceId;

            if (string.IsNullOrWhiteSpace(subjectId))
            {
                return;
            }

            await this.observer
                .RecordLifecycleAsync(
                    new AiRuntimeLifecycleEvent
                    {
                        EventId = AiRuntimeLifecycleEventWriter.CreateEventId(
                            eventType,
                            subjectId,
                            observation.FailureId),
                        EventType = eventType,
                        TimestampUtc = observation.ObservedAtUtc,
                        ControlPlaneId = context.ControlPlaneId,
                        HostCreationMode = context.HostCreationMode,
                        ProviderName = context.ProviderName,
                        PoolId = observation.PoolId ?? context.PoolId,
                        HostId = observation.HostId ?? context.HostId,
                        KubernetesPodUid = context.KubernetesPodUid ??
                            (isHostFailure ? observation.HostId : null),
                        KubernetesNamespace = context.KubernetesNamespace,
                        KubernetesPodName = context.KubernetesPodName,
                        KubernetesNodeName = context.KubernetesNodeName,
                        RuntimeInstanceId = observation.RuntimeInstanceId,
                        RuntimeId = context.RuntimeId,
                        ProcessId = context.ProcessId,
                        RuntimeFailureIncidentId = observation.FailureId,
                        CorrelationId = observation.FailureId,
                        PreviousStatus = isHostFailure ? "present" : "selectable",
                        CurrentStatus = isHostFailure ? "disappeared" : "suppressed",
                        Reason = observation.FailureMessage ?? observation.Kind.ToString(),
                        Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["failure.kind"] = observation.Kind.ToString(),
                            ["failure.scope"] = observation.Scope.ToString(),
                            ["route.id"] = observation.RouteId ?? string.Empty
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
