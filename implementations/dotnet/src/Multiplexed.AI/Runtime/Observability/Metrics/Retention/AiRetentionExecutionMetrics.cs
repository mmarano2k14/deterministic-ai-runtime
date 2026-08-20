using Multiplexed.Abstractions.AI.Execution;
using System.Collections.Concurrent;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Metrics.Retention;
using Multiplexed.Abstractions.AI.Observability;

namespace Multiplexed.AI.Runtime.Observability.Metrics.Retention
{
    /// <summary>
    /// In-memory implementation of <see cref="IAiRetentionExecutionMetrics"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This implementation tracks actual retention work performed by the runtime,
    /// including payload compaction, step eviction, archival marking, completed
    /// retention execution, failed retention execution, and compaction byte savings.
    /// </para>
    ///
    /// <para>
    /// This implementation is safe for singleton usage. Scalar counters use atomic
    /// operations and failure counters use concurrent dictionaries.
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
    /// Metrics are observational only and must not perform compaction, eviction,
    /// archival, persistence, replay, or hot-state mutation.
    /// </para>
    /// </remarks>
    public sealed class AiRetentionExecutionMetrics : IAiRetentionExecutionMetrics
    {
        private const string Category = "RetentionExecution";

        private readonly IAiRuntimeMetricWriter _metricWriter;

        private long _payloadCompactedCount;
        private long _stepEvictedCount;
        private long _stepMarkedArchivedCount;
        private long _retentionCompletedCount;
        private long _retentionFailedCount;
        private long _totalBeforeBytes;
        private long _totalAfterBytes;
        private long _totalBytesSaved;

        private readonly ConcurrentDictionary<string, long> _failuresByExceptionType =
            new(StringComparer.Ordinal);

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRetentionExecutionMetrics"/> class.
        /// </summary>
        /// <param name="metricWriter">The correlated runtime metric writer.</param>
        public AiRetentionExecutionMetrics(
            IAiRuntimeMetricWriter metricWriter)
        {
            _metricWriter = metricWriter
                ?? throw new ArgumentNullException(nameof(metricWriter));
        }

        /// <inheritdoc />
        public void RecordPayloadCompacted(
            string executionId,
            string stepId,
            long? beforeBytes,
            long? afterBytes)
        {
            var safeBeforeBytes = beforeBytes.HasValue
                ? Math.Max(0, beforeBytes.Value)
                : (long?)null;

            var safeAfterBytes = afterBytes.HasValue
                ? Math.Max(0, afterBytes.Value)
                : (long?)null;

            Interlocked.Increment(ref _payloadCompactedCount);

            if (safeBeforeBytes.HasValue)
            {
                Interlocked.Add(
                    ref _totalBeforeBytes,
                    safeBeforeBytes.Value);
            }

            if (safeAfterBytes.HasValue)
            {
                Interlocked.Add(
                    ref _totalAfterBytes,
                    safeAfterBytes.Value);
            }

            var savedBytes = 0L;

            if (safeBeforeBytes.HasValue && safeAfterBytes.HasValue)
            {
                savedBytes = Math.Max(
                    0,
                    safeBeforeBytes.Value - safeAfterBytes.Value);

                Interlocked.Add(
                    ref _totalBytesSaved,
                    savedBytes);
            }

            RecordMetric(
                "retention.execution.payload_compacted",
                executionId,
                stepId,
                new Dictionary<string, string>
                {
                    ["before.bytes"] = safeBeforeBytes?.ToString() ?? string.Empty,
                    ["after.bytes"] = safeAfterBytes?.ToString() ?? string.Empty,
                    ["saved.bytes"] = savedBytes.ToString()
                },
                value: savedBytes > 0 ? savedBytes : 1);
        }

        /// <inheritdoc />
        public void RecordStepEvicted(
            string executionId,
            string stepId)
        {
            Interlocked.Increment(ref _stepEvictedCount);

            RecordMetric(
                "retention.execution.step_evicted",
                executionId,
                stepId);
        }

        /// <inheritdoc />
        public void RecordStepMarkedArchived(
            string executionId,
            string stepId)
        {
            Interlocked.Increment(ref _stepMarkedArchivedCount);

            RecordMetric(
                "retention.execution.step_marked_archived",
                executionId,
                stepId);
        }

        /// <inheritdoc />
        public void RecordRetentionCompleted(
            string executionId)
        {
            Interlocked.Increment(ref _retentionCompletedCount);

            RecordMetric(
                "retention.execution.completed",
                executionId,
                stepId: null);
        }

        /// <inheritdoc />
        public void RecordRetentionFailed(
            string executionId,
            Exception exception)
        {
            Interlocked.Increment(ref _retentionFailedCount);

            var exceptionType = exception?.GetType().Name ?? "unknown";

            _failuresByExceptionType.AddOrUpdate(
                exceptionType,
                _ => 1,
                (_, current) => current + 1);

            RecordMetric(
                "retention.execution.failed",
                executionId,
                stepId: null,
                new Dictionary<string, string>
                {
                    [AiExceptionMetadataKeys.ExceptionType] = exceptionType,
                    [AiExceptionMetadataKeys.ExceptionMessage] = exception?.Message ?? string.Empty
                });
        }

        /// <summary>
        /// Gets the number of compacted payloads.
        /// </summary>
        public long PayloadCompactedCount => Interlocked.Read(ref _payloadCompactedCount);

        /// <summary>
        /// Gets the number of evicted steps.
        /// </summary>
        public long StepEvictedCount => Interlocked.Read(ref _stepEvictedCount);

        /// <summary>
        /// Gets the number of steps marked as archived.
        /// </summary>
        public long StepMarkedArchivedCount => Interlocked.Read(ref _stepMarkedArchivedCount);

        /// <summary>
        /// Gets the number of completed retention executions.
        /// </summary>
        public long RetentionCompletedCount => Interlocked.Read(ref _retentionCompletedCount);

        /// <summary>
        /// Gets the number of failed retention executions.
        /// </summary>
        public long RetentionFailedCount => Interlocked.Read(ref _retentionFailedCount);

        /// <summary>
        /// Gets the total observed payload size before compaction.
        /// </summary>
        public long TotalBeforeBytes => Interlocked.Read(ref _totalBeforeBytes);

        /// <summary>
        /// Gets the total observed payload size after compaction.
        /// </summary>
        public long TotalAfterBytes => Interlocked.Read(ref _totalAfterBytes);

        /// <summary>
        /// Gets the total observed bytes saved by compaction.
        /// </summary>
        public long TotalBytesSaved => Interlocked.Read(ref _totalBytesSaved);

        /// <summary>
        /// Gets failures grouped by exception type.
        /// </summary>
        public IReadOnlyDictionary<string, long> FailuresByExceptionType => _failuresByExceptionType;

        /// <summary>
        /// Records a correlated append-only retention execution metric without blocking the caller.
        /// </summary>
        /// <param name="name">The metric name.</param>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="stepId">The optional step identifier.</param>
        /// <param name="additionalTags">The optional additional tags.</param>
        /// <param name="value">The metric value.</param>
        private void RecordMetric(
            string name,
            string executionId,
            string? stepId,
            IReadOnlyDictionary<string, string>? additionalTags = null,
            double value = 1)
        {
            var tags = new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                [AiExecutionMetadataKeys.ExecutionId] = executionId ?? string.Empty,
                [AiStepMetadataKeys.StepId] = stepId ?? string.Empty
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