namespace MultiplexedRbac.Sample.Crm.Api.AI.Models
{
    public sealed class RuntimeAnalysisScenarioPolicyValidationResult
    {
        public bool Allowed { get; init; }

        public bool RequiresHumanApproval { get; init; }

        public string PlanKey { get; init; } = string.Empty;

        public RuntimeAnalysisSuggestedScenario Scenario { get; init; } =
            new RuntimeAnalysisSuggestedScenario();

        public IReadOnlyList<RuntimeAnalysisScenarioPolicyDecision>
            PolicyDecisions { get; init; } =
                Array.Empty<RuntimeAnalysisScenarioPolicyDecision>();
    }
}
