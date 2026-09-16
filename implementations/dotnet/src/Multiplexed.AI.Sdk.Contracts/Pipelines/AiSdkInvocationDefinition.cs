using System.Text.Json.Serialization;

namespace Multiplexed.AI.Sdk.Contracts.Pipelines
{
    /// <summary>Portable invocation kind independent from runtime worker/provider implementation.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AiSdkInvocationKind>))]
    public enum AiSdkInvocationKind
    {
        Native,
        Custom,
        Mcp
    }

    /// <summary>
    /// Public invocation descriptor. References identify server-resolved resources; they never carry
    /// credentials, worker identities, runtime-instance identities or arbitrary infrastructure handles.
    /// </summary>
    public sealed record AiSdkInvocationDefinition
    {
        [JsonPropertyName("kind")]
        public AiSdkInvocationKind Kind { get; init; }

        [JsonPropertyName("implementationRef")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ImplementationRef { get; init; }

        [JsonPropertyName("connectionRef")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ConnectionRef { get; init; }

        [JsonPropertyName("tool")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Tool { get; init; }
    }
}
