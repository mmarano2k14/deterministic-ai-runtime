using System.Text.Json.Serialization;

namespace Multiplexed.AI.Sdk.Contracts.Publication
{
    /// <summary>Supported custom declaration sites within a public pipeline publication.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AiSdkPublicationFunctionKind>))]
    public enum AiSdkPublicationFunctionKind
    {
        Step,
        ConcurrencyPolicy,
        RetryPolicy,
        DelegationPolicy
    }

    /// <summary>
    /// Identifies one declaration in the published definition closure. DefinitionPath is the canonical
    /// nested Child DAG path and is omitted for root-pipeline declarations.
    /// </summary>
    public sealed record AiSdkPublicationCallSite
    {
        [JsonPropertyName("kind")]
        public AiSdkPublicationFunctionKind Kind { get; init; }

        [JsonPropertyName("stepName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? StepName { get; init; }

        [JsonPropertyName("policyIndex")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? PolicyIndex { get; init; }

        [JsonPropertyName("definitionPath")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DefinitionPath { get; init; }
    }
}
