namespace MultiplexedRbac.Sample.Crm.Api.AI.Models
{
    public sealed class RuntimeAnalysisScenarioPolicyRuntimeExecutionResult
    {
        public string RunId { get; init; } = string.Empty;

        public string ExecutionId { get; init; } = string.Empty;

        public string PipelineName { get; init; } = string.Empty;

        public string StepName { get; init; } = string.Empty;

        public string RuntimeStatus { get; init; } = string.Empty;

        public RuntimeAnalysisScenarioPolicyValidationResult Result { get; init; } =
            new RuntimeAnalysisScenarioPolicyValidationResult();
    }
}
