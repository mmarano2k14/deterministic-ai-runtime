using Multiplexed.Abstractions.AI.Execution;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Metrics.Workers;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;

namespace Multiplexed.AI.Runtime.Observability.Metrics.Workers
{
    /// <summary>
    /// In-memory implementation of <see cref="IAiRuntimeInstanceWorkerMetrics"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This implementation is safe for singleton usage. Scalar counters use atomic
    /// operations and dimensional counters use concurrent dictionaries.
    /// </para>
    ///
    /// <para>
    /// Metrics are observational only and must not influence runtime execution,
    /// scheduling, retries, throttling, or convergence.
    /// </para>
    ///
    /// <para>
    /// In addition to maintaining in-memory counters, this implementation emits
    /// append-only correlated metric records through <see cref="IAiRuntimeMetricWriter"/>.
    /// The writer is responsible for attaching the current runtime correlation context
    /// and persisting the metric to the configured store.
    /// </para>
    /// </remarks>
    public sealed class AiRuntimeInstanceWorkerMetrics : IAiRuntimeInstanceWorkerMetrics
    {
        private const string Category = "Worker";

        private readonly IAiRuntimeMetricWriter _metricWriter;

        private long _workerStartedCount;
        private long _workerCycleCount;
        private long _workerIdleCount;
        private long _workerRaceLostCount;
        private long _workerTerminalCount;
        private long _workerCancelledCount;
        private long _workerMaxCyclesExceededCount;

        private readonly ConcurrentDictionary<string, long> _cyclesByRuntimeInstance =
            new(StringComparer.Ordinal);

        private readonly ConcurrentDictionary<string, long> _terminalByStatus =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, long> _raceLossByRuntimeInstance =
            new(StringComparer.Ordinal);

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeInstanceWorkerMetrics"/> class.
        /// </summary>
        /// <param name="metricWriter">The correlated runtime metric writer.</param>
        public AiRuntimeInstanceWorkerMetrics(
            IAiRuntimeMetricWriter metricWriter)
        {
            _metricWriter = metricWriter
                ?? throw new ArgumentNullException(nameof(metricWriter));
        }

        /// <inheritdoc />
        public void RecordWorkerStarted(
            string executionId,
            string runtimeInstanceId)
        {
            Interlocked.Increment(ref _workerStartedCount);

            IncrementDimension(
                _cyclesByRuntimeInstance,
                runtimeInstanceId,
                0);

            RecordMetric(
                "worker.started",
                executionId,
                runtimeInstanceId);
        }

        /// <inheritdoc />
        public void RecordWorkerCycle(
            string executionId,
            string runtimeInstanceId)
        {
            Interlocked.Increment(ref _workerCycleCount);

            IncrementDimension(
                _cyclesByRuntimeInstance,
                runtimeInstanceId);

            RecordMetric(
                "worker.cycle",
                executionId,
                runtimeInstanceId);
        }

        /// <inheritdoc />
        public void RecordWorkerIdle(
            string executionId,
            string runtimeInstanceId)
        {
            Interlocked.Increment(ref _workerIdleCount);

            RecordMetric(
                "worker.idle",
                executionId,
                runtimeInstanceId);
        }

        /// <inheritdoc />
        public void RecordWorkerRaceLost(
            string executionId,
            string runtimeInstanceId)
        {
            Interlocked.Increment(ref _workerRaceLostCount);

            IncrementDimension(
                _raceLossByRuntimeInstance,
                runtimeInstanceId);

            RecordMetric(
                "worker.race_lost",
                executionId,
                runtimeInstanceId);
        }

        /// <inheritdoc />
        public void RecordWorkerTerminal(
            string executionId,
            string runtimeInstanceId,
            string status)
        {
            Interlocked.Increment(ref _workerTerminalCount);

            IncrementDimension(
                _terminalByStatus,
                status);

            RecordMetric(
                "worker.terminal",
                executionId,
                runtimeInstanceId,
                new Dictionary<string, string>
                {
                    ["status"] = status ?? string.Empty
                });
        }

        /// <inheritdoc />
        public void RecordWorkerCancelled(
            string executionId,
            string runtimeInstanceId)
        {
            Interlocked.Increment(ref _workerCancelledCount);

            RecordMetric(
                "worker.cancelled",
                executionId,
                runtimeInstanceId);
        }

        /// <inheritdoc />
        public void RecordWorkerMaxCyclesExceeded(
            string executionId,
            string runtimeInstanceId,
            int maxCycles)
        {
            Interlocked.Increment(ref _workerMaxCyclesExceededCount);

            RecordMetric(
                "worker.max_cycles_exceeded",
                executionId,
                runtimeInstanceId,
                new Dictionary<string, string>
                {
                    ["max.cycles"] = maxCycles.ToString()
                });
        }

        /// <summary>
        /// Gets the total number of worker starts.
        /// </summary>
        public long WorkerStartedCount => Interlocked.Read(ref _workerStartedCount);

        /// <summary>
        /// Gets the total number of worker cycles.
        /// </summary>
        public long WorkerCycleCount => Interlocked.Read(ref _workerCycleCount);

        /// <summary>
        /// Gets the total number of idle worker cycles.
        /// </summary>
        public long WorkerIdleCount => Interlocked.Read(ref _workerIdleCount);

        /// <summary>
        /// Gets the total number of distributed race losses observed by workers.
        /// </summary>
        public long WorkerRaceLostCount => Interlocked.Read(ref _workerRaceLostCount);

        /// <summary>
        /// Gets the total number of terminal executions observed by workers.
        /// </summary>
        public long WorkerTerminalCount => Interlocked.Read(ref _workerTerminalCount);

        /// <summary>
        /// Gets the total number of worker cancellations.
        /// </summary>
        public long WorkerCancelledCount => Interlocked.Read(ref _workerCancelledCount);

        /// <summary>
        /// Gets the total number of max-cycle exits.
        /// </summary>
        public long WorkerMaxCyclesExceededCount => Interlocked.Read(ref _workerMaxCyclesExceededCount);

        /// <inheritdoc />
        public IReadOnlyDictionary<string, long> GetCyclesByRuntimeInstance()
        {
            return Snapshot(_cyclesByRuntimeInstance);
        }

        /// <inheritdoc />
        public IReadOnlyDictionary<string, long> GetTerminalByStatus()
        {
            return Snapshot(_terminalByStatus);
        }

        /// <inheritdoc />
        public IReadOnlyDictionary<string, long> GetRaceLossByRuntimeInstance()
        {
            return Snapshot(_raceLossByRuntimeInstance);
        }

        /// <summary>
        /// Records a correlated append-only worker metric without blocking the caller.
        /// </summary>
        /// <param name="name">The metric name.</param>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="additionalTags">The optional additional tags.</param>
        private void RecordMetric(
            string name,
            string executionId,
            string runtimeInstanceId,
            IReadOnlyDictionary<string, string>? additionalTags = null)
        {
            var tags = new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                [AiExecutionMetadataKeys.ExecutionId] = executionId ?? string.Empty,
                [AiRuntimeInstanceMetadataKeys.RuntimeInstanceId] = runtimeInstanceId ?? string.Empty
            };

            if (additionalTags is not null)
            {
                foreach (var tag in additionalTags)
                {
                    if (string.IsNullOrWhiteSpace(tag.Key))
                    {
                        continue;
                    }

                    tags[tag.Key] = tag.Value ?? string.Empty;
                }
            }

            _ = _metricWriter.RecordAsync(
                Category,
                name,
                value: 1,
                tags,
                CancellationToken.None);
        }

        /// <summary>
        /// Increments a dimensional counter.
        /// </summary>
        /// <param name="target">The dimensional counter dictionary.</param>
        /// <param name="key">The dimension key.</param>
        /// <param name="amount">The increment amount.</param>
        private static void IncrementDimension(
            ConcurrentDictionary<string, long> target,
            string key,
            long amount = 1)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            if (amount == 0)
            {
                target.TryAdd(key, 0);
                return;
            }

            target.AddOrUpdate(
                key,
                amount,
                (_, current) => current + amount);
        }

        /// <summary>
        /// Creates a read-only snapshot of a dimensional counter dictionary.
        /// </summary>
        /// <param name="source">The source dictionary.</param>
        /// <returns>The read-only snapshot.</returns>
        private static IReadOnlyDictionary<string, long> Snapshot(
            ConcurrentDictionary<string, long> source)
        {
            return new ReadOnlyDictionary<string, long>(
                new Dictionary<string, long>(
                    source,
                    StringComparer.Ordinal));
        }
    }
}