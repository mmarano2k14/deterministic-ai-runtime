namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models
{
    public static class RuntimeAnalysisHumanApprovalStatuses
    {
        public const string Pending = "Pending";
        public const string Approved = "Approved";
        public const string Rejected = "Rejected";
        public const string NotRequired = "NotRequired";
    }

    public static class RuntimeAnalysisHumanApprovalDecisions
    {
        public const string Approve = "approve";
        public const string Reject = "reject";
    }

    public sealed class RuntimeAnalysisHumanApprovalResult
    {
        public bool Required { get; init; }

        public string Status { get; init; } = string.Empty;

        public string? ContinuationId { get; init; }

        public DateTimeOffset? RequestedAtUtc { get; init; }

        public DateTimeOffset? DecidedAtUtc { get; init; }

        public string? DecidedBy { get; init; }

        public string? Message { get; init; }
    }
}
