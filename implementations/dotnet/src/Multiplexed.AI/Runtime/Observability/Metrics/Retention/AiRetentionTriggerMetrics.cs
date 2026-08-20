using Multiplexed.Abstractions.AI.Execution;
using System.Collections.Concurrent;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Metrics.Retention;

namespace Multiplexed.AI.Runtime.Observability.Metrics.Retention
{
    /// <summary>
    /// In-memory implementation of <see cref="IAiRetentionTriggerMetrics"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This implementation tracks retention trigger activity inside the runtime,
    /// including triggered and skipped retention evaluations grouped by reason.
    /// </para>
    ///
    /// <para>
    /// This implementation is safe for singleton usage. Scalar counters use atomic
    /// operations and reason-based counters use concurrent dictionaries.
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
    /// Metrics are observational only and must not influence retention triggering,
    /// retention policy evaluation, compaction, eviction, replay, or execution state.
    /// </para>
    /// </remarks>
    public sealed class AiRetentionTriggerMetrics : IAiRetentionTriggerMetrics
    {
        private const string Category = "RetentionTrigger";

        private readonly IAiRuntimeMetricWriter _metricWriter;

        private long _triggeredCount;
        private long _skippedCount;

        private readonly ConcurrentDictionary<string, long> _triggeredByReason =
            new(StringComparer.Ordinal);

        private readonly ConcurrentDictionary<string, long> _skippedByReason =
            new(StringComparer.Ordinal);

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRetentionTriggerMetrics"/> class.
        /// </summary>
        /// <param name="metricWriter">The correlated runtime metric writer.</param>
        public AiRetentionTriggerMetrics(
            IAiRuntimeMetricWriter metricWriter)
        {
            _metricWriter = metricWriter
                ?? throw new ArgumentNullException(nameof(metricWriter));
        }

        /// <inheritdoc />
        public void RecordTriggered(
            string executionId,
            string reason)
        {
            Interlocked.Increment(ref _triggeredCount);

            var normalizedReason = NormalizeReason(reason);

            IncrementReason(
                _triggeredByReason,
                normalizedReason);

            RecordMetric(
                "retention.trigger.triggered",
                executionId,
                normalizedReason);
        }

        /// <inheritdoc />
        public void RecordSkipped(
            string executionId,
            string reason)
        {
            Interlocked.Increment(ref _skippedCount);

            var normalizedReason = NormalizeReason(reason);

            IncrementReason(
                _skippedByReason,
                normalizedReason);

            RecordMetric(
                "retention.trigger.skipped",
                executionId,
                normalizedReason);
        }

        /// <summary>
        /// Gets the total number of retention trigger events.
        /// </summary>
        public long TriggeredCount => Interlocked.Read(ref _triggeredCount);

        /// <summary>
        /// Gets the total number of retention skipped events.
        /// </summary>
        public long SkippedCount => Interlocked.Read(ref _skippedCount);

        /// <summary>
        /// Gets the number of retention trigger events grouped by reason.
        /// </summary>
        public IReadOnlyDictionary<string, long> TriggeredByReason => _triggeredByReason;

        /// <summary>
        /// Gets the number of retention skipped events grouped by reason.
        /// </summary>
        public IReadOnlyDictionary<string, long> SkippedByReason => _skippedByReason;

        /// <summary>
        /// Records a correlated append-only retention trigger metric without blocking the caller.
        /// </summary>
        /// <param name="name">The metric name.</param>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="reason">The retention trigger reason.</param>
        private void RecordMetric(
            string name,
            string executionId,
            string reason)
        {
            var tags = new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                [AiExecutionMetadataKeys.ExecutionId] = executionId ?? string.Empty,
                ["reason"] = reason ?? string.Empty
            };

            _ = _metricWriter.RecordAsync(
                Category,
                name,
                value: 1,
                tags,
                CancellationToken.None);
        }

        /// <summary>
        /// Increments a reason-based dimensional counter.
        /// </summary>
        /// <param name="target">The target reason counter dictionary.</param>
        /// <param name="reason">The normalized reason.</param>
        private static void IncrementReason(
            ConcurrentDictionary<string, long> target,
            string reason)
        {
            target.AddOrUpdate(
                reason,
                _ => 1,
                (_, current) => current + 1);
        }

        /// <summary>
        /// Normalizes a retention reason.
        /// </summary>
        /// <param name="reason">The retention reason.</param>
        /// <returns>The normalized retention reason.</returns>
        private static string NormalizeReason(
            string reason)
        {
            return string.IsNullOrWhiteSpace(reason)
                ? "unknown"
                : reason.Trim();
        }
    }
}