using System.Text.Json;
using Multiplexed.AI.Sdk.Contracts.Common;

namespace Multiplexed.AI.Sdk.Transport
{
    /// <summary>One normalized invocation handed from an SDK client to its physical transport.</summary>
    public sealed record AiSdkTransportRequest
    {
        public int ProtocolVersion { get; init; } = AiSdkProtocolVersions.Current;
        public string Operation { get; init; } = string.Empty;
        public JsonElement Arguments { get; init; }
    }
}
