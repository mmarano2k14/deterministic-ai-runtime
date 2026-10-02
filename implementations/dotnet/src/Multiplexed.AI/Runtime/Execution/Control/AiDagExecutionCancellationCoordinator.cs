using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Control;
using Multiplexed.AI.Runtime.Execution.Engine.Models;
using Multiplexed.AI.Stores;

namespace Multiplexed.AI.Runtime.Execution.Control
{
    /// <summary>
    /// Bridges durable execution-control cancellation into the authoritative DAG terminal record.
    /// </summary>
    /// <remarks>
    /// The execution-control store remains the authority for the requested cancellation intent.
    /// The DAG store remains the authority for the terminal execution status. This coordinator only
    /// connects those authorities so a parked or externally waiting execution cannot remain non-terminal
    /// forever after a durable cancellation request.
    /// </remarks>
    public sealed class AiDagExecutionCancellationCoordinator
    {
        private const int MaxFinalizeAttempts = 8;
        private const string CancellationWorkerId = "execution-control-cancellation";

        private readonly IAiDagExecutionStore _dagStore;
        private readonly IAiExecutionControlService _controlService;

        public AiDagExecutionCancellationCoordinator(
            IAiDagExecutionStore dagStore,
            IAiExecutionControlService controlService)
        {
            _dagStore = dagStore ?? throw new ArgumentNullException(nameof(dagStore));
            _controlService = controlService ?? throw new ArgumentNullException(nameof(controlService));
        }

        public async Task<AiExecutionControlState> CancelAsync(
            string executionId,
            string? reason = null,
            string? requestedBy = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            var controlState = await _controlService.CancelExecutionAsync(
                    executionId,
                    reason,
                    requestedBy,
                    cancellationToken)
                .ConfigureAwait(false);

            for (var attempt = 0; attempt < MaxFinalizeAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var record = await _dagStore.GetRecordAsync(executionId, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new KeyNotFoundException($"Execution '{executionId}' was not found.");

                if (record.Status == AiExecutionStatus.Cancelled)
                {
                    return await _controlService.MarkCancelledAsync(
                            executionId,
                            requestedBy ?? CancellationWorkerId,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                // Terminal execution state is monotonic. A cancellation racing after a different
                // terminal outcome must not rewrite Completed/Failed into Cancelled.
                if (record.IsTerminal)
                {
                    return controlState;
                }

                var finalized = await _dagStore.TryFinalizeExecutionAsync(
                        new AiDagExecutionFinalizationRequest
                        {
                            ExecutionId = executionId,
                            ExpectedExecutionStepKey = record.ExecutionStepKey,
                            Status = AiExecutionStatus.Cancelled,
                            CompletedAtUtc = DateTime.UtcNow,
                            CompletedSteps = record.CompletedSteps
                                .OrderBy(step => step, StringComparer.Ordinal)
                                .ToArray(),
                            CurrentStep = string.Empty,
                            WorkerId = CancellationWorkerId
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!finalized)
                {
                    continue;
                }

                return await _controlService.MarkCancelledAsync(
                        executionId,
                        requestedBy ?? CancellationWorkerId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                $"Execution '{executionId}' could not be finalized as cancelled after repeated optimistic-concurrency retries.");
        }
    }
}
