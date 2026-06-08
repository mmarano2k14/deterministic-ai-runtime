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
        /// <summary>
        /// Waits until the expected number of runs are dispatched.
        /// </summary>
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
            ArgumentNullException.ThrowIfNull(mcp);

            var deadline = DateTimeOffset.UtcNow.Add(timeout);

            while (DateTimeOffset.UtcNow < deadline)
            {
                var listResult = await mcp.ListSharedRunsAsync(
                    new AiSharedRuntimeControllerRequest
                    {
                        Operation = AiSharedRuntimeControllerOperation.ListRuns,
                        IncludeCompleted = true,
                        IncludeFailed = true,
                        IncludeCancelled = true,
                        RequestedBy = "mcp-integration-test",
                        Source = "mcp-test"
                    });

                var runs = listResult.Runs
                    .Where(run => string.Equals(run.PipelineKey, pipelineKey, StringComparison.Ordinal))
                    .ToArray();

                if (runs.Length == expectedCount &&
                    runs.All(run =>
                        !string.IsNullOrWhiteSpace(run.LocalRunId) &&
                        !string.IsNullOrWhiteSpace(run.AssignedRuntimeInstanceId)))
                {
                    return runs;
                }

                await Task.Delay(100);
            }

            throw new TimeoutException(
                $"Expected '{expectedCount}' dispatched runs for pipeline '{pipelineKey}' within '{timeout}'.");
        }

        public static async Task<IReadOnlyList<AiRuntimeQueueControlPlaneResult>> WaitForTerminalRuntimeRunStatusesAsync(
            McpTestClient mcp,
            IReadOnlyList<AiSharedRunRecord> dispatchedRuns,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(mcp);
            ArgumentNullException.ThrowIfNull(dispatchedRuns);

            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            IReadOnlyList<AiRuntimeQueueControlPlaneResult> lastStatuses = Array.Empty<AiRuntimeQueueControlPlaneResult>();

            while (DateTimeOffset.UtcNow < deadline)
            {
                var statuses = new List<AiRuntimeQueueControlPlaneResult>();

                foreach (var run in dispatchedRuns)
                {
                    var status = await mcp.GetRuntimeQueueRunStatusAsync(
                        new AiRuntimeQueueControlPlaneRequest
                        {
                            Operation = AiRuntimeQueueControlPlaneOperation.GetRunStatus,
                            RuntimeInstanceId = run.AssignedRuntimeInstanceId,
                            RunId = run.LocalRunId,
                            IncludeRunState = true,
                            IncludeDiagnostics = true,
                            RequestedBy = "mcp-integration-test",
                            Source = "mcp-test"
                        });

                    statuses.Add(status);
                }

                lastStatuses = statuses;

                if (statuses.All(IsTerminal))
                {
                    return statuses;
                }

                await Task.Delay(250);
            }

            throw new TimeoutException(
                "Expected all runtime runs to reach terminal status within timeout. " +
                $"LastStatuses={string.Join(", ", lastStatuses.Select(x => $"{x.RunId}:{x.RunState?.Status ?? "null"}"))}");
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
    }
}