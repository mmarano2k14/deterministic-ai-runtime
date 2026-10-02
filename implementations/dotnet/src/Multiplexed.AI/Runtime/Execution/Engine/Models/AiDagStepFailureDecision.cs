namespace Multiplexed.AI.Runtime.Execution.Engine.Models
{
    /// <summary>
    /// Represents an already-evaluated retry disposition that must be applied atomically
    /// to one claimed distributed DAG step.
    /// </summary>
    /// <remarks>
    /// Policy evaluation remains outside the store. The store owns only the claim-fenced
    /// mutation that commits either a retry schedule or terminal failure.
    /// </remarks>
    public sealed record AiDagStepFailureDecision(
        AiDagStepFailureDisposition Disposition,
        TimeSpan? RetryDelay,
        string? Reason)
    {
        public static AiDagStepFailureDecision Retry(TimeSpan delay, string? reason = null)
        {
            if (delay < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(delay));
            }

            return new AiDagStepFailureDecision(
                AiDagStepFailureDisposition.Retry,
                delay,
                reason);
        }

        public static AiDagStepFailureDecision Fail(string? reason = null) =>
            new(
                AiDagStepFailureDisposition.Fail,
                RetryDelay: null,
                Reason: reason);
    }

}
