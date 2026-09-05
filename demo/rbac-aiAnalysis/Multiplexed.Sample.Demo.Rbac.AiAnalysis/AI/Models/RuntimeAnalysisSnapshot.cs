namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models
{
    public sealed class RuntimeAnalysisSnapshot
    {
        public string Scope { get; init; } = RuntimeAnalysisScopes.CurrentRun;

        public DateTimeOffset CapturedAtUtc { get; init; }

        public RuntimeAnalysisScenarioSnapshot? Scenario { get; init; }

        public RuntimeAnalysisMetricsSnapshot Metrics { get; init; } =
            new RuntimeAnalysisMetricsSnapshot();

        public IReadOnlyList<RuntimeAnalysisEvidence> Evidence { get; init; } =
            Array.Empty<RuntimeAnalysisEvidence>();

        public RuntimeAnalysisEvidenceSummary EvidenceSummary { get; init; } =
            new RuntimeAnalysisEvidenceSummary();

        public int EvidenceReceivedCount { get; init; }

        public bool EvidenceTruncated { get; init; }
    }

    public sealed class RuntimeAnalysisScenarioSnapshot
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

    public sealed class RuntimeAnalysisMetricsSnapshot
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

    public sealed class RuntimeAnalysisEvidence
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

    public sealed class RuntimeAnalysisEvidenceSummary
    {
        public IReadOnlyDictionary<string, int> ByCategory { get; init; } =
            new Dictionary<string, int>();

        public IReadOnlyDictionary<string, int> ByEventType { get; init; } =
            new Dictionary<string, int>();

        public int HttpErrorCount { get; init; }

        public int DagRelatedCount { get; init; }

        public int PolicyRelatedCount { get; init; }

        public int RecoveryRelatedCount { get; init; }
    }
}
