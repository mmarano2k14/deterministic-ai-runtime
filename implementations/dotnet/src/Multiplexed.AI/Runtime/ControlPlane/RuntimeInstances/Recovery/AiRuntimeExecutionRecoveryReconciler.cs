using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery
{
    /// <summary>
    /// Default runtime execution recovery reconciler.
    /// </summary>
    /// <remarks>
    /// This reconciler owns execution recovery coordination only.
    ///
    /// It scans unavailable runtime instances, reports unfinished runtime runs,
    /// resolves shared run ownership when available, and routes validated recovery
    /// candidates through the runtime execution recovery transition service.
    ///
    /// It also detects orphaned unfinished runtime runs that are still marked as
    /// running in the runtime run execution index but whose runtime instance is no
    /// longer present in the runtime instance registry.
    ///
    /// It does not directly mutate shared queue state, runtime execution index state,
    /// fail, cancel, dead-letter, restart, or kill anything.
    ///
    /// Runtime health detection is owned by the runtime instance health reconciler.
    /// Runtime lifecycle is owned by providers and host managers.
    /// Recovery mutation boundaries are owned by the runtime execution recovery transition service.
    /// </remarks>
    public sealed class AiRuntimeExecutionRecoveryReconciler : IAiRuntimeExecutionRecoveryReconciler
    {
        private const string RecoveryReconciliationOperation = "runtime-execution-recovery-reconcile";
        private const string RuntimeStatusNotIncludedReason = "runtime-status-not-included";
        private const string NoUnfinishedRuntimeRunsReason = "no-unfinished-runtime-runs";
        private const string OrphanedRuntimeInstanceReason = "orphaned-runtime-instance";

        private readonly IAiRuntimeInstanceRegistry runtimeInstanceRegistry;
        private readonly IAiRuntimeRunExecutionIndex runtimeRunExecutionIndex;
        private readonly IAiSharedRunOwnershipResolver sharedRunOwnershipResolver;
        private readonly IAiRuntimeExecutionRecoveryTransitionService transitionService;
        private readonly IAiRuntimeRecoveryForensicsRecorder forensicsRecorder;
        private readonly IAiControlPlaneObserver observer;
        private readonly AiRuntimeExecutionRecoveryReconciliationOptions options;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeExecutionRecoveryReconciler"/> class.
        /// </summary>
        /// <param name="runtimeInstanceRegistry">The runtime instance registry.</param>
        /// <param name="runtimeRunExecutionIndex">The runtime run execution index.</param>
        /// <param name="sharedRunOwnershipResolver">The shared run ownership resolver.</param>
        /// <param name="transitionService">The runtime execution recovery transition service.</param>
        /// <param name="options">The recovery reconciliation options.</param>
        public AiRuntimeExecutionRecoveryReconciler(
            IAiRuntimeInstanceRegistry runtimeInstanceRegistry,
            IAiRuntimeRunExecutionIndex runtimeRunExecutionIndex,
            IAiSharedRunOwnershipResolver sharedRunOwnershipResolver,
            IAiRuntimeExecutionRecoveryTransitionService transitionService,
            IOptions<AiRuntimeExecutionRecoveryReconciliationOptions> options)
            : this(
                runtimeInstanceRegistry,
                runtimeRunExecutionIndex,
                sharedRunOwnershipResolver,
                transitionService,
                options,
                new NoopAiRuntimeRecoveryForensicsRecorder(),
                new NoopAiControlPlaneObserver())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeExecutionRecoveryReconciler"/> class.
        /// </summary>
        /// <param name="runtimeInstanceRegistry">The runtime instance registry.</param>
        /// <param name="runtimeRunExecutionIndex">The runtime run execution index.</param>
        /// <param name="sharedRunOwnershipResolver">The shared run ownership resolver.</param>
        /// <param name="transitionService">The runtime execution recovery transition service.</param>
        /// <param name="options">The recovery reconciliation options.</param>
        /// <param name="observer">The control-plane observer.</param>
        public AiRuntimeExecutionRecoveryReconciler(
            IAiRuntimeInstanceRegistry runtimeInstanceRegistry,
            IAiRuntimeRunExecutionIndex runtimeRunExecutionIndex,
            IAiSharedRunOwnershipResolver sharedRunOwnershipResolver,
            IAiRuntimeExecutionRecoveryTransitionService transitionService,
            IOptions<AiRuntimeExecutionRecoveryReconciliationOptions> options,
            IAiControlPlaneObserver observer)
            : this(
                runtimeInstanceRegistry,
                runtimeRunExecutionIndex,
                sharedRunOwnershipResolver,
                transitionService,
                options,
                new NoopAiRuntimeRecoveryForensicsRecorder(),
                observer)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeExecutionRecoveryReconciler"/> class.
        /// </summary>
        /// <param name="runtimeInstanceRegistry">The runtime instance registry.</param>
        /// <param name="runtimeRunExecutionIndex">The runtime run execution index.</param>
        /// <param name="sharedRunOwnershipResolver">The shared run ownership resolver.</param>
        /// <param name="transitionService">The runtime execution recovery transition service.</param>
        /// <param name="options">The recovery reconciliation options.</param>
        /// <param name="forensicsRecorder">The runtime recovery forensics recorder.</param>
        public AiRuntimeExecutionRecoveryReconciler(
            IAiRuntimeInstanceRegistry runtimeInstanceRegistry,
            IAiRuntimeRunExecutionIndex runtimeRunExecutionIndex,
            IAiSharedRunOwnershipResolver sharedRunOwnershipResolver,
            IAiRuntimeExecutionRecoveryTransitionService transitionService,
            IOptions<AiRuntimeExecutionRecoveryReconciliationOptions> options,
            IAiRuntimeRecoveryForensicsRecorder forensicsRecorder)
            : this(
                runtimeInstanceRegistry,
                runtimeRunExecutionIndex,
                sharedRunOwnershipResolver,
                transitionService,
                options,
                forensicsRecorder,
                new NoopAiControlPlaneObserver())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeExecutionRecoveryReconciler"/> class.
        /// </summary>
        /// <param name="runtimeInstanceRegistry">The runtime instance registry.</param>
        /// <param name="runtimeRunExecutionIndex">The runtime run execution index.</param>
        /// <param name="sharedRunOwnershipResolver">The shared run ownership resolver.</param>
        /// <param name="transitionService">The runtime execution recovery transition service.</param>
        /// <param name="options">The recovery reconciliation options.</param>
        /// <param name="forensicsRecorder">The runtime recovery forensics recorder.</param>
        /// <param name="observer">The control-plane observer.</param>
        public AiRuntimeExecutionRecoveryReconciler(
            IAiRuntimeInstanceRegistry runtimeInstanceRegistry,
            IAiRuntimeRunExecutionIndex runtimeRunExecutionIndex,
            IAiSharedRunOwnershipResolver sharedRunOwnershipResolver,
            IAiRuntimeExecutionRecoveryTransitionService transitionService,
            IOptions<AiRuntimeExecutionRecoveryReconciliationOptions> options,
            IAiRuntimeRecoveryForensicsRecorder forensicsRecorder,
            IAiControlPlaneObserver observer)
        {
            ArgumentNullException.ThrowIfNull(runtimeInstanceRegistry);
            ArgumentNullException.ThrowIfNull(runtimeRunExecutionIndex);
            ArgumentNullException.ThrowIfNull(sharedRunOwnershipResolver);
            ArgumentNullException.ThrowIfNull(transitionService);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(forensicsRecorder);
            ArgumentNullException.ThrowIfNull(observer);

            this.runtimeInstanceRegistry = runtimeInstanceRegistry;
            this.runtimeRunExecutionIndex = runtimeRunExecutionIndex;
            this.sharedRunOwnershipResolver = sharedRunOwnershipResolver;
            this.transitionService = transitionService;
            this.forensicsRecorder = forensicsRecorder;
            this.observer = observer;
            this.options = options.Value;
        }

        /// <inheritdoc />
        public async Task<AiRuntimeExecutionRecoveryReconciliationResult> ReconcileAsync(
            CancellationToken cancellationToken = default)
        {
            if (!this.options.Enabled)
            {
                return new AiRuntimeExecutionRecoveryReconciliationResult();
            }

            await this.RecordRecoveryReconciliationEventAsync(
                    AiControlPlaneEventType.OperationStarted,
                    null,
                    null,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                var runtimeInstances = await this.runtimeInstanceRegistry
                    .ListAsync(includeStopped: true, cancellationToken)
                    .ConfigureAwait(false);

                var knownRuntimeInstanceIds =
                    runtimeInstances
                        .Select(runtimeInstance => runtimeInstance.RuntimeInstanceId)
                        .Where(runtimeInstanceId => !string.IsNullOrWhiteSpace(runtimeInstanceId))
                        .ToHashSet(StringComparer.Ordinal);

                var scannedRuntimeInstanceCount = 0;
                var ignoredRuntimeInstanceCount = 0;
                var discoveredUnfinishedRunCount = 0;
                var recoveredRunCount = 0;
                var decisions = new List<AiRuntimeExecutionRecoveryDecision>();

                foreach (var runtimeInstance in runtimeInstances)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!this.ShouldInspectRuntimeInstance(runtimeInstance.Status))
                    {
                        ignoredRuntimeInstanceCount++;

                        decisions.Add(new AiRuntimeExecutionRecoveryDecision
                        {
                            RuntimeInstanceId = runtimeInstance.RuntimeInstanceId,
                            TenantId = runtimeInstance.TenantId,
                            TenantGroupId = runtimeInstance.TenantGroupId,
                            Action = "ignore-runtime-instance",
                            Reason = RuntimeStatusNotIncludedReason,
                            Changed = false
                        });

                        continue;
                    }

                    scannedRuntimeInstanceCount++;

                    var unfinishedRuns = await this.runtimeRunExecutionIndex
                        .ListUnfinishedByRuntimeInstanceAsync(
                            runtimeInstance.RuntimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (unfinishedRuns.Count == 0)
                    {
                        decisions.Add(new AiRuntimeExecutionRecoveryDecision
                        {
                            RuntimeInstanceId = runtimeInstance.RuntimeInstanceId,
                            TenantId = runtimeInstance.TenantId,
                            TenantGroupId = runtimeInstance.TenantGroupId,
                            Action = "none",
                            Reason = NoUnfinishedRuntimeRunsReason,
                            Changed = false
                        });

                        continue;
                    }

                    foreach (var unfinishedRun in unfinishedRuns)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        discoveredUnfinishedRunCount++;

                        var changed =
                            await this.ProcessRecoveryCandidateAsync(
                                    runtimeInstance.RuntimeInstanceId,
                                    unfinishedRun,
                                    runtimeInstance.TenantId,
                                    runtimeInstance.TenantGroupId,
                                    decisions,
                                    cancellationToken)
                                .ConfigureAwait(false);

                        if (changed)
                        {
                            recoveredRunCount++;
                        }
                    }
                }

                var orphanedUnfinishedRuns = await this.runtimeRunExecutionIndex
                    .ListUnfinishedAsync(cancellationToken)
                    .ConfigureAwait(false);

                foreach (var orphanedRun in orphanedUnfinishedRuns)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(orphanedRun.RuntimeInstanceId))
                    {
                        continue;
                    }

                    if (knownRuntimeInstanceIds.Contains(orphanedRun.RuntimeInstanceId))
                    {
                        continue;
                    }

                    discoveredUnfinishedRunCount++;

                    var changed =
                        await this.ProcessRecoveryCandidateAsync(
                                orphanedRun.RuntimeInstanceId,
                                orphanedRun,
                                orphanedRun.ExecutionContextSnapshot?.TenantId,
                                orphanedRun.ExecutionContextSnapshot?.TenantGroupId,
                                decisions,
                                cancellationToken)
                            .ConfigureAwait(false);

                    if (changed)
                    {
                        recoveredRunCount++;
                    }

                    decisions.Add(new AiRuntimeExecutionRecoveryDecision
                    {
                        RuntimeInstanceId = orphanedRun.RuntimeInstanceId,
                        LocalRunId = orphanedRun.RunId,
                        ExecutionId = orphanedRun.ExecutionId,
                        TenantId = orphanedRun.ExecutionContextSnapshot?.TenantId,
                        TenantGroupId = orphanedRun.ExecutionContextSnapshot?.TenantGroupId,
                        Action = "orphaned-runtime-instance-detected",
                        Reason = OrphanedRuntimeInstanceReason,
                        Changed = false
                    });
                }

                var result = new AiRuntimeExecutionRecoveryReconciliationResult
                {
                    ScannedRuntimeInstanceCount = scannedRuntimeInstanceCount,
                    IgnoredRuntimeInstanceCount = ignoredRuntimeInstanceCount,
                    DiscoveredUnfinishedRunCount = discoveredUnfinishedRunCount,
                    RecoveredRunCount = recoveredRunCount,
                    Decisions = decisions
                };

                await this.RecordRecoveryReconciliationEventAsync(
                        AiControlPlaneEventType.OperationCompleted,
                        AiControlPlaneOperationOutcome.Succeeded,
                        null,
                        new Dictionary<string, object?>
                        {
                            ["scannedRuntimeInstanceCount"] = result.ScannedRuntimeInstanceCount,
                            ["ignoredRuntimeInstanceCount"] = result.IgnoredRuntimeInstanceCount,
                            ["discoveredUnfinishedRunCount"] = result.DiscoveredUnfinishedRunCount,
                            ["recoveredRunCount"] = result.RecoveredRunCount,
                            ["decisionCount"] = result.Decisions.Count
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                await this.RecordRecoveryReconciliationEventAsync(
                        AiControlPlaneEventType.OperationFailed,
                        AiControlPlaneOperationOutcome.Failed,
                        exception.GetType().Name,
                        new Dictionary<string, object?>
                        {
                            ["exception.type"] = exception.GetType().FullName,
                            ["exception.message"] = exception.Message
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                throw;
            }
        }

        /// <summary>
        /// Processes a single runtime execution recovery candidate.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier associated with the candidate.</param>
        /// <param name="unfinishedRun">The unfinished runtime run index entry.</param>
        /// <param name="fallbackTenantId">The fallback tenant identifier.</param>
        /// <param name="fallbackTenantGroupId">The fallback tenant group identifier.</param>
        /// <param name="decisions">The recovery decisions collection.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns><c>true</c> when the transition changed state; otherwise, <c>false</c>.</returns>
        private async Task<bool> ProcessRecoveryCandidateAsync(
            string runtimeInstanceId,
            AiRuntimeRunExecutionIndexEntry unfinishedRun,
            string? fallbackTenantId,
            string? fallbackTenantGroupId,
            ICollection<AiRuntimeExecutionRecoveryDecision> decisions,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentNullException.ThrowIfNull(unfinishedRun);
            ArgumentNullException.ThrowIfNull(decisions);

            var tenantId =
                unfinishedRun.ExecutionContextSnapshot?.TenantId ??
                fallbackTenantId;

            var tenantGroupId =
                unfinishedRun.ExecutionContextSnapshot?.TenantGroupId ??
                fallbackTenantGroupId;

            var ownership = await this.sharedRunOwnershipResolver
                .ResolveAsync(
                    new AiSharedRunOwnershipResolutionRequest
                    {
                        RuntimeInstanceId = runtimeInstanceId,
                        LocalRunId = unfinishedRun.RunId,
                        ExecutionId = unfinishedRun.ExecutionId,
                        TenantId = tenantId,
                        TenantGroupId = tenantGroupId
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            await this.RecordRecoveryCandidateDetectedForensicsAsync(
                    runtimeInstanceId,
                    unfinishedRun.RunId,
                    unfinishedRun.ExecutionId,
                    ownership.SharedRunId,
                    tenantId,
                    tenantGroupId,
                    ownership.CanRecover,
                    ownership.Reason,
                    cancellationToken)
                .ConfigureAwait(false);

            var dryRun = this.options.DryRun ||
                !this.options.RequeueUnfinishedRuns;

            var transitionReason =
                CreateTransitionReason(
                    unfinishedRun,
                    dryRun);

            var transition = await this.transitionService
                .ApplyAsync(
                    new AiRuntimeExecutionRecoveryTransitionRequest
                    {
                        Ownership = ownership,
                        DryRun = dryRun,
                        Reason = transitionReason
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            decisions.Add(new AiRuntimeExecutionRecoveryDecision
            {
                RuntimeInstanceId = runtimeInstanceId,
                LocalRunId = unfinishedRun.RunId,
                ExecutionId = unfinishedRun.ExecutionId,
                SharedRunId = ownership.SharedRunId,
                TenantId = tenantId,
                TenantGroupId = tenantGroupId,
                Action = transition.Action,
                Reason = transition.Reason,
                Changed = transition.Changed
            });

            return transition.Changed;
        }

        /// <summary>
        /// Records a runtime execution recovery reconciliation control-plane event.
        /// </summary>
        /// <param name="eventType">The control-plane event type.</param>
        /// <param name="outcome">The optional control-plane operation outcome.</param>
        /// <param name="failureReason">The optional failure reason.</param>
        /// <param name="properties">The optional event properties.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>A task that completes when the control-plane event has been recorded.</returns>
        private async Task RecordRecoveryReconciliationEventAsync(
            AiControlPlaneEventType eventType,
            AiControlPlaneOperationOutcome? outcome,
            string? failureReason,
            IReadOnlyDictionary<string, object?>? properties,
            CancellationToken cancellationToken)
        {
            try
            {
                await this.observer.RecordAsync(
                        new AiControlPlaneEvent
                        {
                            EventType = eventType,
                            Area = AiControlPlaneArea.Recovery,
                            Operation = RecoveryReconciliationOperation,
                            Outcome = outcome,
                            FailureReason = failureReason,
                            Correlation = new AiRuntimeExecutionCorrelationContext
                            {
                                CorrelationId = Guid.NewGuid().ToString("N")
                            },
                            Properties = properties
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Control-plane observability must not break recovery reconciliation.
            }
        }

        /// <summary>
        /// Creates the transition reason used by the recovery transition service.
        /// </summary>
        /// <param name="unfinishedRun">The unfinished runtime run index entry.</param>
        /// <param name="dryRun">A value indicating whether the transition is a dry run.</param>
        /// <returns>The transition reason.</returns>
        private static string CreateTransitionReason(
            AiRuntimeRunExecutionIndexEntry unfinishedRun,
            bool dryRun)
        {
            ArgumentNullException.ThrowIfNull(unfinishedRun);

            if (IsLocalQueuedRecoveryCandidate(unfinishedRun))
            {
                return dryRun
                    ? "dry-run-runtime-local-queued-recovery"
                    : "runtime-local-queued-recovery-requeue";
            }

            return dryRun
                ? "dry-run-runtime-execution-recovery"
                : "runtime-execution-recovery-requeue";
        }

        /// <summary>
        /// Determines whether the unfinished run represents local queued work that has not yet started an execution.
        /// </summary>
        /// <param name="unfinishedRun">The unfinished runtime run index entry.</param>
        /// <returns>
        /// <c>true</c> when the run is local queued work without an execution identifier; otherwise, <c>false</c>.
        /// </returns>
        private static bool IsLocalQueuedRecoveryCandidate(
            AiRuntimeRunExecutionIndexEntry unfinishedRun)
        {
            ArgumentNullException.ThrowIfNull(unfinishedRun);

            return string.Equals(
                    unfinishedRun.Status,
                    "queued",
                    StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(unfinishedRun.ExecutionId);
        }

        /// <summary>
        /// Records a recovery candidate detected event.
        /// </summary>
        /// <param name="runtimeInstanceId">The failed or unavailable runtime instance identifier.</param>
        /// <param name="localRunId">The local runtime run identifier.</param>
        /// <param name="executionId">The durable execution identifier.</param>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <param name="tenantId">The tenant identifier.</param>
        /// <param name="tenantGroupId">The tenant group identifier.</param>
        /// <param name="canRecover">A value indicating whether the candidate can be recovered.</param>
        /// <param name="reason">The ownership resolution reason.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>A task that completes when the forensics event has been recorded.</returns>
        private async Task RecordRecoveryCandidateDetectedForensicsAsync(
            string runtimeInstanceId,
            string localRunId,
            string? executionId,
            string? sharedRunId,
            string? tenantId,
            string? tenantGroupId,
            bool canRecover,
            string? reason,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(executionId) ||
                string.IsNullOrWhiteSpace(sharedRunId))
            {
                return;
            }

            var forensicsId = CreateForensicsId(
                executionId,
                sharedRunId,
                localRunId);

            await this.forensicsRecorder
                .RecordEventAsync(
                    new AiRuntimeRecoveryForensicsEvent
                    {
                        EventId = string.Join(
                            ":",
                            forensicsId,
                            AiRuntimeRecoveryForensicsEventType.ExecutionRecoveryCandidateDetected),
                        ForensicsId = forensicsId,
                        TimestampUtc = DateTimeOffset.UtcNow,
                        EventType = AiRuntimeRecoveryForensicsEventType.ExecutionRecoveryCandidateDetected,
                        Outcome = canRecover ? "recoverable" : "not-recoverable",
                        Reason = reason,
                        ExecutionId = executionId,
                        SharedRunId = sharedRunId,
                        LocalRunId = localRunId,
                        RuntimeInstanceId = runtimeInstanceId,
                        Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["tenant.id"] = tenantId ?? string.Empty,
                            ["tenant.group.id"] = tenantGroupId ?? string.Empty,
                            ["candidate.canRecover"] = canRecover.ToString()
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Creates a deterministic forensics identifier.
        /// </summary>
        /// <param name="executionId">The durable execution identifier.</param>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <param name="localRunId">The local runtime run identifier.</param>
        /// <returns>The forensics identifier.</returns>
        private static string CreateForensicsId(
            string executionId,
            string sharedRunId,
            string localRunId)
        {
            return string.Join(
                ":",
                "runtime-recovery",
                executionId,
                sharedRunId,
                localRunId);
        }

        /// <summary>
        /// Determines whether a runtime instance should be inspected by recovery.
        /// </summary>
        /// <param name="status">The runtime instance status.</param>
        /// <returns>
        /// <c>true</c> when the runtime instance should be inspected; otherwise, <c>false</c>.
        /// </returns>
        private bool ShouldInspectRuntimeInstance(
            AiRuntimeInstanceStatus status)
        {
            return status switch
            {
                AiRuntimeInstanceStatus.Unhealthy => this.options.IncludeUnhealthyRuntimeInstances,
                AiRuntimeInstanceStatus.Stopped => this.options.IncludeStoppedRuntimeInstances,
                AiRuntimeInstanceStatus.Draining => this.options.IncludeDrainingRuntimeInstances,
                _ => false
            };
        }
    }
}