using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
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
                TimeSpan noProgressTimeout)
        {
            ArgumentNullException.ThrowIfNull(sharedRunStore);
            ArgumentNullException.ThrowIfNull(runExecutionIndex);
            ArgumentNullException.ThrowIfNull(dagStore);
            ArgumentNullException.ThrowIfNull(submittedSharedRunIds);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            var lastProgressAtUtc = DateTimeOffset.UtcNow;
            string? lastProgressSignature = null;
            string? lastDurableDagProgressSignature = null;
            var nextDagProbeAtUtc = DateTimeOffset.UtcNow;
            IReadOnlyList<AiSharedRunRecord> lastRuns =
                Array.Empty<AiSharedRunRecord>();

            while (DateTimeOffset.UtcNow < deadline)
            {
                lastRuns =
                    await ReadExactSubmittedRunsAsync(
                            sharedRunStore,
                            submittedSharedRunIds,
                            controlPlaneId,
                            tenantId)
                        .ConfigureAwait(false);

                var indexEntries =
                    new List<AiRuntimeRunExecutionIndexEntry>();

                foreach (var run in lastRuns)
                {
                    if (string.IsNullOrWhiteSpace(run.LocalRunId))
                    {
                        continue;
                    }

                    var entry =
                        await runExecutionIndex
                            .GetAsync(run.LocalRunId)
                            .ConfigureAwait(false);

                    if (entry is not null)
                    {
                        indexEntries.Add(entry);
                    }
                }

                if (lastRuns.Count == submittedSharedRunIds.Count &&
                    indexEntries.Count == submittedSharedRunIds.Count &&
                    indexEntries.All(
                        entry =>
                            string.Equals(
                                entry.Status,
                                "completed",
                                StringComparison.OrdinalIgnoreCase)))
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
                        indexEntries.Count,
                        string.Join(
                            ",",
                            indexEntries
                                .GroupBy(
                                    entry => entry.Status ?? "(none)",
                                    StringComparer.OrdinalIgnoreCase)
                                .OrderBy(
                                    group => group.Key,
                                    StringComparer.OrdinalIgnoreCase)
                                .Select(
                                    group =>
                                        $"{group.Key}:{group.Count()}")));

                var nowUtc = DateTimeOffset.UtcNow;
                var durableDagProgressObserved = false;

                if (nowUtc >= nextDagProbeAtUtc)
                {
                    var runningExecutionIds =
                        indexEntries
                            .Where(
                                entry =>
                                    string.Equals(
                                        entry.Status,
                                        "running",
                                        StringComparison.OrdinalIgnoreCase) &&
                                    !string.IsNullOrWhiteSpace(
                                        entry.ExecutionId))
                            .Select(entry => entry.ExecutionId!)
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(value => value, StringComparer.Ordinal)
                            .ToArray();

                    var durableDagProgressSignature =
                        await ProductionRecoveryWaitHelpers
                            .ReadDurableDagProgressSignatureAsync(
                                dagStore,
                                runningExecutionIds)
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
                    throw new TimeoutException(
                        $"The Runtime Pool workload made no durable progress for '{noProgressTimeout}'. Expected='{submittedSharedRunIds.Count}', Observed='{lastRuns.Count}', Progress='{progressSignature}'.");
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
