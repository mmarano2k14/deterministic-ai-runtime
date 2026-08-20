using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Metrics.Retention;

namespace Multiplexed.AI.Runtime.Observability.Metrics.Retention
{
    /// <summary>
    /// In-memory implementation of <see cref="IAiRetentionPlanMetrics"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This implementation tracks how retention plans are structured before execution,
    /// including compacted, evicted, and kept step counts.
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
    /// Metrics are observational only and must not execute retention work, mutate hot
    /// state, compact payloads, evict steps, or influence replay behavior.
    /// </para>
    /// </remarks>
    public sealed class AiRetentionPlanMetrics : IAiRetentionPlanMetrics
    {
        private const string Category = "RetentionPlan";

        private readonly IAiRuntimeMetricWriter _metricWriter;

        private long _planCreatedCount;
        private long _planEmptyCount;
        private long _totalCompactedSteps;
        private long _totalEvictedSteps;
        private long _totalKeepSteps;
        private long _lastCompactedSteps;
        private long _lastEvictedSteps;
        private long _lastKeepSteps;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRetentionPlanMetrics"/> class.
        /// </summary>
        /// <param name="metricWriter">The correlated runtime metric writer.</param>
        public AiRetentionPlanMetrics(
            IAiRuntimeMetricWriter metricWriter)
        {
            _metricWriter = metricWriter
                ?? throw new ArgumentNullException(nameof(metricWriter));
        }

        /// <inheritdoc />
        public void RecordPlanCreated(
            string executionId,
            int compactSteps,
            int evictSteps,
            int keepSteps)
        {
            var safeCompactSteps = Math.Max(0, compactSteps);
            var safeEvictSteps = Math.Max(0, evictSteps);
            var safeKeepSteps = Math.Max(0, keepSteps);
            var totalPlannedSteps = safeCompactSteps + safeEvictSteps + safeKeepSteps;

            Interlocked.Increment(ref _planCreatedCount);

            Interlocked.Add(
                ref _totalCompactedSteps,
                safeCompactSteps);

            Interlocked.Add(
                ref _totalEvictedSteps,
                safeEvictSteps);

            Interlocked.Add(
                ref _totalKeepSteps,
                safeKeepSteps);

            Interlocked.Exchange(
                ref _lastCompactedSteps,
                safeCompactSteps);

            Interlocked.Exchange(
                ref _lastEvictedSteps,
                safeEvictSteps);

            Interlocked.Exchange(
                ref _lastKeepSteps,
                safeKeepSteps);

            RecordMetric(
                "retention.plan.created",
                executionId,
                new Dictionary<string, string>
                {
                    ["compact.steps"] = safeCompactSteps.ToString(),
                    ["evict.steps"] = safeEvictSteps.ToString(),
                    ["keep.steps"] = safeKeepSteps.ToString(),
                    ["total.planned.steps"] = totalPlannedSteps.ToString()
                },
                value: totalPlannedSteps > 0 ? totalPlannedSteps : 1);
        }

        /// <inheritdoc />
        public void RecordPlanEmpty(
            string executionId)
        {
            Interlocked.Increment(ref _planEmptyCount);

            RecordMetric(
                "retention.plan.empty",
                executionId);
        }

        /// <summary>
        /// Gets the number of created retention plans.
        /// </summary>
        public long PlanCreatedCount => Interlocked.Read(ref _planCreatedCount);

        /// <summary>
        /// Gets the number of times no retention plan was created.
        /// </summary>
        public long PlanEmptyCount => Interlocked.Read(ref _planEmptyCount);

        /// <summary>
        /// Gets the total number of steps scheduled for compaction.
        /// </summary>
        public long TotalCompactedSteps => Interlocked.Read(ref _totalCompactedSteps);

        /// <summary>
        /// Gets the total number of steps scheduled for eviction.
        /// </summary>
        public long TotalEvictedSteps => Interlocked.Read(ref _totalEvictedSteps);

        /// <summary>
        /// Gets the total number of steps kept in hot state.
        /// </summary>
        public long TotalKeepSteps => Interlocked.Read(ref _totalKeepSteps);

        /// <summary>
        /// Gets the last recorded compact step count.
        /// </summary>
        public long LastCompactedSteps => Interlocked.Read(ref _lastCompactedSteps);

        /// <summary>
        /// Gets the last recorded evict step count.
        /// </summary>
        public long LastEvictedSteps => Interlocked.Read(ref _lastEvictedSteps);

        /// <summary>
        /// Gets the last recorded kept step count.
        /// </summary>
        public long LastKeepSteps => Interlocked.Read(ref _lastKeepSteps);

        /// <summary>
        /// Records a correlated append-only retention plan metric without blocking the caller.
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