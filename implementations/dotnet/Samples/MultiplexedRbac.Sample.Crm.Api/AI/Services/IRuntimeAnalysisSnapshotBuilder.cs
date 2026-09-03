using MultiplexedRbac.Sample.Crm.Api.AI.Models;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Services
{
    public interface IRuntimeAnalysisSnapshotBuilder
    {
        RuntimeAnalysisSnapshot Build(
            RuntimeAnalysisSnapshotRequest request);
    }
}
