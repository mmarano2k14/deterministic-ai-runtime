using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Providers
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
