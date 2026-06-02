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
    }
}