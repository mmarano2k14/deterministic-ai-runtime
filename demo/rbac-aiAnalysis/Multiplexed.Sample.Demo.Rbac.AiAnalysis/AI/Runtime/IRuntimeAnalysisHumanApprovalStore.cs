using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Runtime
{
    public interface IRuntimeAnalysisHumanApprovalStore
    {
        Task<RuntimeAnalysisHumanApprovalRecord?> GetAsync(
            string executionId,
            CancellationToken cancellationToken = default);

        Task<RuntimeAnalysisHumanApprovalRecord> CreatePendingAsync(
            RuntimeAnalysisHumanApprovalRecord record,
            CancellationToken cancellationToken = default);

        Task<RuntimeAnalysisHumanApprovalRecord> AttachInitialRunIdAsync(
            string executionId,
            string runId,
            CancellationToken cancellationToken = default);

        Task<RuntimeAnalysisHumanApprovalRecord> DecideAsync(
            string executionId,
            string status,
            string decidedBy,
            CancellationToken cancellationToken = default);
    }
}
