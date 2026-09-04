namespace MultiplexedRbac.Sample.Crm.Api.AI.Models
{
    public static class RuntimeAnalysisReanalysisConclusions
    {
        public const string Confirmed = "CONFIRMED";

        public const string Weakened = "WEAKENED";

        public const string NotReproduced = "NOT_REPRODUCED";

        public const string Inconclusive = "INCONCLUSIVE";
    }

    /// <summary>
    /// Structured AI interpretation of one completed deterministic experiment.
    /// </summary>
    public sealed class RuntimeAnalysisReanalysisResult
    {
        public string Conclusion { get; init; } =
            RuntimeAnalysisReanalysisConclusions.Inconclusive;

        public string Answer { get; init; } = string.Empty;

        public string Summary { get; init; } = string.Empty;

        public double Confidence { get; init; }

        /// <summary>
        /// Gets whether the AI recommends one additional bounded experiment.
        /// This flag is advisory only; policy + human approval remain authoritative.
        /// </summary>
        public bool ShouldContinue { get; init; }

        public IReadOnlyList<string> Reasons { get; init; } =
            Array.Empty<string>();

        /// <summary>
        /// Gets the next bounded scenario proposal. The proposal is never executed
        /// directly and still crosses deterministic policy + approval.
        /// </summary>
        public RuntimeAnalysisSuggestedScenario SuggestedScenario { get; init; } =
            new RuntimeAnalysisSuggestedScenario();
    }
}
