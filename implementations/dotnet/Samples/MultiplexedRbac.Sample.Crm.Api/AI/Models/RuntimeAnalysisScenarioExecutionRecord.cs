using Multiplexed.Abstractions.Core.ExecutionContext;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Models
{
    /// <summary>
    /// Application-owned durable state for the external client scenario
    /// execution. It is committed before the DAG step returns Park().
    /// </summary>
    public sealed class RuntimeAnalysisScenarioExecutionRecord
    {
        public required string ExecutionId { get; init; }

        public required string StepName { get; init; }

        public required string ContinuationId { get; init; }

        public string? InitialRunId { get; init; }

        public required string Status { get; init; }

        public required RuntimeAnalysisSuggestedScenario Scenario { get; init; }

        public required string PlanKey { get; init; }

        public required ExecutionContextSnapshot ExecutionContextSnapshot
        {
            get;
            init;
        }

        public required DateTimeOffset RequestedAtUtc { get; init; }

        public DateTimeOffset? CompletedAtUtc { get; init; }

        public RuntimeAnalysisScenarioExecutionObservation? Observation
        {
            get;
            init;
        }

        public string? CompletedBy { get; init; }

        public string? Error { get; init; }
    }
}
