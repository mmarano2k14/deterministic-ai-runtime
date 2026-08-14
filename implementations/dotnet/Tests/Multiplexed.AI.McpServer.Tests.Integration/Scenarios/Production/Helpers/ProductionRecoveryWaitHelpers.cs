using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Health;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.ControlPlane.Signals;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.State;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Models;
using Multiplexed.AI.Stores;
using Xunit;
using Xunit.Abstractions;

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

        /// <summary>
        /// Marks the failed runtime unhealthy and repeatedly runs real recovery until all seeded work is marked recovered.
        /// </summary>
        public static async Task MarkUnhealthyAndReconcileUntilAllSeededWorkRecoveredAsync(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeInstanceHealthReconciler healthReconciler,
            IAiRuntimeExecutionRecoveryReconciler recoveryReconciler,
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            string failedRuntimeInstanceId,
            IReadOnlyList<FailedRuntimeWorkSeed> seededWorks,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(healthReconciler);
            ArgumentNullException.ThrowIfNull(recoveryReconciler);
            ArgumentNullException.ThrowIfNull(runExecutionIndex);
            ArgumentNullException.ThrowIfNull(seededWorks);
            ArgumentException.ThrowIfNullOrWhiteSpace(failedRuntimeInstanceId);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            AiRuntimeExecutionRecoveryReconciliationResult? lastResult = null;
            AiRuntimeInstanceSnapshot? lastSnapshot = null;
            var lastStatuses =
                new Dictionary<string, string?>(StringComparer.Ordinal);

            while (DateTimeOffset.UtcNow < deadline)
            {
                await registry
                    .MarkUnhealthyAsync(failedRuntimeInstanceId)
                    .ConfigureAwait(false);

                await healthReconciler
                    .ReconcileAsync()
                    .ConfigureAwait(false);

                lastSnapshot =
                    await registry
                        .GetAsync(failedRuntimeInstanceId)
                        .ConfigureAwait(false);

                lastResult =
                    await recoveryReconciler
                        .ReconcileAsync()
                        .ConfigureAwait(false);

                lastStatuses.Clear();

                foreach (var work in seededWorks)
                {
                    var entry =
                        await runExecutionIndex
                            .GetAsync(work.FailedLocalRunId)
                            .ConfigureAwait(false);

                    lastStatuses[work.FailedLocalRunId] =
                        entry?.Status;
                }

                if (seededWorks.All(work =>
                        string.Equals(
                            lastStatuses[work.FailedLocalRunId],
                            "requeued-for-recovery",
                            StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                "Runtime execution recovery did not recover all seeded failed-runtime work within the timeout. " +
                $"FailedRuntimeInstanceId='{failedRuntimeInstanceId}', Timeout='{timeout}', LastRuntimeStatus='{lastSnapshot?.Status}', " +
                $"LastRecoveredRunCount='{lastResult?.RecoveredRunCount}', " +
                $"LastStatuses='{string.Join(",", lastStatuses.Select(pair => $"{pair.Key}:{pair.Value}"))}'.");

            throw new InvalidOperationException(
                "Unreachable assertion path.");
        }

        /// <summary>
        /// Writes the failed runtime inventory before recovery.
        /// </summary>
        public static void WriteFailedRuntimeWorkInventory(
            ITestOutputHelper output,
            string failedRuntimeInstanceId,
            IReadOnlyList<FailedRuntimeWorkSeed> seededWorks)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentException.ThrowIfNullOrWhiteSpace(failedRuntimeInstanceId);
            ArgumentNullException.ThrowIfNull(seededWorks);

            output.WriteLine("[FAILED RUNTIME WORK INVENTORY]");
            output.WriteLine($"RuntimeInstanceId='{failedRuntimeInstanceId}'");
            output.WriteLine($"LocalQueuedRunCount='{seededWorks.Count(work => work.Kind == FailedRuntimeWorkKind.LocalQueued)}'");
            output.WriteLine($"InFlightExecutionCount='{seededWorks.Count(work => work.Kind == FailedRuntimeWorkKind.InFlightExecution)}'");
            output.WriteLine($"TotalRecoverableWorkCount='{seededWorks.Count}'");

            var index =
                1;

            foreach (var work in seededWorks)
            {
                output.WriteLine(
                    $"{index:00}. Kind='{work.Kind}', SharedRunId='{work.SharedRunId}', FailedLocalRunId='{work.FailedLocalRunId}', ExecutionId='{work.ExecutionId}'.");

                index++;
            }
        }

        /// <summary>
        /// Writes the recovered runtime inventory after redispatch.
        /// </summary>
        public static void WriteRecoveredRuntimeWorkInventory(
            ITestOutputHelper output,
            string failedRuntimeInstanceId,
            IReadOnlyList<FailedRuntimeWorkSeed> seededWorks,
            IReadOnlyList<AiSharedRunRecord> redispatchedRuns)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentException.ThrowIfNullOrWhiteSpace(failedRuntimeInstanceId);
            ArgumentNullException.ThrowIfNull(seededWorks);
            ArgumentNullException.ThrowIfNull(redispatchedRuns);

            output.WriteLine("[RECOVERED RUNTIME WORK INVENTORY]");
            output.WriteLine($"FailedRuntimeInstanceId='{failedRuntimeInstanceId}'");
            output.WriteLine($"RecoveredCount='{redispatchedRuns.Count}'");

            var index =
                1;

            foreach (var work in seededWorks)
            {
                var recoveredRun =
                    redispatchedRuns.Single(run =>
                        string.Equals(run.SharedRunId, work.SharedRunId, StringComparison.Ordinal));

                output.WriteLine(
                    $"{index:00}. " +
                    $"Kind='{work.Kind}', " +
                    $"SharedRunId='{work.SharedRunId}', " +
                    $"FailedLocalRunId='{work.FailedLocalRunId}', " +
                    $"ReplacementRuntimeInstanceId='{recoveredRun.AssignedRuntimeInstanceId}', " +
                    $"ReplacementLocalRunId='{recoveredRun.LocalRunId}', " +
                    $"ExecutionIdBefore='{work.ExecutionId}', " +
                    $"ExecutionIdAfter='{recoveredRun.ExecutionId}'.");

                index++;
            }
        }

        /// <summary>
        /// Writes the forensics records linked to the recovered failed-runtime inventory.
        /// </summary>
        public static void WriteRuntimeRecoveryInventoryForensics(
            ITestOutputHelper output,
            string failedRuntimeInstanceId,
            IReadOnlyList<AiRuntimeRecoveryForensicsReadModel> records)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentException.ThrowIfNullOrWhiteSpace(failedRuntimeInstanceId);
            ArgumentNullException.ThrowIfNull(records);

            output.WriteLine("[RUNTIME RECOVERY INVENTORY FORENSICS]");
            output.WriteLine($"FailedRuntimeInstanceId='{failedRuntimeInstanceId}'");
            output.WriteLine($"ForensicsRecordCount='{records.Count}'");

            var index =
                1;

            foreach (var record in records)
            {
                output.WriteLine(
                    $"{index:00}. " +
                    $"ForensicsId='{record.ForensicsId}', " +
                    $"ExecutionId='{record.ExecutionId}', " +
                    $"SharedRunId='{record.SharedRunId}', " +
                    $"TenantId='{record.TenantId}', " +
                    $"Timeline='{string.Join(" -> ", record.Timeline.Select(item => item.EventType))}'.");

                index++;
            }
        }

        /// <summary>
        /// Waits until a real durable DAG execution has reached the expected completed step count.
        /// </summary>
        /// <param name="dagStore">The DAG execution store.</param>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="minimumCompletedSteps">The minimum completed step count.</param>
        /// <param name="timeout">The wait timeout.</param>
        /// <returns>A task that completes when the expected progress is visible.</returns>
        public static async Task WaitForDagCompletedStepCountAsync(
            IAiDagExecutionStore dagStore,
            string executionId,
            int minimumCompletedSteps,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(dagStore);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumCompletedSteps);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            var lastCompletedCount =
                0;

            var lastStatusBreakdown =
                string.Empty;

            var lastRunningSteps =
                string.Empty;

            while (DateTimeOffset.UtcNow < deadline)
            {
                var state =
                    await dagStore
                        .GetStateAsync(executionId)
                        .ConfigureAwait(false);

                if (state is not null)
                {
                    lastCompletedCount =
                        state.Steps.Values.Count(step =>
                            step.Status == AiStepExecutionStatus.Completed);

                    lastStatusBreakdown =
                        string.Join(
                            ",",
                            state.Steps.Values
                                .GroupBy(step => step.Status)
                                .OrderBy(group => group.Key.ToString(), StringComparer.Ordinal)
                                .Select(group => $"{group.Key}:{group.Count()}"));

                    lastRunningSteps =
                        string.Join(
                            " | ",
                            state.Steps.Values
                                .Where(step => step.Status == AiStepExecutionStatus.Running)
                                .Take(20)
                                .Select(step =>
                                    $"Step='{ResolveStepProperty(step, "StepId")}', " +
                                    $"Status='{step.Status}', " +
                                    $"Runtime='{ResolveStepProperty(step, "RuntimeInstanceId")}', " +
                                    $"RunId='{ResolveStepProperty(step, "RunId")}', " +
                                    $"Worker='{ResolveStepProperty(step, "WorkerId")}', " +
                                    $"Started='{ResolveStepProperty(step, "StartedAtUtc")}', " +
                                    $"Updated='{ResolveStepProperty(step, "UpdatedAtUtc")}'"));

                    if (lastCompletedCount >= minimumCompletedSteps)
                    {
                        return;
                    }
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                "The real DAG execution did not reach the expected progress before process kill. " +
                $"ExecutionId='{executionId}', " +
                $"ExpectedCompletedSteps='{minimumCompletedSteps}', " +
                $"LastCompletedSteps='{lastCompletedCount}', " +
                $"LastStatusBreakdown='{lastStatusBreakdown}', " +
                $"LastRunningSteps='{lastRunningSteps}'.");

            throw new InvalidOperationException(
                "Unreachable assertion path.");
        }

        /// <summary>
        /// Reads a compact durable step-progress signature for the supplied running DAG executions.
        /// </summary>
        /// <param name="dagStore">The durable DAG execution store.</param>
        /// <param name="executionIds">The running execution identifiers to inspect.</param>
        /// <returns>A deterministic signature containing completed and running step counts per execution.</returns>
        public static async Task<string> ReadDurableDagProgressSignatureAsync(
            IAiDagExecutionStore dagStore,
            IReadOnlyCollection<string> executionIds)
        {
            ArgumentNullException.ThrowIfNull(dagStore);
            ArgumentNullException.ThrowIfNull(executionIds);

            var orderedExecutionIds =
                executionIds
                    .Where(executionId => !string.IsNullOrWhiteSpace(executionId))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(
                        executionId => executionId,
                        StringComparer.Ordinal)
                    .ToArray();

            if (orderedExecutionIds.Length == 0)
            {
                return "(none)";
            }

            var progressEntries =
                await Task.WhenAll(
                        orderedExecutionIds.Select(
                            async executionId =>
                            {
                                var state =
                                    await dagStore
                                        .GetStateAsync(executionId)
                                        .ConfigureAwait(false);

                                if (state is null)
                                {
                                    return $"{executionId}:(missing)";
                                }

                                var completedStepCount =
                                    state.Steps.Values.Count(
                                        step =>
                                            step.Status ==
                                            AiStepExecutionStatus.Completed);

                                var runningStepCount =
                                    state.Steps.Values.Count(
                                        step =>
                                            step.Status ==
                                            AiStepExecutionStatus.Running);

                                return
                                    $"{executionId}:{completedStepCount}:{runningStepCount}";
                            }))
                    .ConfigureAwait(false);

            return string.Join(
                "|",
                progressEntries);
        }

        /// <summary>
        /// Waits until a shared run has a real runtime execution identifier assigned.
        /// </summary>
        /// <param name="sharedRunStore">The shared run store.</param>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <param name="timeout">The wait timeout.</param>
        /// <returns>The shared run record containing the assigned execution identifier.</returns>
        public static async Task<AiSharedRunRecord> WaitForSharedRunExecutionIdAsync(
            IAiSharedRunStore sharedRunStore,
            string sharedRunId,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(sharedRunStore);
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            AiSharedRunRecord? lastRun =
                null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                lastRun =
                    await sharedRunStore
                        .GetAsync(sharedRunId)
                        .ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(lastRun?.ExecutionId))
                {
                    return lastRun;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                "Shared run did not expose a real execution id within the timeout. " +
                $"SharedRunId='{sharedRunId}', LastStatus='{lastRun?.Status}', LastRuntimeInstanceId='{lastRun?.AssignedRuntimeInstanceId}', LastLocalRunId='{lastRun?.LocalRunId}', LastExecutionId='{lastRun?.ExecutionId}'.");

            throw new InvalidOperationException(
                "Unreachable assertion path.");
        }

        /// <summary>
        /// Waits until a dispatched shared run exposes a durable DAG execution identifier.
        /// </summary>
        /// <param name="sharedRunStore">The shared run store.</param>
        /// <param name="runExecutionIndex">The runtime run execution index.</param>
        /// <param name="dagStore">The DAG execution store.</param>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <param name="timeout">The wait timeout.</param>
        /// <returns>The shared run record and the resolved DAG execution identifier.</returns>
        public static async Task<(AiSharedRunRecord SharedRun, string ExecutionId)> WaitForDurableDagExecutionAsync(
            IAiSharedRunStore sharedRunStore,
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            IAiDagExecutionStore dagStore,
            string sharedRunId,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(sharedRunStore);
            ArgumentNullException.ThrowIfNull(runExecutionIndex);
            ArgumentNullException.ThrowIfNull(dagStore);
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            AiSharedRunRecord? lastRun =
                null;

            AiRuntimeRunExecutionIndexEntry? lastIndexEntry =
                null;

            string? lastCandidateExecutionId =
                null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                lastRun =
                    await sharedRunStore
                        .GetAsync(sharedRunId)
                        .ConfigureAwait(false);

                if (lastRun is not null)
                {
                    if (!string.IsNullOrWhiteSpace(lastRun.LocalRunId))
                    {
                        lastIndexEntry =
                            await runExecutionIndex
                                .GetAsync(lastRun.LocalRunId)
                                .ConfigureAwait(false);
                    }

                    var candidates =
                        new[]
                        {
                    lastRun.ExecutionId,
                    lastIndexEntry?.ExecutionId,
                    lastRun.LocalRunId
                        };

                    foreach (var candidate in candidates)
                    {
                        if (string.IsNullOrWhiteSpace(candidate))
                        {
                            continue;
                        }

                        lastCandidateExecutionId =
                            candidate;

                        var state =
                            await dagStore
                                .GetStateAsync(candidate)
                                .ConfigureAwait(false);

                        if (state is not null)
                        {
                            return (lastRun, candidate);
                        }
                    }
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                "Shared run did not expose a durable DAG execution within the timeout. " +
                $"SharedRunId='{sharedRunId}', LastStatus='{lastRun?.Status}', LastRuntimeInstanceId='{lastRun?.AssignedRuntimeInstanceId}', " +
                $"LastLocalRunId='{lastRun?.LocalRunId}', LastExecutionId='{lastRun?.ExecutionId}', LastIndexExecutionId='{lastIndexEntry?.ExecutionId}', " +
                $"LastIndexStatus='{lastIndexEntry?.Status}', LastIndexRuntimeInstanceId='{lastIndexEntry?.RuntimeInstanceId}', LastCandidateExecutionId='{lastCandidateExecutionId}'.");

            throw new InvalidOperationException(
                "Unreachable assertion path.");
        }

        /// <summary>
        /// Waits until a runtime execution is requeued for recovery.
        /// </summary>
        /// <param name="runExecutionIndex">The runtime run execution index.</param>
        /// <param name="localRunId">The local run identifier.</param>
        /// <param name="executionId">The expected execution identifier.</param>
        /// <param name="timeout">The wait timeout.</param>
        /// <returns>The runtime run execution index entry observed as requeued.</returns>
        public static async Task<AiRuntimeRunExecutionIndexEntry> WaitForRuntimeExecutionRequeuedAsync(
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

            AiRuntimeRunExecutionIndexEntry? lastEntry =
                null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                lastEntry =
                    await runExecutionIndex
                        .GetAsync(localRunId)
                        .ConfigureAwait(false);

                if (IsRuntimeExecutionRequeued(lastEntry, executionId))
                {
                    return lastEntry!;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                "Runtime execution was not automatically requeued for recovery within the timeout. " +
                $"LocalRunId='{localRunId}', ExecutionId='{executionId}', " +
                $"LastIndexStatus='{lastEntry?.Status}', LastIndexExecutionId='{lastEntry?.ExecutionId}', LastIndexRuntimeInstanceId='{lastEntry?.RuntimeInstanceId}'.");

            throw new InvalidOperationException(
                "Unreachable assertion path.");
        }

        /// <summary>
        /// Resolves a diagnostic property from a DAG step using reflection.
        /// </summary>
        /// <param name="step">The step object.</param>
        /// <param name="propertyName">The property name.</param>
        /// <returns>The property value when available; otherwise, an empty string.</returns>
        private static string ResolveStepProperty(
            object step,
            string propertyName)
        {
            ArgumentNullException.ThrowIfNull(step);
            ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);

            var property =
                step
                    .GetType()
                    .GetProperty(propertyName);

            var value =
                property?.GetValue(step);

            return value?.ToString() ?? string.Empty;
        }

        /// <summary>
        /// Determines whether the runtime execution index entry is requeued for recovery.
        /// </summary>
        /// <param name="entry">The runtime run execution index entry.</param>
        /// <param name="executionId">The expected execution identifier.</param>
        /// <returns><c>true</c> when the entry is requeued for recovery; otherwise, <c>false</c>.</returns>
        private static bool IsRuntimeExecutionRequeued(
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


        /// <summary>
        /// Waits until a runtime instance is no longer considered safe for routing.
        /// </summary>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="timeout">The wait timeout.</param>
        /// <returns>A task that completes when the runtime instance is no longer safe.</returns>
        public static async Task WaitForRuntimeInstanceUnsafeAsync(
            IAiRuntimeInstanceRegistry registry,
            string runtimeInstanceId,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            object? lastInstance =
                null;

            var lastObservedCount =
                0;

            while (DateTimeOffset.UtcNow < deadline)
            {
                var instances =
                    await registry
                        .ListAsync()
                        .ConfigureAwait(false);

                lastObservedCount =
                    instances.Count;

                lastInstance =
                    instances.FirstOrDefault(instance =>
                        string.Equals(
                            instance.RuntimeInstanceId,
                            runtimeInstanceId,
                            StringComparison.OrdinalIgnoreCase));

                if (lastInstance is null)
                {
                    return;
                }

                var type =
                    lastInstance.GetType();

                var status =
                    type.GetProperty("Status")?.GetValue(lastInstance)?.ToString();

                var isHealthy =
                    type.GetProperty("IsHealthy")?.GetValue(lastInstance) as bool?;

                var isDraining =
                    type.GetProperty("IsDraining")?.GetValue(lastInstance) as bool?;

                var isAvailable =
                    type.GetProperty("IsAvailable")?.GetValue(lastInstance) as bool?;

                if (string.Equals(status, "Unhealthy", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, "Draining", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, "Offline", StringComparison.OrdinalIgnoreCase) ||
                    isHealthy == false ||
                    isDraining == true ||
                    isAvailable == false)
                {
                    return;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                "Runtime instance was not automatically marked unsafe after process kill within the timeout. " +
                $"RuntimeInstanceId='{runtimeInstanceId}', LastObservedRegistryCount='{lastObservedCount}', LastInstance='{lastInstance}'.");

            throw new InvalidOperationException(
                "Unreachable assertion path.");
        }


        /// <summary>
        /// Waits until a recovered shared run is redispatched with a new local runtime run identifier.
        /// </summary>
        /// <param name="sharedRunStore">The shared run store.</param>
        /// <param name="sharedQueue">The durable shared queue.</param>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <param name="failedRuntimeInstanceId">The failed runtime instance identifier.</param>
        /// <param name="failedLocalRunId">The failed local run identifier.</param>
        /// <param name="timeout">The wait timeout.</param>
        /// <returns>The redispatched shared run record.</returns>
        public static async Task<AiSharedRunRecord> WaitForRecoveredRunRedispatchedAsync(
            IAiSharedRunStore sharedRunStore,
            IAiSharedQueue sharedQueue,
            string sharedRunId,
            string failedRuntimeInstanceId,
            string failedLocalRunId,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(sharedRunStore);
            ArgumentNullException.ThrowIfNull(sharedQueue);
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(failedRuntimeInstanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(failedLocalRunId);

            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timeout),
                    timeout,
                    "The wait timeout must be greater than zero.");
            }

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            AiSharedRunRecord? lastRun =
                null;

            AiSharedQueueItem? lastQueueItem =
                null;

            StackExchange.Redis.RedisTimeoutException? lastRedisTimeout =
                null;

            var redisTimeoutCount =
                0;

            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    lastRun =
                        await sharedRunStore
                            .GetAsync(sharedRunId)
                            .ConfigureAwait(false);

                    if (lastRun is not null &&
                        !string.IsNullOrWhiteSpace(lastRun.AssignedRuntimeInstanceId) &&
                        !string.IsNullOrWhiteSpace(lastRun.LocalRunId) &&
                        !string.Equals(
                            lastRun.AssignedRuntimeInstanceId,
                            failedRuntimeInstanceId,
                            StringComparison.Ordinal) &&
                        !string.Equals(
                            lastRun.LocalRunId,
                            failedLocalRunId,
                            StringComparison.Ordinal))
                    {
                        return lastRun;
                    }
                }
                catch (StackExchange.Redis.RedisTimeoutException exception)
                {
                    lastRedisTimeout =
                        exception;

                    redisTimeoutCount++;

                    await Task
                        .Delay(TimeSpan.FromMilliseconds(250))
                        .ConfigureAwait(false);

                    continue;
                }

                if (lastRun is not null &&
                    !string.IsNullOrWhiteSpace(
                        lastRun.AssignedRuntimeInstanceId) &&
                    !string.IsNullOrWhiteSpace(
                        lastRun.LocalRunId) &&
                    !string.Equals(
                        lastRun.AssignedRuntimeInstanceId,
                        failedRuntimeInstanceId,
                        StringComparison.Ordinal) &&
                    !string.Equals(
                        lastRun.LocalRunId,
                        failedLocalRunId,
                        StringComparison.Ordinal))
                {
                    return lastRun;
                }

                /*
                 * Capture the durable queue state only while redispatch has not
                 * converged. This tells us whether the work is pending, claimed,
                 * dispatched, missing, or abandoned by a queue worker.
                 *
                 * A transient Redis timeout is tolerated because this method owns
                 * a larger convergence timeout. The recovery proof still fails when
                 * durable convergence does not occur before that deadline.
                 */
                try
                {
                    lastQueueItem =
                        await sharedQueue
                            .GetAsync(sharedRunId)
                            .ConfigureAwait(false);
                }
                catch (StackExchange.Redis.RedisTimeoutException exception)
                {
                    lastRedisTimeout =
                        exception;

                    redisTimeoutCount++;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            /*
             * Perform final best-effort reads after the timeout so the failure message
             * contains the freshest available shared-store and queue states.
             *
             * A Redis timeout here must not replace the actual convergence assertion.
             * The most recent successfully observed values remain available below.
             */
            try
            {
                lastRun =
                    await sharedRunStore
                        .GetAsync(sharedRunId)
                        .ConfigureAwait(false);
            }
            catch (StackExchange.Redis.RedisTimeoutException exception)
            {
                lastRedisTimeout =
                    exception;

                redisTimeoutCount++;
            }

            if (IsRecoveredRunRedispatched(
                    lastRun,
                    failedRuntimeInstanceId,
                    failedLocalRunId))
            {
                return lastRun!;
            }

            try
            {
                lastQueueItem =
                    await sharedQueue
                        .GetAsync(sharedRunId)
                        .ConfigureAwait(false);
            }
            catch (StackExchange.Redis.RedisTimeoutException exception)
            {
                lastRedisTimeout =
                    exception;

                redisTimeoutCount++;
            }

            var queueMetadata =
                lastQueueItem?.Metadata is { Count: > 0 }
                    ? string.Join(
                        ";",
                        lastQueueItem.Metadata
                            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                            .Select(pair =>
                                $"{pair.Key}={pair.Value}"))
                    : string.Empty;

            Assert.Fail(
                "Recovered shared run was not redispatched with a new local runtime run id within the timeout. " +
                $"SharedRunId='{sharedRunId}', " +
                $"FailedRuntimeInstanceId='{failedRuntimeInstanceId}', " +
                $"FailedLocalRunId='{failedLocalRunId}', " +
                $"Timeout='{timeout}', " +

                $"LastStatus='{lastRun?.Status}', " +
                $"LastRuntimeInstanceId='{lastRun?.AssignedRuntimeInstanceId}', " +
                $"LastLocalRunId='{lastRun?.LocalRunId}', " +
                $"LastExecutionId='{lastRun?.ExecutionId}', " +
                $"LastReason='{lastRun?.Reason}', " +
                $"LastFailureReason='{lastRun?.FailureReason}', " +
                $"LastUpdatedAtUtc='{lastRun?.UpdatedAtUtc:O}', " +

                $"QueueItemExists='{lastQueueItem is not null}', " +
                $"QueueStatus='{lastQueueItem?.Status}', " +
                $"QueueRuntimeInstanceId='{lastQueueItem?.ClaimedByRuntimeInstanceId}', " +
                $"QueueWorkerId='{lastQueueItem?.ClaimedByWorkerId}', " +
                $"QueueClaimToken='{lastQueueItem?.ClaimToken}', " +
                $"QueueClaimedAtUtc='{lastQueueItem?.ClaimedAtUtc?.ToString("O") ?? string.Empty}', " +
                $"QueueClaimExpiresAtUtc='{lastQueueItem?.ClaimExpiresAtUtc?.ToString("O") ?? string.Empty}', " +
                $"QueueUpdatedAtUtc='{lastQueueItem?.UpdatedAtUtc:O}', " +
                $"QueueReason='{lastQueueItem?.Reason}', " +
                $"QueueMetadata='{queueMetadata}', " +

                $"RedisTimeoutCount='{redisTimeoutCount}', " +
                $"LastRedisTimeoutType='{lastRedisTimeout?.GetType().FullName}', " +
                $"LastRedisTimeoutMessage='{lastRedisTimeout?.Message}'.");

            throw new InvalidOperationException(
                "Unreachable assertion path.");
        }

        /// <summary>
        /// Waits until a real durable DAG execution reaches the expected completed step count
        /// by combining targeted runtime signals with a slow durable polling fallback.
        /// </summary>
        /// <remarks>
        /// Runtime signals are wake-up notifications only. Durable DAG state remains authoritative.
        ///
        /// The subscription is activated before the initial durable read so a completion signal
        /// cannot be missed between state inspection and signal registration.
        ///
        /// When no matching signal is received, the durable store is checked at the configured
        /// fallback interval. This preserves correctness when Redis Pub/Sub messages are delayed
        /// or lost without creating the high read pressure of the original hot polling loop.
        /// </remarks>
        /// <param name="dagStore">The durable DAG execution store.</param>
        /// <param name="signalSubscriber">The targeted runtime signal subscriber.</param>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <param name="executionId">The durable execution identifier.</param>
        /// <param name="minimumCompletedSteps">The minimum completed step count.</param>
        /// <param name="timeout">The global wait timeout.</param>
        /// <param name="fallbackPollInterval">The durable fallback polling interval.</param>
        /// <returns>A task that completes when durable DAG progress is confirmed.</returns>
        public static async Task WaitForDagCompletedStepCountHybridAsync(
            IAiDagExecutionStore dagStore,
            IAiRuntimeSignalSubscriber signalSubscriber,
            string controlPlaneId,
            string executionId,
            int minimumCompletedSteps,
            TimeSpan timeout,
            TimeSpan fallbackPollInterval)
        {
            ArgumentNullException.ThrowIfNull(dagStore);
            ArgumentNullException.ThrowIfNull(signalSubscriber);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumCompletedSteps);

            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timeout),
                    timeout,
                    "The wait timeout must be greater than zero.");
            }

            if (fallbackPollInterval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(fallbackPollInterval),
                    fallbackPollInterval,
                    "The fallback polling interval must be greater than zero.");
            }

            var deadline = DateTimeOffset.UtcNow.Add(timeout);

            var lastCompletedCount = 0;
            var lastStatusBreakdown = string.Empty;
            var lastRunningSteps = string.Empty;

            var signalObserved = false;
            int? lastSignalCompletedStepCount = null;
            int? lastSignalTotalStepCount = null;
            DateTimeOffset? lastSignalOccurredAtUtc = null;

            var durableReadCount = 0;
            var fallbackReadCount = 0;
            var redisTimeoutCount = 0;

            StackExchange.Redis.RedisTimeoutException? lastRedisTimeout = null;

            using var signalLifetime = new CancellationTokenSource();

            await using var subscription = await signalSubscriber
                .SubscribeAsync(
                    AiRuntimeSignalType.DagProgressChanged,
                    controlPlaneId,
                    executionId,
                    signalLifetime.Token)
                .ConfigureAwait(false);

            var matchingSignalTask = WaitForDagProgressSignalAsync(
                subscription,
                controlPlaneId,
                executionId,
                minimumCompletedSteps,
                signalLifetime.Token);

            async Task<bool> RefreshDurableProgressAsync()
            {
                durableReadCount++;

                try
                {
                    var state = await dagStore
                        .GetStateAsync(executionId)
                        .ConfigureAwait(false);

                    if (state is null)
                    {
                        return false;
                    }

                    lastCompletedCount = state.Steps.Values.Count(step =>
                        step.Status == AiStepExecutionStatus.Completed);

                    lastStatusBreakdown = string.Join(
                        ",",
                        state.Steps.Values
                            .GroupBy(step => step.Status)
                            .OrderBy(group => group.Key.ToString(), StringComparer.Ordinal)
                            .Select(group => $"{group.Key}:{group.Count()}"));

                    lastRunningSteps = string.Join(
                        " | ",
                        state.Steps.Values
                            .Where(step => step.Status == AiStepExecutionStatus.Running)
                            .Take(20)
                            .Select(step =>
                                $"Step='{ResolveStepProperty(step, "StepId")}', " +
                                $"Status='{step.Status}', " +
                                $"Runtime='{ResolveStepProperty(step, "RuntimeInstanceId")}', " +
                                $"RunId='{ResolveStepProperty(step, "RunId")}', " +
                                $"Worker='{ResolveStepProperty(step, "WorkerId")}', " +
                                $"Started='{ResolveStepProperty(step, "StartedAtUtc")}', " +
                                $"Updated='{ResolveStepProperty(step, "UpdatedAtUtc")}'"));

                    return lastCompletedCount >= minimumCompletedSteps;
                }
                catch (StackExchange.Redis.RedisTimeoutException exception)
                {
                    lastRedisTimeout = exception;
                    redisTimeoutCount++;

                    return false;
                }
            }

            try
            {
                /*
                 * The subscription is active before this initial read.
                 *
                 * This ordering closes the race where the DAG reaches the threshold
                 * immediately before signal subscription becomes active.
                 */
                if (await RefreshDurableProgressAsync().ConfigureAwait(false))
                {
                    return;
                }

                while (DateTimeOffset.UtcNow < deadline)
                {
                    var remaining = deadline - DateTimeOffset.UtcNow;

                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    var waitDuration = remaining < fallbackPollInterval
                        ? remaining
                        : fallbackPollInterval;

                    if (!signalObserved)
                    {
                        var fallbackDelayTask = Task.Delay(waitDuration);

                        var completedTask = await Task
                            .WhenAny(
                                matchingSignalTask,
                                fallbackDelayTask)
                            .ConfigureAwait(false);

                        if (completedTask == matchingSignalTask)
                        {
                            var signal = await matchingSignalTask.ConfigureAwait(false);

                            signalObserved = true;
                            lastSignalCompletedStepCount = signal.CompletedStepCount;
                            lastSignalTotalStepCount = signal.TotalStepCount;
                            lastSignalOccurredAtUtc = signal.OccurredAtUtc;
                        }
                        else
                        {
                            fallbackReadCount++;
                        }
                    }
                    else
                    {
                        await Task
                            .Delay(waitDuration)
                            .ConfigureAwait(false);

                        fallbackReadCount++;
                    }

                    /*
                     * A signal never proves progress by itself.
                     * Every wake-up or fallback interval is confirmed through durable state.
                     */
                    if (await RefreshDurableProgressAsync().ConfigureAwait(false))
                    {
                        return;
                    }
                }

                /*
                 * One final best-effort durable read provides the freshest diagnostics
                 * and accepts convergence that occurred at the timeout boundary.
                 */
                if (await RefreshDurableProgressAsync().ConfigureAwait(false))
                {
                    return;
                }

                Assert.Fail(
                    "The real DAG execution did not reach the expected progress before process kill using hybrid signal observation. " +
                    $"ControlPlaneId='{controlPlaneId}', " +
                    $"ExecutionId='{executionId}', " +
                    $"ExpectedCompletedSteps='{minimumCompletedSteps}', " +
                    $"LastCompletedSteps='{lastCompletedCount}', " +
                    $"LastStatusBreakdown='{lastStatusBreakdown}', " +
                    $"LastRunningSteps='{lastRunningSteps}', " +
                    $"SignalObserved='{signalObserved}', " +
                    $"LastSignalCompletedStepCount='{lastSignalCompletedStepCount}', " +
                    $"LastSignalTotalStepCount='{lastSignalTotalStepCount}', " +
                    $"LastSignalOccurredAtUtc='{lastSignalOccurredAtUtc?.ToString("O") ?? string.Empty}', " +
                    $"DurableReadCount='{durableReadCount}', " +
                    $"FallbackReadCount='{fallbackReadCount}', " +
                    $"RedisTimeoutCount='{redisTimeoutCount}', " +
                    $"LastRedisTimeout='{lastRedisTimeout?.Message}'.");

                throw new InvalidOperationException(
                    "Unreachable assertion path.");
            }
            finally
            {
                signalLifetime.Cancel();

                try
                {
                    await matchingSignalTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected when durable state converges before a matching signal is consumed.
                }
            }
        }

        /// <summary>
        /// Waits until a recovered shared run is durably redispatched, using a targeted
        /// shared-run signal as a wake-up notification and a slow durable fallback.
        /// </summary>
        public static async Task<AiSharedRunRecord> WaitForRecoveredRunRedispatchedHybridAsync(
            IAiSharedRunStore sharedRunStore,
            IAiSharedQueue sharedQueue,
            string sharedRunId,
            string failedRuntimeInstanceId,
            string failedLocalRunId,
            Task<AiRuntimeSignal> sharedRunDispatchedSignalTask,
            TimeSpan timeout,
            TimeSpan fallbackPollInterval)
        {
            ArgumentNullException.ThrowIfNull(sharedRunStore);
            ArgumentNullException.ThrowIfNull(sharedQueue);
            ArgumentNullException.ThrowIfNull(sharedRunDispatchedSignalTask);
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(failedRuntimeInstanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(failedLocalRunId);

            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timeout),
                    timeout,
                    "The wait timeout must be greater than zero.");
            }

            if (fallbackPollInterval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(fallbackPollInterval),
                    fallbackPollInterval,
                    "The fallback polling interval must be greater than zero.");
            }

            var deadline = DateTimeOffset.UtcNow.Add(timeout);

            AiSharedRunRecord? lastRun = null;
            AiSharedQueueItem? lastQueueItem = null;
            AiRuntimeSignal? observedSignal = null;

            StackExchange.Redis.RedisTimeoutException? lastRedisTimeout = null;

            var redisTimeoutCount = 0;
            var fallbackReadCount = 0;
            var initialDurableRead = true;
            Task<AiRuntimeSignal>? pendingSignalTask = sharedRunDispatchedSignalTask;

            while (DateTimeOffset.UtcNow < deadline)
            {
                if (!initialDurableRead)
                {
                    var remaining = deadline - DateTimeOffset.UtcNow;

                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    var waitDuration = remaining < fallbackPollInterval
                        ? remaining
                        : fallbackPollInterval;

                    var fallbackDelayTask = Task.Delay(waitDuration);

                    if (pendingSignalTask is not null)
                    {
                        var completedTask = await Task
                            .WhenAny(
                                pendingSignalTask,
                                fallbackDelayTask)
                            .ConfigureAwait(false);

                        if (completedTask == pendingSignalTask)
                        {
                            try
                            {
                                observedSignal = await pendingSignalTask
                                    .ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                // The signal is best-effort; durable fallback remains authoritative.
                            }
                            catch (InvalidOperationException)
                            {
                                // The signal stream closed; durable fallback remains authoritative.
                            }

                            pendingSignalTask = null;
                        }
                        else
                        {
                            fallbackReadCount++;
                        }
                    }
                    else
                    {
                        await fallbackDelayTask.ConfigureAwait(false);
                        fallbackReadCount++;
                    }
                }

                initialDurableRead = false;

                try
                {
                    lastRun = await sharedRunStore
                        .GetAsync(sharedRunId)
                        .ConfigureAwait(false);

                    if (lastRun is not null &&
                        !string.IsNullOrWhiteSpace(lastRun.AssignedRuntimeInstanceId) &&
                        !string.IsNullOrWhiteSpace(lastRun.LocalRunId) &&
                        !string.Equals(
                            lastRun.AssignedRuntimeInstanceId,
                            failedRuntimeInstanceId,
                            StringComparison.Ordinal) &&
                        !string.Equals(
                            lastRun.LocalRunId,
                            failedLocalRunId,
                            StringComparison.Ordinal))
                    {
                        return lastRun;
                    }
                }
                catch (StackExchange.Redis.RedisTimeoutException exception)
                {
                    lastRedisTimeout = exception;
                    redisTimeoutCount++;
                    continue;
                }

                if (lastRun is not null &&
                    !string.IsNullOrWhiteSpace(lastRun.AssignedRuntimeInstanceId) &&
                    !string.IsNullOrWhiteSpace(lastRun.LocalRunId) &&
                    !string.Equals(
                        lastRun.AssignedRuntimeInstanceId,
                        failedRuntimeInstanceId,
                        StringComparison.Ordinal) &&
                    !string.Equals(
                        lastRun.LocalRunId,
                        failedLocalRunId,
                        StringComparison.Ordinal))
                {
                    return lastRun;
                }

                try
                {
                    lastQueueItem = await sharedQueue
                        .GetAsync(sharedRunId)
                        .ConfigureAwait(false);
                }
                catch (StackExchange.Redis.RedisTimeoutException exception)
                {
                    lastRedisTimeout = exception;
                    redisTimeoutCount++;
                }
            }

            try
            {
                lastRun = await sharedRunStore
                    .GetAsync(sharedRunId)
                    .ConfigureAwait(false);
            }
            catch (StackExchange.Redis.RedisTimeoutException exception)
            {
                lastRedisTimeout = exception;
                redisTimeoutCount++;
            }

            if (IsRecoveredRunRedispatched(
                    lastRun,
                    failedRuntimeInstanceId,
                    failedLocalRunId))
            {
                return lastRun!;
            }

            try
            {
                lastQueueItem = await sharedQueue
                    .GetAsync(sharedRunId)
                    .ConfigureAwait(false);
            }
            catch (StackExchange.Redis.RedisTimeoutException exception)
            {
                lastRedisTimeout = exception;
                redisTimeoutCount++;
            }

            var queueMetadata = lastQueueItem?.Metadata is { Count: > 0 }
                ? string.Join(
                    ";",
                    lastQueueItem.Metadata
                        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => $"{pair.Key}={pair.Value}"))
                : string.Empty;

            Assert.Fail(
                "Recovered shared run was not redispatched with a new local runtime run id within the hybrid timeout. " +
                $"SharedRunId='{sharedRunId}', " +
                $"FailedRuntimeInstanceId='{failedRuntimeInstanceId}', " +
                $"FailedLocalRunId='{failedLocalRunId}', " +
                $"Timeout='{timeout}', " +
                $"SignalObserved='{observedSignal is not null}', " +
                $"SignalRuntimeInstanceId='{observedSignal?.RuntimeInstanceId}', " +
                $"SignalLocalRunId='{observedSignal?.LocalRunId}', " +
                $"SignalExecutionId='{observedSignal?.ExecutionId}', " +
                $"FallbackReadCount='{fallbackReadCount}', " +
                $"LastStatus='{lastRun?.Status}', " +
                $"LastRuntimeInstanceId='{lastRun?.AssignedRuntimeInstanceId}', " +
                $"LastLocalRunId='{lastRun?.LocalRunId}', " +
                $"LastExecutionId='{lastRun?.ExecutionId}', " +
                $"LastReason='{lastRun?.Reason}', " +
                $"LastFailureReason='{lastRun?.FailureReason}', " +
                $"LastUpdatedAtUtc='{lastRun?.UpdatedAtUtc:O}', " +
                $"QueueItemExists='{lastQueueItem is not null}', " +
                $"QueueStatus='{lastQueueItem?.Status}', " +
                $"QueueRuntimeInstanceId='{lastQueueItem?.ClaimedByRuntimeInstanceId}', " +
                $"QueueWorkerId='{lastQueueItem?.ClaimedByWorkerId}', " +
                $"QueueClaimToken='{lastQueueItem?.ClaimToken}', " +
                $"QueueClaimedAtUtc='{lastQueueItem?.ClaimedAtUtc?.ToString("O") ?? string.Empty}', " +
                $"QueueClaimExpiresAtUtc='{lastQueueItem?.ClaimExpiresAtUtc?.ToString("O") ?? string.Empty}', " +
                $"QueueUpdatedAtUtc='{lastQueueItem?.UpdatedAtUtc:O}', " +
                $"QueueReason='{lastQueueItem?.Reason}', " +
                $"QueueMetadata='{queueMetadata}', " +
                $"RedisTimeoutCount='{redisTimeoutCount}', " +
                $"LastRedisTimeoutType='{lastRedisTimeout?.GetType().FullName}', " +
                $"LastRedisTimeoutMessage='{lastRedisTimeout?.Message}'.");

            throw new InvalidOperationException(
                "Unreachable assertion path.");
        }

        /// <summary>
        /// Determines whether a recovered shared run has been durably assigned to a
        /// replacement runtime with a new local runtime run identifier.
        /// </summary>
        /// <param name="record">The shared run record.</param>
        /// <param name="failedRuntimeInstanceId">The failed runtime instance identifier.</param>
        /// <param name="failedLocalRunId">The failed local runtime run identifier.</param>
        /// <returns>
        /// <see langword="true"/> when durable redispatch has converged;
        /// otherwise, <see langword="false"/>.
        /// </returns>
        private static bool IsRecoveredRunRedispatched(
            AiSharedRunRecord? record,
            string failedRuntimeInstanceId,
            string failedLocalRunId)
        {
            return record is not null &&
                !string.IsNullOrWhiteSpace(record.AssignedRuntimeInstanceId) &&
                !string.IsNullOrWhiteSpace(record.LocalRunId) &&
                !string.Equals(
                    record.AssignedRuntimeInstanceId,
                    failedRuntimeInstanceId,
                    StringComparison.Ordinal) &&
                !string.Equals(
                    record.LocalRunId,
                    failedLocalRunId,
                    StringComparison.Ordinal);
        }

        /// <summary>
        /// Waits for the first targeted DAG progress signal that reports the required progress.
        /// </summary>
        /// <param name="subscription">The active targeted runtime signal subscription.</param>
        /// <param name="controlPlaneId">The expected logical control-plane identifier.</param>
        /// <param name="executionId">The expected durable execution identifier.</param>
        /// <param name="minimumCompletedSteps">The minimum completed step count.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The first matching DAG progress signal.</returns>
        private static async Task<AiRuntimeSignal> WaitForDagProgressSignalAsync(
            IAiRuntimeSignalSubscription subscription,
            string controlPlaneId,
            string executionId,
            int minimumCompletedSteps,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(subscription);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumCompletedSteps);

            await foreach (var signal in subscription
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                if (signal.Type != AiRuntimeSignalType.DagProgressChanged ||
                    !string.Equals(
                        signal.ControlPlaneId,
                        controlPlaneId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        signal.ExecutionId,
                        executionId,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (signal.CompletedStepCount is int completedStepCount &&
                    completedStepCount >= minimumCompletedSteps)
                {
                    return signal;
                }
            }

            throw new InvalidOperationException(
                "The targeted DAG progress signal subscription completed unexpectedly.");
        }

    }
}