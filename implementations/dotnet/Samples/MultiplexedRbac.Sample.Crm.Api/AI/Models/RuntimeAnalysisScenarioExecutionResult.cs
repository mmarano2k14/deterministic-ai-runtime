namespace MultiplexedRbac.Sample.Crm.Api.AI.Models
{
    public static class RuntimeAnalysisScenarioExecutionStatuses
    {
        public const string NotStarted = "NotStarted";

        public const string Pending = "Pending";

        public const string Completed = "Completed";

        public const string Failed = "Failed";

        public const string NotExecuted = "NotExecuted";
    }

    public sealed class RuntimeAnalysisScenarioExecutionResult
    {
        public bool Required { get; init; }

        public string Status { get; init; } =
            RuntimeAnalysisScenarioExecutionStatuses.NotStarted;

        public string? ContinuationId { get; init; }

        public DateTimeOffset? RequestedAtUtc { get; init; }

        public DateTimeOffset? CompletedAtUtc { get; init; }

        public RuntimeAnalysisSuggestedScenario Scenario { get; init; } =
            new RuntimeAnalysisSuggestedScenario();

        public string PlanKey { get; init; } = string.Empty;

        public RuntimeAnalysisScenarioExecutionObservation? Observation
        {
            get;
            init;
        }

        public string? CompletedBy { get; init; }

        public string? Message { get; init; }
    }
}
