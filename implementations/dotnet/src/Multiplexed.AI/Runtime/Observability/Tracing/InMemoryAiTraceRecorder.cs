using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Observability.Tracing.Store;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.Abstractions.AI.Execution.Scheduling;


namespace Multiplexed.AI.Runtime.Observability.Tracing
{
    /// <summary>
    /// In-memory implementation of <see cref="IAiTraceRecorder"/>.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Stores completed AI runtime trace records in memory.
    /// - Feeds the trace timeline from completed trace records.
    /// - Optionally writes completed trace records to a configured trace store.
    ///
    /// NAMING:
    /// - Category identifies the runtime component.
    /// - Name identifies the normalized operation result.
    ///
    /// EXAMPLES:
    /// - dag-store / claim.succeeded
    /// - dag-store / complete.succeeded
    /// - retention / retention.succeeded
    /// - step / execute.succeeded
    /// - execution / execution.succeeded
    ///
    /// IMPORTANT:
    /// - This implementation is process-local and non-durable.
    /// - It is suitable for tests, diagnostics, local runtime inspection, and early UI work.
    /// - Durable/exported tracing should use another recorder or exporter implementation later.
    /// - Trace store writes are best-effort and must not break runtime execution.
    /// </remarks>
    public sealed class InMemoryAiTraceRecorder : IAiTraceRecorder
    {
        private readonly ConcurrentQueue<AiTraceRecord> _records = new();
        private readonly IAiTraceTimeline _timeline;
        private readonly IAiRuntimeTraceStore? _traceStore;

        /// <summary>
        /// Initializes a new instance of the <see cref="InMemoryAiTraceRecorder"/> class.
        /// </summary>
        /// <param name="timeline">The timeline receiving projected trace events.</param>
        public InMemoryAiTraceRecorder(
            IAiTraceTimeline timeline)
            : this(
                timeline,
                traceStore: null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="InMemoryAiTraceRecorder"/> class.
        /// </summary>
        /// <param name="timeline">The timeline receiving projected trace events.</param>
        /// <param name="traceStore">The optional trace store receiving completed trace records.</param>
        public InMemoryAiTraceRecorder(
            IAiTraceTimeline timeline,
            IAiRuntimeTraceStore? traceStore)
        {
            _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
            _traceStore = traceStore;
        }

        /// <inheritdoc />
        public void Record(
            AiTraceRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);

            _records.Enqueue(record);

            TryAppendToStore(record);

            var executionId = ResolveExecutionId(record);

            if (string.IsNullOrWhiteSpace(executionId))
            {
                return;
            }

            _timeline.Add(new AiTraceEvent
            {
                ExecutionId = executionId,
                TimestampUtc = record.CompletedAtUtc ?? DateTime.UtcNow,
                Category = ResolveCategory(record),
                Name = ResolveEventName(record),
                StepId = ResolveStepId(record),
                Correlation = record.Correlation,
                Tags = BuildTimelineTags(record)
            });
        }

        /// <inheritdoc />
        public IReadOnlyList<AiTraceRecord> Snapshot()
        {
            return _records.ToArray().ToList();
        }

        /// <summary>
        /// Attempts to append the trace record to the configured trace store.
        /// </summary>
        /// <param name="record">The completed trace record.</param>
        /// <remarks>
        /// Trace persistence is observational and best-effort. Store failures must not
        /// break runtime execution or timeline recording.
        /// </remarks>
        private void TryAppendToStore(
            AiTraceRecord record)
        {
            if (_traceStore is null)
            {
                return;
            }

            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await _traceStore.AppendAsync(
                                record,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // Best-effort trace persistence.
                        // Tracing must not break runtime execution.
                    }
                });
        }

        private static string? ResolveExecutionId(
            AiTraceRecord record)
        {
            return FirstNonEmpty(
                record.ExecutionId,
                record.Correlation?.Runtime?.ExecutionId,
                record.Correlation?.Runtime?.RunId,
                record.Correlation?.Runtime?.CorrelationId);
        }

        private static string? ResolveStepId(
            AiTraceRecord record)
        {
            return FirstNonEmpty(
                record.StepId,
                record.Correlation?.StepId);
        }

        private static string ResolveCategory(
            AiTraceRecord record)
        {
            if (record.Tags.TryGetValue("component", out var component) &&
                component is string componentName &&
                !string.IsNullOrWhiteSpace(componentName))
            {
                return NormalizeComponent(componentName);
            }

            return record.Operation switch
            {
                "execution" => "execution",
                "step" => "step",
                "storage" => "dag-store",
                "retention" => "retention",
                "resolver" => "resolver",
                _ => "runtime"
            };
        }

        private static string ResolveEventName(
            AiTraceRecord record)
        {
            var result = record.Failed
                ? "failed"
                : record.Succeeded
                    ? "succeeded"
                    : "completed";

            if (record.Tags.TryGetValue("operation", out var taggedOperation) &&
                taggedOperation is string operation &&
                !string.IsNullOrWhiteSpace(operation))
            {
                return $"{operation}.{result}";
            }

            return record.Operation switch
            {
                "step" => $"execute.{result}",
                "execution" => $"execution.{result}",
                "retention" => $"retention.{result}",
                "resolver" => $"resolve.{result}",
                _ => $"{record.Operation}.{result}"
            };
        }

        private static string ResolveOperationName(
            AiTraceRecord record)
        {
            if (record.Tags.TryGetValue("operation", out var taggedOperation) &&
                taggedOperation is string operation &&
                !string.IsNullOrWhiteSpace(operation))
            {
                return NormalizeOperation(operation);
            }

            return record.Operation switch
            {
                "step" => "execute",
                "execution" => "execution",
                "retention" => "retention",
                "resolver" => "resolve",
                "storage" => "operation",
                _ => NormalizeOperation(record.Operation)
            };
        }

        private static string NormalizeComponent(
            string component)
        {
            return component.Trim().ToLowerInvariant() switch
            {
                "storage" => "dag-store",
                "dagstore" => "dag-store",
                "dag-store" => "dag-store",
                "redis" => "dag-store",
                "redis-dag-store" => "dag-store",
                "execution" => "execution",
                "step" => "step",
                "retention" => "retention",
                "resolver" => "resolver",
                "payload" => "payload",
                _ => component.Trim().ToLowerInvariant()
            };
        }

        private static string NormalizeOperation(
            string operation)
        {
            return operation.Trim() switch
            {
                "TryClaimNextReadyStep" => "claim",
                "TryCompleteStep" => "complete",
                "TryFailStepException" => "fail.exception",
                "TryFailStepResult" => "fail.result",
                "RecoverTimedOutSteps" => "recover",
                "TryFinalizeExecution" => "finalize",
                "ApplyRetentionPersistAndWarm" => "retention",
                _ => operation.Trim()
                    .Replace(" ", "-", StringComparison.Ordinal)
                    .ToLowerInvariant()
            };
        }

        private static IDictionary<string, object?> BuildTimelineTags(
            AiTraceRecord record)
        {
            var tags = new Dictionary<string, object?>(record.Tags, StringComparer.OrdinalIgnoreCase)
            {
                ["traceId"] = record.Id,
                ["rawOperation"] = record.Operation,
                ["category"] = ResolveCategory(record),
                ["name"] = ResolveEventName(record),
                ["startedAtUtc"] = record.StartedAtUtc,
                ["completedAtUtc"] = record.CompletedAtUtc,
                [AiObservabilityMetadataKeys.DurationMs] = record.Duration?.TotalMilliseconds,
                ["succeeded"] = record.Succeeded,
                ["failed"] = record.Failed
            };

            AddCorrelationTags(
                tags,
                record.Correlation);

            if (!string.IsNullOrWhiteSpace(record.ErrorType))
            {
                tags["errorType"] = record.ErrorType;
            }

            if (!string.IsNullOrWhiteSpace(record.ErrorMessage))
            {
                tags["errorMessage"] = record.ErrorMessage;
            }

            if (record.Tags.TryGetValue("compactedCount", out var compact))
            {
                tags["compactedCount"] = compact;
            }

            if (record.Tags.TryGetValue("evictedCount", out var evict))
            {
                tags["evictedCount"] = evict;
            }

            if (record.Tags.TryGetValue("removedSteps", out var removed))
            {
                tags["removedSteps"] = removed;
            }

            if (record.Tags.TryGetValue("skipped", out var skipped))
            {
                tags["skipped"] = skipped;
            }

            return tags;
        }

        private static void AddCorrelationTags(
            IDictionary<string, object?> tags,
            AiRuntimeTraceCorrelationContext? correlation)
        {
            if (correlation is null)
            {
                return;
            }

            AddTagIfMissing(tags, AiObservabilityMetadataKeys.CamelCaseCorrelationId, correlation.Runtime?.CorrelationId);
            AddTagIfMissing(tags, AiRunMetadataKeys.CamelCaseRunId, correlation.Runtime?.RunId);
            AddTagIfMissing(tags, AiExecutionMetadataKeys.CamelCaseExecutionId, correlation.Runtime?.ExecutionId);
            AddTagIfMissing(tags, AiPipelineMetadataKeys.CamelCasePipelineName, correlation.Runtime?.PipelineName);
            AddTagIfMissing(tags, AiPipelineMetadataKeys.CamelCasePipelineVersion, correlation.Runtime?.PipelineVersion);
            AddTagIfMissing(tags, AiPipelineMetadataKeys.CamelCasePipelineKey, correlation.Runtime?.PipelineKey);
            AddTagIfMissing(tags, AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId, correlation.Runtime?.RuntimeInstanceId);
            AddTagIfMissing(tags, AiWorkerMetadataKeys.CamelCaseWorkerId, correlation.Runtime?.WorkerId);

            AddTagIfMissing(tags, AiStepMetadataKeys.CamelCaseStepId, correlation.StepId);
            AddTagIfMissing(tags, AiStepMetadataKeys.CamelCaseStepKey, correlation.StepKey);
            AddTagIfMissing(tags, AiExecutionClaimMetadataKeys.CamelCaseClaimToken, correlation.ClaimToken);
            AddTagIfMissing(tags, "policyKey", correlation.PolicyKey);
            AddTagIfMissing(tags, "provider", correlation.Provider);
            AddTagIfMissing(tags, "model", correlation.Model);
            AddTagIfMissing(tags, "operation", correlation.Operation);
            AddTagIfMissing(tags, "inputPayloadRef", correlation.InputPayloadRef);
            AddTagIfMissing(tags, "outputPayloadRef", correlation.OutputPayloadRef);
            AddTagIfMissing(tags, "humanInputRef", correlation.HumanInputRef);
            AddTagIfMissing(tags, "promptRef", correlation.PromptRef);
            AddTagIfMissing(tags, "distributedTraceId", correlation.TraceId);
            AddTagIfMissing(tags, "traceScopeId", correlation.TraceScopeId);
            AddTagIfMissing(tags, "parentTraceScopeId", correlation.ParentTraceScopeId);
            AddTagIfMissing(tags, "traceSource", correlation.Source);
        }

        private static void AddTagIfMissing(
            IDictionary<string, object?> tags,
            string key,
            string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (tags.ContainsKey(key))
            {
                return;
            }

            tags[key] = value;
        }

        private static string? FirstNonEmpty(
            params string?[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return null;
        }
    }
}