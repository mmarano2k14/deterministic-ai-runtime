using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Health;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Health
{
    /// <summary>
    /// Default runtime instance health reconciler.
    /// </summary>
    /// <remarks>
    /// This service protects routing safety by marking stale runtime instances as unhealthy.
    /// It does not perform execution recovery, run requeue, host restart, process kill,
    /// or dead-letter queue transitions.
    /// </remarks>
    public sealed class AiRuntimeInstanceHealthReconciler : IAiRuntimeInstanceHealthReconciler
    {
        private readonly IAiRuntimeInstanceRegistry registry;
        private readonly AiRuntimeInstanceHealthReconciliationOptions options;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeInstanceHealthReconciler"/> class.
        /// </summary>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="options">The health reconciliation options.</param>
        public AiRuntimeInstanceHealthReconciler(
            IAiRuntimeInstanceRegistry registry,
            IOptions<AiRuntimeInstanceHealthReconciliationOptions> options)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(options);

            this.registry = registry;
            this.options = options.Value;
        }

        /// <inheritdoc />
        public async Task<AiRuntimeInstanceHealthReconciliationResult> ReconcileAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!options.Enabled)
            {
                return new AiRuntimeInstanceHealthReconciliationResult();
            }

            var now = DateTimeOffset.UtcNow;
            var snapshots = await registry
                .ListAsync(includeStopped: true, cancellationToken)
                .ConfigureAwait(false);

            var decisions = new List<AiRuntimeInstanceHealthDecision>();
            var markedUnhealthyCount = 0;
            var ignoredCount = 0;

            foreach (var snapshot in snapshots)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (ShouldIgnore(snapshot))
                {
                    ignoredCount++;

                    decisions.Add(CreateDecision(
                        snapshot,
                        snapshot.Status,
                        "ignored-runtime-status",
                        now,
                        changed: false));

                    continue;
                }

                if (!ShouldEvaluate(snapshot))
                {
                    ignoredCount++;

                    decisions.Add(CreateDecision(
                        snapshot,
                        snapshot.Status,
                        "runtime-status-not-included",
                        now,
                        changed: false));

                    continue;
                }

                if (!IsHeartbeatStale(snapshot, now))
                {
                    decisions.Add(CreateDecision(
                        snapshot,
                        snapshot.Status,
                        "heartbeat-fresh",
                        now,
                        changed: false));

                    continue;
                }

                if (!options.MarkStaleRuntimeUnhealthy)
                {
                    decisions.Add(CreateDecision(
                        snapshot,
                        AiRuntimeInstanceStatus.Unhealthy,
                        "heartbeat-stale-dry-transition-disabled",
                        now,
                        changed: false));

                    continue;
                }

                if (options.DryRun)
                {
                    decisions.Add(CreateDecision(
                        snapshot,
                        AiRuntimeInstanceStatus.Unhealthy,
                        "heartbeat-stale-dry-run",
                        now,
                        changed: false));

                    continue;
                }

                var updated = await registry
                    .MarkUnhealthyAsync(snapshot.RuntimeInstanceId, cancellationToken)
                    .ConfigureAwait(false);

                if (updated is null)
                {
                    decisions.Add(CreateDecision(
                        snapshot,
                        snapshot.Status,
                        "runtime-missing-during-mark-unhealthy",
                        now,
                        changed: false));

                    continue;
                }

                markedUnhealthyCount++;

                decisions.Add(CreateDecision(
                    snapshot,
                    updated.Status,
                    "heartbeat-stale",
                    now,
                    changed: true));
            }

            return new AiRuntimeInstanceHealthReconciliationResult
            {
                ScannedCount = snapshots.Count,
                MarkedUnhealthyCount = markedUnhealthyCount,
                IgnoredCount = ignoredCount,
                Decisions = decisions
            };
        }

        /// <summary>
        /// Determines whether a runtime instance should be ignored by the health reconciler.
        /// </summary>
        /// <param name="snapshot">The runtime instance snapshot.</param>
        /// <returns><c>true</c> when the runtime instance should be ignored; otherwise, <c>false</c>.</returns>
        private bool ShouldIgnore(
            AiRuntimeInstanceSnapshot snapshot)
        {
            if (snapshot.Status == AiRuntimeInstanceStatus.Stopped &&
                options.IgnoreStoppedRuntimeInstances)
            {
                return true;
            }

            if (snapshot.Status == AiRuntimeInstanceStatus.Paused &&
                options.IgnorePausedRuntimeInstances)
            {
                return true;
            }

            if (snapshot.Status == AiRuntimeInstanceStatus.Draining &&
                options.IgnoreDrainingRuntimeInstances)
            {
                return true;
            }

            return snapshot.Status == AiRuntimeInstanceStatus.Unhealthy;
        }

        /// <summary>
        /// Determines whether a runtime instance status is included in health reconciliation.
        /// </summary>
        /// <param name="snapshot">The runtime instance snapshot.</param>
        /// <returns><c>true</c> when the runtime instance should be evaluated; otherwise, <c>false</c>.</returns>
        private bool ShouldEvaluate(
            AiRuntimeInstanceSnapshot snapshot)
        {
            return snapshot.Status switch
            {
                AiRuntimeInstanceStatus.Ready => options.IncludeReadyRuntimeInstances,
                AiRuntimeInstanceStatus.Busy => options.IncludeBusyRuntimeInstances,
                _ => false
            };
        }

        /// <summary>
        /// Determines whether the runtime instance heartbeat is stale.
        /// </summary>
        /// <param name="snapshot">The runtime instance snapshot.</param>
        /// <param name="now">The current timestamp.</param>
        /// <returns><c>true</c> when the heartbeat is stale; otherwise, <c>false</c>.</returns>
        private bool IsHeartbeatStale(
            AiRuntimeInstanceSnapshot snapshot,
            DateTimeOffset now)
        {
            return now - snapshot.LastHeartbeatAtUtc >= options.StaleHeartbeatThreshold;
        }

        /// <summary>
        /// Creates a health reconciliation decision.
        /// </summary>
        /// <param name="snapshot">The runtime instance snapshot.</param>
        /// <param name="newStatus">The new runtime instance status.</param>
        /// <param name="reason">The decision reason.</param>
        /// <param name="now">The current timestamp.</param>
        /// <param name="changed">A value indicating whether registry state changed.</param>
        /// <returns>The health reconciliation decision.</returns>
        private static AiRuntimeInstanceHealthDecision CreateDecision(
            AiRuntimeInstanceSnapshot snapshot,
            AiRuntimeInstanceStatus newStatus,
            string reason,
            DateTimeOffset now,
            bool changed)
        {
            return new AiRuntimeInstanceHealthDecision
            {
                RuntimeInstanceId = snapshot.RuntimeInstanceId,
                PreviousStatus = snapshot.Status,
                NewStatus = newStatus,
                Reason = reason,
                LastHeartbeatAtUtc = snapshot.LastHeartbeatAtUtc,
                DecisionAtUtc = now,
                Changed = changed
            };
        }
    }
}