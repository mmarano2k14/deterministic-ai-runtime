using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Observability.Logging;

namespace Multiplexed.AI.Runtime.Execution.Instance.Worker
{
    /// <summary>
    /// Default implementation of <see cref="IAiRuntimeInstanceWorker"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A runtime instance worker provides a higher-level orchestration loop on top
    /// of the DAG execution engine. It repeatedly advances a target execution by
    /// invoking bounded batch execution cycles until the execution reaches a terminal
    /// state, reaches an external durable waiting state that can release runtime capacity,
    /// or cancellation is requested.
    /// </para>
    /// <para>
    /// The worker does not implement distributed correctness itself. Correctness is
    /// provided by the underlying runtime engine, Redis-backed DAG store, atomic
    /// claims, claim tokens, retry state, concurrency leases, throttling, and
    /// deterministic convergence.
    /// </para>
    /// <para>
    /// Worker metrics and traces describe orchestration-loop behavior such as worker
    /// starts, cycles, idle waits, distributed race losses, terminal completion,
    /// cancellation, and max-cycle exits.
    /// </para>
    /// </remarks>
    public sealed class AiRuntimeInstanceWorker : IAiRuntimeInstanceWorker
    {
        private const string ConcurrencyConflictMessage =
            "Concurrency conflict on execution update.";

        private readonly AiDagExecutionEngine _engine;
        private readonly IAiRuntimeInstanceWorkerIdentity _workerIdentity;
        private readonly IAiRuntimeLogger _logger;
        private readonly IAiRuntimeObservability _observability;
        private readonly AiRuntimeInstanceWorkerOptions _options;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeInstanceWorker"/> class.
        /// </summary>
        /// <param name="engine">The DAG execution engine used to advance executions.</param>
        /// <param name="workerIdentity">
        /// The logical worker identity for this worker instance. Factory-created workers
        /// must receive a unique identity through <see cref="AiRuntimeInstanceWorkerFactory"/>.
        /// </param>
        /// <param name="logger">The runtime logger.</param>
        /// <param name="observability">The runtime observability facade used for metrics, tracing, ledger, and correlation.</param>
        /// <param name="options">The runtime instance worker options.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when one of the required dependencies is <see langword="null"/>.
        /// </exception>
        public AiRuntimeInstanceWorker(
            AiDagExecutionEngine engine,
            IAiRuntimeInstanceWorkerIdentity workerIdentity,
            IAiRuntimeLogger logger,
            IAiRuntimeObservability observability,
            IOptions<AiRuntimeInstanceWorkerOptions> options)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _workerIdentity = workerIdentity
                ?? throw new ArgumentNullException(nameof(workerIdentity));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _observability = observability ?? throw new ArgumentNullException(nameof(observability));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        }

        /// <inheritdoc />
        public async Task<AiExecutionRecord> RunExecutionAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            var current = _observability.Correlation.Current;
            var runtimeInstanceId = _workerIdentity.RuntimeInstanceIdentity.RuntimeInstanceId;
            var workerId = _workerIdentity.WorkerId;

            var correlation = new AiRuntimeExecutionCorrelationContext
            {
                CorrelationId = current?.CorrelationId ?? current?.RunId ?? executionId,
                RunId = current?.RunId,
                ExecutionId = executionId,
                PipelineName = current?.PipelineName,
                PipelineVersion = current?.PipelineVersion,
                PipelineKey = current?.PipelineKey,
                RuntimeInstanceId = runtimeInstanceId,
                WorkerId = workerId
            };

            using var correlationScope = _observability.Correlation.Push(correlation);

            return await _observability.Tracer.TraceExecutionAsync(
                new AiExecutionTraceContext
                {
                    ExecutionId = executionId,
                    ExecutionMode = "Dag",
                    Status = "WorkerRunning",
                    WorkerId = workerId
                },
                async () =>
                {
                    return await RunExecutionLoopAsync(
                        executionId,
                        runtimeInstanceId,
                        workerId,
                        cancellationToken).ConfigureAwait(false);
                }).ConfigureAwait(false);
        }

        /// <summary>
        /// Runs the internal worker loop until the execution reaches a terminal state,
        /// reaches an external durable waiting state that can release runtime capacity,
        /// or cancellation is requested.
        /// </summary>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="workerId">The logical worker identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The terminal or durably suspended execution record.</returns>
        private async Task<AiExecutionRecord> RunExecutionLoopAsync(
            string executionId,
            string runtimeInstanceId,
            string workerId,
            CancellationToken cancellationToken)
        {
            var cycle = 0;

            _observability.Metrics.Worker.RecordWorkerStarted(
                executionId,
                workerId);

            _logger.Engine.LogInformation(
                $"[AI WORKER] Runtime instance worker started. ExecutionId='{executionId}', RuntimeInstanceId='{runtimeInstanceId}', WorkerId='{workerId}'.");

            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    cycle++;

                    if (_options.MaxCycles > 0 && cycle > _options.MaxCycles)
                    {
                        _observability.Metrics.Worker.RecordWorkerMaxCyclesExceeded(
                            executionId,
                            workerId,
                            _options.MaxCycles);

                        _logger.Engine.LogWarning(
                            $"[AI WORKER] Runtime instance worker exceeded max cycles. ExecutionId='{executionId}', RuntimeInstanceId='{runtimeInstanceId}', WorkerId='{workerId}', MaxCycles='{_options.MaxCycles}'.");

                        throw new TimeoutException(
                            $"Runtime instance worker exceeded max cycles. ExecutionId='{executionId}', RuntimeInstanceId='{runtimeInstanceId}', WorkerId='{workerId}', MaxCycles='{_options.MaxCycles}'.");
                    }

                    _observability.Metrics.Worker.RecordWorkerCycle(
                        executionId,
                        workerId);

                    AiExecutionRecord record;

                    try
                    {
                        record = await TraceWorkerCycleAsync(
                            executionId,
                            runtimeInstanceId,
                            workerId,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException ex)
                        when (_options.IgnoreConcurrencyConflicts &&
                              string.Equals(
                                  ex.Message,
                                  ConcurrencyConflictMessage,
                                  StringComparison.Ordinal))
                    {
                        _observability.Metrics.Worker.RecordWorkerRaceLost(
                            executionId,
                            workerId);

                        _logger.Engine.LogInformation(
                            $"[AI WORKER] Distributed race loss ignored. ExecutionId='{executionId}', RuntimeInstanceId='{runtimeInstanceId}', WorkerId='{workerId}'.");

                        await DelayIfNeededAsync(
                            executionId,
                            runtimeInstanceId,
                            workerId,
                            cancellationToken).ConfigureAwait(false);

                        continue;
                    }

                    if (record.IsTerminal)
                    {
                        _observability.Metrics.Worker.RecordWorkerTerminal(
                            executionId,
                            workerId,
                            record.Status.ToString());

                        _logger.Engine.LogInformation(
                            $"[AI WORKER] Runtime instance worker reached terminal execution. ExecutionId='{executionId}', RuntimeInstanceId='{runtimeInstanceId}', WorkerId='{workerId}', Status='{record.Status}', Cycles='{cycle}'.");

                        return record;
                    }

                    if (record.Status == AiExecutionStatus.Waiting &&
                        await _engine.ShouldReleaseForExternalWaitAsync(
                                executionId,
                                cancellationToken)
                            .ConfigureAwait(false))
                    {
                        _logger.Engine.LogInformation(
                            $"[AI WORKER] Runtime instance worker released an externally waiting execution. ExecutionId='{executionId}', RuntimeInstanceId='{runtimeInstanceId}', WorkerId='{workerId}', Cycles='{cycle}'.");

                        return record;
                    }

                    _observability.Metrics.Worker.RecordWorkerIdle(
                        executionId,
                        workerId);

                    await DelayIfNeededAsync(
                        executionId,
                        runtimeInstanceId,
                        workerId,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _observability.Metrics.Worker.RecordWorkerCancelled(
                    executionId,
                    workerId);

                _logger.Engine.LogInformation(
                    $"[AI WORKER] Runtime instance worker cancelled. ExecutionId='{executionId}', RuntimeInstanceId='{runtimeInstanceId}', WorkerId='{workerId}'.");

                throw;
            }
        }

        /// <summary>
        /// Executes and traces one worker cycle.
        /// </summary>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="workerId">The logical worker identifier used for tracing.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The execution record returned by the engine.</returns>
        private async Task<AiExecutionRecord> TraceWorkerCycleAsync(
            string executionId,
            string runtimeInstanceId,
            string workerId,
            CancellationToken cancellationToken)
        {
            return await _observability.Tracer.TraceExecutionAsync(
                new AiExecutionTraceContext
                {
                    ExecutionId = executionId,
                    ExecutionMode = "Dag",
                    Status = "WorkerCycle",
                    WorkerId = workerId
                },
                async () =>
                {
                    return await _engine.ExecuteBatchAsync(
                        executionId,
                        _options.MaxStepsPerCycle,
                        cancellationToken).ConfigureAwait(false);
                }).ConfigureAwait(false);
        }

        /// <summary>
        /// Applies the configured idle delay when needed.
        /// </summary>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="workerId">The logical worker identifier used for logging.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task DelayIfNeededAsync(
            string executionId,
            string runtimeInstanceId,
            string workerId,
            CancellationToken cancellationToken)
        {
            if (_options.IdleDelay <= TimeSpan.Zero)
            {
                return;
            }

            _logger.Engine.LogInformation(
                $"[AI WORKER] Runtime instance worker idle delay. ExecutionId='{executionId}', RuntimeInstanceId='{runtimeInstanceId}', WorkerId='{workerId}', DelayMs='{_options.IdleDelay.TotalMilliseconds}'.");

            await Task.Delay(
                _options.IdleDelay,
                cancellationToken).ConfigureAwait(false);
        }
    }
}