using MultiplexedRbac.Sample.Crm.Api.AI.Models;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Runtime
{
    public interface IRuntimeAnalysisRuntimeExecutor
    {
        Task<RuntimeAnalysisRuntimeExecutionResult> AnalyzeAsync(
            RuntimeAnalysisProviderRequest request,
            CancellationToken cancellationToken);
    }
}
