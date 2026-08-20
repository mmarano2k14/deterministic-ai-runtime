using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Metrics.Retention;

namespace Multiplexed.AI.Runtime.Metrics.Retention
{
    /// <summary>
    /// In-memory implementation of <see cref="IAiRetentionDecisionMetrics"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This implementation tracks retention decision activity, including compaction
    /// required decisions, eviction required decisions, and no-action decisions.
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
    /// Metrics are observational only and must not create retention plans, perform
    /// compaction, perform eviction, mutate hot state, archive payloads, or influence
    /// replay behavior.
    /// </para>
    /// </remarks>
    public sealed class AiRetentionDecisionMetrics : IAiRetentionDecisionMetrics
    {
        private const string Category = "RetentionDecision";

        private readonly IAiRuntimeMetricWriter _metricWriter;

        private long _compactionRequiredCount;
        private long _evictionRequiredCount;
        private long _noActionRequiredCount;

        private long _lastObservedStepCount;
        private long _lastCompactionThreshold;
        private long _lastEvictionMaxSteps;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRetentionDecisionMetrics"/> class.
        /// </summary>
        /// <param name="metricWriter">The correlated runtime metric writer.</param>
        public AiRetentionDecisionMetrics(
            IAiRuntimeMetricWriter metricWriter)
        {
            _metricWriter = metricWriter
                ?? throw new ArgumentNullException(nameof(metricWriter));
        }

        /// <inheritdoc />
        public void RecordCompactionRequired(
            string executionId,
            int stepCount,
            int threshold)
        {
            var safeStepCount = Math.Max(0, stepCount);
            var safeThreshold = Math.Max(0, threshold);

            Interlocked.Increment(ref _compactionRequiredCount);

            Interlocked.Exchange(
                ref _lastObservedStepCount,
                safeStepCount);

            Interlocked.Exchange(
                ref _lastCompactionThreshold,
                safeThreshold);

            RecordMetric(
                "retention.decision.compaction_required",
                executionId,
                new Dictionary<string, string>
                {
                    [AiStepMetadataKeys.StepCount] = safeStepCount.ToString(),
                    ["compaction.threshold"] = safeThreshold.ToString(),
                    ["decision"] = "compaction_required"
                },
                value: safeStepCount);
        }

        /// <inheritdoc />
        public void RecordEvictionRequired(
            string executionId,
            int stepCount,
            int maxSteps)
        {
            var safeStepCount = Math.Max(0, stepCount);
            var safeMaxSteps = Math.Max(0, maxSteps);

            Interlocked.Increment(ref _evictionRequiredCount);

            Interlocked.Exchange(
                ref _lastObservedStepCount,
                safeStepCount);

            Interlocked.Exchange(
                ref _lastEvictionMaxSteps,
                safeMaxSteps);

            RecordMetric(
                "retention.decision.eviction_required",
                executionId,
                new Dictionary<string, string>
                {
                    [AiStepMetadataKeys.StepCount] = safeStepCount.ToString(),
                    ["eviction.max.steps"] = safeMaxSteps.ToString(),
                    ["decision"] = "eviction_required"
                },
                value: safeStepCount);
        }

        /// <inheritdoc />
        public void RecordNoActionRequired(
            string executionId,
            int stepCount)
        {
            var safeStepCount = Math.Max(0, stepCount);

            Interlocked.Increment(ref _noActionRequiredCount);

            Interlocked.Exchange(
                ref _lastObservedStepCount,
                safeStepCount);

            RecordMetric(
                "retention.decision.no_action_required",
                executionId,
                new Dictionary<string, string>
                {
                    [AiStepMetadataKeys.StepCount] = safeStepCount.ToString(),
                    ["decision"] = "no_action_required"
                },
                value: safeStepCount);
        }

        /// <summary>
        /// Gets the number of decisions where compaction was required.
        /// </summary>
        public long CompactionRequiredCount => Interlocked.Read(ref _compactionRequiredCount);

        /// <summary>
        /// Gets the number of decisions where eviction was required.
        /// </summary>
        public long EvictionRequiredCount => Interlocked.Read(ref _evictionRequiredCount);

        /// <summary>
        /// Gets the number of decisions where no retention action was required.
        /// </summary>
        public long NoActionRequiredCount => Interlocked.Read(ref _noActionRequiredCount);

        /// <summary>
        /// Gets the last observed number of steps in hot state.
        /// </summary>
        public long LastObservedStepCount => Interlocked.Read(ref _lastObservedStepCount);

        /// <summary>
        /// Gets the last observed compaction threshold.
        /// </summary>
        public long LastCompactionThreshold => Interlocked.Read(ref _lastCompactionThreshold);

        /// <summary>
        /// Gets the last observed maximum number of hot steps.
        /// </summary>
        public long LastEvictionMaxSteps => Interlocked.Read(ref _lastEvictionMaxSteps);

        /// <summary>
        /// Records a correlated append-only retention decision metric without blocking the caller.
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