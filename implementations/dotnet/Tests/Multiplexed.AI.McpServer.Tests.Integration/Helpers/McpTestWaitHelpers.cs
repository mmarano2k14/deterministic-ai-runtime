using ModelContextProtocol.Protocol;
using Multiplexed.Abstractions.AI.ControlPlane.Execution;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;

namespace Multiplexed.AI.McpServer.Tests.Integration.Helpers
{
    /// <summary>
    /// Provides reusable asynchronous wait helpers for MCP integration tests.
    /// </summary>
    public static class McpTestWaitHelpers
    {
        /// <param name="mcp">The MCP test client.</param>
        /// <param name="pipelineKey">The pipeline key.</param>
        /// <param name="expectedCount">The expected run count.</param>
        /// <param name="timeout">The timeout.</param>
        /// <returns>The dispatched runs.</returns>
        public static async Task<IReadOnlyList<AiSharedRunRecord>> WaitForDispatchedRunsAsync(
            McpTestClient mcp,
            string pipelineKey,
            int expectedCount,
            TimeSpan timeout)
        {
            return await WaitForDispatchedRunsAsync(
                    mcp,
                    pipelineKey,
                    expectedSharedRunIds: null,
                    expectedCount,
                    timeout)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Waits until the expected number of specific shared runs are dispatched.
        /// </summary>
        /// <param name="mcp">The MCP test client.</param>
        /// <param name="pipelineKey">The pipeline key.</param>
        /// <param name="expectedSharedRunIds">The expected shared run ids to track. When null, all runs matching the pipeline are considered.</param>
        /// <param name="expectedCount">The expected run count.</param>
        /// <param name="timeout">The timeout.</param>
        /// <returns>The dispatched runs.</returns>
        public static async Task<IReadOnlyList<AiSharedRunRecord>> WaitForDispatchedRunsAsync(
            McpTestClient mcp,
            string pipelineKey,
            IReadOnlySet<string>? expectedSharedRunIds,
            int expectedCount,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(mcp);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineKey);

            if (expectedCount <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(expectedCount),
                    expectedCount,
                    "Expected count must be greater than zero.");
            }

            if (expectedSharedRunIds is not null &&
                expectedSharedRunIds.Count != expectedCount)
            {
                throw new InvalidOperationException(
                    $"ExpectedSharedRunIds count mismatch. ExpectedCount='{expectedCount}', ActualIds='{expectedSharedRunIds.Count}'.");
            }

            var startedAtUtc = DateTimeOffset.UtcNow;
            var deadlineUtc = startedAtUtc.Add(timeout);
            var lastRuns = Array.Empty<AiSharedRunRecord>();
            var strictIdFiltering =
                expectedSharedRunIds is not null;

            while (DateTimeOffset.UtcNow < deadlineUtc)
            {
                var runs = await mcp
                    .ListSharedRunsAsync(
                        new AiSharedRuntimeControllerRequest
                        {
                            Operation = AiSharedRuntimeControllerOperation.ListRuns,
                            PipelineKey = pipelineKey,
                            TenantId = null,
                            IncludeCompleted = true,
                            IncludeFailed = true,
                            IncludeCancelled = true,
                            IncludeDiagnostics = true
                        })
                    .ConfigureAwait(false);

                lastRuns = expectedSharedRunIds is null
                    ? runs.Runs.ToArray()
                    : runs.Runs
                        .Where(run => expectedSharedRunIds.Contains(run.SharedRunId))
                        .ToArray();

                var missingRunIds = expectedSharedRunIds is null
                    ? Array.Empty<string>()
                    : expectedSharedRunIds
                        .Except(
                            lastRuns.Select(run => run.SharedRunId),
                            StringComparer.Ordinal)
                        .ToArray();

                var queuedRuns = FilterByStatus(lastRuns, "Queued");
                var claimedRuns = FilterByStatus(lastRuns, "Claimed");
                var dispatchingRuns = FilterByStatus(lastRuns, "Dispatching");
                var dispatchedRuns = FilterByStatus(lastRuns, "Dispatched");
                var runningRuns = FilterByStatus(lastRuns, "Running");
                var completedRuns = FilterByStatus(lastRuns, "Completed");
                var failedRuns = FilterByStatus(lastRuns, "Failed");
                var cancelledRuns = FilterByStatus(lastRuns, "Cancelled");

                var acceptedRuns = lastRuns
                    .Where(IsDispatchedOrBeyond)
                    .ToArray();

                var knownCount =
                    queuedRuns.Length +
                    claimedRuns.Length +
                    dispatchingRuns.Length +
                    dispatchedRuns.Length +
                    runningRuns.Length +
                    completedRuns.Length +
                    failedRuns.Length +
                    cancelledRuns.Length;

                var otherCount =
                    lastRuns.Length - knownCount;

                Console.WriteLine(
                    "[WAIT DISPATCHED RUNS] PipelineKey='{0}' Expected='{1}' FilteredByIds='{2}' Total='{3}' Accepted='{4}' Missing='{5}' Queued='{6}' Claimed='{7}' Dispatching='{8}' Dispatched='{9}' Running='{10}' Completed='{11}' Failed='{12}' Cancelled='{13}' Other='{14}' ElapsedMs='{15}'",
                    pipelineKey,
                    expectedCount,
                    strictIdFiltering,
                    lastRuns.Length,
                    acceptedRuns.Length,
                    missingRunIds.Length,
                    queuedRuns.Length,
                    claimedRuns.Length,
                    dispatchingRuns.Length,
                    dispatchedRuns.Length,
                    runningRuns.Length,
                    completedRuns.Length,
                    failedRuns.Length,
                    cancelledRuns.Length,
                    otherCount,
                    (long)(DateTimeOffset.UtcNow - startedAtUtc).TotalMilliseconds);

                if (strictIdFiltering &&
                    (failedRuns.Length > 0 || cancelledRuns.Length > 0))
                {
                    throw new InvalidOperationException(
                        "One or more expected shared runs reached a failed/cancelled state." +
                        Environment.NewLine +
                        BuildSharedRunDebugDump(lastRuns));
                }

                if (strictIdFiltering)
                {
                    if (acceptedRuns.Length == expectedCount &&
                        missingRunIds.Length == 0)
                    {
                        return acceptedRuns
                            .OrderBy(run => run.SharedRunId, StringComparer.Ordinal)
                            .ToArray();
                    }
                }
                else if (acceptedRuns.Length >= expectedCount)
                {
                    return acceptedRuns
                        .Take(expectedCount)
                        .ToArray();
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(500))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"Expected '{expectedCount}' dispatched/running/completed runs for pipeline '{pipelineKey}' within '{timeout}'. " +
                $"FilteredByIds='{strictIdFiltering}'. LastTotal='{lastRuns.Length}'." +
                Environment.NewLine +
                BuildSharedRunDebugDump(lastRuns));
        }

        private static bool IsDispatchedOrBeyond(
            AiSharedRunRecord run)
        {
            var status =
                run.Status.ToString();

            return string.Equals(status, "Dispatched", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase);
        }

        private static AiSharedRunRecord[] FilterByStatus(
            IReadOnlyCollection<AiSharedRunRecord> runs,
            string status)
        {
            return runs
                .Where(run => string.Equals(
                    run.Status.ToString(),
                    status,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        private static string BuildStatusBreakdown(
            IReadOnlyCollection<AiSharedRunRecord> runs)
        {
            return string.Join(
                ", ",
                runs
                    .GroupBy(run => run.Status.ToString())
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => $"{group.Key}={group.Count()}"));
        }

        private static string BuildSharedRunDebugDump(
            IReadOnlyCollection<AiSharedRunRecord> runs)
        {
            var sampleRuns = string.Join(
                Environment.NewLine,
                runs
                    .OrderBy(run => run.SharedRunId, StringComparer.Ordinal)
                    .Take(50)
                    .Select(run =>
                        $"SharedRunId={run.SharedRunId}, Status={run.Status}, AssignedRuntimeInstanceId={run.AssignedRuntimeInstanceId}, LocalRunId={run.LocalRunId}, ExecutionId={run.ExecutionId}, FailureReason={run.FailureReason}"));

            return
                $"StatusBreakdown='{BuildStatusBreakdown(runs)}'." +
                Environment.NewLine +
                sampleRuns;
        }

        public static async Task<IReadOnlyList<AiRuntimeQueueControlPlaneResult>> WaitForTerminalRuntimeRunStatusesAsync(
    McpTestClient mcp,
    IReadOnlyList<AiSharedRunRecord> dispatchedRuns,
    TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(mcp);
            ArgumentNullException.ThrowIfNull(dispatchedRuns);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            IReadOnlyList<AiRuntimeQueueControlPlaneResult> lastStatuses =
                Array.Empty<AiRuntimeQueueControlPlaneResult>();

            var attempt =
                0;

            while (DateTimeOffset.UtcNow < deadline)
            {
                attempt++;

                var statuses =
                    new List<AiRuntimeQueueControlPlaneResult>();

                foreach (var run in dispatchedRuns)
                {
                    if (string.IsNullOrWhiteSpace(run.AssignedRuntimeInstanceId) ||
                        string.IsNullOrWhiteSpace(run.LocalRunId))
                    {
                        statuses.Add(
                            new AiRuntimeQueueControlPlaneResult
                            {
                                Operation = AiRuntimeQueueControlPlaneOperation.GetRunStatus,
                                Success = false,
                                RuntimeInstanceId = run.AssignedRuntimeInstanceId ?? string.Empty,
                                RunId = run.LocalRunId ?? string.Empty,
                                ExecutionId = run.ExecutionId,
                                FailureReason = "shared-run-runtime-binding-missing",
                                Message = "Shared run does not expose both AssignedRuntimeInstanceId and LocalRunId."
                            });

                        continue;
                    }

                    var status =
                        await mcp.GetRuntimeQueueRunStatusAsync(
                                new AiRuntimeQueueControlPlaneRequest
                                {
                                    Operation = AiRuntimeQueueControlPlaneOperation.GetRunStatus,
                                    RuntimeInstanceId = run.AssignedRuntimeInstanceId,
                                    RunId = run.LocalRunId,
                                    IncludeRunState = true,
                                    IncludeDiagnostics = true,
                                    RequestedBy = "mcp-integration-test",
                                    Source = "mcp-test"
                                })
                            .ConfigureAwait(false);

                    statuses.Add(status);
                }

                lastStatuses =
                    statuses;

                var terminalCount =
                    statuses.Count(IsTerminal);

                var successfulStatusCount =
                    statuses.Count(status => status.Success);

                var nullRunStateCount =
                    statuses.Count(status => status.RunState is null);

                var statusBreakdown =
                    BuildRuntimeQueueStatusBreakdown(statuses);

                Console.WriteLine(
                    "[WAIT TERMINAL RUNTIME STATUSES] Attempt='{0}' Expected='{1}' Terminal='{2}' Success='{3}' NullRunState='{4}' StatusBreakdown='{5}' ElapsedRemainingMs='{6}' Details='{7}'",
                    attempt,
                    dispatchedRuns.Count,
                    terminalCount,
                    successfulStatusCount,
                    nullRunStateCount,
                    statusBreakdown,
                    Math.Max(0, (long)(deadline - DateTimeOffset.UtcNow).TotalMilliseconds),
                    FormatRuntimeQueueStatuses(statuses));

                if (statuses.All(IsTerminal))
                {
                    return statuses;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                "Expected all runtime runs to reach terminal status within timeout. " +
                $"ExpectedCount='{dispatchedRuns.Count}', " +
                $"LastStatusBreakdown='{BuildRuntimeQueueStatusBreakdown(lastStatuses)}', " +
                $"LastStatuses='{FormatRuntimeQueueStatuses(lastStatuses)}', " +
                $"DispatchedRuns='{FormatSharedRunsForRuntimeStatusWait(dispatchedRuns)}'.");
        }

        public static async Task<AiRuntimeQueueControlPlaneResult> WaitForRuntimeRunExecutionIdAsync(
            McpTestClient mcp,
            AiSharedRunRecord run,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(mcp);
            ArgumentNullException.ThrowIfNull(run);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            AiRuntimeQueueControlPlaneResult? lastStatus = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                var status =
                    await mcp.GetRuntimeQueueRunStatusAsync(
                        new AiRuntimeQueueControlPlaneRequest
                        {
                            Operation = AiRuntimeQueueControlPlaneOperation.GetRunStatus,
                            RuntimeInstanceId = run.AssignedRuntimeInstanceId,
                            RunId = run.LocalRunId,
                            RequestedBy = "mcp-integration-test",
                            Source = "mcp-test"
                        });

                lastStatus = status;

                var executionId =
                    status.ExecutionId ??
                    status.RunState?.ExecutionId;

                if (!string.IsNullOrWhiteSpace(executionId))
                {
                    return status;
                }

                await Task.Delay(100);
            }

            throw new TimeoutException(
                $"Expected run '{run.LocalRunId}' to expose an ExecutionId within '{timeout}'. " +
                $"LastStatus='{lastStatus?.RunState?.Status}'.");
        }

        public static async Task<AiExecutionControlPlaneResult> WaitForExecutionControlStatusAsync(
            McpTestClient mcp,
            string executionId,
            TimeSpan timeout,
            IReadOnlyCollection<string> expectedStatuses)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentNullException.ThrowIfNull(expectedStatuses);

            var expected =
                new HashSet<string>(
                    expectedStatuses,
                    StringComparer.OrdinalIgnoreCase);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            AiExecutionControlPlaneResult? lastStatus = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                lastStatus =
                    await mcp.GetExecutionStatusAsync(
                        new AiExecutionControlPlaneRequest
                        {
                            Operation = AiExecutionControlPlaneOperation.GetStatus,
                            ExecutionId = executionId,
                            RequestedBy = "RequestedBy",
                            Source = "Source"
                        });

                Assert.True(
                    lastStatus.Success,
                    lastStatus.FailureReason ?? lastStatus.Message);

                var controlStatus =
                    Convert.ToString(
                        lastStatus.State?.Status);

                if (!string.IsNullOrWhiteSpace(controlStatus) &&
                    expected.Contains(controlStatus))
                {
                    return lastStatus;
                }

                await Task.Delay(
                    TimeSpan.FromMilliseconds(100));
            }

            var lastControlStatus =
                lastStatus is null
                    ? "<none>"
                    : Convert.ToString(lastStatus.State?.Status) ?? "<null>";

            Assert.Fail(
                $"Execution '{executionId}' did not reach expected control status '{string.Join(", ", expectedStatuses)}' within '{timeout}'. LastControlStatus='{lastControlStatus}'.");

            throw new InvalidOperationException(
                "Unreachable assertion path.");
        }

        public static async Task<AiRuntimeQueueControlPlaneResult> WaitForRuntimeRunStatusAsync(
            McpTestClient mcp,
            AiSharedRunRecord run,
            string expectedStatus,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(mcp);
            ArgumentNullException.ThrowIfNull(run);
            ArgumentException.ThrowIfNullOrWhiteSpace(expectedStatus);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            AiRuntimeQueueControlPlaneResult? lastStatus = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                var status =
                    await mcp.GetRuntimeQueueRunStatusAsync(
                        new AiRuntimeQueueControlPlaneRequest
                        {
                            Operation = AiRuntimeQueueControlPlaneOperation.GetRunStatus,
                            RuntimeInstanceId = run.AssignedRuntimeInstanceId,
                            RunId = run.LocalRunId,
                            RequestedBy = "mcp-integration-test",
                            Source = "mcp-test"
                        });

                lastStatus = status;

                if (string.Equals(
                        status.RunState?.Status,
                        expectedStatus,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return status;
                }

                await Task.Delay(100);
            }

            throw new TimeoutException(
                $"Expected run '{run.LocalRunId}' to reach status '{expectedStatus}' within '{timeout}'. " +
                $"LastStatus='{lastStatus?.RunState?.Status}', " +
                $"ExecutionId='{lastStatus?.ExecutionId ?? lastStatus?.RunState?.ExecutionId}'.");
        }

        private static bool IsTerminal(
            AiRuntimeQueueControlPlaneResult result)
        {
            var status = result.RunState?.Status;

            return string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Waits until the specified runtime instance reports full local worker saturation.
        /// </summary>
        /// <param name="mcp">The MCP test client.</param>
        /// <param name="runtimeInstanceId">The expected runtime instance id.</param>
        /// <param name="expectedWorkerCount">The expected total worker count.</param>
        /// <param name="expectedMaxLocalWorkersPerExecution">The expected maximum local workers per execution.</param>
        /// <param name="timeout">The maximum wait duration.</param>
        /// <returns>The saturated runtime instance snapshot.</returns>
        /// <summary>
        /// Waits until the specified runtime instance reports full local worker saturation.
        /// </summary>
        public static async Task<AiRuntimeInstanceSnapshot> WaitForRuntimeInstanceWorkerSaturationAsync(
            McpTestClient mcp,
            string runtimeInstanceId,
            int expectedWorkerCount,
            int expectedMaxLocalWorkersPerExecution,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(mcp);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            AiRuntimeInstanceSnapshot? lastInstance = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                var instances =
                    await mcp.ListRuntimeInstancesAsync(
                            includeStopped: true)
                        .ConfigureAwait(false);

                var instance =
                    instances.FirstOrDefault(item =>
                        string.Equals(
                            item.RuntimeInstanceId,
                            runtimeInstanceId,
                            StringComparison.Ordinal));

                if (instance is not null)
                {
                    lastInstance = instance;

                    if (instance.WorkerCount == expectedWorkerCount &&
                        instance.ActiveWorkerCount == expectedWorkerCount &&
                        instance.AvailableWorkerCount == 0 &&
                        instance.MaxLocalWorkersPerExecution == expectedMaxLocalWorkersPerExecution &&
                        !instance.CanAcceptRun)
                    {
                        return instance;
                    }
                }

                await Task.Delay(
                        TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"Runtime instance '{runtimeInstanceId}' did not report worker saturation within '{timeout}'. " +
                $"LastWorkerCount='{lastInstance?.WorkerCount}', " +
                $"LastActiveWorkerCount='{lastInstance?.ActiveWorkerCount}', " +
                $"LastAvailableWorkerCount='{lastInstance?.AvailableWorkerCount}', " +
                $"LastMaxLocalWorkersPerExecution='{lastInstance?.MaxLocalWorkersPerExecution}', " +
                $"LastCanAcceptRun='{lastInstance?.CanAcceptRun}'.");
        }


        /// <summary>
        /// Waits until a shared run is assigned to the expected runtime instance.
        /// </summary>
        /// <param name="sharedRunStore">The shared run store.</param>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <param name="expectedRuntimeInstanceId">The expected runtime instance identifier.</param>
        /// <param name="timeout">The maximum wait duration.</param>
        /// <returns>The shared run record assigned to the expected runtime instance.</returns>
        /// <exception cref="TimeoutException">Thrown when the shared run is not assigned to the expected runtime instance in time.</exception>
        public static async Task<AiSharedRunRecord> WaitForSharedRunAssignedToRuntimeAsync(
            IAiSharedRunStore sharedRunStore,
            string sharedRunId,
            string expectedRuntimeInstanceId,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(sharedRunStore);
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(expectedRuntimeInstanceId);

            var startedAt =
                DateTimeOffset.UtcNow;

            AiSharedRunRecord? lastRun = null;

            while (DateTimeOffset.UtcNow - startedAt < timeout)
            {
                lastRun =
                    await sharedRunStore
                        .GetAsync(sharedRunId)
                        .ConfigureAwait(false);

                if (lastRun is not null &&
                    string.Equals(
                        lastRun.AssignedRuntimeInstanceId,
                        expectedRuntimeInstanceId,
                        StringComparison.Ordinal))
                {
                    return lastRun;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"Shared run '{sharedRunId}' was not assigned to runtime instance '{expectedRuntimeInstanceId}' within '{timeout}'. " +
                $"LastStatus='{lastRun?.Status.ToString() ?? "<null>"}', " +
                $"LastAssignedRuntimeInstanceId='{lastRun?.AssignedRuntimeInstanceId ?? "<null>"}'.");
        }

        private static string BuildRuntimeQueueStatusBreakdown(
            IReadOnlyCollection<AiRuntimeQueueControlPlaneResult> statuses)
        {
            if (statuses.Count == 0)
            {
                return "<none>";
            }

            return string.Join(
                ",",
                statuses
                    .GroupBy(status => status.RunState?.Status ?? "<null>", StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(group => $"{group.Key}:{group.Count()}"));
        }

        private static string FormatRuntimeQueueStatuses(
            IReadOnlyCollection<AiRuntimeQueueControlPlaneResult> statuses)
        {
            if (statuses.Count == 0)
            {
                return "<none>";
            }

            return string.Join(
                " | ",
                statuses.Select(status =>
                    $"Runtime='{status.RuntimeInstanceId}', " +
                    $"Run='{status.RunId}', " +
                    $"Execution='{status.ExecutionId ?? status.RunState?.ExecutionId}', " +
                    $"Success='{status.Success}', " +
                    $"Status='{status.RunState?.Status ?? "<null>"}', " +
                    $"RunStateFailureReason='{status.RunState?.FailureReason}', " +
                    $"ControlPlaneFailureReason='{status.FailureReason}', " +
                    $"Message='{status.Message}', " +
                    $"Diagnostics='{FormatDiagnostics(status.Diagnostics)}'"));
        }

        private static string FormatSharedRunsForRuntimeStatusWait(
            IReadOnlyCollection<AiSharedRunRecord> runs)
        {
            if (runs.Count == 0)
            {
                return "<none>";
            }

            return string.Join(
                " | ",
                runs.Select(run =>
                    $"SharedRun='{run.SharedRunId}', " +
                    $"Runtime='{run.AssignedRuntimeInstanceId}', " +
                    $"LocalRun='{run.LocalRunId}', " +
                    $"Execution='{run.ExecutionId}', " +
                    $"Status='{run.Status}', " +
                    $"FailureReason='{run.FailureReason}'"));
        }

        private static string FormatDiagnostics(
            IReadOnlyCollection<string>? diagnostics)
        {
            if (diagnostics is null ||
                diagnostics.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(
                ";",
                diagnostics.Take(10));
        }
    }
}