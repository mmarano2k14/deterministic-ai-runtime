using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Persistence.Replay;
using Multiplexed.Abstractions.AI.Execution.Persistence.Replay.Metadata;
using Multiplexed.Abstractions.AI.Execution.Persistence.Replay.Models;
using Multiplexed.Abstractions.AI.Execution.Persistence.Replay.Reports;
using Multiplexed.Abstractions.AI.Execution.Persistence.Replay.Validators;
using Multiplexed.AI.Runtime.Execution.Persistence.Replay.Fingerprint;

namespace Multiplexed.AI.Runtime.Execution.Persistence.Replay.Validators
{
    /// <summary>
    /// Validates replay determinism by comparing reconstructed execution fingerprints,
    /// validating payload references, validating step state consistency, and validating
    /// dependency graph integrity.
    /// </summary>
    public sealed class DefaultAiExecutionReplayValidator : IAiExecutionReplayValidator
    {
        private readonly IAiExecutionReplayMetadataStore _metadataStore;
        private readonly IAiExecutionReplayFingerprintBuilder _fingerprintBuilder;
        private readonly IAiExecutionReplayPayloadValidator _payloadValidator;
        private readonly IAiExecutionReplayStepStateValidator _stepStateValidator;
        private readonly IAiExecutionReplayDependencyGraphValidator _dependencyGraphValidator;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultAiExecutionReplayValidator"/> class.
        /// </summary>
        public DefaultAiExecutionReplayValidator(
            IAiExecutionReplayMetadataStore metadataStore,
            IAiExecutionReplayFingerprintBuilder fingerprintBuilder,
            IAiExecutionReplayPayloadValidator payloadValidator,
            IAiExecutionReplayStepStateValidator stepStateValidator,
            IAiExecutionReplayDependencyGraphValidator dependencyGraphValidator)
        {
            _metadataStore = metadataStore
                ?? throw new ArgumentNullException(nameof(metadataStore));

            _fingerprintBuilder = fingerprintBuilder
                ?? throw new ArgumentNullException(nameof(fingerprintBuilder));

            _payloadValidator = payloadValidator
                ?? throw new ArgumentNullException(nameof(payloadValidator));

            _stepStateValidator = stepStateValidator
                ?? throw new ArgumentNullException(nameof(stepStateValidator));

            _dependencyGraphValidator = dependencyGraphValidator
                ?? throw new ArgumentNullException(nameof(dependencyGraphValidator));
        }

        /// <inheritdoc />
        public async Task<AiExecutionReplayReport> ValidateAsync(
            AiExecutionReplayRequest request,
            AiExecutionRecord record,
            AiExecutionState state,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(record);
            ArgumentNullException.ThrowIfNull(state);

            var metadata = await _metadataStore.GetAsync(
                    record.ExecutionId,
                    cancellationToken)
                .ConfigureAwait(false);

            var reconstructedFingerprint = _fingerprintBuilder.Build(
                record,
                state);

            var fingerprintFound =
                !string.IsNullOrWhiteSpace(metadata?.Fingerprint);

            var fingerprintMatches =
                fingerprintFound &&
                string.Equals(
                    metadata!.Fingerprint,
                    reconstructedFingerprint,
                    StringComparison.Ordinal);

            var issues = new List<AiExecutionReplayIssue>();

            if (!fingerprintFound)
            {
                issues.Add(new AiExecutionReplayIssue
                {
                    Code = "replay.fingerprint.missing",
                    Message = "Replay fingerprint metadata was not found."
                });
            }
            else if (!fingerprintMatches)
            {
                issues.Add(new AiExecutionReplayIssue
                {
                    Code = "replay.fingerprint.mismatch",
                    Message = "Replay fingerprint does not match the persisted terminal fingerprint."
                });
            }

            var payloadValidation = request.ValidatePayloadReferences
                ? await _payloadValidator.ValidateAsync(
                        state,
                        cancellationToken)
                    .ConfigureAwait(false)
                : new AiExecutionReplayPayloadValidationResult
                {
                    IsValid = true
                };

            issues.AddRange(payloadValidation.Issues);

            var stepStateValidation = await _stepStateValidator.ValidateAsync(
                    state,
                    cancellationToken)
                .ConfigureAwait(false);

            issues.AddRange(stepStateValidation.Issues);

            var dependencyGraphValidation =
                await _dependencyGraphValidator.ValidateAsync(
                        state,
                        cancellationToken)
                    .ConfigureAwait(false);

            issues.AddRange(dependencyGraphValidation.Issues);

            var totalSteps =
                state.Steps.Count;

            var completedSteps =
                state.Steps.Values.Count(x => x.IsCompleted);

            var failedSteps =
                state.Steps.Values.Count(x => x.Status == AiStepExecutionStatus.Failed);

            var waitingForRetrySteps =
                state.Steps.Values.Count(x => x.Status == AiStepExecutionStatus.WaitingForRetry);

            var runningSteps =
                state.Steps.Values.Count(x => x.Status == AiStepExecutionStatus.Running);

            var retryCount =
                state.Steps.Values.Sum(x => x.RetryState?.RetryCount ?? 0);

            var recoveryCount =
                state.Steps.Values.Sum(x => x.RecoveryCount);

            var steps = request.IncludeStepDetails
                ? state.Steps
                    .OrderBy(x => x.Key, StringComparer.Ordinal)
                    .Select(x => new AiExecutionReplayStepReport
                    {
                        StepKey = x.Key,
                        Status = x.Value.Status.ToString(),
                        HasResult = x.Value.Result is not null,
                        IsExternalized =
                            x.Value.Result?.DataPayloads?.Values.Any(
                                p => !p.IsInline) == true,
                        PayloadReferenceValid = payloadValidation.IsValid,
                        RetryCount = x.Value.RetryState?.RetryCount ?? 0,
                        RecoveryCount = x.Value.RecoveryCount
                    })
                    .ToArray()
                : Array.Empty<AiExecutionReplayStepReport>();

            var replayValid =
                fingerprintMatches &&
                payloadValidation.IsValid &&
                stepStateValidation.IsValid &&
                dependencyGraphValidation.IsValid;

            return new AiExecutionReplayReport
            {
                ExecutionId = record.ExecutionId,
                Mode = request.Mode,
                ExecutionFound = true,
                SnapshotFound = true,

                FingerprintFound = fingerprintFound,
                OriginalFingerprint = metadata?.Fingerprint,
                ReconstructedFingerprint = reconstructedFingerprint,
                FingerprintMatches = fingerprintMatches,

                ReplayMetadata = metadata,

                DependencyGraphValid = dependencyGraphValidation.IsValid,
                StepStateValid = stepStateValidation.IsValid,
                PayloadReferencesValid = payloadValidation.IsValid,

                ReplayValid = replayValid,

                PipelineName = record.PipelineName,
                Status = record.Status.ToString(),

                TotalSteps = totalSteps,
                CompletedSteps = completedSteps,
                FailedSteps = failedSteps,
                WaitingForRetrySteps = waitingForRetrySteps,
                RunningSteps = runningSteps,
                RetryCount = retryCount,
                RecoveryCount = recoveryCount,

                FailureReason = replayValid
                    ? null
                    : BuildFailureReason(
                        fingerprintFound,
                        fingerprintMatches,
                        payloadValidation,
                        stepStateValidation,
                        dependencyGraphValidation,
                        totalSteps,
                        completedSteps,
                        runningSteps,
                        failedSteps,
                        waitingForRetrySteps,
                        retryCount,
                        recoveryCount),

                Issues = issues,
                Steps = steps
            };
        }

        private static string BuildFailureReason(
            bool fingerprintFound,
            bool fingerprintMatches,
            AiExecutionReplayPayloadValidationResult payloadValidation,
            AiExecutionReplayStepStateValidationResult stepStateValidation,
            AiExecutionReplayDependencyGraphValidationResult dependencyGraphValidation,
            int totalSteps,
            int completedSteps,
            int runningSteps,
            int failedSteps,
            int waitingForRetrySteps,
            int retryCount,
            int recoveryCount)
        {
            if (!fingerprintFound)
            {
                return "Replay fingerprint metadata not found.";
            }

            if (!fingerprintMatches)
            {
                return "Replay fingerprint mismatch.";
            }

            if (!payloadValidation.IsValid)
            {
                return
                    "Replay payload reference validation failed. " +
                    $"TotalSteps='{totalSteps}', CompletedSteps='{completedSteps}', RunningSteps='{runningSteps}', FailedSteps='{failedSteps}', WaitingForRetrySteps='{waitingForRetrySteps}', RetryCount='{retryCount}', RecoveryCount='{recoveryCount}', " +
                    $"Issues='{FormatIssues(payloadValidation.Issues)}'.";
            }

            if (!stepStateValidation.IsValid)
            {
                return
                    "Replay step state validation failed. " +
                    $"TotalSteps='{totalSteps}', CompletedSteps='{completedSteps}', RunningSteps='{runningSteps}', FailedSteps='{failedSteps}', WaitingForRetrySteps='{waitingForRetrySteps}', RetryCount='{retryCount}', RecoveryCount='{recoveryCount}', " +
                    $"Issues='{FormatIssues(stepStateValidation.Issues)}'.";
            }

            return
                "Replay dependency graph validation failed. " +
                $"TotalSteps='{totalSteps}', CompletedSteps='{completedSteps}', RunningSteps='{runningSteps}', FailedSteps='{failedSteps}', WaitingForRetrySteps='{waitingForRetrySteps}', RetryCount='{retryCount}', RecoveryCount='{recoveryCount}', " +
                $"Issues='{FormatIssues(dependencyGraphValidation.Issues)}'.";
        }

        private static string FormatIssues(
            IReadOnlyCollection<AiExecutionReplayIssue> issues)
        {
            if (issues.Count == 0)
            {
                return "-";
            }

            return string.Join(
                "; ",
                issues.Select(issue => $"{issue.Code}: {issue.Message}"));
        }
    }
}