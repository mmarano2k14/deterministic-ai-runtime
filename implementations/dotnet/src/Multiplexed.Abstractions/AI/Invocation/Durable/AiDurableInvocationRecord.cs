namespace Multiplexed.Abstractions.AI.Invocation.Durable
{
    public enum AiDurableInvocationStatus { Prepared, Leased, Succeeded, Failed }
    public enum AiDurableInvocationContinuationStatus { None, Pending, Scheduled, Applied, Suppressed }
    public enum AiDurableInvocationCompletionStatus { Accepted, AlreadyAccepted, LeaseRejected, NotFound }

    /// <summary>
    /// Authority for one worker assignment. Epoch/token fence old assignments; neither
    /// is the operation identity or the external-effect idempotency key.
    /// </summary>
    public sealed record AiDurableInvocationLease(
        string WorkerId,
        long Epoch,
        string Token,
        DateTimeOffset ExpiresAtUtc);

    /// <summary>
    /// A business result or a reported execution failure with JSON data. Transport failure
    /// and lease expiry are not synthesized into this result. It cannot command Park/retry.
    /// </summary>
    public sealed record AiDurableInvocationResult(bool Success, string PayloadJson);

    /// <summary>
    /// One immutable snapshot of the authoritative invocation journal. Every replacement
    /// increments Revision. Terminal result and continuation intent are persisted together.
    /// </summary>
    public sealed record AiDurableInvocationRecord
    {
        public int SchemaVersion { get; init; } = 1;
        public required AiDurableInvocationDefinition Definition { get; init; }
        public required string OperationId { get; init; }
        public required string EffectIdempotencyKey { get; init; }
        public required string InputsSha256 { get; init; }
        public long Revision { get; init; }
        public AiDurableInvocationStatus Status { get; init; }
        public AiDurableInvocationLease? Lease { get; init; }
        public AiDurableInvocationResult? Result { get; init; }
        public string? ResultSha256 { get; init; }
        public AiDurableInvocationContinuationStatus ContinuationStatus { get; init; }
        public required DateTimeOffset CreatedAtUtc { get; init; }
        public required DateTimeOffset UpdatedAtUtc { get; init; }
        public DateTimeOffset? CompletedAtUtc { get; init; }
        public string? ContinuationReason { get; init; }
    }

    /// <summary>
    /// Acknowledgement from trusted runtime continuation code, after observing application
    /// of this exact result or an authoritative terminal parent. Queue acceptance is not
    /// such evidence. The journal checks correlation, not the parent DAG itself.
    /// </summary>
    public sealed record AiDurableInvocationContinuationAcknowledgement(
        string OperationId,
        string ResultSha256,
        AiDurableInvocationContinuationStatus Status,
        string Reason);
}
