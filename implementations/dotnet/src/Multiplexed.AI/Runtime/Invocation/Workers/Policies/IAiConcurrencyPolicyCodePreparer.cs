using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Workers;

namespace Multiplexed.AI.Runtime.Invocation.Workers.Policies
{
    /// <summary>
    /// Resolves one custom concurrency policy to the exact immutable code bundle pinned
    /// to the owning execution. Implementations authorize and verify publication state;
    /// they do not evaluate the policy or mutate DAG lifecycle state.
    /// </summary>
    public interface IAiConcurrencyPolicyCodePreparer
    {
        Task<AiWorkerCodeBundle> PrepareAsync(
            AiConcurrencyPolicyRequest request,
            CancellationToken cancellationToken = default);
    }
}
