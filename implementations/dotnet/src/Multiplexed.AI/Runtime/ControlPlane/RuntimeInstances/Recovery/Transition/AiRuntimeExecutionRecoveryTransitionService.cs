using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery.Transition
{
    /// <summary>
    /// Default runtime execution recovery transition service.
    /// </summary>
    /// <remarks>
    /// This service owns mutation boundaries for runtime execution recovery.
    ///
    /// It does not detect runtime health, scan runtime instances, restart hosts,
    /// kill processes, or decide which runtime instance should be recovered.
    ///
    /// When dry-run is enabled, it validates the transition and reports the action
    /// without mutating shared queue state or runtime execution index state.
    ///
    /// When mutation is enabled, it requeues the dispatched shared queue item and
    /// then marks the local runtime execution index entry as requeued for recovery.
    /// </remarks>
    public sealed class AiRuntimeExecutionRecoveryTransitionService : IAiRuntimeExecutionRecoveryTransitionService
    {
        private const string RecoveryForensicsIdMetadataKey = "recovery.forensicsId";
        private const string RecoveryModeMetadataKey = "recovery.mode";
        private const string RecoveryModeResumeExistingExecution = "resume-existing-execution";
        private const string RecoveryModeRequeueLocalQueuedRun = "requeue-local-queued-run";
        private const string RecoveryKindInFlightExecutionResume = "in-flight-execution-resume";
        private const string RecoveryFailedExecutionIdMetadataKey = "recovery.failedExecutionId";
        private const string RecoveryFailedRuntimeInstanceIdMetadataKey = "recovery.failedRuntimeInstanceId";
        private const string RecoveryFailedLocalRunIdMetadataKey = "recovery.failedLocalRunId";
        private const string RecoveryReasonMetadataKey = "recovery.reason";
        private const string FailedRuntimeInstanceIdMetadataKey = "failed.runtimeInstanceId";
        private const string FailedLocalRunIdMetadataKey = "failed.localRunId";

        private readonly IAiSharedQueue sharedQueue;
        private readonly IAiRuntimeRunExecutionIndex runtimeRunExecutionIndex;
        private readonly IAiRuntimeRecoveryForensicsRecorder forensicsRecorder;
        private readonly AiRuntimeExecutionRecoveryReconciliationOptions options;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeExecutionRecoveryTransitionService"/> class.
        /// </summary>
        /// <param name="sharedQueue">The shared queue.</param>
        /// <param name="runtimeRunExecutionIndex">The runtime run execution index.</param>
        public AiRuntimeExecutionRecoveryTransitionService(
            IAiSharedQueue sharedQueue,
            IAiRuntimeRunExecutionIndex runtimeRunExecutionIndex)
            : this(
                sharedQueue,
                runtimeRunExecutionIndex,
                Options.Create(new AiRuntimeExecutionRecoveryReconciliationOptions()),
                new NoopAiRuntimeRecoveryForensicsRecorder())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeExecutionRecoveryTransitionService"/> class.
        /// </summary>
        /// <param name="sharedQueue">The shared queue.</param>
        /// <param name="runtimeRunExecutionIndex">The runtime run execution index.</param>
        /// <param name="options">The runtime execution recovery reconciliation options.</param>
        public AiRuntimeExecutionRecoveryTransitionService(
            IAiSharedQueue sharedQueue,
            IAiRuntimeRunExecutionIndex runtimeRunExecutionIndex,
            IOptions<AiRuntimeExecutionRecoveryReconciliationOptions> options)
            : this(
                sharedQueue,
                runtimeRunExecutionIndex,
                options,
                new NoopAiRuntimeRecoveryForensicsRecorder())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeExecutionRecoveryTransitionService"/> class.
        /// </summary>
        /// <param name="sharedQueue">The shared queue.</param>
        /// <param name="runtimeRunExecutionIndex">The runtime run execution index.</param>
        /// <param name="options">The runtime execution recovery reconciliation options.</param>
        /// <param name="forensicsRecorder">The runtime recovery forensics recorder.</param>
        public AiRuntimeExecutionRecoveryTransitionService(
            IAiSharedQueue sharedQueue,
            IAiRuntimeRunExecutionIndex runtimeRunExecutionIndex,
            IOptions<AiRuntimeExecutionRecoveryReconciliationOptions> options,
            IAiRuntimeRecoveryForensicsRecorder forensicsRecorder)
        {
            ArgumentNullException.ThrowIfNull(sharedQueue);
            ArgumentNullException.ThrowIfNull(runtimeRunExecutionIndex);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(forensicsRecorder);

            this.sharedQueue = sharedQueue;
            this.runtimeRunExecutionIndex = runtimeRunExecutionIndex;
            this.forensicsRecorder = forensicsRecorder;
            this.options = options.Value;
        }

        /// <inheritdoc />
        public async Task<AiRuntimeExecutionRecoveryTransitionResult> ApplyAsync(
            AiRuntimeExecutionRecoveryTransitionRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            cancellationToken.ThrowIfCancellationRequested();

            var ownership = request.Ownership;

            if (!ownership.Resolved)
            {
                return new AiRuntimeExecutionRecoveryTransitionResult
                {
                    Accepted = false,
                    Changed = false,
                    SharedRunId = ownership.SharedRunId,
                    RuntimeInstanceId = ownership.RuntimeInstanceId,
                    LocalRunId = ownership.LocalRunId,
                    ExecutionId = ownership.ExecutionId,
                    Action = "none",
                    Reason = "ownership-not-resolved"
                };
            }

            if (!ownership.CanRecover)
            {
                return new AiRuntimeExecutionRecoveryTransitionResult
                {
                    Accepted = false,
                    Changed = false,
                    SharedRunId = ownership.SharedRunId,
                    RuntimeInstanceId = ownership.RuntimeInstanceId,
                    LocalRunId = ownership.LocalRunId,
                    ExecutionId = ownership.ExecutionId,
                    Action = "none",
                    Reason = "ownership-not-recoverable"
                };
            }

            if (string.IsNullOrWhiteSpace(ownership.SharedRunId))
            {
                return new AiRuntimeExecutionRecoveryTransitionResult
                {
                    Accepted = false,
                    Changed = false,
                    SharedRunId = ownership.SharedRunId,
                    RuntimeInstanceId = ownership.RuntimeInstanceId,
                    LocalRunId = ownership.LocalRunId,
                    ExecutionId = ownership.ExecutionId,
                    Action = "none",
                    Reason = "shared-run-id-missing"
                };
            }

            if (string.IsNullOrWhiteSpace(ownership.ClaimToken))
            {
                return new AiRuntimeExecutionRecoveryTransitionResult
                {
                    Accepted = false,
                    Changed = false,
                    SharedRunId = ownership.SharedRunId,
                    RuntimeInstanceId = ownership.RuntimeInstanceId,
                    LocalRunId = ownership.LocalRunId,
                    ExecutionId = ownership.ExecutionId,
                    Action = "none",
                    Reason = "claim-token-missing"
                };
            }

            if (string.IsNullOrWhiteSpace(ownership.LocalRunId))
            {
                return new AiRuntimeExecutionRecoveryTransitionResult
                {
                    Accepted = false,
                    Changed = false,
                    SharedRunId = ownership.SharedRunId,
                    RuntimeInstanceId = ownership.RuntimeInstanceId,
                    LocalRunId = ownership.LocalRunId,
                    ExecutionId = ownership.ExecutionId,
                    Action = "none",
                    Reason = "local-run-id-missing"
                };
            }

            var isLocalQueuedRecovery =
                IsLocalQueuedRecovery(ownership);

            var reason =
                request.Reason ?? (isLocalQueuedRecovery
                    ? "runtime-local-queued-recovery-requeue"
                    : "runtime-execution-recovery-requeue");

            var forensicsId =
                CreateForensicsId(
                    ownership,
                    isLocalQueuedRecovery);

            if (request.DryRun)
            {
                return new AiRuntimeExecutionRecoveryTransitionResult
                {
                    Accepted = true,
                    Changed = false,
                    SharedRunId = ownership.SharedRunId,
                    RuntimeInstanceId = ownership.RuntimeInstanceId,
                    LocalRunId = ownership.LocalRunId,
                    ExecutionId = ownership.ExecutionId,
                    Action = "dry-run-requeue-shared-run",
                    Reason = reason
                };
            }

            var metadata =
                this.options.EnableDagExecutionResume
                    ? CreateRecoveryMetadata(
                        ownership,
                        reason,
                        forensicsId,
                        isLocalQueuedRecovery)
                    : null;

            var requeued = await this.sharedQueue
                .RequeueDispatchedAsync(
                    ownership.SharedRunId,
                    ownership.ClaimToken,
                    reason,
                    metadata,
                    cancellationToken)
                .ConfigureAwait(false);

            if (requeued is null)
            {
                return new AiRuntimeExecutionRecoveryTransitionResult
                {
                    Accepted = false,
                    Changed = false,
                    SharedRunId = ownership.SharedRunId,
                    RuntimeInstanceId = ownership.RuntimeInstanceId,
                    LocalRunId = ownership.LocalRunId,
                    ExecutionId = ownership.ExecutionId,
                    Action = "none",
                    Reason = "shared-queue-requeue-dispatched-rejected"
                };
            }

            var markedRequeued = await this.runtimeRunExecutionIndex
                .MarkRequeuedForRecoveryAsync(
                    ownership.LocalRunId,
                    ownership.ExecutionId,
                    reason,
                    cancellationToken)
                .ConfigureAwait(false);

            Console.WriteLine(
                 $"[EXECUTION RECOVERY INDEX MARK REQUEUED RESULT] LocalRunId='{ownership.LocalRunId}', ExecutionId='{ownership.ExecutionId}', SharedRunId='{ownership.SharedRunId}', RuntimeInstanceId='{ownership.RuntimeInstanceId}', MarkedRequeued='{markedRequeued}', Reason='{reason}'.");

            if (!markedRequeued)
            {

                Console.WriteLine(
                    $"[EXECUTION RECOVERY INDEX MARK REQUEUED REJECTED] LocalRunId='{ownership.LocalRunId}', ExecutionId='{ownership.ExecutionId}', SharedRunId='{ownership.SharedRunId}', RuntimeInstanceId='{ownership.RuntimeInstanceId}', Reason='{reason}'.");

                return new AiRuntimeExecutionRecoveryTransitionResult
                {
                    Accepted = false,
                    Changed = false,
                    SharedRunId = ownership.SharedRunId,
                    RuntimeInstanceId = ownership.RuntimeInstanceId,
                    LocalRunId = ownership.LocalRunId,
                    ExecutionId = ownership.ExecutionId,
                    Action = "none",
                    Reason = "runtime-run-index-requeue-for-recovery-rejected"
                };
            }

            if (!string.IsNullOrWhiteSpace(forensicsId))
            {
                await this.RecordSuccessfulRecoveryTransitionForensicsAsync(
                        ownership,
                        reason,
                        forensicsId,
                        isLocalQueuedRecovery,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return new AiRuntimeExecutionRecoveryTransitionResult
            {
                Accepted = true,
                Changed = true,
                SharedRunId = ownership.SharedRunId,
                RuntimeInstanceId = ownership.RuntimeInstanceId,
                LocalRunId = ownership.LocalRunId,
                ExecutionId = ownership.ExecutionId,
                Action = "requeue-shared-run",
                Reason = reason
            };
        }

        /// <summary>
        /// Creates metadata instructing the next runtime dispatch how to recover the shared run.
        /// </summary>
        /// <param name="ownership">The resolved shared run ownership.</param>
        /// <param name="reason">The recovery reason.</param>
        /// <param name="forensicsId">The optional recovery forensics identifier.</param>
        /// <param name="isLocalQueuedRecovery">A value indicating whether the recovery candidate is local queued work without an execution id.</param>
        /// <returns>The recovery metadata.</returns>
        private static IReadOnlyDictionary<string, string> CreateRecoveryMetadata(
            AiSharedRunOwnershipResolutionResult ownership,
            string reason,
            string? forensicsId,
            bool isLocalQueuedRecovery)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [RecoveryForensicsIdMetadataKey] = forensicsId ?? string.Empty,
                [RecoveryModeMetadataKey] = isLocalQueuedRecovery
                    ? RecoveryModeRequeueLocalQueuedRun
                    : RecoveryModeResumeExistingExecution,
                [RecoveryFailedExecutionIdMetadataKey] = ownership.ExecutionId ?? string.Empty,
                [RecoveryFailedRuntimeInstanceIdMetadataKey] = ownership.RuntimeInstanceId ?? string.Empty,
                [RecoveryFailedLocalRunIdMetadataKey] = ownership.LocalRunId ?? string.Empty,
                [RecoveryReasonMetadataKey] = reason,
                [FailedRuntimeInstanceIdMetadataKey] = ownership.RuntimeInstanceId ?? string.Empty,
                [FailedLocalRunIdMetadataKey] = ownership.LocalRunId ?? string.Empty
            };
        }

        /// <summary>
        /// Records forensics evidence after a successful recovery transition mutation.
        /// </summary>
        /// <param name="ownership">The resolved shared run ownership.</param>
        /// <param name="reason">The recovery reason.</param>
        /// <param name="forensicsId">The recovery forensics identifier.</param>
        /// <param name="isLocalQueuedRecovery">A value indicating whether the recovered work was local queued work without a durable execution id.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>A task that completes when the forensics evidence has been recorded.</returns>
        private async Task RecordSuccessfulRecoveryTransitionForensicsAsync(
            AiSharedRunOwnershipResolutionResult ownership,
            string reason,
            string forensicsId,
            bool isLocalQueuedRecovery,
            CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            var runtimeFailureIncidentId = CreateRuntimeFailureIncidentId(ownership);
            var metadata = CreateRecoveryForensicsMetadata(
                ownership,
                reason,
                forensicsId,
                isLocalQueuedRecovery);

            var recoveryMode = isLocalQueuedRecovery
                ? RecoveryModeRequeueLocalQueuedRun
                : RecoveryModeResumeExistingExecution;

            var recoveryKind = isLocalQueuedRecovery
                ? "local-queued-run-requeue"
                : RecoveryKindInFlightExecutionResume;

            var record = new AiRuntimeRecoveryForensicsRecord
            {
                Identity = new AiRuntimeRecoveryForensicsIdentity
                {
                    ForensicsId = forensicsId,
                    ExecutionId = ownership.ExecutionId ?? string.Empty,
                    SharedRunId = ownership.SharedRunId,
                    TenantId = ResolveMetadataValue(metadata, "tenantId", "tenant.id"),
                    TenantGroupId = ResolveMetadataValue(metadata, "tenantGroupId", "tenant.group.id"),
                    ControlPlaneId = ResolveMetadataValue(metadata, "controlPlaneId", "control.plane.id"),
                    PipelineName = ResolveMetadataValue(metadata, "pipelineName", "pipeline.name", "pipelineKey", "pipeline.key")
                },
                Failure = new AiRuntimeRecoveryFailureInfo
                {
                    RuntimeFailureIncidentId = runtimeFailureIncidentId,
                    FailedRuntimeInstanceId = ownership.RuntimeInstanceId,
                    FailedLocalRunId = ownership.LocalRunId,
                    FailureSignal = "runtime-execution-recovery",
                    SuppressCapacityReason = reason,
                    FailureDetectedAtUtc = now
                },
                Recovery = new AiRuntimeRecoveryInfo
                {
                    RecoveryMode = recoveryMode,
                    RecoveryKind = recoveryKind,
                    Outcome = "requeued",
                    Reason = reason,
                    RecoveryStartedAtUtc = now
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
                Events =
                [
                    CreateForensicsEvent(
                        forensicsId,
                        isLocalQueuedRecovery
                            ? "SharedRunRequeuedForLocalQueuedRecovery"
                            : AiRuntimeRecoveryForensicsEventType.SharedRunRequeuedForResume,
                        "requeued",
                        reason,
                        ownership,
                        now,
                        metadata),
                    CreateForensicsEvent(
                        forensicsId,
                        AiRuntimeRecoveryForensicsEventType.FailedLocalRunMarkedRequeuedForRecovery,
                        "requeued",
                        reason,
                        ownership,
                        now,
                        metadata)
                ],
                Metadata = metadata,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            await this.forensicsRecorder
                .RecordAsync(
                    record,
                    cancellationToken)
                .ConfigureAwait(false);
        }


        /// <summary>
        /// Determines whether the ownership represents local queued work that has not yet started a durable execution.
        /// </summary>
        /// <param name="ownership">The resolved shared run ownership.</param>
        /// <returns><c>true</c> when the ownership has no execution id; otherwise, <c>false</c>.</returns>
        private static bool IsLocalQueuedRecovery(
            AiSharedRunOwnershipResolutionResult ownership)
        {
            ArgumentNullException.ThrowIfNull(ownership);

            return string.IsNullOrWhiteSpace(ownership.ExecutionId);
        }

        /// <summary>
        /// Creates a deterministic forensics identifier for the recovery attempt.
        /// </summary>
        /// <param name="ownership">The resolved shared run ownership.</param>
        /// <param name="isLocalQueuedRecovery">A value indicating whether the recovered work was local queued work without a durable execution id.</param>
        /// <returns>The forensics identifier.</returns>
        private static string CreateForensicsId(
            AiSharedRunOwnershipResolutionResult ownership,
            bool isLocalQueuedRecovery)
        {
            return string.Join(
                ":",
                "runtime-recovery",
                isLocalQueuedRecovery
                    ? "local-queued"
                    : ownership.ExecutionId,
                ownership.SharedRunId,
                ownership.LocalRunId);
        }

        /// <summary>
        /// Creates a deterministic runtime failure incident identifier.
        /// </summary>
        /// <param name="ownership">The resolved shared run ownership.</param>
        /// <returns>The runtime failure incident identifier.</returns>
        private static string CreateRuntimeFailureIncidentId(
            AiSharedRunOwnershipResolutionResult ownership)
        {
            return string.Join(
                ":",
                "runtime-failure",
                ownership.RuntimeInstanceId);
        }

        /// <summary>
        /// Creates metadata shared by recovery forensics records and events.
        /// </summary>
        /// <param name="ownership">The resolved shared run ownership.</param>
        /// <param name="reason">The recovery reason.</param>
        /// <param name="forensicsId">The recovery forensics identifier.</param>
        /// <param name="isLocalQueuedRecovery">A value indicating whether the recovered work was local queued work without a durable execution id.</param>
        /// <returns>The metadata dictionary.</returns>
        private static IReadOnlyDictionary<string, string> CreateRecoveryForensicsMetadata(
            AiSharedRunOwnershipResolutionResult ownership,
            string reason,
            string forensicsId,
            bool isLocalQueuedRecovery)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [RecoveryForensicsIdMetadataKey] = forensicsId,
                [RecoveryModeMetadataKey] = isLocalQueuedRecovery
                    ? RecoveryModeRequeueLocalQueuedRun
                    : RecoveryModeResumeExistingExecution,
                [RecoveryFailedExecutionIdMetadataKey] = ownership.ExecutionId ?? string.Empty,
                [RecoveryFailedRuntimeInstanceIdMetadataKey] = ownership.RuntimeInstanceId ?? string.Empty,
                [RecoveryFailedLocalRunIdMetadataKey] = ownership.LocalRunId ?? string.Empty,
                [RecoveryReasonMetadataKey] = reason,
                [FailedRuntimeInstanceIdMetadataKey] = ownership.RuntimeInstanceId ?? string.Empty,
                [FailedLocalRunIdMetadataKey] = ownership.LocalRunId ?? string.Empty
            };
        }

        /// <summary>
        /// Creates a recovery forensics event.
        /// </summary>
        /// <param name="forensicsId">The forensics identifier.</param>
        /// <param name="eventType">The event type.</param>
        /// <param name="outcome">The event outcome.</param>
        /// <param name="reason">The event reason.</param>
        /// <param name="ownership">The resolved shared run ownership.</param>
        /// <param name="timestampUtc">The event timestamp.</param>
        /// <param name="metadata">The recovery metadata.</param>
        /// <returns>The recovery forensics event.</returns>
        private static AiRuntimeRecoveryForensicsEvent CreateForensicsEvent(
            string forensicsId,
            string eventType,
            string outcome,
            string reason,
            AiSharedRunOwnershipResolutionResult ownership,
            DateTimeOffset timestampUtc,
            IReadOnlyDictionary<string, string> metadata)
        {
            return new AiRuntimeRecoveryForensicsEvent
            {
                EventId = string.Join(
                    ":",
                    forensicsId,
                    eventType),
                ForensicsId = forensicsId,
                TimestampUtc = timestampUtc,
                EventType = eventType,
                Outcome = outcome,
                Reason = reason,
                ExecutionId = ownership.ExecutionId,
                SharedRunId = ownership.SharedRunId,
                LocalRunId = ownership.LocalRunId,
                RuntimeInstanceId = ownership.RuntimeInstanceId,
                Metadata = metadata
            };
        }

        private static string? ResolveMetadataValue(
            IReadOnlyDictionary<string, string> metadata,
            params string[] keys)
        {
            foreach (var key in keys)
            {
                if (metadata.TryGetValue(key, out var value) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            foreach (var key in keys)
            {
                var match = metadata.FirstOrDefault(pair =>
                    string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(match.Value))
                {
                    return match.Value;
                }
            }

            return null;
        }
    }
}