using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Persistence.Replay;
using Multiplexed.Abstractions.AI.Execution.Persistence.Replay.Validators;

namespace Multiplexed.AI.Runtime.Execution.Persistence.Replay.Validators
{
    /// <summary>
    /// Default structural validator for replay step state consistency.
    /// </summary>
    public sealed class DefaultAiExecutionReplayStepStateValidator : IAiExecutionReplayStepStateValidator
    {
        /// <inheritdoc />
        public Task<AiExecutionReplayStepStateValidationResult> ValidateAsync(
            AiExecutionState state,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(state);

            var issues = new List<AiExecutionReplayIssue>();

            foreach (var step in state.Steps.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                ValidateStep(
                    step.Key,
                    step.Value,
                    issues);
            }

            return Task.FromResult(new AiExecutionReplayStepStateValidationResult
            {
                IsValid = issues.Count == 0,
                Issues = issues
            });
        }

        /// <summary>
        /// Validates a single replay step state.
        /// </summary>
        private static void ValidateStep(
            string stepKey,
            AiStepState step,
            ICollection<AiExecutionReplayIssue> issues)
        {
            if (string.IsNullOrWhiteSpace(stepKey))
            {
                issues.Add(new AiExecutionReplayIssue
                {
                    Code = "replay.step.key.missing",
                    Message = "Replay step key is missing."
                });
            }

            if (string.IsNullOrWhiteSpace(step.StepName))
            {
                issues.Add(new AiExecutionReplayIssue
                {
                    Code = "replay.step.name.missing",
                    StepKey = stepKey,
                    Message = "Replay step name is missing."
                });
            }

            if (step.Status == AiStepExecutionStatus.Running &&
                string.IsNullOrWhiteSpace(step.ClaimToken))
            {
                issues.Add(new AiExecutionReplayIssue
                {
                    Code = "replay.step.running.claim_token_missing",
                    StepKey = stepKey,
                    Message = "Running replay step is missing a claim token."
                });
            }

            if (step.Status == AiStepExecutionStatus.WaitingForRetry &&
                step.RetryState is null)
            {
                issues.Add(new AiExecutionReplayIssue
                {
                    Code = "replay.step.retry_state.missing",
                    StepKey = stepKey,
                    Message = "Step is waiting for retry but retry state is missing."
                });
            }

            if (step.RetryState?.RetryCount > step.Retry?.MaxRetries)
            {
                issues.Add(new AiExecutionReplayIssue
                {
                    Code = "replay.step.retry_budget.exceeded",
                    StepKey = stepKey,
                    Message = $"Step retry count exceeds max retries. RetryCount={step.RetryState.RetryCount}, MaxRetries={step.Retry?.MaxRetries}."
                });
            }

            if (step.RecoveryCount < 0)
            {
                issues.Add(new AiExecutionReplayIssue
                {
                    Code = "replay.step.recovery_count.invalid",
                    StepKey = stepKey,
                    Message = "Step recovery count cannot be negative."
                });
            }

            if (step.Status == AiStepExecutionStatus.Completed &&
                step.Result is null &&
                !step.IsEvictedFromHotState &&
                !step.IsCompacted)
            {
                issues.Add(new AiExecutionReplayIssue
                {
                    Code = "replay.step.completed.result_missing",
                    StepKey = stepKey,
                    Message =
                        $"Completed step has no result and is not marked as evicted or compacted. " +
                        $"StepKey='{stepKey}', " +
                        $"StepName='{step.StepName}', " +
                        $"IsEvictedFromHotState='{step.IsEvictedFromHotState}', " +
                        $"IsCompacted='{step.IsCompacted}', " +
                        $"ArchivePayloadId='{step.ArchivePayloadId ?? string.Empty}', " +
                        $"CompactionReason='{step.CompactionReason ?? string.Empty}', " +
                        $"RetentionReason='{ResolveOptionalStringProperty(step, "RetentionReason")}', " +
                        $"InlinePayloadSizeBytes='{step.InlinePayloadSizeBytes}'."
                });
            }

            if (step.IsCompacted &&
                string.IsNullOrWhiteSpace(step.ArchivePayloadId))
            {
                issues.Add(new AiExecutionReplayIssue
                {
                    Code = "replay.step.compacted.archive_missing",
                    StepKey = stepKey,
                    Message = "Compacted step does not reference an archived payload artifact."
                });
            }
        }

        /// <summary>
        /// Resolves an optional string property by reflection for replay diagnostics without requiring
        /// every retention metadata field to exist on older step-state contracts.
        /// </summary>
        /// <param name="instance">The object instance.</param>
        /// <param name="propertyName">The property name.</param>
        /// <returns>The string value when available; otherwise, an empty string.</returns>
        private static string ResolveOptionalStringProperty(
            object instance,
            string propertyName)
        {
            var property = instance
                .GetType()
                .GetProperty(propertyName);

            return property?.GetValue(instance)?.ToString()
                ?? string.Empty;
        }
    }
}