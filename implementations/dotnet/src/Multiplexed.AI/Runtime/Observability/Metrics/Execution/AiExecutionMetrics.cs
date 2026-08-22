using Multiplexed.Abstractions.AI.Execution;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Metrics.Execution;
using Multiplexed.Abstractions.AI.Observability.Events;

namespace Multiplexed.AI.Runtime.Observability.Metrics.Execution
{
    /// <summary>
    /// In-memory implementation of <see cref="IAiExecutionMetrics"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This implementation provides observability over execution lifecycle,
    /// step execution activity, retry, recovery, claim, and finalization behavior.
    /// </para>
    ///
    /// <para>
    /// The implementation is safe for singleton usage. Scalar counters use atomic
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
    /// Metrics are strictly observational and must not influence execution decisions,
    /// retries, recovery, finalization, scheduling, or state transitions.
    /// </para>
    /// </remarks>
    public sealed class AiExecutionMetrics : IAiExecutionMetrics
    {
        private const string Category = "Execution";

        private readonly IAiRuntimeMetricWriter _metricWriter;

        private long _executionStartedCount;
        private long _executionCompletedCount;
        private long _executionFailedCount;
        private long _stepClaimedCount;
        private long _stepClaimMissCount;
        private long _stepCompletedCount;
        private long _stepFailedCount;
        private long _stepRetriedCount;
        private long _stepRecoveredCount;
        private long _finalizeAttemptCount;
        private long _finalizeSuccessCount;
        private long _finalizeConflictCount;

        private readonly ConcurrentDictionary<string, long> _retryByStep =
            new(StringComparer.Ordinal);

        private readonly ConcurrentDictionary<string, long> _claimSuccessByStep =
            new(StringComparer.Ordinal);

        private readonly ConcurrentDictionary<string, long> _recoveryByExecution =
            new(StringComparer.Ordinal);

        /// <summary>
        /// Initializes a new instance of the <see cref="AiExecutionMetrics"/> class.
        /// </summary>
        /// <param name="metricWriter">The correlated runtime metric writer.</param>
        public AiExecutionMetrics(
            IAiRuntimeMetricWriter metricWriter)
        {
            _metricWriter = metricWriter
                ?? throw new ArgumentNullException(nameof(metricWriter));
        }

        /// <inheritdoc />
        public void RecordExecutionStarted(
            string executionId)
        {
            Interlocked.Increment(ref _executionStartedCount);

            RecordMetric(
                AiEngineEvents.Execution.Started,
                executionId);
        }

        /// <inheritdoc />
        public void RecordExecutionCompleted(
            string executionId)
        {
            Interlocked.Increment(ref _executionCompletedCount);

            RecordMetric(
                AiEngineEvents.Execution.Completed,
                executionId);
        }

        /// <inheritdoc />
        public void RecordExecutionFailed(
            string executionId)
        {
            Interlocked.Increment(ref _executionFailedCount);

            RecordMetric(
                AiEngineEvents.Execution.Failed,
                executionId);
        }

        /// <inheritdoc />
        public void RecordStepClaimed(
            string executionId,
            string stepId)
        {
            Interlocked.Increment(ref _stepClaimedCount);

            IncrementDimension(
                _claimSuccessByStep,
                stepId);

            RecordMetric(
                "step.claimed",
                executionId,
                new Dictionary<string, string>
                {
                    [AiStepMetadataKeys.StepId] = stepId ?? string.Empty
                });
        }

        /// <inheritdoc />
        public void RecordStepClaimMiss(
            string executionId)
        {
            Interlocked.Increment(ref _stepClaimMissCount);

            RecordMetric(
                "step.claim_miss",
                executionId);
        }

        /// <inheritdoc />
        public void RecordStepCompleted(
            string executionId,
            string stepId)
        {
            Interlocked.Increment(ref _stepCompletedCount);

            RecordMetric(
                AiEngineEvents.Step.Completed,
                executionId,
                new Dictionary<string, string>
                {
                    [AiStepMetadataKeys.StepId] = stepId ?? string.Empty
                });
        }

        /// <inheritdoc />
        public void RecordStepFailed(
            string executionId,
            string stepId)
        {
            Interlocked.Increment(ref _stepFailedCount);

            RecordMetric(
                AiEngineEvents.Step.Failed,
                executionId,
                new Dictionary<string, string>
                {
                    [AiStepMetadataKeys.StepId] = stepId ?? string.Empty
                });
        }

        /// <inheritdoc />
        public void RecordStepRetried(
            string executionId,
            string stepId)
        {
            Interlocked.Increment(ref _stepRetriedCount);

            IncrementDimension(
                _retryByStep,
                stepId);

            RecordMetric(
                "step.retried",
                executionId,
                new Dictionary<string, string>
                {
                    [AiStepMetadataKeys.StepId] = stepId ?? string.Empty
                });
        }

        /// <inheritdoc />
        public void RecordStepRecovered(
            string executionId,
            string stepId)
        {
            Interlocked.Increment(ref _stepRecoveredCount);

            IncrementDimension(
                _recoveryByExecution,
                executionId);

            RecordMetric(
                "step.recovered",
                executionId,
                new Dictionary<string, string>
                {
                    [AiStepMetadataKeys.StepId] = stepId ?? string.Empty
                });
        }

        /// <inheritdoc />
        public void RecordStepsRecovered(
            string executionId,
            int recoveredCount)
        {
            if (recoveredCount <= 0)
            {
                return;
            }

            Interlocked.Add(
                ref _stepRecoveredCount,
                recoveredCount);

            IncrementDimension(
                _recoveryByExecution,
                executionId,
                recoveredCount);

            RecordMetric(
                "steps.recovered",
                executionId,
                new Dictionary<string, string>
                {
                    ["recovered.count"] = recoveredCount.ToString()
                },
                value: recoveredCount);
        }

        /// <inheritdoc />
        public void RecordFinalizeAttempt(
            string executionId)
        {
            Interlocked.Increment(ref _finalizeAttemptCount);

            RecordMetric(
                "finalize.attempt",
                executionId);
        }

        /// <inheritdoc />
        public void RecordFinalizeSuccess(
            string executionId)
        {
            Interlocked.Increment(ref _finalizeSuccessCount);

            RecordMetric(
                "finalize.success",
                executionId);
        }

        /// <inheritdoc />
        public void RecordFinalizeConflict(
            string executionId)
        {
            Interlocked.Increment(ref _finalizeConflictCount);

            RecordMetric(
                "finalize.conflict",
                executionId);
        }

        /// <summary>
        /// Gets the total number of executions that have started.
        /// </summary>
        public long ExecutionStartedCount => Interlocked.Read(ref _executionStartedCount);

        /// <summary>
        /// Gets the total number of executions that completed successfully.
        /// </summary>
        public long ExecutionCompletedCount => Interlocked.Read(ref _executionCompletedCount);

        /// <summary>
        /// Gets the total number of executions that failed.
        /// </summary>
        public long ExecutionFailedCount => Interlocked.Read(ref _executionFailedCount);

        /// <summary>
        /// Gets the total number of successful step claims.
        /// </summary>
        public long StepClaimedCount => Interlocked.Read(ref _stepClaimedCount);

        /// <summary>
        /// Gets the total number of claim misses.
        /// </summary>
        public long StepClaimMissCount => Interlocked.Read(ref _stepClaimMissCount);

        /// <summary>
        /// Gets the total number of successfully completed steps.
        /// </summary>
        public long StepCompletedCount => Interlocked.Read(ref _stepCompletedCount);

        /// <summary>
        /// Gets the total number of failed step executions.
        /// </summary>
        public long StepFailedCount => Interlocked.Read(ref _stepFailedCount);

        /// <summary>
        /// Gets the total number of step retries.
        /// </summary>
        public long StepRetriedCount => Interlocked.Read(ref _stepRetriedCount);

        /// <summary>
        /// Gets the total number of recovered steps.
        /// </summary>
        public long StepRecoveredCount => Interlocked.Read(ref _stepRecoveredCount);

        /// <summary>
        /// Gets the total number of finalization attempts.
        /// </summary>
        public long FinalizeAttemptCount => Interlocked.Read(ref _finalizeAttemptCount);

        /// <summary>
        /// Gets the total number of successful finalizations.
        /// </summary>
        public long FinalizeSuccessCount => Interlocked.Read(ref _finalizeSuccessCount);

        /// <summary>
        /// Gets the total number of finalization conflicts.
        /// </summary>
        public long FinalizeConflictCount => Interlocked.Read(ref _finalizeConflictCount);

        /// <inheritdoc />
        public IReadOnlyDictionary<string, long> GetRetryByStep()
        {
            return Snapshot(_retryByStep);
        }

        /// <inheritdoc />
        public IReadOnlyDictionary<string, long> GetClaimSuccessByStep()
        {
            return Snapshot(_claimSuccessByStep);
        }

        /// <inheritdoc />
        public IReadOnlyDictionary<string, long> GetRecoveryByExecution()
        {
            return Snapshot(_recoveryByExecution);
        }

        /// <summary>
        /// Records a correlated append-only execution metric without blocking the caller.
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
            if (amount <= 0 || string.IsNullOrWhiteSpace(key))
            {
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