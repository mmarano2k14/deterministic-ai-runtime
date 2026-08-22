using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Metrics.Policy;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.Abstractions.AI.Policies;

namespace Multiplexed.AI.Runtime.Observability.Metrics.Policy
{
    /// <summary>
    /// In-memory implementation of <see cref="IAiPolicyMetrics"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This implementation tracks policy execution frequency, execution outcomes,
    /// policy decisions, failures, and execution duration.
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
    /// Metrics are observational only and must not influence policy execution,
    /// policy decisions, retries, blocking, retention, throttling, or runtime flow.
    /// </para>
    /// </remarks>
    public sealed class AiPolicyMetrics : IAiPolicyMetrics
    {
        private const string Category = "Policy";

        private readonly IAiRuntimeMetricWriter _metricWriter;

        private long _policyExecutionCount;
        private long _policySuccessCount;
        private long _policyFailureCount;
        private long _policyRetryCount;
        private long _policyBlockCount;
        private long _lastExecutionDurationMs;
        private long _totalExecutionDurationMs;
        private string? _lastPolicyName;
        private long _lastExecutionTimestamp;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiPolicyMetrics"/> class.
        /// </summary>
        /// <param name="metricWriter">The correlated runtime metric writer.</param>
        public AiPolicyMetrics(
            IAiRuntimeMetricWriter metricWriter)
        {
            _metricWriter = metricWriter
                ?? throw new ArgumentNullException(nameof(metricWriter));
        }

        /// <inheritdoc />
        public void RecordExecution(
            string executionId,
            string policyName,
            bool success,
            TimeSpan duration)
        {
            Interlocked.Increment(ref _policyExecutionCount);

            if (success)
            {
                Interlocked.Increment(ref _policySuccessCount);
            }
            else
            {
                Interlocked.Increment(ref _policyFailureCount);
            }

            var durationMs = (long)Math.Max(
                0,
                duration.TotalMilliseconds);

            Interlocked.Exchange(
                ref _lastExecutionDurationMs,
                durationMs);

            Interlocked.Add(
                ref _totalExecutionDurationMs,
                durationMs);

            _lastPolicyName = policyName;

            Interlocked.Exchange(
                ref _lastExecutionTimestamp,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            RecordMetric(
                "policy.execution",
                executionId,
                policyName,
                new Dictionary<string, string>
                {
                    ["success"] = success.ToString(),
                    [AiObservabilityMetadataKeys.DottedDurationMs] = durationMs.ToString()
                },
                value: durationMs > 0 ? durationMs : 1);
        }

        /// <inheritdoc />
        public void RecordFailure(
            string executionId,
            string policyName)
        {
            Interlocked.Increment(ref _policyFailureCount);

            _lastPolicyName = policyName;

            Interlocked.Exchange(
                ref _lastExecutionTimestamp,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            RecordMetric(
                "policy.failure",
                executionId,
                policyName);
        }

        /// <inheritdoc />
        public void RecordDecision(
            string executionId,
            string policyName,
            AiPolicyResultKind kind)
        {
            switch (kind)
            {
                case AiPolicyResultKind.Retry:
                    Interlocked.Increment(ref _policyRetryCount);
                    break;

                case AiPolicyResultKind.Block:
                    Interlocked.Increment(ref _policyBlockCount);
                    break;

                case AiPolicyResultKind.Success:
                default:
                    break;
            }

            _lastPolicyName = policyName;

            Interlocked.Exchange(
                ref _lastExecutionTimestamp,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            RecordMetric(
                "policy.decision",
                executionId,
                policyName,
                new Dictionary<string, string>
                {
                    [AiPolicyMetadataKeys.ResultKind] = kind.ToString()
                });
        }

        /// <summary>
        /// Gets the total number of policy executions.
        /// </summary>
        public long PolicyExecutionCount => Interlocked.Read(ref _policyExecutionCount);

        /// <summary>
        /// Gets the number of successful policy executions.
        /// </summary>
        public long PolicySuccessCount => Interlocked.Read(ref _policySuccessCount);

        /// <summary>
        /// Gets the number of failed policy executions.
        /// </summary>
        public long PolicyFailureCount => Interlocked.Read(ref _policyFailureCount);

        /// <summary>
        /// Gets the number of retry decisions produced by policies.
        /// </summary>
        public long PolicyRetryCount => Interlocked.Read(ref _policyRetryCount);

        /// <summary>
        /// Gets the number of block decisions produced by policies.
        /// </summary>
        public long PolicyBlockCount => Interlocked.Read(ref _policyBlockCount);

        /// <summary>
        /// Gets the duration in milliseconds of the last policy execution.
        /// </summary>
        public long LastExecutionDurationMs => Interlocked.Read(ref _lastExecutionDurationMs);

        /// <summary>
        /// Gets the total accumulated policy execution duration in milliseconds.
        /// </summary>
        public long TotalExecutionDurationMs => Interlocked.Read(ref _totalExecutionDurationMs);

        /// <summary>
        /// Gets the name of the last executed policy.
        /// </summary>
        public string? LastPolicyName => _lastPolicyName;

        /// <summary>
        /// Gets the Unix timestamp in milliseconds of the last policy execution.
        /// </summary>
        public long LastExecutionTimestamp => Interlocked.Read(ref _lastExecutionTimestamp);

        /// <summary>
        /// Records a correlated append-only policy metric without blocking the caller.
        /// </summary>
        /// <param name="name">The metric name.</param>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="policyName">The policy name.</param>
        /// <param name="additionalTags">The optional additional tags.</param>
        /// <param name="value">The metric value.</param>
        private void RecordMetric(
            string name,
            string executionId,
            string policyName,
            IReadOnlyDictionary<string, string>? additionalTags = null,
            double value = 1)
        {
            var tags = new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                [AiExecutionMetadataKeys.ExecutionId] = executionId ?? string.Empty,
                [AiPolicyMetadataKeys.Name] = policyName ?? string.Empty
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