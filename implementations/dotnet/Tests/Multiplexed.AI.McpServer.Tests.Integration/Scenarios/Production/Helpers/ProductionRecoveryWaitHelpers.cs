using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Health;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.State;
using Multiplexed.AI.Stores;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Helpers
{
    /// <summary>
    /// Provides reusable wait helpers for production runtime recovery scenarios.
    /// </summary>
    public static class ProductionRecoveryWaitHelpers
    {
        /// <summary>
        /// Repeatedly marks a runtime unhealthy, reconciles routing health, and runs execution recovery
        /// until one in-flight run is recovered.
        /// </summary>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="healthReconciler">The runtime instance health reconciler.</param>
        /// <param name="recoveryReconciler">The runtime execution recovery reconciler.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier to mark unhealthy.</param>
        /// <param name="timeout">The maximum wait duration.</param>
        /// <returns>The recovery reconciliation result.</returns>
        public static async Task<AiRuntimeExecutionRecoveryReconciliationResult> MarkUnhealthyAndReconcileUntilRecoveredAsync(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeInstanceHealthReconciler healthReconciler,
            IAiRuntimeExecutionRecoveryReconciler recoveryReconciler,
            string runtimeInstanceId,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(healthReconciler);
            ArgumentNullException.ThrowIfNull(recoveryReconciler);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            AiRuntimeExecutionRecoveryReconciliationResult? lastResult = null;
            AiRuntimeInstanceSnapshot? lastSnapshot = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                await registry
                    .MarkUnhealthyAsync(runtimeInstanceId)
                    .ConfigureAwait(false);

                await healthReconciler
                    .ReconcileAsync()
                    .ConfigureAwait(false);

                lastSnapshot =
                    await registry
                        .GetAsync(runtimeInstanceId)
                        .ConfigureAwait(false);

                lastResult =
                    await recoveryReconciler
                        .ReconcileAsync()
                        .ConfigureAwait(false);

                if (lastResult.RecoveredRunCount == 1)
                {
                    return lastResult;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                $"Runtime execution recovery did not recover the in-flight run within '{timeout}'. " +
                $"RuntimeInstanceId='{runtimeInstanceId}', LastRuntimeStatus='{lastSnapshot?.Status}', " +
                $"LastRecoveredRunCount='{lastResult?.RecoveredRunCount}'.");

            throw new InvalidOperationException(
                "Unreachable assertion path.");
        }

        /// <summary>
        /// Waits until the shared run is assigned to a runtime different from the failed runtime.
        /// </summary>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="healthReconciler">The runtime instance health reconciler.</param>
        /// <param name="sharedRunStore">The shared run store.</param>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <param name="failedRuntimeInstanceId">The failed runtime instance identifier.</param>
        /// <param name="timeout">The maximum wait duration.</param>
        /// <returns>The redispatched shared run record.</returns>
        public static async Task<AiSharedRunRecord> WaitForSharedRunAssignedAwayFromRuntimeAsync(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeInstanceHealthReconciler healthReconciler,
            IAiSharedRunStore sharedRunStore,
            string sharedRunId,
            string failedRuntimeInstanceId,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(healthReconciler);
            ArgumentNullException.ThrowIfNull(sharedRunStore);
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(failedRuntimeInstanceId);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            AiSharedRunRecord? lastRecord = null;
            AiRuntimeInstanceSnapshot? lastFailedRuntimeSnapshot = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                await registry
                    .MarkUnhealthyAsync(failedRuntimeInstanceId)
                    .ConfigureAwait(false);

                await healthReconciler
                    .ReconcileAsync()
                    .ConfigureAwait(false);

                lastFailedRuntimeSnapshot =
                    await registry
                        .GetAsync(failedRuntimeInstanceId)
                        .ConfigureAwait(false);

                lastRecord =
                    await sharedRunStore
                        .GetAsync(sharedRunId)
                        .ConfigureAwait(false);

                if (lastRecord is not null &&
                    !string.IsNullOrWhiteSpace(lastRecord.AssignedRuntimeInstanceId) &&
                    !string.Equals(
                        lastRecord.AssignedRuntimeInstanceId,
                        failedRuntimeInstanceId,
                        StringComparison.Ordinal))
                {
                    return lastRecord;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                $"Shared run was not redispatched away from failed runtime within '{timeout}'. " +
                $"SharedRunId='{sharedRunId}', FailedRuntimeInstanceId='{failedRuntimeInstanceId}', " +
                $"LastFailedRuntimeStatus='{lastFailedRuntimeSnapshot?.Status}', " +
                $"LastAssignedRuntimeInstanceId='{lastRecord?.AssignedRuntimeInstanceId}', " +
                $"LastLocalRunId='{lastRecord?.LocalRunId}', LastExecutionId='{lastRecord?.ExecutionId}'.");

            throw new InvalidOperationException(
                "Unreachable assertion path.");
        }

        /// <summary>
        /// Waits until the runtime run execution index contains an entry matching the expected execution id.
        /// </summary>
        /// <param name="runExecutionIndex">The runtime run execution index.</param>
        /// <param name="localRunId">The local runtime run identifier.</param>
        /// <param name="executionId">The expected durable execution identifier.</param>
        /// <param name="timeout">The maximum wait duration.</param>
        /// <returns>The matching runtime run execution index entry.</returns>
        public static async Task<AiRuntimeRunExecutionIndexEntry> WaitForRunExecutionIndexAsync(
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            string localRunId,
            string executionId,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(runExecutionIndex);
            ArgumentException.ThrowIfNullOrWhiteSpace(localRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            AiRuntimeRunExecutionIndexEntry? lastEntry = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                lastEntry =
                    await runExecutionIndex
                        .GetAsync(localRunId)
                        .ConfigureAwait(false);

                if (lastEntry is not null &&
                    string.Equals(lastEntry.ExecutionId, executionId, StringComparison.Ordinal))
                {
                    return lastEntry;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                $"Runtime run execution index entry was not found within '{timeout}'. LocalRunId='{localRunId}', ExecutionId='{executionId}', LastEntryExecutionId='{lastEntry?.ExecutionId}', LastEntryStatus='{lastEntry?.Status}'.");

            throw new InvalidOperationException(
                "Unreachable assertion path.");
        }

        /// <summary>
        /// Waits until the runtime run execution index contains a real execution id for the local runtime run.
        /// </summary>
        /// <param name="runExecutionIndex">The runtime run execution index.</param>
        /// <param name="localRunId">The local runtime run identifier.</param>
        /// <param name="timeout">The maximum wait duration.</param>
        /// <returns>The runtime run execution index entry containing a non-empty execution id.</returns>
        public static async Task<AiRuntimeRunExecutionIndexEntry> WaitForRuntimeIndexWithExecutionIdAsync(
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            string localRunId,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(runExecutionIndex);
            ArgumentException.ThrowIfNullOrWhiteSpace(localRunId);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            AiRuntimeRunExecutionIndexEntry? lastEntry = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                lastEntry =
                    await runExecutionIndex
                        .GetAsync(localRunId)
                        .ConfigureAwait(false);

                if (lastEntry is not null &&
                    !string.IsNullOrWhiteSpace(lastEntry.ExecutionId))
                {
                    return lastEntry;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                $"Runtime run execution index did not contain an execution id within '{timeout}'. LocalRunId='{localRunId}', LastStatus='{lastEntry?.Status}', LastExecutionId='{lastEntry?.ExecutionId}'.");

            throw new InvalidOperationException(
                "Unreachable assertion path.");
        }

        /// <summary>
        /// Waits until the durable DAG record exists and carries a non-empty ContextKey.
        /// </summary>
        /// <param name="dagStore">The durable DAG execution store.</param>
        /// <param name="executionId">The durable execution identifier.</param>
        /// <param name="timeout">The maximum wait duration.</param>
        /// <returns>The durable execution record containing a non-empty ContextKey.</returns>
        public static async Task<AiExecutionRecord> WaitForDagRecordWithContextKeyAsync(
            IAiDagExecutionStore dagStore,
            string executionId,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(dagStore);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            AiExecutionRecord? lastRecord = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                lastRecord =
                    await dagStore
                        .GetRecordAsync(executionId)
                        .ConfigureAwait(false);

                if (lastRecord is not null &&
                    !string.IsNullOrWhiteSpace(lastRecord.ContextKey))
                {
                    return lastRecord;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                $"Durable DAG execution record did not contain ContextKey within '{timeout}'. ExecutionId='{executionId}', RecordFound='{lastRecord is not null}', LastContextKey='{lastRecord?.ContextKey}', LastStatus='{lastRecord?.Status}'.");

            throw new InvalidOperationException(
                "Unreachable assertion path.");
        }
    }
}