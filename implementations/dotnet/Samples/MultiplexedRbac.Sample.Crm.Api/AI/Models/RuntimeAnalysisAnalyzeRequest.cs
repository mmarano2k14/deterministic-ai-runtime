namespace MultiplexedRbac.Sample.Crm.Api.AI.Models
{
    public sealed class RuntimeAnalysisAnalyzeRequest
    {
        public string Question { get; init; } = string.Empty;

        public string InvestigationMode { get; init; } =
            RuntimeAnalysisInvestigationModes.StopWhenConclusive;

        public RuntimeAnalysisSnapshotRequest SnapshotRequest { get; init; } =
            new RuntimeAnalysisSnapshotRequest();
    }
}
