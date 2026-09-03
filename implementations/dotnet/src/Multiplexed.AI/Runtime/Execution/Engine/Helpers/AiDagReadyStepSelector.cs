using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Scheduling;

namespace Multiplexed.AI.Runtime.Execution.Engine.Helpers
{
    /// <summary>
    /// Selects deterministic pre-claim DAG candidates from an already materialized execution state.
    /// </summary>
    /// <remarks>
    /// This selector is intentionally side-effect free. Distributed claim ownership remains enforced
    /// by the atomic DAG store claim transition; this type only avoids reloading the same durable state
    /// when the caller already owns a current materialized snapshot for pre-claim evaluation.
    /// </remarks>
    internal static class AiDagReadyStepSelector
    {
        /// <summary>
        /// Selects ready DAG steps using the canonical pre-claim eligibility rules.
        /// </summary>
        /// <param name="executionId">The durable execution identifier.</param>
        /// <param name="state">The already materialized execution state.</param>
        /// <param name="maxSteps">The maximum number of candidates to return.</param>
        /// <param name="nowUtc">The UTC instant used to evaluate retry windows.</param>
        /// <returns>The deterministic ready-step candidates.</returns>
        public static IReadOnlyList<AiClaimedStep> Select(
            string executionId,
            AiExecutionState state,
            int maxSteps,
            DateTime nowUtc)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentNullException.ThrowIfNull(state);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxSteps, 1);

            if (state.Steps.Count == 0)
            {
                return Array.Empty<AiClaimedStep>();
            }

            var completedSteps = state.Steps
                .Where(step => step.Value.Status == AiStepExecutionStatus.Completed)
                .Select(step => step.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return state.Steps
                .Where(step => IsClaimCandidate(step.Value, nowUtc))
                .Where(step =>
                    step.Value.DependsOn is null ||
                    step.Value.DependsOn.Count == 0 ||
                    step.Value.DependsOn.All(completedSteps.Contains))
                .OrderBy(step => step.Key, StringComparer.OrdinalIgnoreCase)
                .Take(maxSteps)
                .Select(step => new AiClaimedStep
                {
                    ExecutionId = executionId,
                    StepName = step.Key,
                    ClaimToken = string.Empty
                })
                .ToList();
        }

        /// <summary>
        /// Determines whether a step is eligible for pre-claim concurrency evaluation.
        /// </summary>
        private static bool IsClaimCandidate(
            AiStepState step,
            DateTime nowUtc)
        {
            if (step.Status is AiStepExecutionStatus.Ready or AiStepExecutionStatus.None)
            {
                return true;
            }

            if (step.Status != AiStepExecutionStatus.WaitingForRetry)
            {
                return false;
            }

            var retryState = step.RetryState;

            if (retryState is null)
            {
                return false;
            }

            if (!retryState.NextRetryAtUtc.HasValue)
            {
                return true;
            }

            return retryState.NextRetryAtUtc.Value <= nowUtc;
        }
    }
}
