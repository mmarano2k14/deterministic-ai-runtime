using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery
{
    /// <summary>
    /// Default runtime execution recovery reconciler.
    /// </summary>
    /// <remarks>
    /// This reconciler owns execution recovery only.
    ///
    /// Current implementation is discovery-only / dry-run safe:
    /// it scans unavailable runtime instances, reports unfinished runtime runs,
    /// and resolves shared run ownership when available.
    ///
    /// It does not requeue, fail, cancel, dead-letter, restart, or kill anything.
    ///
    /// Runtime health detection is owned by the runtime instance health reconciler.
    /// Runtime lifecycle is owned by providers and host managers.
    /// </remarks>
    public sealed class AiRuntimeExecutionRecoveryReconciler : IAiRuntimeExecutionRecoveryReconciler
    {
        private readonly IAiRuntimeInstanceRegistry runtimeInstanceRegistry;
        private readonly IAiRuntimeRunExecutionIndex runtimeRunExecutionIndex;
        private readonly IAiSharedRunOwnershipResolver sharedRunOwnershipResolver;
        private readonly AiRuntimeExecutionRecoveryReconciliationOptions options;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeExecutionRecoveryReconciler"/> class.
        /// </summary>
        /// <param name="runtimeInstanceRegistry">The runtime instance registry.</param>
        /// <param name="runtimeRunExecutionIndex">The runtime run execution index.</param>
        /// <param name="sharedRunOwnershipResolver">The shared run ownership resolver.</param>
        /// <param name="options">The recovery reconciliation options.</param>
        public AiRuntimeExecutionRecoveryReconciler(
            IAiRuntimeInstanceRegistry runtimeInstanceRegistry,
            IAiRuntimeRunExecutionIndex runtimeRunExecutionIndex,
            IAiSharedRunOwnershipResolver sharedRunOwnershipResolver,
            IOptions<AiRuntimeExecutionRecoveryReconciliationOptions> options)
        {
            ArgumentNullException.ThrowIfNull(runtimeInstanceRegistry);
            ArgumentNullException.ThrowIfNull(runtimeRunExecutionIndex);
            ArgumentNullException.ThrowIfNull(sharedRunOwnershipResolver);
            ArgumentNullException.ThrowIfNull(options);

            this.runtimeInstanceRegistry = runtimeInstanceRegistry;
            this.runtimeRunExecutionIndex = runtimeRunExecutionIndex;
            this.sharedRunOwnershipResolver = sharedRunOwnershipResolver;
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

                    var ownership = await sharedRunOwnershipResolver
                        .ResolveAsync(
                            new AiSharedRunOwnershipResolutionRequest
                            {
                                RuntimeInstanceId = runtimeInstance.RuntimeInstanceId,
                                LocalRunId = unfinishedRun.RunId,
                                ExecutionId = unfinishedRun.ExecutionId,
                                TenantId = unfinishedRun.ExecutionContextSnapshot?.TenantId ?? runtimeInstance.TenantId,
                                TenantGroupId = unfinishedRun.ExecutionContextSnapshot?.TenantGroupId ?? runtimeInstance.TenantGroupId
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                    decisions.Add(new AiRuntimeExecutionRecoveryDecision
                    {
                        RuntimeInstanceId = runtimeInstance.RuntimeInstanceId,
                        LocalRunId = unfinishedRun.RunId,
                        ExecutionId = unfinishedRun.ExecutionId,
                        SharedRunId = ownership.SharedRunId,
                        TenantId = unfinishedRun.ExecutionContextSnapshot?.TenantId ?? runtimeInstance.TenantId,
                        TenantGroupId = unfinishedRun.ExecutionContextSnapshot?.TenantGroupId ?? runtimeInstance.TenantGroupId,
                        Action = ResolveDryRunAction(ownership),
                        Reason = ResolveDryRunReason(ownership),
                        Changed = false
                    });
                }
            }

            return new AiRuntimeExecutionRecoveryReconciliationResult
            {
                ScannedRuntimeInstanceCount = scannedRuntimeInstanceCount,
                IgnoredRuntimeInstanceCount = ignoredRuntimeInstanceCount,
                DiscoveredUnfinishedRunCount = discoveredUnfinishedRunCount,
                RecoveredRunCount = 0,
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

        /// <summary>
        /// Resolves the dry-run recovery action from shared run ownership resolution.
        /// </summary>
        /// <param name="ownership">The shared run ownership resolution result.</param>
        /// <returns>The dry-run recovery action.</returns>
        private static string ResolveDryRunAction(
            AiSharedRunOwnershipResolutionResult ownership)
        {
            if (!ownership.Resolved)
            {
                return "report-unresolved-unfinished-run";
            }

            return ownership.CanRecover
                ? "report-recoverable-unfinished-run"
                : "report-non-recoverable-unfinished-run";
        }

        /// <summary>
        /// Resolves the dry-run recovery reason from shared run ownership resolution.
        /// </summary>
        /// <param name="ownership">The shared run ownership resolution result.</param>
        /// <returns>The dry-run recovery reason.</returns>
        private static string ResolveDryRunReason(
            AiSharedRunOwnershipResolutionResult ownership)
        {
            if (!ownership.Resolved)
            {
                return "dry-run-discovered-unresolved-shared-run";
            }

            return ownership.CanRecover
                ? "dry-run-discovered-recoverable-shared-run"
                : "dry-run-discovered-non-recoverable-shared-run";
        }
    }
}