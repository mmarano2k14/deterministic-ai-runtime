using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Services
{
    public interface IRuntimeAnalysisSnapshotBuilder
    {
        RuntimeAnalysisSnapshot Build(
            RuntimeAnalysisSnapshotRequest request);
    }
}
