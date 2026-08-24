using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Helpers;
using Multiplexed.AI.Stores;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Runners
{
    /// <summary>
    /// Observes a transport-neutral Runtime Pool workload through durable shared-run, execution,
    /// and DAG state.
    /// </summary>
    internal static class RuntimePoolProductionWorkloadObserver
    {
        /// <summary>
        /// Waits for every exact submitted SharedRun to complete while durable DAG progress keeps
        /// the bounded no-progress watchdog alive.
        /// </summary>
        public static async Task<IReadOnlyList<AiSharedRunRecord>>
            WaitForSubmittedRunsToCompleteAsync(
                IAiSharedRunStore sharedRunStore,
                IAiRuntimeRunExecutionIndex runExecutionIndex,
                IAiDagExecutionStore dagStore,
                IReadOnlySet<string> submittedSharedRunIds,
                string controlPlaneId,
                string tenantId,
                TimeSpan timeout,
                TimeSpan noProgressTimeout,
                bool useDagExecutionCompletion = false)
        {
            ArgumentNullException.ThrowIfNull(sharedRunStore);
            ArgumentNullException.ThrowIfNull(runExecutionIndex);
            ArgumentNullException.ThrowIfNull(dagStore);
            ArgumentNullException.ThrowIfNull(submittedSharedRunIds);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

            if (noProgressTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(noProgressTimeout));
            }

            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            var lastProgressAtUtc = DateTimeOffset.UtcNow;
            string? lastProgressSignature = null;
            string? lastDurableDagProgressSignature = null;
            var nextDagProbeAtUtc = DateTimeOffset.UtcNow;
            IReadOnlyList<AiSharedRunRecord> lastRuns =
                Array.Empty<AiSharedRunRecord>();
            IReadOnlyList<AiRuntimeRunExecutionIndexEntry> lastUnfinishedRuntimeRuns =
                Array.Empty<AiRuntimeRunExecutionIndexEntry>();

            while (DateTimeOffset.UtcNow < deadline)
            {
                lastRuns =
                    await ReadExactSubmittedRunsAsync(
                            sharedRunStore,
                            submittedSharedRunIds,
                            controlPlaneId,
                            tenantId)
                        .ConfigureAwait(false);

                var observations =
                    await Task.WhenAll(
                            lastRuns.Select(
                                async run =>
                                {
                                    AiRuntimeRunExecutionIndexEntry? indexEntry = null;

                                    if (!string.IsNullOrWhiteSpace(run.LocalRunId))
                                    {
                                        indexEntry = await runExecutionIndex
                                            .GetAsync(run.LocalRunId)
                                            .ConfigureAwait(false);
                                    }

                                    var executionId =
                                        !string.IsNullOrWhiteSpace(run.ExecutionId)
                                            ? run.ExecutionId
                                            : indexEntry?.ExecutionId;

                                    AiExecutionStatus? dagStatus = null;

                                    if (useDagExecutionCompletion &&
                                        !string.IsNullOrWhiteSpace(executionId))
                                    {
                                        var dagRecord = await dagStore
                                            .GetRecordAsync(executionId)
                                            .ConfigureAwait(false);
                                        dagStatus = dagRecord?.Status;
                                    }

                                    var completed =
                                        useDagExecutionCompletion
                                            ? !string.IsNullOrWhiteSpace(executionId) &&
                                              dagStatus == AiExecutionStatus.Completed
                                            : indexEntry is not null &&
                                              string.Equals(
                                                  indexEntry.Status,
                                                  "completed",
                                                  StringComparison.OrdinalIgnoreCase);

                                    return new
                                    {
                                        Run = run,
                                        IndexEntry = indexEntry,
                                        ExecutionId = executionId,
                                        DagStatus = dagStatus,
                                        Completed = completed
                                    };
                                }))
                        .ConfigureAwait(false);

                if (lastRuns.Count == submittedSharedRunIds.Count &&
                    observations.Length == submittedSharedRunIds.Count &&
                    observations.All(observation => observation.Completed))
                {
                    return lastRuns;
                }

                var progressSignature =
                    string.Join(
                        "|",
                        lastRuns.Count,
                        lastRuns.Count(
                            run =>
                                !string.IsNullOrWhiteSpace(
                                    run.AssignedRuntimeInstanceId)),
                        lastRuns.Count(
                            run => !string.IsNullOrWhiteSpace(run.LocalRunId)),
                        observations.Count(
                            observation => observation.IndexEntry is not null),
                        observations.Count(
                            observation =>
                                !string.IsNullOrWhiteSpace(
                                    observation.ExecutionId)),
                        observations.Count(observation => observation.Completed),
                        string.Join(
                            ",",
                            observations
                                .GroupBy(
                                    observation =>
                                        observation.IndexEntry?.Status ??
                                        "(index-missing)",
                                    StringComparer.OrdinalIgnoreCase)
                                .OrderBy(
                                    group => group.Key,
                                    StringComparer.OrdinalIgnoreCase)
                                .Select(
                                    group =>
                                        $"{group.Key}:{group.Count()}")),
                        string.Join(
                            ",",
                            observations
                                .Where(observation =>
                                    observation.DagStatus.HasValue)
                                .GroupBy(observation =>
                                    observation.DagStatus!.Value)
                                .OrderBy(group => group.Key)
                                .Select(group =>
                                    $"{group.Key}:{group.Count()}")));

                var nowUtc = DateTimeOffset.UtcNow;
                var durableDagProgressObserved = false;

                if (nowUtc >= nextDagProbeAtUtc)
                {
                    var submittedExecutionIds =
                        observations
                            .Where(
                                observation =>
                                    !observation.Completed &&
                                    (useDagExecutionCompletion ||
                                     string.Equals(
                                         observation.IndexEntry?.Status,
                                         "running",
                                         StringComparison.OrdinalIgnoreCase)) &&
                                    !string.IsNullOrWhiteSpace(
                                        observation.ExecutionId))
                            .Select(observation => observation.ExecutionId!)
                            .ToArray();

                    var unfinishedExecutionIds = Array.Empty<string>();

                    if (useDagExecutionCompletion)
                    {
                        var unfinishedRuntimeRuns = await runExecutionIndex
                            .ListUnfinishedAsync()
                            .ConfigureAwait(false);

                        lastUnfinishedRuntimeRuns =
                            unfinishedRuntimeRuns
                                .Where(
                                    entry =>
                                        string.Equals(
                                            entry.ExecutionContextSnapshot.TenantId,
                                            tenantId,
                                            StringComparison.Ordinal))
                                .ToArray();

                        unfinishedExecutionIds =
                            lastUnfinishedRuntimeRuns
                                .Where(
                                    entry =>
                                        !string.IsNullOrWhiteSpace(
                                            entry.ExecutionId))
                                .Select(entry => entry.ExecutionId!)
                                .ToArray();
                    }

                    var activeExecutionIds =
                        submittedExecutionIds
                            .Concat(unfinishedExecutionIds)
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(value => value, StringComparer.Ordinal)
                            .ToArray();

                    var durableDagProgressSignature =
                        await ProductionRecoveryWaitHelpers
                            .ReadDurableDagProgressSignatureAsync(
                                dagStore,
                                activeExecutionIds)
                            .ConfigureAwait(false);

                    durableDagProgressObserved =
                        lastDurableDagProgressSignature is not null &&
                        !StringComparer.Ordinal.Equals(
                            durableDagProgressSignature,
                            lastDurableDagProgressSignature);

                    lastDurableDagProgressSignature =
                        durableDagProgressSignature;
                    nextDagProbeAtUtc = nowUtc.AddSeconds(5);
                }

                if (!StringComparer.Ordinal.Equals(
                        progressSignature,
                        lastProgressSignature) ||
                    durableDagProgressObserved)
                {
                    lastProgressSignature = progressSignature;
                    lastProgressAtUtc = nowUtc;
                }
                else if (nowUtc - lastProgressAtUtc >= noProgressTimeout)
                {
                    var childAwareDiagnostics =
                        useDagExecutionCompletion
                            ? $" UnfinishedTenantRuntimeRuns='{lastUnfinishedRuntimeRuns.Count}'."
                            : string.Empty;

                    throw new TimeoutException(
                        $"The Runtime Pool workload made no durable progress for '{noProgressTimeout}'. Expected='{submittedSharedRunIds.Count}', Observed='{lastRuns.Count}', Progress='{progressSignature}'.{childAwareDiagnostics}");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"The Runtime Pool workload did not complete within '{timeout}'. Expected='{submittedSharedRunIds.Count}', Observed='{lastRuns.Count}'.");
        }

        /// <summary>
        /// Reads the exact submitted SharedRun set even when a store-level list operation is
        /// intentionally bounded. The list provides the efficient common path; only submitted
        /// identifiers absent from that window are resolved individually.
        /// </summary>
        private static async Task<IReadOnlyList<AiSharedRunRecord>>
            ReadExactSubmittedRunsAsync(
                IAiSharedRunStore sharedRunStore,
                IReadOnlySet<string> submittedSharedRunIds,
                string controlPlaneId,
                string tenantId)
        {
            var listedRuns =
                await sharedRunStore
                    .ListAsync(
                        includeCancelled: true,
                        includeCompleted: true,
                        includeFailed: true)
                    .ConfigureAwait(false);

            var exactRunsById =
                new Dictionary<string, AiSharedRunRecord>(
                    StringComparer.Ordinal);

            foreach (var run in listedRuns)
            {
                if (IsExactSubmittedRun(
                        run,
                        submittedSharedRunIds,
                        controlPlaneId,
                        tenantId))
                {
                    exactRunsById[run.SharedRunId] = run;
                }
            }

            var missingSharedRunIds =
                submittedSharedRunIds
                    .Where(
                        sharedRunId =>
                            !exactRunsById.ContainsKey(sharedRunId))
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();

            if (missingSharedRunIds.Length > 0)
            {
                var missingRuns =
                    await Task.WhenAll(
                            missingSharedRunIds.Select(
                                sharedRunId =>
                                    sharedRunStore.GetAsync(sharedRunId)))
                        .ConfigureAwait(false);

                foreach (var run in missingRuns)
                {
                    if (run is not null &&
                        IsExactSubmittedRun(
                            run,
                            submittedSharedRunIds,
                            controlPlaneId,
                            tenantId))
                    {
                        exactRunsById[run.SharedRunId] = run;
                    }
                }
            }

            return exactRunsById
                .Values
                .OrderBy(run => run.SharedRunId, StringComparer.Ordinal)
                .ToArray();
        }

        private static bool IsExactSubmittedRun(
            AiSharedRunRecord run,
            IReadOnlySet<string> submittedSharedRunIds,
            string controlPlaneId,
            string tenantId) =>
            submittedSharedRunIds.Contains(run.SharedRunId) &&
            StringComparer.Ordinal.Equals(
                run.ControlPlaneId,
                controlPlaneId) &&
            StringComparer.Ordinal.Equals(
                run.ExecutionContextSnapshot.TenantId,
                tenantId);
    }
}
