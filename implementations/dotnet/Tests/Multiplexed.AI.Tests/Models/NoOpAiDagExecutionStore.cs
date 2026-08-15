using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Scheduling;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.Execution.Engine.Models;
using Multiplexed.AI.Runtime.Execution.Retention.Models;
using Multiplexed.AI.Stores;

namespace Multiplexed.AI.Tests.Fakes
{
    public sealed class NoOpAiDagExecutionStore : IAiDagExecutionStore
    {
        public Task CreateAsync(
            AiExecutionRecord record,
            AiExecutionState state,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<AiExecutionRecord?> GetRecordAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<AiExecutionRecord?>(null);
        }

        public Task<AiExecutionState?> GetStateAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<AiExecutionState?>(null);
        }

        public Task SaveRecordAsync(
            AiExecutionRecord record,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SaveStateAsync(
            string executionId,
            AiExecutionState state,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task DeleteRecordAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task DeleteStepsAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task DeleteExecutionBundleAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<AiClaimedStep?> TryClaimNextReadyStepAsync(
            string executionId,
            string workerId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<AiClaimedStep?>(null);
        }

        public Task<bool> TryCompleteStepAsync(
            string executionId,
            string stepName,
            string claimToken,
            AiStepResult result,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<bool> TryParkStepAsync(
            string executionId,
            string stepName,
            string claimToken,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<bool> TryResumeExternalWaitingStepAsync(
            string executionId,
            string stepName,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }

        public Task<bool> TryFailStepAsync(
            string executionId,
            string stepName,
            string claimToken,
            string? error,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<int> RecoverTimedOutStepsAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<bool> TryFinalizeExecutionAsync(AiDagExecutionFinalizationRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task DeleteStateAsync(string executionId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task RestoreAsync(AiExecutionRecord record, AiExecutionState state, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task DeleteStepAsync(string executionId, string stepName, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AiClaimedStep>> TryClaimReadyStepsAsync(string executionId, string workerId, int maxSteps, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AiClaimedStep>>(Array.Empty<AiClaimedStep>());
        }

        public Task<IReadOnlyList<AiClaimedStep>> GetReadyStepsAsync(string executionId, int maxSteps, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AiClaimedStep>>(Array.Empty<AiClaimedStep>());
        }

        public Task<AiClaimedStep?> TryClaimStepAsync(string executionId, string stepName, string workerId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<AiClaimedStep?>(null);
        }

        public Task<AiRetentionPatchResult> TryApplyRetentionPatchAsync(string executionId, IReadOnlyCollection<AiRetentionPatchCandidate> candidates, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
            new AiRetentionPatchResult
            {
                SkippedSteps = candidates
                    .Select(x => x.StepName)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToArray()
            });
        }

        public Task<int> RecoverRunningStepsForRecoveryAsync(string executionId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<int>(1);
        }
    }
}