using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Metrics.HotState;

namespace Multiplexed.AI.Runtime.Observability.Metrics.HotState
{
    /// <summary>
    /// In-memory implementation of <see cref="IAiHotStateMetrics"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This implementation tracks hot execution state growth, state reduction,
    /// compaction effectiveness, and observed hot state size.
    /// </para>
    ///
    /// <para>
    /// This implementation is safe for singleton usage. Scalar counters use atomic
    /// operations.
    /// </para>
    ///
    /// <para>
    /// In addition to maintaining in-memory counters, this implementation emits
    /// append-only correlated metric records through <see cref="IAiRuntimeMetricWriter"/>.
    /// The writer is responsible for attaching the current runtime correlation context
    /// and persisting the metric to the configured metric store.
    /// </para>
    ///
    /// <para>
    /// Metrics are observational only and must not change execution state, retention
    /// behavior, compaction decisions, eviction decisions, or replay behavior.
    /// </para>
    /// </remarks>
    public sealed class AiHotStateMetrics : IAiHotStateMetrics
    {
        private const string Category = "HotState";

        private readonly IAiRuntimeMetricWriter _metricWriter;

        private long _stateStepAddedCount;
        private long _stateStepRemovedCount;
        private long _stateCompactedCount;
        private long _stateSizeObservedCount;
        private long _lastObservedStepCount;
        private long _lastEstimatedBytes;
        private long _lastCompactionBeforeSteps;
        private long _lastCompactionAfterSteps;
        private long _totalStepsRemovedByCompaction;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiHotStateMetrics"/> class.
        /// </summary>
        /// <param name="metricWriter">The correlated runtime metric writer.</param>
        public AiHotStateMetrics(
            IAiRuntimeMetricWriter metricWriter)
        {
            _metricWriter = metricWriter
                ?? throw new ArgumentNullException(nameof(metricWriter));
        }

        /// <inheritdoc />
        public void RecordStateStepAdded(
            string executionId,
            string stepId)
        {
            Interlocked.Increment(ref _stateStepAddedCount);

            RecordMetric(
                "hot_state.step_added",
                executionId,
                new Dictionary<string, string>
                {
                    [AiStepMetadataKeys.StepId] = stepId ?? string.Empty
                });
        }

        /// <inheritdoc />
        public void RecordStateStepRemoved(
            string executionId,
            string stepId)
        {
            Interlocked.Increment(ref _stateStepRemovedCount);

            RecordMetric(
                "hot_state.step_removed",
                executionId,
                new Dictionary<string, string>
                {
                    [AiStepMetadataKeys.StepId] = stepId ?? string.Empty
                });
        }

        /// <inheritdoc />
        public void RecordStateCompacted(
            string executionId,
            int beforeSteps,
            int afterSteps)
        {
            var safeBeforeSteps = Math.Max(0, beforeSteps);
            var safeAfterSteps = Math.Max(0, afterSteps);
            var removed = Math.Max(0, safeBeforeSteps - safeAfterSteps);

            Interlocked.Increment(ref _stateCompactedCount);

            Interlocked.Exchange(
                ref _lastCompactionBeforeSteps,
                safeBeforeSteps);

            Interlocked.Exchange(
                ref _lastCompactionAfterSteps,
                safeAfterSteps);

            Interlocked.Add(
                ref _totalStepsRemovedByCompaction,
                removed);

            RecordMetric(
                "hot_state.compacted",
                executionId,
                new Dictionary<string, string>
                {
                    ["before.steps"] = safeBeforeSteps.ToString(),
                    ["after.steps"] = safeAfterSteps.ToString(),
                    ["removed.steps"] = removed.ToString()
                },
                value: removed);
        }

        /// <inheritdoc />
        public void RecordStateSizeObserved(
            string executionId,
            int stepCount,
            long? estimatedBytes)
        {
            var safeStepCount = Math.Max(0, stepCount);

            Interlocked.Increment(ref _stateSizeObservedCount);

            Interlocked.Exchange(
                ref _lastObservedStepCount,
                safeStepCount);

            if (estimatedBytes.HasValue)
            {
                Interlocked.Exchange(
                    ref _lastEstimatedBytes,
                    Math.Max(0, estimatedBytes.Value));
            }

            RecordMetric(
                "hot_state.size_observed",
                executionId,
                new Dictionary<string, string>
                {
                    [AiStepMetadataKeys.StepCount] = safeStepCount.ToString(),
                    ["estimated.bytes"] = estimatedBytes?.ToString() ?? string.Empty
                },
                value: safeStepCount);
        }

        /// <summary>
        /// Gets the number of steps added to hot state.
        /// </summary>
        public long StateStepAddedCount => Interlocked.Read(ref _stateStepAddedCount);

        /// <summary>
        /// Gets the number of steps removed from hot state.
        /// </summary>
        public long StateStepRemovedCount => Interlocked.Read(ref _stateStepRemovedCount);

        /// <summary>
        /// Gets the number of hot state compaction events.
        /// </summary>
        public long StateCompactedCount => Interlocked.Read(ref _stateCompactedCount);

        /// <summary>
        /// Gets the number of state size observations.
        /// </summary>
        public long StateSizeObservedCount => Interlocked.Read(ref _stateSizeObservedCount);

        /// <summary>
        /// Gets the last observed number of steps in hot state.
        /// </summary>
        public long LastObservedStepCount => Interlocked.Read(ref _lastObservedStepCount);

        /// <summary>
        /// Gets the last observed estimated state size in bytes.
        /// </summary>
        public long LastEstimatedBytes => Interlocked.Read(ref _lastEstimatedBytes);

        /// <summary>
        /// Gets the last observed step count before compaction.
        /// </summary>
        public long LastCompactionBeforeSteps => Interlocked.Read(ref _lastCompactionBeforeSteps);

        /// <summary>
        /// Gets the last observed step count after compaction.
        /// </summary>
        public long LastCompactionAfterSteps => Interlocked.Read(ref _lastCompactionAfterSteps);

        /// <summary>
        /// Gets the total number of steps removed by hot state compaction.
        /// </summary>
        public long TotalStepsRemovedByCompaction => Interlocked.Read(ref _totalStepsRemovedByCompaction);

        /// <summary>
        /// Records a correlated append-only hot-state metric without blocking the caller.
        /// </summary>
        /// <param name="name">The metric name.</param>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="additionalTags">The optional additional tags.</param>
        /// <param name="value">The metric value.</param>
        private void RecordMetric(
            string name,
            string executionId,
            IReadOnlyDictionary<string, string>? additionalTags = null,
            double value = 1)
        {
            var tags = new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                [AiExecutionMetadataKeys.ExecutionId] = executionId ?? string.Empty
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
                value,
                tags,
                CancellationToken.None);
        }
    }
}