using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Health;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
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
        /// until at least one in-flight run is recovered.
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
            AiRuntimeInstanceSnapshot? lastSnapshotBeforeHealth = null;
            AiRuntimeInstanceSnapshot? lastSnapshotBeforeRecovery = null;
            AiRuntimeInstanceSnapshot? lastSnapshotAfterRecovery = null;
            var attempt = 0;

            while (DateTimeOffset.UtcNow < deadline)
            {
                attempt++;

                await registry
                    .MarkUnhealthyAsync(runtimeInstanceId)
                    .ConfigureAwait(false);

                lastSnapshotBeforeHealth =
                    await registry
                        .GetAsync(runtimeInstanceId)
                        .ConfigureAwait(false);

                await healthReconciler
                    .ReconcileAsync()
                    .ConfigureAwait(false);

                await registry
                    .MarkUnhealthyAsync(runtimeInstanceId)
                    .ConfigureAwait(false);

                lastSnapshotBeforeRecovery =
                    await registry
                        .GetAsync(runtimeInstanceId)
                        .ConfigureAwait(false);

                lastResult =
                    await recoveryReconciler
                        .ReconcileAsync()
                        .ConfigureAwait(false);

                lastSnapshotAfterRecovery =
                    await registry
                        .GetAsync(runtimeInstanceId)
                        .ConfigureAwait(false);

                if (lastResult.RecoveredRunCount >= 1)
                {
                    return lastResult;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(150))
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                $"Runtime execution recovery did not recover the in-flight run within '{timeout}'. " +
                $"RuntimeInstanceId='{runtimeInstanceId}', Attempts='{attempt}', " +
                $"LastRuntimeStatusBeforeHealth='{lastSnapshotBeforeHealth?.Status}', " +
                $"LastRuntimeStatusBeforeRecovery='{lastSnapshotBeforeRecovery?.Status}', " +
                $"LastRuntimeStatusAfterRecovery='{lastSnapshotAfterRecovery?.Status}', " +
                $"LastScannedRuntimeInstanceCount='{lastResult?.ScannedRuntimeInstanceCount}', " +
                $"LastIgnoredRuntimeInstanceCount='{lastResult?.IgnoredRuntimeInstanceCount}', " +
                $"LastDiscoveredUnfinishedRunCount='{lastResult?.DiscoveredUnfinishedRunCount}', " +
                $"LastRecoveredRunCount='{lastResult?.RecoveredRunCount}', " +
                $"LastDecisions='{FormatRecoveryDecisions(lastResult)}'.");

            throw new InvalidOperationException(
                "Unreachable assertion path.");
        }

        /// <summary>
        /// Formats recovery reconciliation decisions for test diagnostics.
        /// </summary>
        /// <param name="result">The recovery reconciliation result.</param>
        /// <returns>A compact diagnostic string.</returns>
        private static string FormatRecoveryDecisions(
            AiRuntimeExecutionRecoveryReconciliationResult? result)
        {
            if (result is null ||
                result.Decisions is null ||
                result.Decisions.Count == 0)
            {
                return "<none>";
            }

            return string.Join(
                " | ",
                result.Decisions
                    .Take(20)
                    .Select(
                        decision =>
                            $"Runtime='{decision.RuntimeInstanceId}', LocalRun='{decision.LocalRunId}', Execution='{decision.ExecutionId}', SharedRun='{decision.SharedRunId}', Action='{decision.Action}', Reason='{decision.Reason}', Changed='{decision.Changed}'"));
        }

        /// <summary>
        /// Waits until the shared run is assigned to a runtime different from the failed runtime.
        /// </summary>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="healthReconciler">The runtime instance health reconciler.</param>
        /// <param name="sharedRunStore">The shared run store.</param>
        /// <param name="sharedQueue">The shared queue.</param>
        /// <param name="sharedQueueDispatcher">The shared queue dispatcher.</param>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <param name="failedRuntimeInstanceId">The failed runtime instance identifier.</param>
        /// <param name="timeout">The maximum wait duration.</param>
        /// <returns>The redispatched shared run record.</returns>
        public static async Task<AiSharedRunRecord> WaitForSharedRunAssignedAwayFromRuntimeAsync(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeInstanceHealthReconciler healthReconciler,
            IAiSharedRunStore sharedRunStore,
            IAiSharedQueue sharedQueue,
            IAiSharedQueueDispatcher sharedQueueDispatcher,
            string sharedRunId,
            string failedRuntimeInstanceId,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(healthReconciler);
            ArgumentNullException.ThrowIfNull(sharedRunStore);
            ArgumentNullException.ThrowIfNull(sharedQueue);
            ArgumentNullException.ThrowIfNull(sharedQueueDispatcher);
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(failedRuntimeInstanceId);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            AiSharedRunRecord? lastRecord = null;
            AiSharedQueueItem? lastQueueItem = null;
            AiRuntimeInstanceSnapshot? lastFailedRuntimeSnapshot = null;
            IReadOnlyList<AiRuntimeInstanceSnapshot> lastRuntimeSnapshots =
                Array.Empty<AiRuntimeInstanceSnapshot>();

            AiSharedQueueDispatchResult? lastDispatchResult = null;

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

                lastRuntimeSnapshots =
                    await registry
                        .ListAsync()
                        .ConfigureAwait(false);

                lastRecord =
                    await sharedRunStore
                        .GetAsync(sharedRunId)
                        .ConfigureAwait(false);

                lastQueueItem =
                    await sharedQueue
                        .GetAsync(sharedRunId)
                        .ConfigureAwait(false);

                if (IsAssignedAwayFromFailedRuntime(lastRecord, failedRuntimeInstanceId))
                {
                    return lastRecord!;
                }

                if (lastRecord is not null)
                {
                    lastDispatchResult =
                        await sharedQueueDispatcher
                            .DispatchNextAsync(
                                new AiSharedQueueDispatchRequest
                                {
                                    RuntimeInstanceId = "test-recovery-shared-queue-dispatcher",
                                    WorkerId = "test-recovery-shared-queue-dispatcher-worker",
                                    TenantId = lastRecord.ExecutionContextSnapshot.TenantId,
                                    PipelineKey = lastRecord.PipelineKey,
                                    ClaimTtl = TimeSpan.FromSeconds(30),
                                    CorrelationId = lastRecord.CorrelationId,
                                    RequestedBy = lastRecord.RequestedBy,
                                    Source = lastRecord.Source,
                                    Reason = "test-driven-recovery-redispatch"
                                })
                            .ConfigureAwait(false);

                    lastRecord =
                        await sharedRunStore
                            .GetAsync(sharedRunId)
                            .ConfigureAwait(false);

                    lastQueueItem =
                        await sharedQueue
                            .GetAsync(sharedRunId)
                            .ConfigureAwait(false);

                    if (IsAssignedAwayFromFailedRuntime(lastRecord, failedRuntimeInstanceId))
                    {
                        return lastRecord!;
                    }

                    if (IsStuckDispatchedToFailedRuntime(
                            lastRecord,
                            lastQueueItem,
                            lastDispatchResult,
                            failedRuntimeInstanceId))
                    {
                        Assert.Fail(
                            "Recovered shared queue item is Dispatched, but the shared run is still assigned to the failed runtime. " +
                            "The dispatcher also reports NoItemAvailable, so the run is no longer claimable and waiting cannot make progress. " +
                            $"SharedRunId='{sharedRunId}', " +
                            $"FailedRuntimeInstanceId='{failedRuntimeInstanceId}', " +
                            $"LastFailedRuntimeStatus='{lastFailedRuntimeSnapshot?.Status}', " +
                            $"LastAssignedRuntimeInstanceId='{lastRecord?.AssignedRuntimeInstanceId}', " +
                            $"LastLocalRunId='{lastRecord?.LocalRunId}', " +
                            $"LastExecutionId='{lastRecord?.ExecutionId}', " +
                            $"LastSharedRunStatus='{lastRecord?.Status}', " +
                            $"LastQueueStatus='{lastQueueItem?.Status}', " +
                            $"LastQueueClaimToken='{lastQueueItem?.ClaimToken}', " +
                            $"LastQueueRecoveryMode='{ResolveMetadata(lastQueueItem?.Metadata, "recovery.mode")}', " +
                            $"LastQueueFailedRuntimeInstanceId='{ResolveMetadata(lastQueueItem?.Metadata, "recovery.failedRuntimeInstanceId")}', " +
                            $"LastDispatchSuccess='{lastDispatchResult?.Success}', " +
                            $"LastDispatchNoItemAvailable='{lastDispatchResult?.NoItemAvailable}', " +
                            $"LastDispatchFailureReason='{lastDispatchResult?.FailureReason}', " +
                            $"LastDispatchMessage='{lastDispatchResult?.Message}', " +
                            $"KnownRuntimeInstances='{FormatRuntimeSnapshots(lastRuntimeSnapshots)}'.");
                    }
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                $"Shared run was not redispatched away from failed runtime within '{timeout}'. " +
                $"SharedRunId='{sharedRunId}', FailedRuntimeInstanceId='{failedRuntimeInstanceId}', " +
                $"LastFailedRuntimeStatus='{lastFailedRuntimeSnapshot?.Status}', " +
                $"LastAssignedRuntimeInstanceId='{lastRecord?.AssignedRuntimeInstanceId}', " +
                $"LastLocalRunId='{lastRecord?.LocalRunId}', LastExecutionId='{lastRecord?.ExecutionId}', " +
                $"LastSharedRunStatus='{lastRecord?.Status}', " +
                $"LastQueueStatus='{lastQueueItem?.Status}', " +
                $"LastQueueClaimToken='{lastQueueItem?.ClaimToken}', " +
                $"LastQueueRecoveryMode='{ResolveMetadata(lastQueueItem?.Metadata, "recovery.mode")}', " +
                $"LastQueueFailedRuntimeInstanceId='{ResolveMetadata(lastQueueItem?.Metadata, "recovery.failedRuntimeInstanceId")}', " +
                $"LastDispatchSuccess='{lastDispatchResult?.Success}', " +
                $"LastDispatchNoItemAvailable='{lastDispatchResult?.NoItemAvailable}', " +
                $"LastDispatchFailureReason='{lastDispatchResult?.FailureReason}', " +
                $"LastDispatchMessage='{lastDispatchResult?.Message}', " +
                $"KnownRuntimeInstances='{FormatRuntimeSnapshots(lastRuntimeSnapshots)}'.");

            throw new InvalidOperationException(
                "Unreachable assertion path.");
        }

        /// <summary>
        /// Waits until the runtime run execution index exposes the seeded unfinished run.
        /// </summary>
        /// <param name="runtimeRunExecutionIndex">The runtime run execution index.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="localRunId">The local runtime run identifier.</param>
        /// <param name="executionId">The durable execution identifier.</param>
        /// <param name="timeout">The maximum wait duration.</param>
        /// <returns>A task that completes when the unfinished run is visible.</returns>
        public static async Task WaitForSeededUnfinishedRuntimeRunVisibleAsync(
            IAiRuntimeRunExecutionIndex runtimeRunExecutionIndex,
            string runtimeInstanceId,
            string localRunId,
            string executionId,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(runtimeRunExecutionIndex);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(localRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            IReadOnlyList<AiRuntimeRunExecutionIndexEntry>? lastEntries = null;
            AiRuntimeRunExecutionIndexEntry? lastMatchingEntry = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                lastEntries =
                    await runtimeRunExecutionIndex
                        .ListUnfinishedByRuntimeInstanceAsync(runtimeInstanceId)
                        .ConfigureAwait(false);

                lastMatchingEntry =
                    lastEntries.FirstOrDefault(
                        entry =>
                            string.Equals(entry.RunId, localRunId, StringComparison.Ordinal) &&
                            string.Equals(entry.RuntimeInstanceId, runtimeInstanceId, StringComparison.Ordinal) &&
                            string.Equals(entry.ExecutionId, executionId, StringComparison.Ordinal));

                if (lastMatchingEntry is not null)
                {
                    return;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                "Seeded runtime run execution index entry was not visible as unfinished before recovery. " +
                $"RuntimeInstanceId='{runtimeInstanceId}', " +
                $"LocalRunId='{localRunId}', " +
                $"ExecutionId='{executionId}', " +
                $"LastUnfinishedCount='{lastEntries?.Count}', " +
                $"LastEntries='{FormatRuntimeRunExecutionIndexEntries(lastEntries)}'.");

            throw new InvalidOperationException(
                "Unreachable assertion path.");
        }

        /// <summary>
        /// Formats runtime run execution index entries for test diagnostics.
        /// </summary>
        /// <param name="entries">The index entries.</param>
        /// <returns>A compact diagnostic string.</returns>
        private static string FormatRuntimeRunExecutionIndexEntries(
            IReadOnlyList<AiRuntimeRunExecutionIndexEntry>? entries)
        {
            if (entries is null ||
                entries.Count == 0)
            {
                return "<none>";
            }

            return string.Join(
                " | ",
                entries
                    .Take(20)
                    .Select(
                        entry =>
                            $"Runtime='{entry.RuntimeInstanceId}', " +
                            $"Run='{entry.RunId}', " +
                            $"Execution='{entry.ExecutionId}', " +
                            $"Status='{entry.Status}', " +
                            $"SharedRun='{ResolveIndexMetadata(entry.Metadata, "sharedRunId")}', " +
                            $"RecoveryMode='{ResolveIndexMetadata(entry.Metadata, "recovery.mode")}', " +
                            $"FailureReason='{entry.FailureReason}'"));
        }

        /// <summary>
        /// Resolves an index metadata value.
        /// </summary>
        /// <param name="metadata">The metadata dictionary.</param>
        /// <param name="key">The metadata key.</param>
        /// <returns>The metadata value when present; otherwise, an empty string.</returns>
        private static string ResolveIndexMetadata(
            IReadOnlyDictionary<string, string>? metadata,
            string key)
        {
            if (metadata is null ||
                metadata.Count == 0)
            {
                return string.Empty;
            }

            if (metadata.TryGetValue(key, out var value))
            {
                return value ?? string.Empty;
            }

            foreach (var pair in metadata)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value ?? string.Empty;
                }
            }

            return string.Empty;
        }

        

        /// <summary>
        /// Determines whether the shared run is already assigned away from the failed runtime.
        /// </summary>
        /// <param name="record">The shared run record.</param>
        /// <param name="failedRuntimeInstanceId">The failed runtime instance identifier.</param>
        /// <returns><c>true</c> when the run is assigned to another runtime; otherwise, <c>false</c>.</returns>
        private static bool IsAssignedAwayFromFailedRuntime(
            AiSharedRunRecord? record,
            string failedRuntimeInstanceId)
        {
            return record is not null &&
                !string.IsNullOrWhiteSpace(record.AssignedRuntimeInstanceId) &&
                !string.Equals(
                    record.AssignedRuntimeInstanceId,
                    failedRuntimeInstanceId,
                    StringComparison.Ordinal);
        }

        /// <summary>
        /// Determines whether the recovered queue item is stuck in a non-claimable dispatched state
        /// while the shared run still points to the failed runtime.
        /// </summary>
        /// <param name="record">The shared run record.</param>
        /// <param name="queueItem">The shared queue item.</param>
        /// <param name="dispatchResult">The latest dispatcher result.</param>
        /// <param name="failedRuntimeInstanceId">The failed runtime instance identifier.</param>
        /// <returns><c>true</c> when the wait cannot make progress; otherwise, <c>false</c>.</returns>
        private static bool IsStuckDispatchedToFailedRuntime(
            AiSharedRunRecord? record,
            AiSharedQueueItem? queueItem,
            AiSharedQueueDispatchResult? dispatchResult,
            string failedRuntimeInstanceId)
        {
            return record is not null &&
                queueItem is not null &&
                dispatchResult is not null &&
                queueItem.Status == AiSharedQueueItemStatus.Dispatched &&
                dispatchResult.NoItemAvailable &&
                string.Equals(
                    record.AssignedRuntimeInstanceId,
                    failedRuntimeInstanceId,
                    StringComparison.Ordinal);
        }

        /// <summary>
        /// Resolves a metadata value safely.
        /// </summary>
        /// <param name="metadata">The metadata dictionary.</param>
        /// <param name="key">The metadata key.</param>
        /// <returns>The metadata value when present; otherwise, <c>null</c>.</returns>
        private static string? ResolveMetadata(
            IReadOnlyDictionary<string, string>? metadata,
            string key)
        {
            if (metadata is null)
            {
                return null;
            }

            return metadata.TryGetValue(key, out var value)
                ? value
                : null;
        }

        /// <summary>
        /// Formats runtime instance snapshots for timeout diagnostics.
        /// </summary>
        /// <param name="snapshots">The runtime instance snapshots.</param>
        /// <returns>The formatted runtime snapshot summary.</returns>
        private static string FormatRuntimeSnapshots(
            IReadOnlyList<AiRuntimeInstanceSnapshot> snapshots)
        {
            ArgumentNullException.ThrowIfNull(snapshots);

            if (snapshots.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(
                ",",
                snapshots.Select(snapshot =>
                    $"{snapshot.RuntimeInstanceId}:{snapshot.Status}:Heartbeat='{snapshot.LastHeartbeatAtUtc:O}'"));
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