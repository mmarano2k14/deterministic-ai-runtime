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

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            while (DateTimeOffset.UtcNow < deadline)
            {
                var listResult =
                    await mcp.ListSharedRunsAsync(
                        new AiSharedRuntimeControllerRequest
                        {
                            Operation = AiSharedRuntimeControllerOperation.ListRuns,
                            IncludeCompleted = true,
                            IncludeFailed = true,
                            IncludeCancelled = true,
                            RequestedBy = "mcp-integration-test",
                            Source = "mcp-test"
                        });

                var runs =
                    listResult.Runs
                        .Where(run =>
                            string.Equals(
                                run.PipelineKey,
                                pipelineKey,
                                StringComparison.Ordinal))
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
    }
}