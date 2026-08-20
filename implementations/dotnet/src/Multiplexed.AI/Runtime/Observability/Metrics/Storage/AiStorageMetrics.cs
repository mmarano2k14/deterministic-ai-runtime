using Multiplexed.Abstractions.AI.Execution;
using System.Collections.Concurrent;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Metrics.Storage;
using Multiplexed.Abstractions.AI.Observability;

namespace Multiplexed.AI.Runtime.Observability.Metrics.Storage
{
    /// <summary>
    /// In-memory implementation of <see cref="IAiStorageMetrics"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This implementation tracks storage operations performed by the AI runtime,
    /// including payload persistence, payload loading, cache hits, cache misses,
    /// failures, and stored payload byte counts.
    /// </para>
    ///
    /// <para>
    /// This implementation is safe for singleton usage. Scalar counters use atomic
    /// operations and dimensional counters use concurrent dictionaries.
    /// </para>
    ///
    /// <para>
    /// In addition to maintaining in-memory counters, this implementation emits
    /// append-only correlated metric records through <see cref="IAiRuntimeMetricWriter"/>.
    /// The writer is responsible for attaching the current runtime correlation context
    /// and persisting the metric to the configured store.
    /// </para>
    ///
    /// <para>
    /// Metrics are observational only and must not perform storage, caching,
    /// serialization, retry, or runtime decision logic.
    /// </para>
    /// </remarks>
    public sealed class AiStorageMetrics : IAiStorageMetrics
    {
        private const string Category = "Storage";

        private readonly IAiRuntimeMetricWriter _metricWriter;

        private long _payloadStoredCount;
        private long _payloadLoadedCount;
        private long _payloadStoreHitCount;
        private long _payloadStoreMissCount;
        private long _payloadStoreFailureCount;
        private long _totalPayloadStoredBytes;

        private readonly ConcurrentDictionary<string, long> _operationsByStorageKind =
            new(StringComparer.Ordinal);

        private readonly ConcurrentDictionary<string, long> _failuresByExceptionType =
            new(StringComparer.Ordinal);

        /// <summary>
        /// Initializes a new instance of the <see cref="AiStorageMetrics"/> class.
        /// </summary>
        /// <param name="metricWriter">The correlated runtime metric writer.</param>
        public AiStorageMetrics(
            IAiRuntimeMetricWriter metricWriter)
        {
            _metricWriter = metricWriter
                ?? throw new ArgumentNullException(nameof(metricWriter));
        }

        /// <inheritdoc />
        public void RecordPayloadStored(
            string executionId,
            string stepId,
            string storageKind,
            long? bytes)
        {
            Interlocked.Increment(ref _payloadStoredCount);

            IncrementStorageKind(storageKind);

            if (bytes.HasValue)
            {
                Interlocked.Add(
                    ref _totalPayloadStoredBytes,
                    Math.Max(0, bytes.Value));
            }

            RecordMetric(
                "payload.stored",
                executionId,
                stepId,
                storageKind,
                new Dictionary<string, string>
                {
                    ["bytes"] = bytes?.ToString() ?? string.Empty
                },
                value: bytes.HasValue ? Math.Max(0, bytes.Value) : 1);
        }

        /// <inheritdoc />
        public void RecordPayloadLoaded(
            string executionId,
            string stepId,
            string storageKind)
        {
            Interlocked.Increment(ref _payloadLoadedCount);

            IncrementStorageKind(storageKind);

            RecordMetric(
                "payload.loaded",
                executionId,
                stepId,
                storageKind);
        }

        /// <inheritdoc />
        public void RecordPayloadStoreHit(
            string executionId,
            string stepId,
            string storageKind)
        {
            Interlocked.Increment(ref _payloadStoreHitCount);

            IncrementStorageKind(storageKind);

            RecordMetric(
                "payload.store_hit",
                executionId,
                stepId,
                storageKind);
        }

        /// <inheritdoc />
        public void RecordPayloadStoreMiss(
            string executionId,
            string stepId,
            string storageKind)
        {
            Interlocked.Increment(ref _payloadStoreMissCount);

            IncrementStorageKind(storageKind);

            RecordMetric(
                "payload.store_miss",
                executionId,
                stepId,
                storageKind);
        }

        /// <inheritdoc />
        public void RecordPayloadStoreFailure(
            string executionId,
            string stepId,
            string storageKind,
            Exception exception)
        {
            Interlocked.Increment(ref _payloadStoreFailureCount);

            IncrementStorageKind(storageKind);

            var exceptionType = exception?.GetType().Name ?? "unknown";

            _failuresByExceptionType.AddOrUpdate(
                exceptionType,
                _ => 1,
                (_, current) => current + 1);

            RecordMetric(
                "payload.store_failure",
                executionId,
                stepId,
                storageKind,
                new Dictionary<string, string>
                {
                    [AiExceptionMetadataKeys.ExceptionType] = exceptionType,
                    [AiExceptionMetadataKeys.ExceptionMessage] = exception?.Message ?? string.Empty
                });
        }

        /// <summary>
        /// Gets the number of payload store operations.
        /// </summary>
        public long PayloadStoredCount => Interlocked.Read(ref _payloadStoredCount);

        /// <summary>
        /// Gets the number of payload load operations.
        /// </summary>
        public long PayloadLoadedCount => Interlocked.Read(ref _payloadLoadedCount);

        /// <summary>
        /// Gets the number of cache hits.
        /// </summary>
        public long PayloadStoreHitCount => Interlocked.Read(ref _payloadStoreHitCount);

        /// <summary>
        /// Gets the number of cache misses.
        /// </summary>
        public long PayloadStoreMissCount => Interlocked.Read(ref _payloadStoreMissCount);

        /// <summary>
        /// Gets the number of storage failures.
        /// </summary>
        public long PayloadStoreFailureCount => Interlocked.Read(ref _payloadStoreFailureCount);

        /// <summary>
        /// Gets the total number of payload bytes stored.
        /// </summary>
        public long TotalPayloadStoredBytes => Interlocked.Read(ref _totalPayloadStoredBytes);

        /// <summary>
        /// Gets storage operations grouped by storage kind.
        /// </summary>
        public IReadOnlyDictionary<string, long> OperationsByStorageKind => _operationsByStorageKind;

        /// <summary>
        /// Gets failures grouped by exception type.
        /// </summary>
        public IReadOnlyDictionary<string, long> FailuresByExceptionType => _failuresByExceptionType;

        /// <summary>
        /// Records a correlated append-only storage metric without blocking the caller.
        /// </summary>
        /// <param name="name">The metric name.</param>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="stepId">The step identifier.</param>
        /// <param name="storageKind">The storage kind.</param>
        /// <param name="additionalTags">The optional additional tags.</param>
        /// <param name="value">The metric value.</param>
        private void RecordMetric(
            string name,
            string executionId,
            string stepId,
            string storageKind,
            IReadOnlyDictionary<string, string>? additionalTags = null,
            double value = 1)
        {
            var tags = new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                [AiExecutionMetadataKeys.ExecutionId] = executionId ?? string.Empty,
                [AiStepMetadataKeys.StepId] = stepId ?? string.Empty,
                ["storage.kind"] = Normalize(storageKind)
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

        /// <summary>
        /// Increments the storage-kind dimensional counter.
        /// </summary>
        /// <param name="storageKind">The storage kind.</param>
        private void IncrementStorageKind(
            string storageKind)
        {
            var key = Normalize(storageKind);

            _operationsByStorageKind.AddOrUpdate(
                key,
                _ => 1,
                (_, current) => current + 1);
        }

        /// <summary>
        /// Normalizes a metric dimension value.
        /// </summary>
        /// <param name="value">The value to normalize.</param>
        /// <returns>The normalized value.</returns>
        private static string Normalize(
            string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? "unknown"
                : value.Trim();
        }
    }
}