using MultiplexedRbac.Sample.Crm.Api.AI.Models;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Runtime
{
    public interface IRuntimeAnalysisScenarioExecutionService
    {
        Task<RuntimeAnalysisRuntimeExecutionResult> CompleteAsync(
            string executionId,
            RuntimeAnalysisScenarioExecutionObservation observation,
            string completedBy,
            CancellationToken cancellationToken);
    }
}
