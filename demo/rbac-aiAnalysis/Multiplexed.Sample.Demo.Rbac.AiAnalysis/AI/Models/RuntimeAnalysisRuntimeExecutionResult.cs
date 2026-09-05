namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models
{
    public sealed class RuntimeAnalysisRuntimeExecutionResult
    {
        public string RunId { get; init; } = string.Empty;

        /// <summary>
        /// Latest physical controller run created when an external-wait
        /// continuation re-drives the same durable ExecutionId.
        /// </summary>
        public string? ContinuationRunId { get; init; }

        public string ExecutionId { get; init; } = string.Empty;

        public string PipelineName { get; init; } = string.Empty;

        public string StepName { get; init; } = string.Empty;

        public string RuntimeStatus { get; init; } = string.Empty;

        public RuntimeAnalysisResult Result { get; init; } =
            new RuntimeAnalysisResult();

        public RuntimeAnalysisScenarioPolicyValidationResult PolicyValidation
        {
            get;
            init;
        } = new RuntimeAnalysisScenarioPolicyValidationResult();

        public RuntimeAnalysisHumanApprovalResult HumanApproval { get; init; } =
            new RuntimeAnalysisHumanApprovalResult();

        public RuntimeAnalysisScenarioExecutionResult ScenarioExecution
        {
            get;
            init;
        } = new RuntimeAnalysisScenarioExecutionResult();

        public RuntimeAnalysisChildDagResult ChildDag { get; init; } =
            new RuntimeAnalysisChildDagResult();

        public RuntimeAnalysisVerificationResult Verification { get; init; } =
            new RuntimeAnalysisVerificationResult();
    }
}
