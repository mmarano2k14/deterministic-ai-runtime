using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership;

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
    /// It does not directly mutate shared queue state, fail, cancel, dead-letter,
    /// restart, or kill anything.
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
        {
            ArgumentNullException.ThrowIfNull(runtimeInstanceRegistry);
            ArgumentNullException.ThrowIfNull(runtimeRunExecutionIndex);
            ArgumentNullException.ThrowIfNull(sharedRunOwnershipResolver);
            ArgumentNullException.ThrowIfNull(transitionService);
            ArgumentNullException.ThrowIfNull(options);

            this.runtimeInstanceRegistry = runtimeInstanceRegistry;
            this.runtimeRunExecutionIndex = runtimeRunExecutionIndex;
            this.sharedRunOwnershipResolver = sharedRunOwnershipResolver;
            this.transitionService = transitionService;
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