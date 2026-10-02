using System.Text.Json;
using System.Text.Json.Serialization;

namespace Multiplexed.AI.Sdk.Contracts.Executions
{
    /// <summary>
    /// Sanitized public terminal failure. Internal exception types, stack traces, worker identities and
    /// persistence diagnostics are not part of this contract.
    /// </summary>
    public sealed record AiSdkExecutionFailure
    {
        [JsonPropertyName("code")]
        public string Code { get; init; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; init; } = string.Empty;

        [JsonPropertyName("details")]
        public IReadOnlyDictionary<string, JsonElement> Details { get; init; }
            = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    }
}
