using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Steps;

namespace MMultiplexed.AI.Runtime.Pipeline.Steps.Test
{
    /// <summary>
    /// Step that fails once using a non-success result, then succeeds.
    /// This is the safest way to validate retry behavior without depending
    /// on exception classification semantics.
    /// </summary>
    [AiStep("fail-once-then-succeed")]
    public sealed class FailOnceThenSucceedStep : IAiStep
    {
        public string Name => "fail-once-then-succeed";

        public Task<AiStepResult> ExecuteAsync(
            AiStepExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            var stepState = context.Execution.StateWriter.GetOrCreateStep(context.Execution.State, context.Step.Name);

            if ((stepState.RetryState?.RetryCount ?? 0) == 0)
            {
                return Task.FromResult(new AiStepResult
                {
                    Success = false,
                    Error = "Intentional first failure"
                });
            }

            return Task.FromResult(
                AiStepResult.Ok(
                    output: "Recovered after retry",
                    data: new Dictionary<string, object?>
                    {
                        ["status"] = "success-after-retry"
                    }));
        }
    }
}