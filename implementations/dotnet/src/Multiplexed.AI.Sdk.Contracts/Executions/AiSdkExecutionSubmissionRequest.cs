using System.Text.Json;
using System.Text.Json.Serialization;
using Multiplexed.AI.Sdk.Contracts.Common;

namespace Multiplexed.AI.Sdk.Contracts.Executions
{
    /// <summary>
    /// Public request to submit one execution of an immutable publication. Tenant identity is derived from
    /// the authenticated server context and is deliberately absent from the caller-controlled body.
    /// </summary>
    public sealed record AiSdkExecutionSubmissionRequest
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = AiSdkSchemaVersions.ExecutionSubmissionRequest;

        [JsonPropertyName("publicationRef")]
        public string PublicationRef { get; init; } = string.Empty;

        /// <summary>
        /// Optional caller-owned idempotency key for safely retrying submission. The server owns the mapping
        /// from this value to its durable execution identity and internal queue/control-plane identities.
        /// </summary>
        [JsonPropertyName("idempotencyKey")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? IdempotencyKey { get; init; }

        /// <summary>Optional JSON payload used to seed execution state.</summary>
        [JsonPropertyName("input")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? Input { get; init; }

        /// <summary>Caller metadata that is diagnostic only and is not an execution authority.</summary>
        [JsonPropertyName("metadata")]
        public IReadOnlyDictionary<string, string> Metadata { get; init; }
            = new Dictionary<string, string>(StringComparer.Ordinal);

        [JsonPropertyName("correlationId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CorrelationId { get; init; }
    }
}
