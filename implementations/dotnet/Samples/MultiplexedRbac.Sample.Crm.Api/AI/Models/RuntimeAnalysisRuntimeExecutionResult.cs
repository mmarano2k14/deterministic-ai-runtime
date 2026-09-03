namespace MultiplexedRbac.Sample.Crm.Api.AI.Models
{
    public sealed class RuntimeAnalysisRuntimeExecutionResult
    {
        public string RunId { get; init; } = string.Empty;

        public string ExecutionId { get; init; } = string.Empty;

        public string PipelineName { get; init; } = string.Empty;

        public string StepName { get; init; } = string.Empty;

        public string RuntimeStatus { get; init; } = string.Empty;

        public RuntimeAnalysisResult Result { get; init; } =
            new RuntimeAnalysisResult();
    }
}
