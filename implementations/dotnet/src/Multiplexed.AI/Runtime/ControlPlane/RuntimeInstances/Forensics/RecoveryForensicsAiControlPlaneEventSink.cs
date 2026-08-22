using System;
using System.Collections.Generic;
using System.Globalization;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Events;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.AI.Runtime.ControlPlane.Observability;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Projects canonical runtime recovery engine events to the existing recovery-forensics recorder.
    /// </summary>
    /// <remarks>
    /// This sink is intentionally thin. It does not own storage behavior, strict-vs-best-effort policy,
    /// merge semantics, or idempotency. Those guarantees remain owned by the existing
    /// <see cref="IAiRuntimeRecoveryForensicsRecorder"/> and its configured store.
    /// </remarks>
    public sealed class RecoveryForensicsAiControlPlaneEventSink : IAiControlPlaneEventProjectionSink
    {
        private const string RecoveryKindInFlightExecutionResume = "in-flight-execution-resume";
        private const string RecoveryKindLocalQueuedRunRequeue = "local-queued-run-requeue";
        private const string RuntimeExecutionRecoveryFailureSignal = "runtime-execution-recovery";

        private readonly IAiRuntimeRecoveryForensicsRecorder recorder;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecoveryForensicsAiControlPlaneEventSink"/> class.
        /// </summary>
        /// <param name="recorder">The existing recovery-forensics recorder.</param>
        public RecoveryForensicsAiControlPlaneEventSink(
            IAiRuntimeRecoveryForensicsRecorder recorder)
        {
            this.recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        }

        /// <inheritdoc />
        public AiEngineEventProjectionTarget ProjectionTarget =>
            AiEngineEventProjectionTarget.RecoveryForensics;

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

            var recoveryEvent = CreateRecoveryForensicsEvent(controlPlaneEvent);

            if (IsRecoveryTransitionRecordStart(controlPlaneEvent.SemanticEventType))
            {
                await this.recorder
                    .RecordAsync(
                        CreateRecoveryTransitionRecord(controlPlaneEvent, recoveryEvent),
                        cancellationToken)
                    .ConfigureAwait(false);

                return;
            }

            await this.recorder
                .RecordEventAsync(recoveryEvent, cancellationToken)
                .ConfigureAwait(false);
        }

        private static bool IsRecoveryTransitionRecordStart(string semanticEventType)
        {
            return string.Equals(
                       semanticEventType,
                       AiEngineEvents.Recovery.SharedRunRequeuedForResume,
                       StringComparison.Ordinal) ||
                   string.Equals(
                       semanticEventType,
                       AiEngineEvents.Recovery.SharedRunRequeuedForLocalQueuedRecovery,
                       StringComparison.Ordinal);
        }

        private static AiRuntimeRecoveryForensicsEvent CreateRecoveryForensicsEvent(
            AiControlPlaneEvent controlPlaneEvent)
        {
            var metadata = CreatePersistedMetadata(controlPlaneEvent.Properties);
            var forensicsId = GetRequiredProjectionProperty(
                controlPlaneEvent,
                AiRuntimeRecoveryMetadataKeys.ProjectionForensicsId);

            return new AiRuntimeRecoveryForensicsEvent
            {
                EventId = controlPlaneEvent.EventId,
                ForensicsId = forensicsId,
                TimestampUtc = controlPlaneEvent.TimestampUtc,
                EventType = controlPlaneEvent.SemanticEventType!,
                Outcome = GetProjectionProperty(
                    controlPlaneEvent,
                    AiRuntimeRecoveryMetadataKeys.ProjectionOutcome),
                Reason = GetProjectionProperty(
                    controlPlaneEvent,
                    AiRuntimeRecoveryMetadataKeys.ProjectionReason),
                ExecutionId = controlPlaneEvent.Correlation.ExecutionId,
                SharedRunId = GetProjectionProperty(
                    controlPlaneEvent,
                    AiRuntimeRecoveryMetadataKeys.ProjectionSharedRunId),
                LocalRunId = GetProjectionProperty(
                    controlPlaneEvent,
                    AiRuntimeRecoveryMetadataKeys.ProjectionLocalRunId),
                RuntimeInstanceId = controlPlaneEvent.Correlation.RuntimeInstanceId,
                Metadata = metadata
            };
        }

        private static AiRuntimeRecoveryForensicsRecord CreateRecoveryTransitionRecord(
            AiControlPlaneEvent controlPlaneEvent,
            AiRuntimeRecoveryForensicsEvent recoveryEvent)
        {
            var metadata = recoveryEvent.Metadata;
            var isLocalQueuedRecovery = string.Equals(
                controlPlaneEvent.SemanticEventType,
                AiEngineEvents.Recovery.SharedRunRequeuedForLocalQueuedRecovery,
                StringComparison.Ordinal);
            var recoveryMode = isLocalQueuedRecovery
                ? AiRuntimeRecoveryModes.RequeueLocalQueuedRun
                : AiRuntimeRecoveryModes.ResumeExistingExecution;
            var recoveryKind = isLocalQueuedRecovery
                ? RecoveryKindLocalQueuedRunRequeue
                : RecoveryKindInFlightExecutionResume;
            var reason = recoveryEvent.Reason;

            return new AiRuntimeRecoveryForensicsRecord
            {
                Identity = new AiRuntimeRecoveryForensicsIdentity
                {
                    ForensicsId = recoveryEvent.ForensicsId,
                    ExecutionId = recoveryEvent.ExecutionId ?? string.Empty,
                    SharedRunId = recoveryEvent.SharedRunId,
                    TenantId = ResolveMetadataValue(
                        metadata,
                        AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantId,
                        AiRuntimeInstanceIsolationMetadataKeys.TenantId),
                    TenantGroupId = ResolveMetadataValue(
                        metadata,
                        AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantGroupId,
                        AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId),
                    ControlPlaneId = ResolveMetadataValue(
                        metadata,
                        AiControlPlaneMetadataKeys.ControlPlaneId,
                        AiControlPlaneMetadataKeys.LegacyDottedControlPlaneId),
                    PipelineName = ResolveMetadataValue(
                        metadata,
                        AiPipelineMetadataKeys.CamelCasePipelineName,
                        AiPipelineMetadataKeys.Name,
                        AiPipelineMetadataKeys.CamelCasePipelineKey,
                        AiPipelineMetadataKeys.Key)
                },
                Failure = new AiRuntimeRecoveryFailureInfo
                {
                    RuntimeFailureIncidentId = ResolveMetadataValue(
                        metadata,
                        AiRuntimeRecoveryMetadataKeys.FailureIncidentId),
                    FailedRuntimeInstanceId = recoveryEvent.RuntimeInstanceId,
                    FailedLocalRunId = ResolveMetadataValue(
                        metadata,
                        AiRuntimeRecoveryMetadataKeys.FailedLocalRunId,
                        AiRuntimeRecoveryMetadataKeys.TransitionFailedLocalRunId),
                    FailureSignal = RuntimeExecutionRecoveryFailureSignal,
                    SuppressCapacityReason = reason,
                    FailureDetectedAtUtc = controlPlaneEvent.TimestampUtc
                },
                Recovery = new AiRuntimeRecoveryInfo
                {
                    RecoveryMode = recoveryMode,
                    RecoveryKind = recoveryKind,
                    Outcome = recoveryEvent.Outcome,
                    Reason = reason,
                    RecoveryStartedAtUtc = controlPlaneEvent.TimestampUtc
                },
                Artifacts = new AiRuntimeRecoveryArtifacts
                {
                    Restored = isLocalQueuedRecovery
                        ?
                        [
                            AiRuntimeRecoveryArtifactName.SharedRunMetadata,
                            AiRuntimeRecoveryArtifactName.RecoveryMetadata
                        ]
                        :
                        [
                            AiRuntimeRecoveryArtifactName.DurableExecutionId,
                            AiRuntimeRecoveryArtifactName.SharedRunMetadata,
                            AiRuntimeRecoveryArtifactName.RecoveryMetadata
                        ],
                    Recreated =
                    [
                        AiRuntimeRecoveryArtifactName.DispatchAssignment
                    ],
                    LostVolatile =
                    [
                        AiRuntimeRecoveryArtifactName.FailedRuntimeLocalQueueMemory,
                        AiRuntimeRecoveryArtifactName.OldClaimToken,
                        AiRuntimeRecoveryArtifactName.OldLease,
                        AiRuntimeRecoveryArtifactName.OldLocalRunAsActiveWork
                    ]
                },
                Events = [recoveryEvent],
                Metadata = metadata,
                CreatedAtUtc = controlPlaneEvent.TimestampUtc,
                UpdatedAtUtc = controlPlaneEvent.TimestampUtc
            };
        }

        private static IReadOnlyDictionary<string, string> CreatePersistedMetadata(
            IReadOnlyDictionary<string, object?> properties)
        {
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in properties)
            {
                if (IsProjectionTransportProperty(pair.Key))
                {
                    continue;
                }

                metadata[pair.Key] = ConvertToInvariantString(pair.Value);
            }

            return metadata;
        }

        private static bool IsProjectionTransportProperty(string key)
        {
            return string.Equals(key, AiRuntimeRecoveryMetadataKeys.ProjectionForensicsId, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, AiRuntimeRecoveryMetadataKeys.ProjectionOutcome, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, AiRuntimeRecoveryMetadataKeys.ProjectionReason, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, AiRuntimeRecoveryMetadataKeys.ProjectionSharedRunId, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, AiRuntimeRecoveryMetadataKeys.ProjectionLocalRunId, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetRequiredProjectionProperty(
            AiControlPlaneEvent controlPlaneEvent,
            string key)
        {
            var value = GetProjectionProperty(controlPlaneEvent, key);

            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            throw new InvalidOperationException(
                $"Canonical recovery event '{controlPlaneEvent.SemanticEventType}' is missing required projection property '{key}'.");
        }

        private static string? GetProjectionProperty(
            AiControlPlaneEvent controlPlaneEvent,
            string key)
        {
            if (!controlPlaneEvent.Properties.TryGetValue(key, out var value) || value is null)
            {
                return null;
            }

            var text = ConvertToInvariantString(value);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        private static string ConvertToInvariantString(object? value)
        {
            return value switch
            {
                null => string.Empty,
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
                _ => value.ToString() ?? string.Empty
            };
        }

        private static string? ResolveMetadataValue(
            IReadOnlyDictionary<string, string> metadata,
            params string[] keys)
        {
            foreach (var key in keys)
            {
                if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }
    }
}
