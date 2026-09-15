using System.Text.Json;

namespace Multiplexed.Abstractions.AI.Invocation
{
    /// <summary>Trusted server transport for one short retry/v1 custom policy evaluation.</summary>
    public interface IAiRetryPolicyTransport
    {
        string ExecutionLanguage { get; }
        Task<JsonElement> EvaluateAsync(AiRetryPolicyRequest request, CancellationToken cancellationToken = default);
    }
}
