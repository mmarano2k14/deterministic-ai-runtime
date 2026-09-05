using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Runtime
{
    public interface IRuntimeAnalysisRuntimeExecutor
    {
        Task<RuntimeAnalysisRuntimeExecutionResult> AnalyzeAsync(
            RuntimeAnalysisProviderRequest request,
            CancellationToken cancellationToken);
    }
}
