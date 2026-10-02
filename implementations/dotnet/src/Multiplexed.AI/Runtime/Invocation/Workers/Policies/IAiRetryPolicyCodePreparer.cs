using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Workers;

namespace Multiplexed.AI.Runtime.Invocation.Workers.Policies
{
    public interface IAiRetryPolicyCodePreparer
    {
        Task<AiWorkerCodeBundle> PrepareAsync(AiRetryPolicyRequest request, CancellationToken cancellationToken = default);
    }
}
