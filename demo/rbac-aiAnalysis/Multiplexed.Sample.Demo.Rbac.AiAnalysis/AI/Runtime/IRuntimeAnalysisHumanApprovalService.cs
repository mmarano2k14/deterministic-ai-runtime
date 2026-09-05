using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Runtime
{
    public interface IRuntimeAnalysisHumanApprovalService
    {
        Task<RuntimeAnalysisRuntimeExecutionResult> DecideAsync(
            string executionId,
            string decision,
            string decidedBy,
            CancellationToken cancellationToken);
    }
}
