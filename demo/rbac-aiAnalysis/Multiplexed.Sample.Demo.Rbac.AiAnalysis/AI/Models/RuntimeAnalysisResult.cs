namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models
{
    public sealed class RuntimeAnalysisResult
    {
        public string Answer { get; init; } = string.Empty;

        public string Summary { get; init; } = string.Empty;

        public string Severity { get; init; } = string.Empty;

        public double Confidence { get; init; }

        public IReadOnlyList<RuntimeAnalysisObservation> Observations { get; init; } =
            Array.Empty<RuntimeAnalysisObservation>();

        public RuntimeAnalysisSuggestedScenario SuggestedScenario { get; init; } =
            new RuntimeAnalysisSuggestedScenario();
    }

    public sealed class RuntimeAnalysisObservation
    {
        public string Title { get; init; } = string.Empty;

        public string Detail { get; init; } = string.Empty;

        public IReadOnlyList<int> EvidenceIndexes { get; init; } =
            Array.Empty<int>();
    }

    public sealed class RuntimeAnalysisSuggestedScenario
    {
        public string Name { get; init; } = string.Empty;

        public string Rationale { get; init; } = string.Empty;

        public string ScenarioType { get; init; } = string.Empty;

        public int TotalRequests { get; init; }

        public int? Concurrency { get; init; }

        public int? BatchSize { get; init; }

        public int DelayMs { get; init; }

        public int? WavePauseMs { get; init; }

        public int MaxInFlight { get; init; }

        public int RotationOverlapMs { get; init; }

        public int? DurationSeconds { get; init; }
    }
}
