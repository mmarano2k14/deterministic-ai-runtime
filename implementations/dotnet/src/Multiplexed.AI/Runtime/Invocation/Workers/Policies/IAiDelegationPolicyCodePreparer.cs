using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Workers;

namespace Multiplexed.AI.Runtime.Invocation.Workers.Policies
{
    /// <summary>Resolves one Delegation policy to the exact immutable code pinned to its parent execution.</summary>
    public interface IAiDelegationPolicyCodePreparer
    {
        Task<AiWorkerCodeBundle> PrepareAsync(
            AiDelegationPolicyRequest request,
            CancellationToken cancellationToken = default);
    }
}
