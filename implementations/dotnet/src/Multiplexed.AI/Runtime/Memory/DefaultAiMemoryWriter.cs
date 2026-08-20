using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Memory;
using Multiplexed.Abstractions.AI.Steps;

namespace Multiplexed.AI.Runtime.Memory
{
    /// <summary>
    /// Default memory writer for consolidated memories.
    ///
    /// PURPOSE:
    /// - Converts selected step outputs into durable consolidated memory records
    /// - Computes initial memory scores using deterministic scoring policy
    /// - Stores eligible memories in <see cref="IAiConsolidatedMemoryStore"/>
    ///
    /// DESIGN:
    /// - Only successful step results are considered
    /// - Memory content is derived from output, value summary, and structured data summary
    /// - Large payloads should already have been externalized before this writer runs
    ///
    /// IMPORTANT:
    /// - This writer does not mutate execution state
    /// - This writer does not affect DAG convergence
    /// - This writer does not participate in replay correctness
    /// </summary>
    public sealed class DefaultAiMemoryWriter : IAiMemoryWriter
    {
        private readonly IAiConsolidatedMemoryStore _store;
        private readonly IAiMemoryScoringPolicy _scoringPolicy;

        public DefaultAiMemoryWriter(
            IAiConsolidatedMemoryStore store,
            IAiMemoryScoringPolicy scoringPolicy)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(scoringPolicy);

            _store = store;
            _scoringPolicy = scoringPolicy;
        }

        /// <summary>
        /// Writes a consolidated memory derived from a successful step result.
        ///
        /// ELIGIBILITY:
        /// - Result must be successful
        /// - Result must contain output, value, data, or payload reference
        ///
        /// SCORING DEFAULTS:
        /// - Task relevance is initialized conservatively
        /// - Novelty is initialized moderately
        /// - Confidence is higher for successful execution-derived records
        /// </summary>
        public async Task<AiConsolidatedMemoryRecord?> WriteFromStepResultAsync(
            AiExecutionRecord record,
            string stepName,
            AiStepResult result,
            string scope,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
            ArgumentNullException.ThrowIfNull(result);
            ArgumentException.ThrowIfNullOrWhiteSpace(scope);

            if (!result.Success)
                return null;

            if (!HasMemorySignal(result))
                return null;

            var memory = new AiConsolidatedMemoryRecord
            {
                Scope = scope,
                Kind = "execution.step.result",
                Content = BuildContent(stepName, result),
                TaskRelevance = 0.50,
                Novelty = 0.50,
                Confidence = 0.80,
                AccessCount = 0,
                AgeInSessions = 0,
                ProvenanceExecutionIds = new List<string>
                {
                    record.ExecutionId
                },
                ProvenanceStepNames = new List<string>
                {
                    stepName
                },
                Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [AiStepMetadataKeys.CamelCaseStepName] = stepName,
                    [AiExecutionMetadataKeys.CamelCaseExecutionId] = record.ExecutionId,
                    ["hasPayload"] = result.Payload != null,
                    ["hasDataPayloads"] = result.DataPayloads != null && result.DataPayloads.Count > 0
                }
            };

            memory.InitialScore = _scoringPolicy.ComputeInitialScore(memory);
            memory.CurrentScore = memory.InitialScore;

            await _store.SaveAsync(memory, cancellationToken);

            return memory;
        }

        /// <summary>
        /// Determines whether the step result contains enough information to become memory.
        /// </summary>
        private static bool HasMemorySignal(AiStepResult result)
        {
            if (!string.IsNullOrWhiteSpace(result.Output))
                return true;

            if (result.Value is not null)
                return true;

            if (result.Payload is not null)
                return true;

            if (result.Data.Count > 0)
                return true;

            if (result.DataPayloads is not null && result.DataPayloads.Count > 0)
                return true;

            return false;
        }

        /// <summary>
        /// Builds compact textual memory content from a step result.
        ///
        /// IMPORTANT:
        /// - This method intentionally avoids resolving artifact payloads
        /// - The writer stores a compact derived memory, not the full raw payload
        /// - Artifact ids remain available through metadata and summaries
        /// </summary>
        private static string BuildContent(
            string stepName,
            AiStepResult result)
        {
            var content = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [AiStepMetadataKeys.CamelCaseStepName] = stepName,
                ["output"] = result.Output,
                ["value"] = result.Value,
                ["data"] = result.Data,
                ["payload"] = result.Payload is null
                    ? null
                    : new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["isInline"] = result.Payload.IsInline,
                        ["artifactId"] = result.Payload.ArtifactId,
                        ["contentHash"] = result.Payload.ContentHash,
                        ["sizeBytes"] = result.Payload.SizeBytes,
                        ["contentType"] = result.Payload.ContentType
                    },
                ["dataPayloads"] = result.DataPayloads?.ToDictionary(
                    x => x.Key,
                    x => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["isInline"] = x.Value.IsInline,
                        ["artifactId"] = x.Value.ArtifactId,
                        ["contentHash"] = x.Value.ContentHash,
                        ["sizeBytes"] = x.Value.SizeBytes,
                        ["contentType"] = x.Value.ContentType
                    },
                    StringComparer.Ordinal)
            };

            return JsonSerializer.Serialize(content);
        }
    }
}