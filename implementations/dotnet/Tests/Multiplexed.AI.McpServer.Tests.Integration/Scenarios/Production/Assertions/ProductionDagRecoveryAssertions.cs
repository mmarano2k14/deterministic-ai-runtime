using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.State;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Assertions
{
    /// <summary>
    /// Provides reusable assertions for production DAG recovery scenarios.
    /// </summary>
    public static class ProductionDagRecoveryAssertions
    {
        /// <summary>
        /// Asserts that a durable DAG state is stopped at the expected failure point before recovery redispatch.
        /// </summary>
        /// <param name="state">The durable DAG state.</param>
        /// <param name="failureStepNumber">The one-based failure step number.</param>
        /// <param name="stepCount">The expected total number of DAG steps.</param>
        public static void AssertDagStoppedAtFailurePoint(
            AiExecutionState? state,
            int failureStepNumber,
            int stepCount)
        {
            Assert.NotNull(state);

            var ordered =
                state!.Steps.Values
                    .OrderBy(step => step.StepName, StringComparer.Ordinal)
                    .ToArray();

            Assert.Equal(stepCount, ordered.Length);

            for (var index = 0; index < failureStepNumber - 1; index++)
            {
                Assert.Equal(AiStepExecutionStatus.Completed, ordered[index].Status);
                Assert.Equal(0, ordered[index].RecoveryCount);
            }

            var failedStep =
                ordered[failureStepNumber - 1];

            Assert.Equal(AiStepExecutionStatus.Running, failedStep.Status);
            Assert.False(string.IsNullOrWhiteSpace(failedStep.ClaimToken));
            Assert.NotNull(failedStep.LeaseExpiresAtUtc);
            Assert.True(failedStep.LeaseExpiresAtUtc < DateTime.UtcNow);
            Assert.Equal(0, failedStep.RecoveryCount);
        }

        /// <summary>
        /// Asserts that a durable DAG state completed after recovery from the expected failure point.
        /// </summary>
        /// <param name="state">The durable DAG state.</param>
        /// <param name="failureStepNumber">The one-based failure step number.</param>
        /// <param name="stepCount">The expected total number of DAG steps.</param>
        public static void AssertDagCompletedFromFailurePoint(
            AiExecutionState? state,
            int failureStepNumber,
            int stepCount)
        {
            Assert.NotNull(state);

            var ordered =
                state!.Steps.Values
                    .OrderBy(step => step.StepName, StringComparer.Ordinal)
                    .ToArray();

            Assert.Equal(stepCount, ordered.Length);

            Assert.All(
                ordered,
                step => Assert.Equal(AiStepExecutionStatus.Completed, step.Status));

            for (var index = 0; index < failureStepNumber - 1; index++)
            {
                Assert.Equal(0, ordered[index].RecoveryCount);
            }

            Assert.True(
                ordered[failureStepNumber - 1].RecoveryCount >= 1,
                $"Expected failure step '{ordered[failureStepNumber - 1].StepName}' to be recovered before resume.");
        }

        /// <summary>
        /// Formats a compact DAG state summary for failed recovery diagnostics.
        /// </summary>
        /// <param name="state">The durable DAG state.</param>
        /// <returns>The formatted DAG state summary.</returns>
        public static string FormatDagStateSummary(
            AiExecutionState? state)
        {
            if (state is null)
            {
                return "<null>";
            }

            var grouped =
                state.Steps.Values
                    .GroupBy(step => step.Status)
                    .OrderBy(group => group.Key.ToString(), StringComparer.Ordinal)
                    .Select(group => $"{group.Key}={group.Count()}");

            var nonCompleted =
                state.Steps.Values
                    .Where(step => step.Status != AiStepExecutionStatus.Completed)
                    .OrderBy(step => step.StepName, StringComparer.Ordinal)
                    .Take(20)
                    .Select(step =>
                        $"{step.StepName}:{step.Status}:Recovery={step.RecoveryCount}:ClaimedBy={step.ClaimedBy ?? string.Empty}:Lease={step.LeaseExpiresAtUtc?.ToString("O") ?? string.Empty}:Error={step.Error ?? string.Empty}");

            return
                $"ExecutionId='{state.ExecutionId}', PipelineName='{state.PipelineName}', " +
                $"Counts='{string.Join(",", grouped)}', " +
                $"NonCompleted='{string.Join(" | ", nonCompleted)}'";
        }
    }
}