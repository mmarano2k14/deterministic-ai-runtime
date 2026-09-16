using System.Text.Json;
using System.Text.Json.Serialization;

namespace Multiplexed.AI.Sdk.Contracts.Pipelines
{
    /// <summary>Portable step execution metadata owned by the server runtime.</summary>
    public sealed record AiSdkPipelineStepExecutionDefinition
    {
        [JsonPropertyName("maxRetries")]
        public int MaxRetries { get; init; }

        [JsonPropertyName("retryDelayMs")]
        public int RetryDelayMs { get; init; }
    }

    /// <summary>Portable declarative step definition used by external SDKs.</summary>
    public sealed record AiSdkPipelineStepDefinition
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("stepKey")]
        public string StepKey { get; init; } = string.Empty;

        [JsonPropertyName("executionLanguage")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ExecutionLanguage { get; init; }

        [JsonPropertyName("invocation")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AiSdkInvocationDefinition? Invocation { get; init; }

        [JsonPropertyName("order")]
        public int Order { get; init; }

        [JsonPropertyName("dependsOn")]
        public IReadOnlyList<string> DependsOn { get; init; } = Array.Empty<string>();

        /// <summary>
        /// JSON-portable declarative inputs. CLR object graphs are deliberately excluded from the public boundary.
        /// </summary>
        [JsonPropertyName("input")]
        public IReadOnlyDictionary<string, JsonElement> Input { get; init; }
            = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        /// <summary>
        /// JSON-portable business/runtime configuration. Typed SDK helpers may project into this object later.
        /// </summary>
        [JsonPropertyName("config")]
        public IReadOnlyDictionary<string, JsonElement> Config { get; init; }
            = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        [JsonPropertyName("execution")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AiSdkPipelineStepExecutionDefinition? Execution { get; init; }
    }
}
