using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership;
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
    /// It does not directly mutate shared queue state, runtime execution index state,
    /// fail, cancel, dead-letter, restart, or kill anything.
    ///
    /// Runtime health detection is owned by the runtime instance health reconciler.
    /// Runtime lifecycle is owned by providers and host managers.
    /// Recovery mutation boundaries are owned by the runtime execution recovery transition service.
    /// </remarks>
    public sealed class AiRuntimeExecutionRecoveryReconciler : IAiRuntimeExecutionRecoveryReconciler
    {
        private readonly IAiRuntimeInstanceRegistry runtimeInstanceRegistry;
        private readonly IAiRuntimeRunExecutionIndex runtimeRunExecutionIndex;
        private readonly IAiSharedRunOwnershipResolver sharedRunOwnershipResolver;
        private readonly IAiRuntimeExecutionRecoveryTransitionService transitionService;
        private readonly IAiRuntimeRecoveryForensicsRecorder forensicsRecorder;
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
                new NoopAiRuntimeRecoveryForensicsRecorder())
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
        {
            ArgumentNullException.ThrowIfNull(runtimeInstanceRegistry);
            ArgumentNullException.ThrowIfNull(runtimeRunExecutionIndex);
            ArgumentNullException.ThrowIfNull(sharedRunOwnershipResolver);
            ArgumentNullException.ThrowIfNull(transitionService);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(forensicsRecorder);

            this.runtimeInstanceRegistry = runtimeInstanceRegistry;
            this.runtimeRunExecutionIndex = runtimeRunExecutionIndex;
            this.sharedRunOwnershipResolver = sharedRunOwnershipResolver;
            this.transitionService = transitionService;
            this.forensicsRecorder = forensicsRecorder;
            this.options = options.Value;
        }

        /// <inheritdoc />
        public async Task<AiRuntimeExecutionRecoveryReconciliationResult> ReconcileAsync(
            CancellationToken cancellationToken = default)
        {
            if (!options.Enabled)
            {
                return new AiRuntimeExecutionRecoveryReconciliationResult();
            }

            var runtimeInstances = await runtimeInstanceRegistry
                .ListAsync(includeStopped: true, cancellationToken)
                .ConfigureAwait(false);

            var scannedRuntimeInstanceCount = 0;
            var ignoredRuntimeInstanceCount = 0;
            var discoveredUnfinishedRunCount = 0;
            var recoveredRunCount = 0;
            var decisions = new List<AiRuntimeExecutionRecoveryDecision>();

            foreach (var runtimeInstance in runtimeInstances)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!ShouldInspectRuntimeInstance(runtimeInstance.Status))
                {
                    ignoredRuntimeInstanceCount++;

                    decisions.Add(new AiRuntimeExecutionRecoveryDecision
                    {
                        RuntimeInstanceId = runtimeInstance.RuntimeInstanceId,
                        TenantId = runtimeInstance.TenantId,
                        TenantGroupId = runtimeInstance.TenantGroupId,
                        Action = "ignore-runtime-instance",
                        Reason = "runtime-status-not-included",
                        Changed = false
                    });

                    continue;
                }

                scannedRuntimeInstanceCount++;

                var unfinishedRuns = await runtimeRunExecutionIndex
                    .ListUnfinishedByRuntimeInstanceAsync(runtimeInstance.RuntimeInstanceId, cancellationToken)
                    .ConfigureAwait(false);

                if (unfinishedRuns.Count == 0)
                {
                    decisions.Add(new AiRuntimeExecutionRecoveryDecision
                    {
                        RuntimeInstanceId = runtimeInstance.RuntimeInstanceId,
                        TenantId = runtimeInstance.TenantId,
                        TenantGroupId = runtimeInstance.TenantGroupId,
                        Action = "none",
                        Reason = "no-unfinished-runtime-runs",
                        Changed = false
                    });

                    continue;
                }

                foreach (var unfinishedRun in unfinishedRuns)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    discoveredUnfinishedRunCount++;

                    var tenantId = unfinishedRun.ExecutionContextSnapshot?.TenantId ??
                        runtimeInstance.TenantId;

                    var tenantGroupId = unfinishedRun.ExecutionContextSnapshot?.TenantGroupId ??
                        runtimeInstance.TenantGroupId;

                    var ownership = await sharedRunOwnershipResolver
                        .ResolveAsync(
                            new AiSharedRunOwnershipResolutionRequest
                            {
                                RuntimeInstanceId = runtimeInstance.RuntimeInstanceId,
                                LocalRunId = unfinishedRun.RunId,
                                ExecutionId = unfinishedRun.ExecutionId,
                                TenantId = tenantId,
                                TenantGroupId = tenantGroupId
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                    await this.RecordRecoveryCandidateDetectedForensicsAsync(
                            runtimeInstance.RuntimeInstanceId,
                            unfinishedRun.RunId,
                            unfinishedRun.ExecutionId,
                            ownership.SharedRunId,
                            tenantId,
                            tenantGroupId,
                            ownership.CanRecover,
                            ownership.Reason,
                            cancellationToken)
                        .ConfigureAwait(false);

                    var dryRun = options.DryRun ||
                        !options.RequeueUnfinishedRuns;

                    var transition = await transitionService
                        .ApplyAsync(
                            new AiRuntimeExecutionRecoveryTransitionRequest
                            {
                                Ownership = ownership,
                                DryRun = dryRun,
                                Reason = dryRun
                                    ? "dry-run-runtime-execution-recovery"
                                    : "runtime-execution-recovery-requeue"
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (transition.Changed)
                    {
                        recoveredRunCount++;
                    }

                    decisions.Add(new AiRuntimeExecutionRecoveryDecision
                    {
                        RuntimeInstanceId = runtimeInstance.RuntimeInstanceId,
                        LocalRunId = unfinishedRun.RunId,
                        ExecutionId = unfinishedRun.ExecutionId,
                        SharedRunId = ownership.SharedRunId,
                        TenantId = tenantId,
                        TenantGroupId = tenantGroupId,
                        Action = transition.Action,
                        Reason = transition.Reason,
                        Changed = transition.Changed
                    });
                }
            }

            return new AiRuntimeExecutionRecoveryReconciliationResult
            {
                ScannedRuntimeInstanceCount = scannedRuntimeInstanceCount,
                IgnoredRuntimeInstanceCount = ignoredRuntimeInstanceCount,
                DiscoveredUnfinishedRunCount = discoveredUnfinishedRunCount,
                RecoveredRunCount = recoveredRunCount,
                Decisions = decisions
            };
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
        /// <returns><c>true</c> when the runtime instance should be inspected; otherwise, <c>false</c>.</returns>
        private bool ShouldInspectRuntimeInstance(
            AiRuntimeInstanceStatus status)
        {
            return status switch
            {
                AiRuntimeInstanceStatus.Unhealthy => options.IncludeUnhealthyRuntimeInstances,
                AiRuntimeInstanceStatus.Stopped => options.IncludeStoppedRuntimeInstances,
                AiRuntimeInstanceStatus.Draining => options.IncludeDrainingRuntimeInstances,
                _ => false
            };
        }
    }
}