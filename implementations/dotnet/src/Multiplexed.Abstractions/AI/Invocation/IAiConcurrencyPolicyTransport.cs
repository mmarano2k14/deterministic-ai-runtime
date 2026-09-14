using System.Text.Json;

namespace Multiplexed.Abstractions.AI.Invocation
{
    /// <summary>
    /// Trusted server transport for one language's short, side-effect-free concurrency
    /// policy evaluations. Implementations may use host-owned language workers, but the
    /// transport never owns admission, claims, retries or DAG lifecycle. Implementations
    /// must honour cancellation, bound their own I/O and return a detached JSON value.
    /// </summary>
    public interface IAiConcurrencyPolicyTransport
    {
        string ExecutionLanguage { get; }

        /// <summary>
        /// Evaluates the explicit concurrency/v1 request. The response must echo its
        /// requestId and use schemaVersion, policyKind, decision, reason and optional
        /// retryAfterMs. The server validates this response before making a decision.
        /// Transport exceptions must propagate; an empty response is not an approval.
        /// </summary>
        Task<JsonElement> EvaluateAsync(
            AiConcurrencyPolicyRequest request,
            CancellationToken cancellationToken = default);
    }
}
