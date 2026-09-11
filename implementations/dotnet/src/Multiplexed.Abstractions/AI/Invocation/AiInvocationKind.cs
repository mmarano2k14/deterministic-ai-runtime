using System.Text.Json.Serialization;

namespace Multiplexed.Abstractions.AI.Invocation
{
    /// <summary>
    /// Identifies the invocation mechanism, independently of orchestration mode and language.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AiInvocationKind>))]
    public enum AiInvocationKind
    {
        Native = 0,
        Custom = 1,
        Mcp = 2
    }
}
