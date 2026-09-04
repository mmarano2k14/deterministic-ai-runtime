namespace MultiplexedRbac.Sample.Crm.Api.AI.Models
{
    public sealed class RuntimeAnalysisProviderRequest
    {
        public string Question { get; init; } = string.Empty;

        public string InvestigationMode { get; init; } =
            RuntimeAnalysisInvestigationModes.StopWhenConclusive;

        public RuntimeAnalysisSnapshot Snapshot { get; init; } =
            new RuntimeAnalysisSnapshot();
    }
}
