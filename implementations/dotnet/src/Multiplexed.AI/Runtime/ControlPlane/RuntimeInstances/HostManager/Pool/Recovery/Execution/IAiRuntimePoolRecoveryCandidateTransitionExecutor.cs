using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Execution
{
    /// <summary>
    /// Executes the existing durable recovery transition boundary for authorized candidates.
    /// </summary>
    public interface IAiRuntimePoolRecoveryCandidateTransitionExecutor
    {
        Task<IReadOnlyList<AiRuntimePoolRecoveryCandidateOutcome>> ExecuteAsync(
            string failureId,
            IReadOnlyList<AiRuntimePoolAssignedWorkCandidate> candidates,
            Func<AiRuntimePoolAssignedWorkCandidate, bool> isAuthorized,
            Func<CancellationToken, Task> ensureActiveLeaseAsync,
            CancellationToken cancellationToken = default);
    }
}
