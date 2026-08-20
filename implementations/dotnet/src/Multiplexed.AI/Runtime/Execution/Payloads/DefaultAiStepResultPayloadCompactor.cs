using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Payloads;
using Multiplexed.Abstractions.AI.Execution.Payloads.Metrics;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.Metrics;

namespace Multiplexed.AI.Runtime.Execution.Payloads
{
    /// <summary>
    /// Centralized payload compaction for all step results.
    ///
    /// PURPOSE:
    /// - Ensures all execution paths (DAG, operations, RAG, etc.) use the same compaction logic.
    /// - Prevents bypass when steps do not go through AiStepExecutor.
    /// - Keeps Redis/Lua and snapshots small by externalizing large result values.
    ///
    /// IMPORTANT:
    /// - Externalized values are NOT removed without trace.
    /// - The result keeps a minimal summary in Value/Data so the state remains inspectable.
    /// - The full content is stored in Payload/DataPayloads and resolved through IAiExecutionPayloadResolver.
    /// - The original CompactAsync(AiStepResult, CancellationToken) signature is preserved for compatibility.
    /// </summary>
    public sealed class DefaultAiStepResultPayloadCompactor : IAiStepResultPayloadCompactor
    {
        private readonly IAiExecutionDataPolicy _dataPolicy;
        private readonly IAiPayloadMetrics _payloadMetrics;
        private readonly IAiRuntimeMetrics? _runtimeMetrics;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultAiStepResultPayloadCompactor"/> class.
        ///
        /// PURPOSE:
        /// - Preserves the original constructor signature.
        /// - Allows existing tests, DI registrations, and callers to continue working unchanged.
        /// </summary>
        public DefaultAiStepResultPayloadCompactor(
            IAiExecutionDataPolicy dataPolicy,
            IAiPayloadMetrics payloadMetrics)
        {
            _dataPolicy = dataPolicy ?? throw new ArgumentNullException(nameof(dataPolicy));
            _payloadMetrics = payloadMetrics ?? throw new ArgumentNullException(nameof(payloadMetrics));
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultAiStepResultPayloadCompactor"/> class
        /// with runtime metrics support.
        ///
        /// PURPOSE:
        /// - Preserves existing payload metrics.
        /// - Adds runtime storage observability when <see cref="IAiRuntimeMetrics"/> is available.
        /// </summary>
        public DefaultAiStepResultPayloadCompactor(
            IAiExecutionDataPolicy dataPolicy,
            IAiPayloadMetrics payloadMetrics,
            IAiRuntimeMetrics runtimeMetrics)
            : this(dataPolicy, payloadMetrics)
        {
            _runtimeMetrics = runtimeMetrics ?? throw new ArgumentNullException(nameof(runtimeMetrics));
        }

        /// <inheritdoc />
        public Task CompactAsync(
            AiStepResult result,
            CancellationToken cancellationToken = default)
        {
            return CompactInternalAsync(
                executionId: null,
                stepId: null,
                result,
                cancellationToken);
        }

        /// <summary>
        /// Compacts a step result and records runtime storage metrics using execution and step identity.
        ///
        /// PURPOSE:
        /// - Allows DAG / distributed execution paths to emit storage metrics with execution context.
        /// - Keeps the original interface method backward compatible.
        /// </summary>
        public Task CompactAsync(
            string executionId,
            string stepId,
            AiStepResult result,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(stepId);

            return CompactInternalAsync(
                executionId,
                stepId,
                result,
                cancellationToken);
        }

        /// <summary>
        /// Applies payload compaction to Value and Data entries.
        /// </summary>
        private async Task CompactInternalAsync(
            string? executionId,
            string? stepId,
            AiStepResult result,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(result);

            if (result.Value is not null && result.Payload is null)
            {
                var payload = await _dataPolicy.StoreAsync(
                    result.Value,
                    cancellationToken);

                result.Payload = payload;
                RecordPayloadCompaction(payload, executionId, stepId);

                if (!payload.IsInline)
                {
                    result.Value = CreatePayloadSummary(payload);
                }
            }

            if (result.Data is null || result.Data.Count == 0)
            {
                return;
            }

            foreach (var entry in result.Data.ToList())
            {
                if (entry.Value is null)
                {
                    continue;
                }

                var payload = await _dataPolicy.StoreAsync(
                    entry.Value,
                    cancellationToken);

                RecordPayloadCompaction(payload, executionId, stepId);

                if (payload.IsInline)
                {
                    continue;
                }

                result.DataPayloads ??= new Dictionary<string, AiStoredPayload>(StringComparer.Ordinal);
                result.DataPayloads[entry.Key] = payload;

                result.Data[entry.Key] = CreatePayloadSummary(payload);
            }
        }

        /// <summary>
        /// Records payload compaction metrics according to the storage decision.
        /// </summary>
        private void RecordPayloadCompaction(
            AiStoredPayload payload,
            string? executionId,
            string? stepId)
        {
            var sizeBytes = payload.SizeBytes ?? 0L;

            if (payload.IsInline)
            {
                _payloadMetrics.RecordInlinePayload(sizeBytes);
                return;
            }

            _payloadMetrics.RecordExternalizedPayload(sizeBytes);

            _runtimeMetrics?.Storage.RecordPayloadStored(
                NormalizeMetricValue(executionId, AiPayloadIdentifiers.UnknownExecutionId),
                NormalizeMetricValue(stepId, "unknown-step"),
                "externalized-payload",
                sizeBytes);
        }

        /// <summary>
        /// Creates the compact inline summary used when a payload is externalized.
        /// </summary>
        private static Dictionary<string, object?> CreatePayloadSummary(
            AiStoredPayload payload)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["payloadExternalized"] = true,
                ["artifactId"] = payload.ArtifactId,
                ["contentHash"] = payload.ContentHash,
                ["sizeBytes"] = payload.SizeBytes,
                ["contentType"] = payload.ContentType
            };
        }

        /// <summary>
        /// Normalizes metric dimensions so storage metrics never receive empty keys.
        /// </summary>
        private static string NormalizeMetricValue(string? value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value)
                ? fallback
                : value.Trim();
        }
    }
}