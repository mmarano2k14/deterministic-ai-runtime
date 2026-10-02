using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Control;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.Pipeline.Steps.Control;
using Multiplexed.AI.Tests.Runtime.Invocation.Durable.Dag;
using Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Support;

namespace Multiplexed.AI.Tests.Runtime.Execution.Control
{
    public sealed class AwaitExecutionInputStepTests
    {
        [Fact]
        public async Task ExecuteAsync_When_No_Input_Exists_Commits_Wait_Then_Parks()
        {
            AiExecutionControlState? stored = null;
            var markCalls = 0;
            var control = DagTestProxy.Create<IAiExecutionControlService>((method, args) => method.Name switch
            {
                nameof(IAiExecutionControlService.GetStateAsync) => Task.FromResult(stored),
                nameof(IAiExecutionControlService.MarkWaitingForInputAsync) => Mark(args),
                _ => throw new NotSupportedException($"Unexpected execution-control call: {method.Name}")
            });
            var context = CreateContext(control, "approval:demo", "Approve the proposed answer.");
            var step = new AwaitExecutionInputStep();

            var result = await step.ExecuteAsync(context);

            Assert.Equal(AiStepExecutionOutcome.Park, result.EffectiveOutcome);
            Assert.Equal(1, markCalls);
            Assert.NotNull(stored);
            Assert.Equal(AiExecutionControlStatus.WaitingForInput, stored!.Status);
            Assert.Equal(AiExecutionControlAction.WaitForInput, stored.PendingAction);
            Assert.Equal("approval:demo", stored.WaitingKey);
            Assert.Equal("approval", stored.WaitingStepName);
            Assert.Equal("Approve the proposed answer.", stored.Reason);
            Assert.Equal(AwaitExecutionInputStep.StepKey, stored.RequestedBy);
            Assert.Equal(AiExecutionInputWaitMode.ExternalWaitStep, stored.InputWaitMode);

            Task<AiExecutionControlState> Mark(object?[]? args)
            {
                markCalls++;
                Assert.NotNull(args);
                stored = new AiExecutionControlState
                {
                    ExecutionId = (string)args![0]!,
                    Status = AiExecutionControlStatus.WaitingForInput,
                    PendingAction = AiExecutionControlAction.WaitForInput,
                    WaitingKey = (string)args[1]!,
                    WaitingStepName = (string?)args[2],
                    Reason = (string?)args[3],
                    RequestedBy = (string?)args[4],
                    InputWaitMode = (AiExecutionInputWaitMode)args[5]!,
                    WaitingStartedAtUtc = DateTime.UtcNow,
                    Version = 1
                };
                return Task.FromResult(stored);
            }
        }

        [Fact]
        public async Task ExecuteAsync_When_Same_Wait_Is_Already_Durable_Reparks_Without_Rewriting_Control_State()
        {
            var state = new AiExecutionControlState
            {
                ExecutionId = "execution-1",
                Status = AiExecutionControlStatus.WaitingForInput,
                PendingAction = AiExecutionControlAction.WaitForInput,
                WaitingKey = "approval:demo",
                WaitingStepName = "approval",
                InputWaitMode = AiExecutionInputWaitMode.ExternalWaitStep,
                WaitingStartedAtUtc = DateTime.UtcNow,
                Version = 7
            };
            var control = DagTestProxy.Create<IAiExecutionControlService>((method, _) => method.Name switch
            {
                nameof(IAiExecutionControlService.GetStateAsync) => Task.FromResult<AiExecutionControlState?>(state),
                nameof(IAiExecutionControlService.MarkWaitingForInputAsync) => throw new InvalidOperationException("Durable wait must not be rewritten."),
                _ => throw new NotSupportedException($"Unexpected execution-control call: {method.Name}")
            });

            var result = await new AwaitExecutionInputStep().ExecuteAsync(
                CreateContext(control, "approval:demo"));

            Assert.Equal(AiStepExecutionOutcome.Park, result.EffectiveOutcome);
        }

        [Fact]
        public async Task ExecuteAsync_When_Input_Was_Submitted_Completes_With_Durable_Input()
        {
            var receivedAt = DateTime.UtcNow;
            var state = new AiExecutionControlState
            {
                ExecutionId = "execution-1",
                Status = AiExecutionControlStatus.Running,
                PendingAction = AiExecutionControlAction.None,
                WaitingKey = "approval:demo",
                WaitingStepName = "approval",
                InputWaitMode = AiExecutionInputWaitMode.ExternalWaitStep,
                InputReceivedAtUtc = receivedAt,
                Input = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["approved"] = true,
                    ["feedback"] = "continue"
                },
                Version = 9
            };
            var control = DagTestProxy.Create<IAiExecutionControlService>((method, _) => method.Name switch
            {
                nameof(IAiExecutionControlService.GetStateAsync) => Task.FromResult<AiExecutionControlState?>(state),
                nameof(IAiExecutionControlService.MarkWaitingForInputAsync) => throw new InvalidOperationException("Input already exists."),
                _ => throw new NotSupportedException($"Unexpected execution-control call: {method.Name}")
            });

            var result = await new AwaitExecutionInputStep().ExecuteAsync(
                CreateContext(control, "approval:demo"));

            Assert.True(result.Success);
            Assert.Equal(AiStepExecutionOutcome.Complete, result.EffectiveOutcome);
            var input = Assert.IsType<Dictionary<string, object?>>(result.Value);
            Assert.True(Assert.IsType<bool>(input["approved"]));
            Assert.Equal("continue", Assert.IsType<string>(input["feedback"]));
            Assert.Equal("approval:demo", Assert.IsType<string>(result.Data["waitingKey"]));
            Assert.Equal(receivedAt, Assert.IsType<DateTime>(result.Data["inputReceivedAtUtc"]));
        }

        [Fact]
        public async Task ExecuteAsync_When_Another_Wait_Is_Active_Fails_Closed()
        {
            var state = new AiExecutionControlState
            {
                ExecutionId = "execution-1",
                Status = AiExecutionControlStatus.WaitingForInput,
                PendingAction = AiExecutionControlAction.WaitForInput,
                WaitingKey = "approval:other",
                WaitingStepName = "other-step",
                InputWaitMode = AiExecutionInputWaitMode.ExternalWaitStep,
                WaitingStartedAtUtc = DateTime.UtcNow,
                Version = 3
            };
            var control = DagTestProxy.Create<IAiExecutionControlService>((method, _) => method.Name switch
            {
                nameof(IAiExecutionControlService.GetStateAsync) => Task.FromResult<AiExecutionControlState?>(state),
                _ => throw new NotSupportedException($"Unexpected execution-control call: {method.Name}")
            });

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new AwaitExecutionInputStep().ExecuteAsync(CreateContext(control, "approval:demo")));

            Assert.Contains("different input boundary", exception.Message, StringComparison.Ordinal);
        }

        private static AiStepExecutionContext CreateContext(
            IAiExecutionControlService control,
            string waitingKey,
            string? reason = null)
        {
            var services = new ServiceCollection()
                .AddSingleton(control)
                .BuildServiceProvider();
            var record = new AiExecutionRecord
            {
                ExecutionId = "execution-1",
                PipelineName = "input-demo",
                ExecutionMode = AiExecutionMode.Dag,
                Status = AiExecutionStatus.Running
            };
            var state = new AiExecutionState
            {
                ExecutionId = record.ExecutionId,
                PipelineName = record.PipelineName
            };
            var baseline = ChildDagCompositionTestData.CreateExecutionContext(record, state);
            var execution = new AiExecutionContext(
                record,
                state,
                services,
                baseline.StateReader,
                baseline.StateWriter,
                CancellationToken.None);
            var config = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [AwaitExecutionInputStep.WaitingKeyConfigKey] = waitingKey
            };
            if (!string.IsNullOrWhiteSpace(reason))
            {
                config[AwaitExecutionInputStep.ReasonConfigKey] = reason;
            }
            var resolved = new ResolvedAiPipelineStep
            {
                Name = "approval",
                StepKey = AwaitExecutionInputStep.StepKey,
                Step = new AwaitExecutionInputStep(),
                Config = config,
                Input = new Dictionary<string, object?>(StringComparer.Ordinal)
            };
            return new AiStepExecutionContext(execution, resolved);
        }
    }
}
