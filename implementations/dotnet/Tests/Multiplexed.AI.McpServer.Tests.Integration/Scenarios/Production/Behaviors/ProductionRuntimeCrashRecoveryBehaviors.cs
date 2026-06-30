using System;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Health;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Behaviors
{
    /// <summary>
    /// Provides active behaviors used by real runtime crash recovery production scenarios.
    /// </summary>
    public static class ProductionRuntimeCrashRecoveryBehaviors
    {
        /// <summary>
        /// Marks a runtime instance unhealthy and runs recovery reconciliation until the in-flight execution is requeued for recovery.
        /// </summary>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="healthReconciler">The runtime instance health reconciler.</param>
        /// <param name="recoveryReconciler">The runtime execution recovery reconciler.</param>
        /// <param name="runExecutionIndex">The runtime run execution index.</param>
        /// <param name="failedRuntimeInstanceId">The failed runtime instance identifier.</param>
        /// <param name="failedLocalRunId">The failed local run identifier.</param>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="timeout">The wait timeout.</param>
        /// <returns>A task that completes when the execution is requeued for recovery.</returns>
        public static async Task MarkRuntimeUnhealthyAndRecoverUntilRequeuedAsync(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeInstanceHealthReconciler healthReconciler,
            IAiRuntimeExecutionRecoveryReconciler recoveryReconciler,
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            string failedRuntimeInstanceId,
            string failedLocalRunId,
            string executionId,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(healthReconciler);
            ArgumentNullException.ThrowIfNull(recoveryReconciler);
            ArgumentNullException.ThrowIfNull(runExecutionIndex);
            ArgumentException.ThrowIfNullOrWhiteSpace(failedRuntimeInstanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(failedLocalRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            object? lastRecoveryResult =
                null;

            AiRuntimeRunExecutionIndexEntry? lastEntry =
                null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                await registry
                    .MarkUnhealthyAsync(failedRuntimeInstanceId)
                    .ConfigureAwait(false);

                await healthReconciler
                    .ReconcileAsync()
                    .ConfigureAwait(false);

                await registry
                    .MarkUnhealthyAsync(failedRuntimeInstanceId)
                    .ConfigureAwait(false);

                lastRecoveryResult =
                    await recoveryReconciler
                        .ReconcileAsync()
                        .ConfigureAwait(false);

                lastEntry =
                    await runExecutionIndex
                        .GetAsync(failedLocalRunId)
                        .ConfigureAwait(false);

                if (IsRequeuedForRecovery(lastEntry, executionId))
                {
                    return;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                "Real runtime process-kill recovery did not requeue the in-flight execution within the timeout. " +
                $"FailedRuntimeInstanceId='{failedRuntimeInstanceId}', FailedLocalRunId='{failedLocalRunId}', ExecutionId='{executionId}', " +
                $"LastIndexStatus='{lastEntry?.Status}', LastIndexExecutionId='{lastEntry?.ExecutionId}', LastIndexRuntimeInstanceId='{lastEntry?.RuntimeInstanceId}', LastRecoveryResult='{lastRecoveryResult}'.");

            throw new InvalidOperationException(
                "Unreachable assertion path.");
        }

        /// <summary>
        /// Determines whether the runtime run execution index entry is requeued for recovery.
        /// </summary>
        /// <param name="entry">The runtime run execution index entry.</param>
        /// <param name="executionId">The expected execution identifier.</param>
        /// <returns><c>true</c> when the entry represents the expected execution and is requeued for recovery; otherwise, <c>false</c>.</returns>
        private static bool IsRequeuedForRecovery(
            AiRuntimeRunExecutionIndexEntry? entry,
            string executionId)
        {
            if (!string.Equals(entry?.ExecutionId, executionId, StringComparison.Ordinal))
            {
                return false;
            }

            return string.Equals(entry.Status, "requeued-for-recovery", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(entry.Status, "requeued", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(entry.Status, "queued", StringComparison.OrdinalIgnoreCase);
        }
    }
}