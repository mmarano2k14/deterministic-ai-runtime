using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Steps;

namespace Multiplexed.AI.Runtime.Invocation.Durable.Dag
{
    /// <summary>
    /// Maps one validated terminal journal snapshot. The entire business payload becomes
    /// Value; nested properties never control Outcome, retry, grants or the receipt.
    /// </summary>
    public static class AiDurableInvocationResultMapper
    {
        public static AiStepResult Map(AiDurableInvocationRecord record)
        {
            AiDurableInvocationValidation.ValidateRecord(record);
            if (!AiDurableInvocationValidation.Terminal(record))
                throw new InvalidOperationException("Only an authoritative terminal invocation has a step result.");
            using var payload = JsonDocument.Parse(record.Result!.PayloadJson);
            var value = payload.RootElement.Clone();
            var result = record.Result.Success
                ? AiStepResult.Ok(value: value)
                : AiStepResult.Fail("Hosted custom invocation reported a failure; details are retained in its durable result.", value: value);
            result.InvocationReceipt = new AiDurableInvocationApplicationReceipt(record.OperationId, record.ResultSha256!);
            return result;
        }

        internal static bool IsApplied(AiDurableInvocationRecord invocation, Multiplexed.Abstractions.AI.Execution.AiStepState step)
        {
            var result = step.Result;
            var receipt = result?.InvocationReceipt;
            return receipt is not null && receipt.OperationId == invocation.OperationId &&
                receipt.ResultSha256 == invocation.ResultSha256 && result!.Success == invocation.Result!.Success &&
                (invocation.Result.Success
                    ? step.Status == Multiplexed.Abstractions.AI.Execution.AiStepExecutionStatus.Completed &&
                      result.EffectiveOutcome == AiStepExecutionOutcome.Complete
                    : step.Status == Multiplexed.Abstractions.AI.Execution.AiStepExecutionStatus.Failed &&
                      result.EffectiveOutcome == AiStepExecutionOutcome.Fail);
        }
    }
}
