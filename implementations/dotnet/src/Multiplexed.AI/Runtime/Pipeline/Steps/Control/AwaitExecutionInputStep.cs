using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Control;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.Execution.Context;

namespace Multiplexed.AI.Runtime.Pipeline.Steps.Control
{
    /// <summary>
    /// Parks the current DAG step until durable external or human input is submitted
    /// for the configured waiting key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The execution-control state is committed before <see cref="AiStepResult.Park"/>
    /// is returned so the waiting condition remains observable and input can be accepted even
    /// if the physical worker disappears around the park boundary.
    /// </para>
    /// <para>
    /// Input submission does not create a new execution. The public boundary re-drives this
    /// exact <see cref="Multiplexed.Abstractions.AI.Execution.AiStepExecutionStatus.WaitingForExternal"/>
    /// step through the existing external-wait continuation contract.
    /// </para>
    /// </remarks>
    [AiStep(StepKey)]
    public sealed class AwaitExecutionInputStep : IAiStep
    {
        /// <summary>The canonical native step key.</summary>
        public const string StepKey = "execution.await-input";

        /// <summary>The required configuration key containing the durable input correlation key.</summary>
        public const string WaitingKeyConfigKey = "waitingKey";

        /// <summary>The optional configuration key containing the human-readable waiting reason.</summary>
        public const string ReasonConfigKey = "reason";

        /// <inheritdoc />
        public string Name => StepKey;

        /// <inheritdoc />
        public async Task<AiStepResult> ExecuteAsync(
            AiStepExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            var helper = context.GetHelper();
            var waitingKey = await helper
                .GetRequiredConfigAsync<string>(WaitingKeyConfigKey, cancellationToken)
                .ConfigureAwait(false);
            var reason = await helper
                .GetConfigAsync<string>(ReasonConfigKey, cancellationToken)
                .ConfigureAwait(false);

            ArgumentException.ThrowIfNullOrWhiteSpace(waitingKey);

            var control = context.GetRequiredService<IAiExecutionControlService>();
            var state = await control
                .GetStateAsync(context.ExecutionId, cancellationToken)
                .ConfigureAwait(false);

            if (HasSubmittedInput(state, waitingKey, context.StepName))
            {
                return CreateCompletedResult(state!, waitingKey);
            }

            if (state is not null && state.Status == AiExecutionControlStatus.WaitingForInput)
            {
                EnsureWaitingIdentity(state, context.ExecutionId, waitingKey, context.StepName);

                return AiStepResult.Park(
                    reason ?? $"Waiting for external input '{waitingKey}'.");
            }

            var waiting = await control
                .MarkWaitingForInputAsync(
                    context.ExecutionId,
                    waitingKey,
                    context.StepName,
                    reason,
                    requestedBy: StepKey,
                    inputWaitMode: AiExecutionInputWaitMode.ExternalWaitStep,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            EnsureWaitingIdentity(waiting, context.ExecutionId, waitingKey, context.StepName);

            if (waiting.Status != AiExecutionControlStatus.WaitingForInput ||
                waiting.PendingAction != AiExecutionControlAction.WaitForInput)
            {
                throw new InvalidOperationException(
                    $"Execution '{context.ExecutionId}' did not enter the durable waiting-for-input state for step '{context.StepName}'. " +
                    $"Status='{waiting.Status}', PendingAction='{waiting.PendingAction}'.");
            }

            return AiStepResult.Park(
                reason ?? $"Waiting for external input '{waitingKey}'.");
        }

        private static bool HasSubmittedInput(
            AiExecutionControlState? state,
            string waitingKey,
            string stepName)
        {
            return state is not null &&
                   state.InputReceivedAtUtc.HasValue &&
                   state.InputWaitMode == AiExecutionInputWaitMode.ExternalWaitStep &&
                   string.Equals(state.WaitingKey, waitingKey, StringComparison.Ordinal) &&
                   string.Equals(state.WaitingStepName, stepName, StringComparison.Ordinal);
        }

        private static AiStepResult CreateCompletedResult(
            AiExecutionControlState state,
            string waitingKey)
        {
            var input = new Dictionary<string, object?>(state.Input, StringComparer.Ordinal);

            return AiStepResult.Ok(
                value: input,
                output: "External input received.",
                data: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["waitingKey"] = waitingKey,
                    ["input"] = input,
                    ["inputReceivedAtUtc"] = state.InputReceivedAtUtc
                });
        }

        private static void EnsureWaitingIdentity(
            AiExecutionControlState state,
            string executionId,
            string waitingKey,
            string stepName)
        {
            if (!string.Equals(state.WaitingKey, waitingKey, StringComparison.Ordinal) ||
                !string.Equals(state.WaitingStepName, stepName, StringComparison.Ordinal) ||
                state.InputWaitMode != AiExecutionInputWaitMode.ExternalWaitStep)
            {
                throw new InvalidOperationException(
                    $"Execution '{executionId}' is already waiting on a different input boundary. " +
                    $"ExpectedKey='{waitingKey}', ActualKey='{state.WaitingKey ?? string.Empty}', " +
                    $"ExpectedStep='{stepName}', ActualStep='{state.WaitingStepName ?? string.Empty}'.");
            }
        }
    }
}
