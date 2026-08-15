using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.Observability.Logging;

namespace Multiplexed.AI.Runtime.Execution.Instance.Worker
{
    /// <summary>
    /// Default implementation of <see cref="IAiRuntimeInstanceWorkerGroup"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This group coordinates multiple runtime instance workers against the same
    /// existing execution identifier. It starts all supplied workers, returns the first
    /// terminal or durably waiting execution record, and cancels the remaining workers.
    /// </para>
    /// <para>
    /// Before returning a terminal result, the group gives the worker that observed
    /// terminal execution one final non-cancelled execution pass. This allows terminal
    /// lifecycle work such as finalization, cleanup coordination, retention completion,
    /// and snapshot persistence to complete before the group returns. A durable waiting
    /// result is non-terminal and therefore bypasses terminal finalization.
    /// </para>
    /// <para>
    /// The group does not create executions, does not select pipelines, and must not be
    /// used to repurpose an execution identifier for another workflow run.
    /// </para>
    /// <para>
    /// Each execution run must have its own distinct execution identifier. The execution
    /// identifier is the namespace for the execution record, DAG state, step states,
    /// retention artifacts, externalized payloads, resolver indexes, snapshots, and
    /// replay data.
    /// </para>
    /// <para>
    /// Multiple runtime instance workers may safely advance the same existing execution
    /// identifier because distributed correctness is enforced by the underlying runtime
    /// engine and distributed DAG store.
    /// </para>
    /// <para>
    /// The group is an orchestration helper only. It does not perform step claiming,
    /// retry handling, throttling, retention, or convergence itself.
    /// </para>
    /// </remarks>
    public sealed class AiRuntimeInstanceWorkerGroup : IAiRuntimeInstanceWorkerGroup
    {
        private readonly IAiRuntimeLogger _logger;
        private readonly IAiRuntimeObservability _observability;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeInstanceWorkerGroup"/> class.
        /// </summary>
        /// <param name="logger">The runtime logger.</param>
        /// <param name="observability">The runtime observability facade.</param>
        public AiRuntimeInstanceWorkerGroup(
            IAiRuntimeLogger logger,
            IAiRuntimeObservability observability)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _observability = observability ?? throw new ArgumentNullException(nameof(observability));
        }

        /// <inheritdoc />
        public async Task<AiExecutionRecord> RunExecutionAsync(
            string executionId,
            IReadOnlyCollection<IAiRuntimeInstanceWorker> workers,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentNullException.ThrowIfNull(workers);

            if (workers.Count == 0)
            {
                throw new ArgumentException(
                    "At least one runtime instance worker is required.",
                    nameof(workers));
            }

            return await _observability.Tracer.TraceExecutionAsync(
                new AiExecutionTraceContext
                {
                    ExecutionId = executionId,
                    ExecutionMode = "Dag",
                    Status = "WorkerGroupRunning",
                    WorkerId = "worker-group"
                },
                async () =>
                {
                    return await RunWorkerGroupInternalAsync(
                        executionId,
                        workers,
                        cancellationToken).ConfigureAwait(false);
                }).ConfigureAwait(false);
        }

        /// <summary>
        /// Runs the supplied workers against the same existing execution identifier
        /// and returns the first terminal or durably waiting result observed by any worker.
        /// </summary>
        /// <param name="executionId">
        /// The existing execution identifier to advance. This identifier must belong
        /// to a previously created execution run and must not be reused for another run.
        /// </param>
        /// <param name="workers">
        /// The runtime instance workers participating in the execution.
        /// </param>
        /// <param name="cancellationToken">
        /// The cancellation token.
        /// </param>
        /// <returns>
        /// The terminal or durably waiting execution record observed by the winning worker.
        /// </returns>
        private async Task<AiExecutionRecord> RunWorkerGroupInternalAsync(
            string executionId,
            IReadOnlyCollection<IAiRuntimeInstanceWorker> workers,
            CancellationToken cancellationToken)
        {
            using var linkedCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var workerTasks = workers
                .Select(worker => new WorkerGroupTask(
                    worker,
                    RunWorkerSafelyAsync(
                        worker,
                        executionId,
                        linkedCancellation.Token)))
                .ToList();

            _logger.Engine.LogInformation(
                $"[AI WORKER GROUP] Runtime instance worker group started. ExecutionId='{executionId}', WorkerCount='{workers.Count}'.");

            try
            {
                while (workerTasks.Count > 0)
                {
                    var completedTask = await Task.WhenAny(
                        workerTasks.Select(item => item.Task)).ConfigureAwait(false);

                    var completedWorkerTask = workerTasks.Single(item =>
                        ReferenceEquals(item.Task, completedTask));

                    workerTasks.Remove(completedWorkerTask);

                    var result = await completedWorkerTask.Task.ConfigureAwait(false);

                    if (result.IsTerminal)
                    {
                        _logger.Engine.LogInformation(
                            $"[AI WORKER GROUP] Terminal execution observed. ExecutionId='{executionId}', Status='{result.Status}'.");

                        var finalized = await FinalizeTerminalObservationAsync(
                            completedWorkerTask.Worker,
                            executionId).ConfigureAwait(false);

                        await linkedCancellation.CancelAsync().ConfigureAwait(false);

                        await ObserveRemainingWorkersAsync(
                            workerTasks.Select(item => item.Task).ToList()).ConfigureAwait(false);

                        return finalized;
                    }

                    if (result.Status == AiExecutionStatus.Waiting)
                    {
                        _logger.Engine.LogInformation(
                            $"[AI WORKER GROUP] Durable execution suspension observed. ExecutionId='{executionId}'.");

                        await linkedCancellation.CancelAsync().ConfigureAwait(false);

                        await ObserveRemainingWorkersAsync(
                            workerTasks.Select(item => item.Task).ToList()).ConfigureAwait(false);

                        return result;
                    }
                }

                throw new InvalidOperationException(
                    $"Runtime instance worker group completed without observing a terminal or durably waiting execution. ExecutionId='{executionId}'.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.Engine.LogInformation(
                    $"[AI WORKER GROUP] Runtime instance worker group cancelled. ExecutionId='{executionId}'.");

                throw;
            }
        }

        /// <summary>
        /// Runs one final non-cancelled execution pass after terminal observation.
        /// </summary>
        /// <param name="worker">
        /// The worker that observed terminal execution.
        /// </param>
        /// <param name="executionId">
        /// The existing execution identifier to finalize.
        /// </param>
        /// <returns>
        /// The finalized terminal execution record.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This pass intentionally does not use the worker group cancellation token.
        /// Once a terminal record has been observed, the group must not cancel terminal
        /// lifecycle work that may be required for durable snapshots, retention completion,
        /// cleanup coordination, and replay safety.
        /// </para>
        /// <para>
        /// Distributed correctness still remains in the underlying runtime engine and
        /// DAG store. This method only gives the winning worker one additional chance
        /// to drive terminal lifecycle completion before the group returns.
        /// </para>
        /// </remarks>
        private async Task<AiExecutionRecord> FinalizeTerminalObservationAsync(
            IAiRuntimeInstanceWorker worker,
            string executionId)
        {
            ArgumentNullException.ThrowIfNull(worker);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            _logger.Engine.LogInformation(
                $"[AI WORKER GROUP] Running terminal finalization pass. ExecutionId='{executionId}'.");

            var finalized = await worker.RunExecutionAsync(
                executionId,
                CancellationToken.None).ConfigureAwait(false);

            _logger.Engine.LogInformation(
                $"[AI WORKER GROUP] Terminal finalization pass completed. ExecutionId='{executionId}', Status='{finalized.Status}'.");

            return finalized;
        }

        /// <summary>
        /// Runs one worker against the existing execution identifier.
        /// </summary>
        /// <param name="worker">The worker to run.</param>
        /// <param name="executionId">
        /// The existing execution identifier to advance.
        /// </param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The execution record returned by the worker.</returns>
        private static async Task<AiExecutionRecord> RunWorkerSafelyAsync(
            IAiRuntimeInstanceWorker worker,
            string executionId,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(worker);

            return await worker.RunExecutionAsync(
                executionId,
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Observes remaining worker tasks after a terminal result cancels the group.
        /// </summary>
        /// <param name="workerTasks">The remaining worker tasks.</param>
        private static async Task ObserveRemainingWorkersAsync(
            IReadOnlyCollection<Task<AiExecutionRecord>> workerTasks)
        {
            foreach (var task in workerTasks)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected after the first worker observes terminal execution.
                }
            }
        }

        /// <summary>
        /// Represents a runtime worker and the task currently advancing an execution.
        /// </summary>
        /// <param name="Worker">
        /// The runtime instance worker.
        /// </param>
        /// <param name="Task">
        /// The worker execution task.
        /// </param>
        private sealed record WorkerGroupTask(
            IAiRuntimeInstanceWorker Worker,
            Task<AiExecutionRecord> Task);
    }
}