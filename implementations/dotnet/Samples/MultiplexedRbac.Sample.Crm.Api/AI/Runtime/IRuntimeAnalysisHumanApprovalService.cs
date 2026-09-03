using MultiplexedRbac.Sample.Crm.Api.AI.Models;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Runtime
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
