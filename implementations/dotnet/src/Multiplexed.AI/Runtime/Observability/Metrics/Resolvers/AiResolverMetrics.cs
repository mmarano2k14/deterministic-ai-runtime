using Multiplexed.Abstractions.AI.Execution;
using System.Collections.Concurrent;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Metrics.Resolvers;
using Multiplexed.Abstractions.AI.Observability;

namespace Multiplexed.AI.Runtime.Observability.Metrics.Resolvers
{
    /// <summary>
    /// In-memory implementation of <see cref="IAiResolverMetrics"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This implementation tracks resolver behavior during runtime execution, including
    /// resolver starts, successful resolutions, misses, failures, paths, and exception
    /// types.
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
    /// and persisting the metric to the configured metric store.
    /// </para>
    ///
    /// <para>
    /// Metrics are observational only and must not perform resolution, change resolver
    /// output, modify execution state, or influence runtime behavior.
    /// </para>
    /// </remarks>
    public sealed class AiResolverMetrics : IAiResolverMetrics
    {
        private const string Category = "Resolver";

        private readonly IAiRuntimeMetricWriter _metricWriter;

        private long _resolvedStartedCount;
        private long _resolvedSuccessCount;
        private long _resolvedMissCount;
        private long _resolvedFailedCount;

        private readonly ConcurrentDictionary<string, long> _operationsByPath =
            new(StringComparer.Ordinal);

        private readonly ConcurrentDictionary<string, long> _failuresByExceptionType =
            new(StringComparer.Ordinal);

        /// <summary>
        /// Initializes a new instance of the <see cref="AiResolverMetrics"/> class.
        /// </summary>
        /// <param name="metricWriter">The correlated runtime metric writer.</param>
        public AiResolverMetrics(
            IAiRuntimeMetricWriter metricWriter)
        {
            _metricWriter = metricWriter
                ?? throw new ArgumentNullException(nameof(metricWriter));
        }

        /// <inheritdoc />
        public void RecordResolveStarted(
            string executionId,
            string stepId,
            string path)
        {
            Interlocked.Increment(ref _resolvedStartedCount);

            IncrementPath(path);

            RecordMetric(
                "resolver.started",
                executionId,
                stepId,
                path);
        }

        /// <inheritdoc />
        public void RecordResolveSuccess(
            string executionId,
            string stepId,
            string path)
        {
            Interlocked.Increment(ref _resolvedSuccessCount);

            IncrementPath(path);

            RecordMetric(
                "resolver.success",
                executionId,
                stepId,
                path);
        }

        /// <inheritdoc />
        public void RecordResolveMiss(
            string executionId,
            string stepId,
            string path)
        {
            Interlocked.Increment(ref _resolvedMissCount);

            IncrementPath(path);

            RecordMetric(
                "resolver.miss",
                executionId,
                stepId,
                path);
        }

        /// <inheritdoc />
        public void RecordResolveFailed(
            string executionId,
            string stepId,
            string path,
            Exception exception)
        {
            Interlocked.Increment(ref _resolvedFailedCount);

            IncrementPath(path);

            var exceptionType = exception?.GetType().Name ?? "unknown";

            _failuresByExceptionType.AddOrUpdate(
                exceptionType,
                _ => 1,
                (_, current) => current + 1);

            RecordMetric(
                "resolver.failed",
                executionId,
                stepId,
                path,
                new Dictionary<string, string>
                {
                    [AiExceptionMetadataKeys.ExceptionType] = exceptionType,
                    [AiExceptionMetadataKeys.ExceptionMessage] = exception?.Message ?? string.Empty
                });
        }

        /// <summary>
        /// Gets the number of started resolver operations.
        /// </summary>
        public long ResolvedStartedCount => Interlocked.Read(ref _resolvedStartedCount);

        /// <summary>
        /// Gets the number of successful resolver operations.
        /// </summary>
        public long ResolvedSuccessCount => Interlocked.Read(ref _resolvedSuccessCount);

        /// <summary>
        /// Gets the number of resolver misses.
        /// </summary>
        public long ResolvedMissCount => Interlocked.Read(ref _resolvedMissCount);

        /// <summary>
        /// Gets the number of failed resolver operations.
        /// </summary>
        public long ResolvedFailedCount => Interlocked.Read(ref _resolvedFailedCount);

        /// <summary>
        /// Gets resolver operations grouped by path.
        /// </summary>
        public IReadOnlyDictionary<string, long> OperationsByPath => _operationsByPath;

        /// <summary>
        /// Gets resolver failures grouped by exception type.
        /// </summary>
        public IReadOnlyDictionary<string, long> FailuresByExceptionType => _failuresByExceptionType;

        /// <summary>
        /// Records a correlated append-only resolver metric without blocking the caller.
        /// </summary>
        /// <param name="name">The metric name.</param>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="stepId">The step identifier.</param>
        /// <param name="path">The resolved path.</param>
        /// <param name="additionalTags">The optional additional tags.</param>
        private void RecordMetric(
            string name,
            string executionId,
            string stepId,
            string path,
            IReadOnlyDictionary<string, string>? additionalTags = null)
        {
            var tags = new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                [AiExecutionMetadataKeys.ExecutionId] = executionId ?? string.Empty,
                [AiStepMetadataKeys.StepId] = stepId ?? string.Empty,
                ["path"] = Normalize(path)
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
        /// Increments the path dimensional counter.
        /// </summary>
        /// <param name="path">The resolver path.</param>
        private void IncrementPath(
            string path)
        {
            var key = Normalize(path);

            _operationsByPath.AddOrUpdate(
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