using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery
{
    /// <summary>
    /// Default runtime execution recovery reconciler.
    /// </summary>
    /// <remarks>
    /// This reconciler owns execution recovery only.
    ///
    /// Current implementation is discovery-only / dry-run safe:
    /// it scans unavailable runtime instances and reports unfinished runtime runs,
    /// but does not requeue, fail, cancel, dead-letter, restart, or kill anything.
    ///
    /// Runtime health detection is owned by the runtime instance health reconciler.
    /// Runtime lifecycle is owned by providers and host managers.
    /// </remarks>
    public sealed class AiRuntimeExecutionRecoveryReconciler : IAiRuntimeExecutionRecoveryReconciler
    {
        private readonly IAiRuntimeInstanceRegistry runtimeInstanceRegistry;
        private readonly IAiRuntimeRunExecutionIndex runtimeRunExecutionIndex;
        private readonly AiRuntimeExecutionRecoveryReconciliationOptions options;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeExecutionRecoveryReconciler"/> class.
        /// </summary>
        /// <param name="runtimeInstanceRegistry">The runtime instance registry.</param>
        /// <param name="runtimeRunExecutionIndex">The runtime run execution index.</param>
        /// <param name="options">The recovery reconciliation options.</param>
        public AiRuntimeExecutionRecoveryReconciler(
            IAiRuntimeInstanceRegistry runtimeInstanceRegistry,
            IAiRuntimeRunExecutionIndex runtimeRunExecutionIndex,
            IOptions<AiRuntimeExecutionRecoveryReconciliationOptions> options)
        {
            ArgumentNullException.ThrowIfNull(runtimeInstanceRegistry);
            ArgumentNullException.ThrowIfNull(runtimeRunExecutionIndex);
            ArgumentNullException.ThrowIfNull(options);

            this.runtimeInstanceRegistry = runtimeInstanceRegistry;
            this.runtimeRunExecutionIndex = runtimeRunExecutionIndex;
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

                    decisions.Add(new AiRuntimeExecutionRecoveryDecision
                    {
                        RuntimeInstanceId = runtimeInstance.RuntimeInstanceId,
                        LocalRunId = unfinishedRun.RunId,
                        ExecutionId = unfinishedRun.ExecutionId,
                        TenantId = unfinishedRun.ExecutionContextSnapshot?.TenantId ?? runtimeInstance.TenantId,
                        TenantGroupId = unfinishedRun.ExecutionContextSnapshot?.TenantGroupId ?? runtimeInstance.TenantGroupId,
                        Action = options.RequeueUnfinishedRuns && !options.DryRun
                            ? "requeue-not-implemented"
                            : "report-unfinished-run",
                        Reason = options.RequeueUnfinishedRuns && !options.DryRun
                            ? "requeue-transition-not-implemented"
                            : "dry-run-discovered-unfinished-run",
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
    }
}