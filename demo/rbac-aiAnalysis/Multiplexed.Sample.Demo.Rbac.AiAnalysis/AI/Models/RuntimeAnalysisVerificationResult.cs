namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models
{
    public static class RuntimeAnalysisVerificationStatuses
    {
        public const string Pending = "Pending";

        public const string Verified = "Verified";

        public const string Skipped = "Skipped";
    }

    public sealed class RuntimeAnalysisVerificationResult
    {
        public string Status { get; init; } =
            RuntimeAnalysisVerificationStatuses.Skipped;

        public bool Executed { get; init; }

        public bool CompletedMatchesPlan { get; init; }

        public bool NoResidualInFlight { get; init; }

        public bool OutcomeCountConsistent { get; init; }

        public int ExpectedRequests { get; init; }

        public int ObservedCompleted { get; init; }

        public int ObservedOk { get; init; }

        public int ObservedHttpNonOk { get; init; }

        public int ObservedErrors { get; init; }

        public double? BaselineP50Ms { get; init; }

        public double? ObservedP50Ms { get; init; }

        public double? P50DeltaMs { get; init; }

        public double? BaselineP95Ms { get; init; }

        public double? ObservedP95Ms { get; init; }

        public double? P95DeltaMs { get; init; }

        public string Summary { get; init; } = string.Empty;
    }
}
