using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Policies;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models
{
    public sealed class RuntimeAnalysisProviderRequest
    {
        public string Question { get; init; } = string.Empty;

        public string InvestigationMode { get; init; } =
            RuntimeAnalysisInvestigationModes.StopWhenConclusive;

        public RuntimeAnalysisSnapshot Snapshot { get; init; } =
            new RuntimeAnalysisSnapshot();

        /// <summary>
        /// Exact deterministic scenario-policy envelope embedded in the same
        /// runtime-analysis DAG that later validates the AI proposal.
        ///
        /// The AI receives this only as a generation constraint. The
        /// downstream runtime policy step remains the authoritative decision.
        /// </summary>
        public RuntimeAnalysisScenarioPolicyDefinition?
            ScenarioPolicyDefinition { get; init; }
    }
}
