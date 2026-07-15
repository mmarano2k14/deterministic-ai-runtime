using Multiplexed.Abstractions.AI.Execution.Control;
using Multiplexed.AI.Runtime.Execution.Engine.Batch;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Engine.Creation;
using Multiplexed.AI.Runtime.Execution.Engine.Distributed;
using Multiplexed.AI.Runtime.Execution.Engine.Finalization;
using Multiplexed.AI.Runtime.Execution.Engine.Helpers;
using Multiplexed.AI.Runtime.Execution.Engine.Local;
using Multiplexed.AI.Runtime.Execution.Engine.Retention;
using Multiplexed.AI.Runtime.Execution.Engine.Steps;

namespace Multiplexed.AI.Tests.Integration.Fixtures
{
    /// <summary>
    /// Creates composed DAG runtime services for tests.
    /// </summary>
    public static class AiDagExecutionEngineRuntimeServicesFixture
    {
        /// <summary>
        /// Creates composed DAG runtime services.
        /// </summary>
        /// <param name="engineServices">
        /// The DAG execution engine services.
        /// </param>
        /// <returns>
        /// The composed DAG runtime services.
        /// </returns>
        public static IAiDagExecutionEngineRuntimeServices Create(
            IAiDagExecutionEngineServices engineServices)
        {
            ArgumentNullException.ThrowIfNull(engineServices);

            var lifecycleHelper = new AiDagExecutionLifecycleHelper(
                engineServices);

            var retentionCoordinator = new AiDagRetentionCoordinator(
                engineServices);

            var claimService = new AiDagStepClaimService(
                engineServices);

            var claimedStepExecutor = new AiDagClaimedStepExecutor(
                engineServices);

            var finalizationService = new AiDagExecutionFinalizationService(
                engineServices,
                retentionCoordinator);

            var creator = new AiDagExecutionCreator(
                engineServices);

            var localRunner = new AiDagLocalExecutionRunner(
                engineServices,
                lifecycleHelper);

            var distributedRunner = new AiDagDistributedExecutionRunner(
                engineServices,
                claimService,
                claimedStepExecutor,
                retentionCoordinator,
                finalizationService,
                lifecycleHelper);

            var batchRunner = new AiDagBatchExecutionRunner(
                engineServices,
                claimService,
                claimedStepExecutor,
                finalizationService,
                lifecycleHelper, retentionCoordinator);

            return new AiDagExecutionEngineRuntimeServices(
                creator,
                localRunner,
                distributedRunner,
                batchRunner,
                claimService,
                claimedStepExecutor,
                retentionCoordinator,
                finalizationService,
                lifecycleHelper);
        }
    }

    /// <summary>
    /// No-op execution control gate used by local DAG engine tests.
    /// </summary>
    public sealed class NoOpAiExecutionControlGate : IAiExecutionControlGate
    {
        /// <inheritdoc />
        public Task<AiExecutionControlDecision> CheckBeforeAdvanceAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                AiExecutionControlDecision.Continue());
        }
    }

    /// <summary>
    /// No-op execution control service used by local DAG engine tests.
    /// </summary>
    public sealed class NoOpAiExecutionControlService : IAiExecutionControlService
    {
        /// <inheritdoc />
        public Task<AiExecutionControlState> PauseExecutionAsync(
            string executionId,
            string? reason = null,
            string? requestedBy = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                CreateRunningState(executionId));
        }

        /// <inheritdoc />
        public Task<AiExecutionControlState> MarkPausedAsync(
            string executionId,
            string? requestedBy = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                CreateRunningState(executionId));
        }

        /// <inheritdoc />
        public Task<AiExecutionControlState> ResumeExecutionAsync(
            string executionId,
            string? requestedBy = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                CreateRunningState(executionId));
        }

        /// <inheritdoc />
        public Task<AiExecutionControlState> CancelExecutionAsync(
            string executionId,
            string? reason = null,
            string? requestedBy = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                CreateRunningState(executionId));
        }

        /// <inheritdoc />
        public Task<AiExecutionControlState> MarkWaitingForInputAsync(
            string executionId,
            string waitingKey,
            string? waitingStepName = null,
            string? reason = null,
            string? requestedBy = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                CreateRunningState(executionId));
        }

        /// <inheritdoc />
        public Task<AiExecutionControlState> SubmitHumanInputAsync(
            string executionId,
            string waitingKey,
            IReadOnlyDictionary<string, object?> input,
            string? submittedBy = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                CreateRunningState(executionId));
        }

        /// <inheritdoc />
        public Task<AiExecutionControlDecision> CheckCanAdvanceAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                AiExecutionControlDecision.Continue());
        }

        private static AiExecutionControlState CreateRunningState(
            string executionId)
        {
            return new AiExecutionControlState
            {
                ExecutionId = executionId,
                Status = AiExecutionControlStatus.Running,
                PendingAction = AiExecutionControlAction.None,
                Version = 1,
                UpdatedAtUtc = DateTime.UtcNow
            };
        }

        /// <inheritdoc />
        public Task<AiExecutionControlState> MarkRunningAsync(
            string executionId,
            string? requestedBy = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                new AiExecutionControlState
                {
                    ExecutionId = executionId,
                    Status = AiExecutionControlStatus.Running,
                    PendingAction = AiExecutionControlAction.None,
                    RequestedBy = requestedBy,
                    Version = 1,
                    UpdatedAtUtc = DateTime.UtcNow
                });
        }

        public Task<AiExecutionControlState> MarkCancelledAsync(string executionId, string? requestedBy = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                new AiExecutionControlState
                {
                    ExecutionId = executionId,
                    Status = AiExecutionControlStatus.Cancelled,
                    PendingAction = AiExecutionControlAction.None,
                    RequestedBy = requestedBy,
                    Version = 1,
                    UpdatedAtUtc = DateTime.UtcNow
                });
        }

        public Task<AiExecutionControlState?> GetStateAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<AiExecutionControlState?>(
                new AiExecutionControlState
                {
                    ExecutionId = executionId,
                    Status = AiExecutionControlStatus.Running,
                    PendingAction = AiExecutionControlAction.None,
                    Version = 1,
                    UpdatedAtUtc = DateTime.UtcNow
                });
        }

        public Task<AiExecutionControlState> PauseExecutionForRecoveryAsync(string executionId, string recoveryOwnerId, string? reason = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
               CreateRunningState(executionId));
        }

        public Task<AiExecutionControlState> ResumeExecutionFromRecoveryAsync(string executionId, string recoveryOwnerId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
               CreateRunningState(executionId));
        }
    }
}