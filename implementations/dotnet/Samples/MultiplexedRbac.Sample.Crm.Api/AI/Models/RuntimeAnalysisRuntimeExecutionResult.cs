namespace MultiplexedRbac.Sample.Crm.Api.AI.Models
{
    public sealed class RuntimeAnalysisRuntimeExecutionResult
    {
        public string RunId { get; init; } = string.Empty;

        /// <summary>
        /// Physical controller run created when a durable external-wait
        /// continuation re-drives the same ExecutionId.
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
    }
}
