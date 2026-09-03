using MultiplexedRbac.Sample.Crm.Api.AI.Models;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Runtime
{
    public interface IRuntimeAnalysisScenarioPolicyExecutor
    {
        Task<RuntimeAnalysisScenarioPolicyRuntimeExecutionResult> ValidateAsync(
            RuntimeAnalysisSuggestedScenario scenario,
            CancellationToken cancellationToken);
    }
}
