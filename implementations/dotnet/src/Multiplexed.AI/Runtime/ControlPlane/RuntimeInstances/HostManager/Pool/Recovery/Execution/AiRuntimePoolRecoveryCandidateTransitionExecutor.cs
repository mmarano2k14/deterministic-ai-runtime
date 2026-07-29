using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Execution
{
    /// <summary>
    /// Centralizes candidate-level recovery validation and the existing transition boundary.
    /// </summary>
    public sealed class AiRuntimePoolRecoveryCandidateTransitionExecutor :
        IAiRuntimePoolRecoveryCandidateTransitionExecutor
    {
        private const string InFlightReason =
            "runtime-pool-claimed-in-flight-recovery";
        private const string LocalQueuedReason =
            "runtime-pool-claimed-local-queued-recovery";
        private const string UnsupportedCandidateReason =
            "unsupported-recovery-candidate-kind";
        private const string RecoveryReconciliationOperation =
            "runtime-execution-recovery-reconcile";
        private const string RecoveryForensicsIdMetadataKey =
            "recovery.forensicsId";
        private const string RecoveryModeMetadataKey =
            "recovery.mode";
        private const string RecoveryReasonMetadataKey =
            "recovery.reason";
        private const string RecoveryFailedRuntimeInstanceIdMetadataKey =
            "recovery.failedRuntimeInstanceId";
        private const string RecoveryFailedLocalRunIdMetadataKey =
            "recovery.failedLocalRunId";
        private const string RecoveryFailedExecutionIdMetadataKey =
            "recovery.failedExecutionId";
        private const string RecoveryModeResumeExistingExecution =
            "resume-existing-execution";
        private const string RecoveryModeRequeueLocalQueuedRun =
            "requeue-local-queued-run";

        private readonly IAiSharedRunOwnershipResolver ownershipResolver;
        private readonly IAiRuntimeExecutionRecoveryTransitionService
            transitionService;
        private readonly IAiRuntimeRecoveryForensicsRecorder
            forensicsRecorder;
        private readonly IAiControlPlaneObserver observer;

        /// <summary>
        /// Preserves the existing public composition with no-op observability.
        /// </summary>
        public AiRuntimePoolRecoveryCandidateTransitionExecutor(
            IAiSharedRunOwnershipResolver ownershipResolver,
            IAiRuntimeExecutionRecoveryTransitionService transitionService)
            : this(
                ownershipResolver,
                transitionService,
                new NoopAiRuntimeRecoveryForensicsRecorder(),
                new NoopAiControlPlaneObserver())
        {
        }

        /// <summary>
        /// Preserves the existing public composition with no-op control-plane observability.
        /// </summary>
        public AiRuntimePoolRecoveryCandidateTransitionExecutor(
            IAiSharedRunOwnershipResolver ownershipResolver,
            IAiRuntimeExecutionRecoveryTransitionService transitionService,
            IAiRuntimeRecoveryForensicsRecorder forensicsRecorder)
            : this(
                ownershipResolver,
                transitionService,
                forensicsRecorder,
                new NoopAiControlPlaneObserver())
        {
        }

        /// <summary>
        /// Initializes the claimed Runtime Pool recovery transition executor with
        /// durable forensics and the existing control-plane observability pipeline.
        /// </summary>
        /// <param name="ownershipResolver">The shared run ownership resolver.</param>
        /// <param name="transitionService">The recovery transition service.</param>
        /// <param name="forensicsRecorder">The recovery forensics recorder.</param>
        /// <param name="observer">The control-plane observer.</param>
        public AiRuntimePoolRecoveryCandidateTransitionExecutor(
            IAiSharedRunOwnershipResolver ownershipResolver,
            IAiRuntimeExecutionRecoveryTransitionService transitionService,
            IAiRuntimeRecoveryForensicsRecorder forensicsRecorder,
            IAiControlPlaneObserver observer)
        {
            this.ownershipResolver =
                ownershipResolver
                ?? throw new ArgumentNullException(nameof(ownershipResolver));
            this.transitionService =
                transitionService
                ?? throw new ArgumentNullException(nameof(transitionService));
            this.forensicsRecorder =
                forensicsRecorder
                ?? throw new ArgumentNullException(nameof(forensicsRecorder));
            this.observer =
                observer
                ?? throw new ArgumentNullException(nameof(observer));
        }

        public async Task<IReadOnlyList<AiRuntimePoolRecoveryCandidateOutcome>>
            ExecuteAsync(
                string failureId,
                IReadOnlyList<AiRuntimePoolAssignedWorkCandidate> candidates,
                Func<AiRuntimePoolAssignedWorkCandidate, bool> isAuthorized,
                Func<CancellationToken, Task> ensureActiveLeaseAsync,
                CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(failureId);
            ArgumentNullException.ThrowIfNull(candidates);
            ArgumentNullException.ThrowIfNull(isAuthorized);
            ArgumentNullException.ThrowIfNull(ensureActiveLeaseAsync);

            var outcomes =
                new List<AiRuntimePoolRecoveryCandidateOutcome>(
                    candidates.Count);

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(candidate);

                if (!isAuthorized(candidate))
                {
                    throw CreateAuthorityException(
                        failureId,
                        candidate.LocalRunId,
                        AiRuntimePoolRecoveryExecutionAuthorityFailure
                            .CandidateBoundaryViolation,
                        $"Candidate '{candidate.LocalRunId}' escaped its claimed recovery inventory boundary.");
                }

                await ensureActiveLeaseAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (candidate.Kind ==
                    AiRuntimePoolAssignedWorkKind.OtherRecoverable)
                {
                    outcomes.Add(CreateUnsupportedOutcome(candidate));
                    continue;
                }

                ValidateCandidateKind(candidate);

                var ownership =
                    await this.ownershipResolver
                        .ResolveAsync(
                            new AiSharedRunOwnershipResolutionRequest
                            {
                                RuntimeInstanceId =
                                    candidate.RuntimeInstanceId,
                                LocalRunId = candidate.LocalRunId,
                                ExecutionId = candidate.ExecutionId,
                                SharedRunId = candidate.SharedRunId,
                                TenantId = candidate.TenantId,
                                TenantGroupId = candidate.TenantGroupId
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                ValidateOwnershipBoundary(
                    failureId,
                    candidate,
                    ownership);

                await this.RecordRecoveryCandidateDetectedForensicsAsync(
                        candidate,
                        ownership,
                        cancellationToken)
                    .ConfigureAwait(false);

                var transition =
                    await this.transitionService
                        .ApplyAsync(
                            new AiRuntimeExecutionRecoveryTransitionRequest
                            {
                                Ownership = ownership,
                                DryRun = false,
                                Reason =
                                    candidate.Kind ==
                                        AiRuntimePoolAssignedWorkKind.InFlight
                                        ? InFlightReason
                                        : LocalQueuedReason
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                ValidateTransitionBoundary(
                    failureId,
                    candidate,
                    transition);

                await ensureActiveLeaseAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (transition.Accepted && transition.Changed)
                {
                    await this.RecordRecoveryReconciliationSucceededAsync(
                            failureId,
                            candidate,
                            ownership,
                            transition,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                outcomes.Add(
                    new AiRuntimePoolRecoveryCandidateOutcome
                    {
                        Candidate = candidate,
                        Ownership = ownership,
                        Transition = transition,
                        CompletedAtUtc = DateTimeOffset.UtcNow
                    });
            }

            return outcomes;
        }

        /// <summary>
        /// Records candidate detection for the claimed Runtime Pool recovery path.
        /// </summary>
        /// <remarks>
        /// Runtime Pool process and Pod recovery execute the shared transition service
        /// directly and therefore do not pass through
        /// AiRuntimeExecutionRecoveryReconciler. Recording the event here keeps the
        /// forensics timeline complete for every recovery entry point.
        /// </remarks>
        private async Task RecordRecoveryCandidateDetectedForensicsAsync(
            AiRuntimePoolAssignedWorkCandidate candidate,
            AiSharedRunOwnershipResolutionResult ownership,
            CancellationToken cancellationToken)
        {
            if (candidate.Kind !=
                    AiRuntimePoolAssignedWorkKind.InFlight ||
                string.IsNullOrWhiteSpace(candidate.ExecutionId) ||
                string.IsNullOrWhiteSpace(ownership.SharedRunId))
            {
                return;
            }

            var forensicsId =
                CreateForensicsId(
                    candidate,
                    ownership);

            await this.forensicsRecorder
                .RecordEventAsync(
                    new AiRuntimeRecoveryForensicsEvent
                    {
                        EventId =
                            string.Join(
                                ":",
                                forensicsId,
                                AiRuntimeRecoveryForensicsEventType
                                    .ExecutionRecoveryCandidateDetected),
                        ForensicsId = forensicsId,
                        TimestampUtc = DateTimeOffset.UtcNow,
                        EventType =
                            AiRuntimeRecoveryForensicsEventType
                                .ExecutionRecoveryCandidateDetected,
                        Outcome = ownership.CanRecover
                            ? "recoverable"
                            : "not-recoverable",
                        Reason = ownership.Reason,
                        ExecutionId = candidate.ExecutionId,
                        SharedRunId = ownership.SharedRunId,
                        LocalRunId = candidate.LocalRunId,
                        RuntimeInstanceId =
                            candidate.RuntimeInstanceId,
                        Metadata =
                            new Dictionary<string, string>(
                                StringComparer.OrdinalIgnoreCase)
                            {
                                ["tenant.id"] =
                                    candidate.TenantId ??
                                    string.Empty,
                                ["tenant.group.id"] =
                                    candidate.TenantGroupId ??
                                    string.Empty,
                                ["candidate.canRecover"] =
                                    ownership.CanRecover.ToString()
                            }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Records one successful Runtime Pool recovery transition through the existing
        /// control-plane observability pipeline used by the generic recovery reconciler.
        /// </summary>
        private async Task RecordRecoveryReconciliationSucceededAsync(
            string failureId,
            AiRuntimePoolAssignedWorkCandidate candidate,
            AiSharedRunOwnershipResolutionResult ownership,
            AiRuntimeExecutionRecoveryTransitionResult transition,
            CancellationToken cancellationToken)
        {
            var sharedRunId =
                transition.SharedRunId ??
                ownership.SharedRunId ??
                candidate.SharedRunId;

            var forensicsId =
                CreateForensicsId(
                    candidate,
                    ownership);

            try
            {
                await this.observer
                    .RecordAsync(
                        new AiControlPlaneEvent
                        {
                            EventType =
                                AiControlPlaneEventType.OperationCompleted,
                            Area = AiControlPlaneArea.Recovery,
                            Operation = RecoveryReconciliationOperation,
                            Outcome =
                                AiControlPlaneOperationOutcome.Succeeded,
                            Correlation =
                                new AiRuntimeExecutionCorrelationContext
                                {
                                    CorrelationId = forensicsId,
                                    RunId = sharedRunId,
                                    ExecutionId = candidate.ExecutionId,
                                    RuntimeInstanceId =
                                        candidate.RuntimeInstanceId
                                },
                            Message =
                                "Runtime Pool recovery transition completed successfully.",
                            Properties =
                                new Dictionary<string, object?>
                                {
                                    ["failureId"] = failureId,
                                    ["poolId"] = candidate.PoolId,
                                    ["hostId"] = candidate.HostId,
                                    ["routeId"] = candidate.RouteId,
                                    ["tenantId"] = candidate.TenantId,
                                    ["tenant.id"] = candidate.TenantId,
                                    ["tenantGroupId"] =
                                        candidate.TenantGroupId,
                                    ["tenant.group.id"] =
                                        candidate.TenantGroupId,
                                    ["sharedRunId"] = sharedRunId,
                                    ["localRunId"] = candidate.LocalRunId,
                                    ["executionId"] = candidate.ExecutionId,
                                    ["runtimeInstanceId"] =
                                        candidate.RuntimeInstanceId,
                                    [RecoveryForensicsIdMetadataKey] =
                                        forensicsId,
                                    [RecoveryModeMetadataKey] =
                                        candidate.Kind ==
                                            AiRuntimePoolAssignedWorkKind
                                                .InFlight
                                            ? RecoveryModeResumeExistingExecution
                                            : RecoveryModeRequeueLocalQueuedRun,
                                    [RecoveryReasonMetadataKey] =
                                        transition.Reason,
                                    [RecoveryFailedRuntimeInstanceIdMetadataKey] =
                                        candidate.RuntimeInstanceId,
                                    [RecoveryFailedLocalRunIdMetadataKey] =
                                        candidate.LocalRunId,
                                    [RecoveryFailedExecutionIdMetadataKey] =
                                        candidate.ExecutionId
                                }
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Control-plane observability must not break claimed recovery.
            }
        }

        /// <summary>
        /// Creates the deterministic forensics identifier shared by transition forensics
        /// and control-plane recovery ledger evidence.
        /// </summary>
        private static string CreateForensicsId(
            AiRuntimePoolAssignedWorkCandidate candidate,
            AiSharedRunOwnershipResolutionResult ownership)
        {
            var isLocalQueued =
                candidate.Kind ==
                AiRuntimePoolAssignedWorkKind.LocalQueued;

            return string.Join(
                ":",
                "runtime-recovery",
                isLocalQueued
                    ? "local-queued"
                    : candidate.ExecutionId,
                ownership.SharedRunId ?? candidate.SharedRunId,
                candidate.LocalRunId);
        }

        private static void ValidateCandidateKind(
            AiRuntimePoolAssignedWorkCandidate candidate)
        {
            if (candidate.Kind ==
                    AiRuntimePoolAssignedWorkKind.InFlight &&
                string.IsNullOrWhiteSpace(candidate.ExecutionId))
            {
                throw CreateAuthorityException(
                    candidate.FailureId,
                    candidate.LocalRunId,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .InFlightExecutionIdMissing,
                    $"In-flight candidate '{candidate.LocalRunId}' is missing its durable ExecutionId.");
            }

            if (candidate.Kind ==
                    AiRuntimePoolAssignedWorkKind.LocalQueued &&
                string.IsNullOrWhiteSpace(candidate.SharedRunId))
            {
                throw CreateAuthorityException(
                    candidate.FailureId,
                    candidate.LocalRunId,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .LocalQueuedSharedRunIdMissing,
                    $"Local-queued candidate '{candidate.LocalRunId}' is missing its durable SharedRunId.");
            }

            if (candidate.Kind ==
                    AiRuntimePoolAssignedWorkKind.LocalQueued &&
                !string.IsNullOrWhiteSpace(candidate.ExecutionId))
            {
                throw CreateAuthorityException(
                    candidate.FailureId,
                    candidate.LocalRunId,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .CandidateBoundaryViolation,
                    $"Local-queued candidate '{candidate.LocalRunId}' unexpectedly carries ExecutionId '{candidate.ExecutionId}'.");
            }
        }

        private static void ValidateOwnershipBoundary(
            string failureId,
            AiRuntimePoolAssignedWorkCandidate candidate,
            AiSharedRunOwnershipResolutionResult ownership)
        {
            ArgumentNullException.ThrowIfNull(ownership);

            var matches =
                StringComparer.Ordinal.Equals(
                    ownership.RuntimeInstanceId,
                    candidate.RuntimeInstanceId) &&
                (string.IsNullOrWhiteSpace(ownership.LocalRunId) ||
                 StringComparer.Ordinal.Equals(
                     ownership.LocalRunId,
                     candidate.LocalRunId)) &&
                (string.IsNullOrWhiteSpace(ownership.ExecutionId) ||
                 StringComparer.Ordinal.Equals(
                     ownership.ExecutionId,
                     candidate.ExecutionId)) &&
                (string.IsNullOrWhiteSpace(ownership.TenantId) ||
                 StringComparer.Ordinal.Equals(
                     ownership.TenantId,
                     candidate.TenantId)) &&
                (string.IsNullOrWhiteSpace(ownership.TenantGroupId) ||
                 StringComparer.Ordinal.Equals(
                     ownership.TenantGroupId,
                     candidate.TenantGroupId)) &&
                (string.IsNullOrWhiteSpace(ownership.SharedRunId) ||
                 string.IsNullOrWhiteSpace(candidate.SharedRunId) ||
                 StringComparer.Ordinal.Equals(
                     ownership.SharedRunId,
                     candidate.SharedRunId));

            if (!matches)
            {
                throw CreateAuthorityException(
                    failureId,
                    candidate.LocalRunId,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .OwnershipBoundaryViolation,
                    $"Ownership resolution for '{candidate.LocalRunId}' escaped the exact candidate identity boundary.");
            }

            if (ownership.Resolved)
            {
                var complete =
                    StringComparer.Ordinal.Equals(
                        ownership.LocalRunId,
                        candidate.LocalRunId) &&
                    StringComparer.Ordinal.Equals(
                        ownership.ExecutionId,
                        candidate.ExecutionId) &&
                    StringComparer.Ordinal.Equals(
                        ownership.TenantId,
                        candidate.TenantId) &&
                    StringComparer.Ordinal.Equals(
                        ownership.TenantGroupId,
                        candidate.TenantGroupId) &&
                    (string.IsNullOrWhiteSpace(candidate.SharedRunId) ||
                     StringComparer.Ordinal.Equals(
                         ownership.SharedRunId,
                         candidate.SharedRunId));

                if (!complete)
                {
                    throw CreateAuthorityException(
                        failureId,
                        candidate.LocalRunId,
                        AiRuntimePoolRecoveryExecutionAuthorityFailure
                            .OwnershipBoundaryViolation,
                        $"Resolved ownership for '{candidate.LocalRunId}' did not preserve its complete durable identity.");
                }
            }
        }

        private static void ValidateTransitionBoundary(
            string failureId,
            AiRuntimePoolAssignedWorkCandidate candidate,
            AiRuntimeExecutionRecoveryTransitionResult transition)
        {
            ArgumentNullException.ThrowIfNull(transition);

            var matches =
                StringComparer.Ordinal.Equals(
                    transition.RuntimeInstanceId,
                    candidate.RuntimeInstanceId) &&
                (string.IsNullOrWhiteSpace(transition.LocalRunId) ||
                 StringComparer.Ordinal.Equals(
                     transition.LocalRunId,
                     candidate.LocalRunId)) &&
                (string.IsNullOrWhiteSpace(transition.ExecutionId) ||
                 StringComparer.Ordinal.Equals(
                     transition.ExecutionId,
                     candidate.ExecutionId)) &&
                (string.IsNullOrWhiteSpace(transition.SharedRunId) ||
                 string.IsNullOrWhiteSpace(candidate.SharedRunId) ||
                 StringComparer.Ordinal.Equals(
                     transition.SharedRunId,
                     candidate.SharedRunId));

            if (!matches)
            {
                throw CreateAuthorityException(
                    failureId,
                    candidate.LocalRunId,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .TransitionBoundaryViolation,
                    $"Recovery transition for '{candidate.LocalRunId}' returned another identity.");
            }
        }

        private static AiRuntimePoolRecoveryCandidateOutcome
            CreateUnsupportedOutcome(
                AiRuntimePoolAssignedWorkCandidate candidate)
        {
            return new AiRuntimePoolRecoveryCandidateOutcome
            {
                Candidate = candidate,
                Transition =
                    new AiRuntimeExecutionRecoveryTransitionResult
                    {
                        Accepted = false,
                        Changed = false,
                        SharedRunId = candidate.SharedRunId,
                        RuntimeInstanceId = candidate.RuntimeInstanceId,
                        LocalRunId = candidate.LocalRunId,
                        ExecutionId = candidate.ExecutionId,
                        Action = "none",
                        Reason = UnsupportedCandidateReason
                    },
                CompletedAtUtc = DateTimeOffset.UtcNow
            };
        }

        private static AiRuntimePoolRecoveryExecutionAuthorityException
            CreateAuthorityException(
                string failureId,
                string? localRunId,
                AiRuntimePoolRecoveryExecutionAuthorityFailure reason,
                string message)
        {
            return new AiRuntimePoolRecoveryExecutionAuthorityException(
                failureId,
                localRunId,
                reason,
                message);
        }
    }
}
