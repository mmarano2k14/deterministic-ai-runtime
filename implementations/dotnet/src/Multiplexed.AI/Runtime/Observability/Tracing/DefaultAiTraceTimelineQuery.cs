using Microsoft.Extensions.Logging;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Observability.Tracing.Store;

namespace Multiplexed.AI.Runtime.Observability.Tracing
{
    /// <summary>
    /// Queries trace events from the process-local timeline first, then falls back to the durable trace store.
    /// </summary>
    /// <remarks>
    /// This query abstraction keeps the MCP observability tools independent from the storage topology.
    /// In single-process scenarios, traces are usually available through <see cref="IAiTraceTimeline"/>.
    /// In multi-process scenarios, such as process-hosted runtime instances, the parent MCP process cannot
    /// see the child process in-memory timeline, so the query falls back to <see cref="IAiRuntimeTraceStore"/>.
    /// </remarks>
    public sealed class DefaultAiTraceTimelineQuery : IAiTraceTimelineQuery
    {
        private readonly IAiTraceTimeline traceTimeline;
        private readonly IAiRuntimeTraceStore traceStore;
        private readonly ILogger<DefaultAiTraceTimelineQuery> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultAiTraceTimelineQuery"/> class.
        /// </summary>
        /// <param name="traceTimeline">The process-local trace timeline.</param>
        /// <param name="traceStore">The durable runtime trace store.</param>
        /// <param name="logger">The logger.</param>
        public DefaultAiTraceTimelineQuery(
            IAiTraceTimeline traceTimeline,
            IAiRuntimeTraceStore traceStore,
            ILogger<DefaultAiTraceTimelineQuery> logger)
        {
            this.traceTimeline = traceTimeline
                ?? throw new ArgumentNullException(nameof(traceTimeline));

            this.traceStore = traceStore
                ?? throw new ArgumentNullException(nameof(traceStore));

            this.logger = logger
                ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Gets ordered trace timeline events for an execution.
        /// </summary>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The ordered trace events for the execution.</returns>
        public async Task<IReadOnlyList<AiTraceEvent>> GetByExecutionAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            var inMemoryEvents =
                this.traceTimeline.Get(executionId);

            if (inMemoryEvents.Count > 0)
            {
                return inMemoryEvents;
            }

            this.logger.LogDebug(
                "No process-local trace events found. Falling back to durable trace store. ExecutionId={ExecutionId}",
                executionId);

            var durableRecords =
                await this.traceStore
                    .GetByExecutionAsync(
                        executionId,
                        cancellationToken)
                    .ConfigureAwait(false);

            return durableRecords
                .Select(ToTraceEvent)
                .OrderBy(traceEvent => traceEvent.TimestampUtc)
                .ToArray();
        }

        /// <summary>
        /// Converts a durable trace record into a trace timeline event.
        /// </summary>
        /// <param name="record">The durable trace record.</param>
        /// <returns>The trace event representation.</returns>
        private static AiTraceEvent ToTraceEvent(
            AiTraceRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);

            var tags =
                new Dictionary<string, object?>(
                    record.Tags,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["trace.id"] = record.Id,
                    ["trace.operation"] = record.Operation,
                    ["trace.succeeded"] = record.Succeeded,
                    ["trace.failed"] = record.Failed
                };

            if (record.CompletedAtUtc.HasValue)
            {
                tags["trace.completedAtUtc"] = record.CompletedAtUtc.Value;
            }

            if (record.Duration.HasValue)
            {
                tags["trace.durationMs"] = record.Duration.Value.TotalMilliseconds;
            }

            if (!string.IsNullOrWhiteSpace(record.ErrorType))
            {
                tags["trace.errorType"] = record.ErrorType;
            }

            if (!string.IsNullOrWhiteSpace(record.ErrorMessage))
            {
                tags["trace.errorMessage"] = record.ErrorMessage;
            }

            return new AiTraceEvent
            {
                ExecutionId = record.ExecutionId ?? string.Empty,
                StepId = record.StepId,
                TimestampUtc = record.StartedAtUtc,
                Category = ResolveCategory(record),
                Name = ResolveName(record),
                Tags = tags,
                Correlation = record.Correlation
            };
        }

        /// <summary>
        /// Resolves the timeline event category from a durable trace record.
        /// </summary>
        /// <param name="record">The durable trace record.</param>
        /// <returns>The resolved trace event category.</returns>
        private static string ResolveCategory(
            AiTraceRecord record)
        {
            if (!string.IsNullOrWhiteSpace(record.StepId))
            {
                return "Step";
            }

            if (record.Operation.Contains("storage", StringComparison.OrdinalIgnoreCase))
            {
                return "Storage";
            }

            if (record.Operation.Contains("retention", StringComparison.OrdinalIgnoreCase))
            {
                return "Retention";
            }

            if (record.Operation.Contains("policy", StringComparison.OrdinalIgnoreCase))
            {
                return "Policy";
            }

            return "Execution";
        }

        /// <summary>
        /// Resolves the timeline event name from a durable trace record.
        /// </summary>
        /// <param name="record">The durable trace record.</param>
        /// <returns>The resolved trace event name.</returns>
        private static string ResolveName(
            AiTraceRecord record)
        {
            if (record.Failed)
            {
                return $"{record.Operation}.failed";
            }

            if (record.Succeeded)
            {
                return $"{record.Operation}.completed";
            }

            return record.Operation;
        }
    }
}