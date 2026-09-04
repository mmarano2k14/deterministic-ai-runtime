namespace MultiplexedRbac.Sample.Crm.Api.AI.Models
{
    public sealed class RuntimeAnalysisChildApprovalDecisionRequest
    {
        public string RootRunId { get; init; } = string.Empty;

        public string Decision { get; init; } = string.Empty;
    }

    public sealed class RuntimeAnalysisChildScenarioExecutionRequest
    {
        public string RootRunId { get; init; } = string.Empty;

        public RuntimeAnalysisScenarioExecutionObservation Observation { get; init; } =
            new RuntimeAnalysisScenarioExecutionObservation();
    }
}
