using MultiplexedRbac.Sample.Crm.Api.AI.Models;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Providers
{
    public interface IAiRuntimeAnalysisProvider
    {
        RuntimeAnalysisProviderStatus Status { get; }

        Task<RuntimeAnalysisResult> AnalyzeAsync(
            RuntimeAnalysisProviderRequest request,
            CancellationToken cancellationToken);

        Task<RuntimeAnalysisReanalysisResult> ReanalyzeAsync(
            RuntimeAnalysisReanalysisProviderRequest request,
            CancellationToken cancellationToken);
    }
}
