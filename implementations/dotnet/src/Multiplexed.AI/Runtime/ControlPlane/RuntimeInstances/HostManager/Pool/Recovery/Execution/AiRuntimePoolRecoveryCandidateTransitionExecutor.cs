using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Pool;


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

        private readonly IAiSharedRunOwnershipResolver ownershipResolver;
        private readonly IAiRuntimeExecutionRecoveryTransitionService
            transitionService;
        private readonly IAiRuntimeRecoveryForensicsRecorder
            forensicsRecorder;
        private readonly IAiControlPlaneObserver observer;
        private readonly AiRuntimeLifecycleEventWriter lifecycleWriter;

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
                new NoopAiControlPlaneObserver(),
                NoopAiRuntimeLifecycleJournal.Instance)
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
                new NoopAiControlPlaneObserver(),
                NoopAiRuntimeLifecycleJournal.Instance)
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
            : this(
                ownershipResolver,
                transitionService,
                forensicsRecorder,
                observer,
                NoopAiRuntimeLifecycleJournal.Instance)
        {
        }

        /// <summary>
        /// Initializes claimed recovery with durable lifecycle correlation.
        /// </summary>
        public AiRuntimePoolRecoveryCandidateTransitionExecutor(
            IAiSharedRunOwnershipResolver ownershipResolver,
            IAiRuntimeExecutionRecoveryTransitionService transitionService,
            IAiRuntimeRecoveryForensicsRecorder forensicsRecorder,
            IAiControlPlaneObserver observer,
            IAiRuntimeLifecycleJournal lifecycleJournal)
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
            this.lifecycleWriter = new AiRuntimeLifecycleEventWriter(
                lifecycleJournal
                ?? throw new ArgumentNullException(nameof(lifecycleJournal)));
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
                                RuntimeFailureIncidentId = failureId,
                                CorrelationId = CreateForensicsId(candidate, ownership),
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

                    await this.RecordWorkReleasedLifecycleAsync(
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
                            ? AiRuntimeRecoveryOutcomeCodes.Recoverable
                            : AiRuntimeRecoveryOutcomeCodes.NotRecoverable,
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
                                [AiRuntimeInstanceIsolationMetadataKeys.TenantId] =
                                    candidate.TenantId ??
                                    string.Empty,
                                [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] =
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
                            Operation = AiRuntimeRecoveryOperationNames.ExecutionRecoveryReconcile,
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
                                    [AiRuntimePoolMetadataKeys.CamelCasePoolId] = candidate.PoolId,
                                    [AiRuntimeHostMetadataKeys.CamelCaseHostId] = candidate.HostId,
                                    ["routeId"] = candidate.RouteId,
                                    [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantId] = candidate.TenantId,
                                    [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = candidate.TenantId,
                                    [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantGroupId] =
                                        candidate.TenantGroupId,
                                    [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] =
                                        candidate.TenantGroupId,
                                    [AiRunMetadataKeys.CamelCaseSharedRunId] = sharedRunId,
                                    [AiRunMetadataKeys.CamelCaseLocalRunId] = candidate.LocalRunId,
                                    [AiExecutionMetadataKeys.CamelCaseExecutionId] = candidate.ExecutionId,
                                    [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] =
                                        candidate.RuntimeInstanceId,
                                    [AiRuntimeRecoveryMetadataKeys.ForensicsId] =
                                        forensicsId,
                                    [AiRuntimeRecoveryMetadataKeys.Mode] =
                                        candidate.Kind ==
                                            AiRuntimePoolAssignedWorkKind
                                                .InFlight
                                            ? AiRuntimeRecoveryModes.ResumeExistingExecution
                                            : AiRuntimeRecoveryModes.RequeueLocalQueuedRun,
                                    [AiRuntimeRecoveryMetadataKeys.Reason] =
                                        transition.Reason,
                                    [AiRuntimeRecoveryMetadataKeys.FailedRuntimeInstanceId] =
                                        candidate.RuntimeInstanceId,
                                    [AiRuntimeRecoveryMetadataKeys.FailedLocalRunId] =
                                        candidate.LocalRunId,
                                    [AiRuntimeRecoveryMetadataKeys.FailedExecutionId] =
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
        /// Records that failed-runtime ownership was durably released for redispatch.
        /// </summary>
        private async Task RecordWorkReleasedLifecycleAsync(
            string failureId,
            AiRuntimePoolAssignedWorkCandidate candidate,
            AiSharedRunOwnershipResolutionResult ownership,
            AiRuntimeExecutionRecoveryTransitionResult transition,
            CancellationToken cancellationToken)
        {
            var context = await this.lifecycleWriter
                .ResolveContextAsync(
                    candidate.RuntimeInstanceId,
                    candidate.HostId,
                    candidate.PoolId,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var forensicsId = CreateForensicsId(candidate, ownership);
            var eventId = AiRuntimeLifecycleEventWriter.CreateEventId(
                AiRuntimeLifecycleEventType.WorkReleased,
                candidate.LocalRunId,
                failureId);

            await this.lifecycleWriter
                .AppendOnceAsync(
                    new AiRuntimeLifecycleEvent
                    {
                        EventId = eventId,
                        EventType = AiRuntimeLifecycleEventType.WorkReleased,
                        TimestampUtc = DateTimeOffset.UtcNow,
                        ControlPlaneId = context.ControlPlaneId,
                        HostCreationMode = context.HostCreationMode,
                        ProviderName = context.ProviderName,
                        PoolId = candidate.PoolId ?? context.PoolId,
                        HostId = candidate.HostId ?? context.HostId,
                        KubernetesPodUid = context.KubernetesPodUid,
                        KubernetesNamespace = context.KubernetesNamespace,
                        KubernetesPodName = context.KubernetesPodName,
                        KubernetesNodeName = context.KubernetesNodeName,
                        RuntimeInstanceId = candidate.RuntimeInstanceId,
                        RuntimeId = context.RuntimeId,
                        ProcessId = context.ProcessId,
                        TenantId = candidate.TenantId,
                        TenantGroupId = candidate.TenantGroupId,
                        SharedRunId = transition.SharedRunId ?? ownership.SharedRunId ?? candidate.SharedRunId,
                        LocalRunId = candidate.LocalRunId,
                        ExecutionId = candidate.ExecutionId,
                        RuntimeFailureIncidentId = failureId,
                        ForensicsId = forensicsId,
                        CorrelationId = forensicsId,
                        PreviousStatus = "assigned",
                        CurrentStatus = AiRuntimeRecoveryTransitionStatuses.ReleasedForRecovery,
                        Reason = transition.Reason,
                        Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["recovery.candidateKind"] = candidate.Kind.ToString(),
                            ["recovery.transitionAction"] = transition.Action
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
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
