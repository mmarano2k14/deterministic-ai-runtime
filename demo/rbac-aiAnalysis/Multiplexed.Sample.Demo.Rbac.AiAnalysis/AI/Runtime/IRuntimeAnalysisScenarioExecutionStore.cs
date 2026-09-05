using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Runtime
{
    public interface IRuntimeAnalysisScenarioExecutionStore
    {
        Task<RuntimeAnalysisScenarioExecutionRecord?> GetAsync(
            string executionId,
            CancellationToken cancellationToken = default);

        Task<RuntimeAnalysisScenarioExecutionRecord> CreatePendingAsync(
            RuntimeAnalysisScenarioExecutionRecord record,
            CancellationToken cancellationToken = default);

        Task<RuntimeAnalysisScenarioExecutionRecord> CompleteAsync(
            string executionId,
            RuntimeAnalysisScenarioExecutionObservation observation,
            string completedBy,
            CancellationToken cancellationToken = default);
    }
}
