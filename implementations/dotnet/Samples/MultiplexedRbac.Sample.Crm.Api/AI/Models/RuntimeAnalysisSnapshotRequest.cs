namespace MultiplexedRbac.Sample.Crm.Api.AI.Models
{
    public sealed class RuntimeAnalysisSnapshotRequest
    {
        public string Scope { get; init; } = RuntimeAnalysisScopes.CurrentRun;

        public DateTimeOffset CapturedAtUtc { get; init; }

        public RuntimeAnalysisScenarioInput? Scenario { get; init; }

        public RuntimeAnalysisMetricsInput Metrics { get; init; } =
            new RuntimeAnalysisMetricsInput();

        public IReadOnlyList<RuntimeAnalysisEvidenceInput> Evidence { get; init; } =
            Array.Empty<RuntimeAnalysisEvidenceInput>();
    }

    public sealed class RuntimeAnalysisScenarioInput
    {
        public string? Name { get; init; }

        public string? DispatchMode { get; init; }

        public string? PlanKey { get; init; }

        public int TotalRequests { get; init; }

        public int? Concurrency { get; init; }

        public int? BatchSize { get; init; }

        public int? DelayMs { get; init; }

        public int? WavePauseMs { get; init; }

        public int? MaxInFlight { get; init; }

        public int? RotationOverlapMs { get; init; }
    }

    public sealed class RuntimeAnalysisMetricsInput
    {
        public int Completed { get; init; }

        public int InFlight { get; init; }

        public int Ok { get; init; }

        public int Unauthorized { get; init; }

        public int Forbidden { get; init; }

        public int TooManyRequests { get; init; }

        public int OtherHttp { get; init; }

        public int Errors { get; init; }

        public double? P50Ms { get; init; }

        public double? P95Ms { get; init; }

        public double? ElapsedMs { get; init; }

        public int LiveLogCount { get; init; }
    }

    public sealed class RuntimeAnalysisEvidenceInput
    {
        public DateTimeOffset TimestampUtc { get; init; }

        public string Category { get; init; } = string.Empty;

        public string EventType { get; init; } = string.Empty;

        public string? Message { get; init; }

        public int? StatusCode { get; init; }

        public double? DurationMs { get; init; }

        public string? CorrelationId { get; init; }

        public string? SharedRunId { get; init; }

        public string? ExecutionId { get; init; }

        public string? DagId { get; init; }

        public string? StepId { get; init; }

        public string? ChildExecutionId { get; init; }

        public string? PolicyKey { get; init; }

        public IReadOnlyDictionary<string, string?> Metadata { get; init; } =
            new Dictionary<string, string?>();
    }
}
