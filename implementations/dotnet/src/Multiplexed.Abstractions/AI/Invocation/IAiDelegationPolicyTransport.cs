using System.Text.Json;

namespace Multiplexed.Abstractions.AI.Invocation
{
    /// <summary>
    /// Executes one family-specific delegation/v1 policy evaluation.
    /// Implementations are transport adapters only; they do not allocate children or mutate durable relations.
    /// </summary>
    public interface IAiDelegationPolicyTransport
    {
        /// <summary>Gets the canonical hosted execution language handled by this transport.</summary>
        string ExecutionLanguage { get; }

        /// <summary>Evaluates one delegation policy and returns its portable response envelope.</summary>
        Task<JsonElement> EvaluateAsync(
            AiDelegationPolicyRequest request,
            CancellationToken cancellationToken = default);
    }
}
