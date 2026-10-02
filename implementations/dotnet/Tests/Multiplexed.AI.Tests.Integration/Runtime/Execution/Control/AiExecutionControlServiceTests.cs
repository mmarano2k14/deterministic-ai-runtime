using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.Execution.Control;
using Multiplexed.AI.Runtime.Execution.Control;
using Multiplexed.AI.Stores.Cache.Redis.Control;
using StackExchange.Redis;
using Xunit;

namespace Multiplexed.AI.Tests.Integration.Runtime.Execution.Control
{
    /// <summary>
    /// Integration tests for <see cref="AiExecutionControlService"/>.
    /// </summary>
    public sealed class AiExecutionControlServiceTests : IAsyncLifetime
    {
        private readonly string _redisConnectionString = "localhost:6379";

        private IConnectionMultiplexer _multiplexer = default!;
        private RedisAiExecutionControlStore _store = default!;
        private AiExecutionControlService _service = default!;

        /// <inheritdoc />
        public async Task InitializeAsync()
        {
            _multiplexer = await ConnectionMultiplexer.ConnectAsync(_redisConnectionString)
                .ConfigureAwait(false);

            _store = new RedisAiExecutionControlStore(
                _multiplexer,
                new RedisExecutionControlKeyBuilder());

            _service = new AiExecutionControlService(_store);
        }

        /// <inheritdoc />
        public async Task DisposeAsync()
        {
            await _multiplexer.CloseAsync().ConfigureAwait(false);
            _multiplexer.Dispose();
        }

        /// <summary>
        /// Verifies that pause creates a durable pausing control state.
        /// </summary>
        [Fact]
        public async Task PauseExecutionAsync_WhenStateDoesNotExist_ShouldCreatePausingState()
        {
            var executionId = CreateExecutionId();

            var state = await _service.PauseExecutionAsync(
                    executionId,
                    reason: "operator pause",
                    requestedBy: "test")
                .ConfigureAwait(false);

            Assert.Equal(executionId, state.ExecutionId);
            Assert.Equal(AiExecutionControlStatus.Pausing, state.Status);
            Assert.Equal(AiExecutionControlAction.Pause, state.PendingAction);
            Assert.Equal("operator pause", state.Reason);
            Assert.Equal("test", state.RequestedBy);
            Assert.Equal(1, state.Version);
            Assert.NotNull(state.PauseRequestedAtUtc);

            var decision = await _service.CheckCanAdvanceAsync(executionId).ConfigureAwait(false);

            Assert.False(decision.CanContinue);
            Assert.True(decision.ShouldStopClaiming);
            Assert.False(decision.ShouldCancel);
            Assert.Equal(AiExecutionControlStatus.Pausing, decision.Status);

            await _store.DeleteAsync(executionId).ConfigureAwait(false);
        }

        /// <summary>
        /// Verifies that resume moves the execution control state to resuming.
        /// </summary>
        [Fact]
        public async Task ResumeExecutionAsync_WhenPaused_ShouldCreateResumingState()
        {
            var executionId = CreateExecutionId();

            await _service.PauseExecutionAsync(
                    executionId,
                    reason: "operator pause",
                    requestedBy: "test")
                .ConfigureAwait(false);

            var state = await _service.ResumeExecutionAsync(
                    executionId,
                    requestedBy: "operator")
                .ConfigureAwait(false);

            Assert.Equal(AiExecutionControlStatus.Resuming, state.Status);
            Assert.Equal(AiExecutionControlAction.Resume, state.PendingAction);
            Assert.Equal("operator", state.RequestedBy);
            Assert.Equal(2, state.Version);
            Assert.NotNull(state.ResumeRequestedAtUtc);

            var decision = await _service.CheckCanAdvanceAsync(executionId).ConfigureAwait(false);

            Assert.True(decision.CanContinue);
            Assert.False(decision.ShouldStopClaiming);
            Assert.False(decision.ShouldCancel);
            Assert.Equal(AiExecutionControlStatus.Resuming, decision.Status);

            await _store.DeleteAsync(executionId).ConfigureAwait(false);
        }

        /// <summary>
        /// Verifies that cancellation blocks execution advancement.
        /// </summary>
        [Fact]
        public async Task CancelExecutionAsync_ShouldCreateCancellingStateAndBlockAdvance()
        {
            var executionId = CreateExecutionId();

            var state = await _service.CancelExecutionAsync(
                    executionId,
                    reason: "operator cancel",
                    requestedBy: "test")
                .ConfigureAwait(false);

            Assert.Equal(AiExecutionControlStatus.Cancelling, state.Status);
            Assert.Equal(AiExecutionControlAction.Cancel, state.PendingAction);
            Assert.Equal("operator cancel", state.Reason);
            Assert.Equal("test", state.RequestedBy);
            Assert.Equal(1, state.Version);
            Assert.NotNull(state.CancellationRequestedAtUtc);

            var decision = await _service.CheckCanAdvanceAsync(executionId).ConfigureAwait(false);

            Assert.False(decision.CanContinue);
            Assert.True(decision.ShouldStopClaiming);
            Assert.True(decision.ShouldCancel);
            Assert.Equal(AiExecutionControlStatus.Cancelling, decision.Status);

            await _store.DeleteAsync(executionId).ConfigureAwait(false);
        }

        /// <summary>
        /// Verifies that cancellation has priority over resume.
        /// </summary>
        [Fact]
        public async Task ResumeExecutionAsync_WhenCancelling_ShouldNotOverrideCancellation()
        {
            var executionId = CreateExecutionId();

            await _service.CancelExecutionAsync(
                    executionId,
                    reason: "operator cancel",
                    requestedBy: "test")
                .ConfigureAwait(false);

            var state = await _service.ResumeExecutionAsync(
                    executionId,
                    requestedBy: "operator")
                .ConfigureAwait(false);

            Assert.Equal(AiExecutionControlStatus.Cancelling, state.Status);
            Assert.Equal(AiExecutionControlAction.Cancel, state.PendingAction);
            Assert.Equal("operator cancel", state.Reason);

            var decision = await _service.CheckCanAdvanceAsync(executionId).ConfigureAwait(false);

            Assert.False(decision.CanContinue);
            Assert.True(decision.ShouldCancel);

            await _store.DeleteAsync(executionId).ConfigureAwait(false);
        }

        /// <summary>
        /// Verifies that waiting for input blocks execution advancement.
        /// </summary>
        [Fact]
        public async Task MarkWaitingForInputAsync_ShouldCreateWaitingStateAndBlockAdvance()
        {
            var executionId = CreateExecutionId();

            var state = await _service.MarkWaitingForInputAsync(
                    executionId,
                    waitingKey: "approval:pricing",
                    waitingStepName: "human-approval",
                    reason: "approval required",
                    requestedBy: "runtime")
                .ConfigureAwait(false);

            Assert.Equal(AiExecutionControlStatus.WaitingForInput, state.Status);
            Assert.Equal(AiExecutionControlAction.WaitForInput, state.PendingAction);
            Assert.Equal("approval:pricing", state.WaitingKey);
            Assert.Equal("human-approval", state.WaitingStepName);
            Assert.Equal(AiExecutionInputWaitMode.ExecutionGate, state.InputWaitMode);
            Assert.Equal("approval required", state.Reason);
            Assert.Equal("runtime", state.RequestedBy);
            Assert.NotNull(state.WaitingStartedAtUtc);

            var decision = await _service.CheckCanAdvanceAsync(executionId).ConfigureAwait(false);

            Assert.False(decision.CanContinue);
            Assert.True(decision.ShouldStopClaiming);
            Assert.False(decision.ShouldCancel);
            Assert.True(decision.IsWaitingForInput);
            Assert.Equal(AiExecutionControlStatus.WaitingForInput, decision.Status);

            await _store.DeleteAsync(executionId).ConfigureAwait(false);
        }

        /// <summary>
        /// Verifies that submitted human input is persisted and moves the execution to resuming.
        /// </summary>
        [Fact]
        public async Task SubmitHumanInputAsync_WhenWaitingKeyMatches_ShouldPersistInputAndResume()
        {
            var executionId = CreateExecutionId();

            await _service.MarkWaitingForInputAsync(
                    executionId,
                    waitingKey: "approval:pricing",
                    waitingStepName: "human-approval",
                    reason: "approval required",
                    requestedBy: "runtime")
                .ConfigureAwait(false);

            var state = await _service.SubmitHumanInputAsync(
                    executionId,
                    waitingKey: "approval:pricing",
                    input: new Dictionary<string, object?>
                    {
                        ["approved"] = true,
                        ["comment"] = "approved by operator"
                    },
                    submittedBy: "operator")
                .ConfigureAwait(false);

            Assert.Equal(AiExecutionControlStatus.Resuming, state.Status);
            Assert.Equal(AiExecutionControlAction.SubmitInput, state.PendingAction);
            Assert.Equal("operator", state.RequestedBy);
            Assert.NotNull(state.InputReceivedAtUtc);
            Assert.True(state.Input.ContainsKey("approved"));
            Assert.True(state.Input.ContainsKey("comment"));

            var decision = await _service.CheckCanAdvanceAsync(executionId).ConfigureAwait(false);

            Assert.True(decision.CanContinue);
            Assert.False(decision.ShouldStopClaiming);
            Assert.False(decision.ShouldCancel);

            await _store.DeleteAsync(executionId).ConfigureAwait(false);
        }

        /// <summary>
        /// Verifies that a new waiting boundary cannot inherit input from a previous completed wait.
        /// </summary>
        [Fact]
        public async Task MarkWaitingForInputAsync_WhenStartingNewWait_ShouldClearPreviousInput()
        {
            var executionId = CreateExecutionId();

            await _service.MarkWaitingForInputAsync(
                    executionId,
                    waitingKey: "approval:first",
                    waitingStepName: "first-approval",
                    requestedBy: "runtime")
                .ConfigureAwait(false);

            await _service.SubmitHumanInputAsync(
                    executionId,
                    waitingKey: "approval:first",
                    input: new Dictionary<string, object?> { ["approved"] = true },
                    submittedBy: "operator")
                .ConfigureAwait(false);

            await _service.MarkRunningAsync(executionId, "runtime").ConfigureAwait(false);

            var state = await _service.MarkWaitingForInputAsync(
                    executionId,
                    waitingKey: "approval:second",
                    waitingStepName: "second-approval",
                    requestedBy: "runtime")
                .ConfigureAwait(false);

            Assert.Equal(AiExecutionControlStatus.WaitingForInput, state.Status);
            Assert.Equal("approval:second", state.WaitingKey);
            Assert.Equal("second-approval", state.WaitingStepName);
            Assert.Empty(state.Input);
            Assert.Null(state.InputReceivedAtUtc);
            Assert.NotNull(state.WaitingStartedAtUtc);

            await _store.DeleteAsync(executionId).ConfigureAwait(false);
        }

        /// <summary>
        /// Verifies that an exact parked-step input wait persists its continuation mode.
        /// </summary>
        [Fact]
        public async Task MarkWaitingForInputAsync_WithExternalWaitMode_ShouldPersistMode()
        {
            var executionId = CreateExecutionId();

            var state = await _service.MarkWaitingForInputAsync(
                    executionId,
                    waitingKey: "approval:parked",
                    waitingStepName: "approval",
                    reason: "approval required",
                    requestedBy: "execution.await-input",
                    inputWaitMode: AiExecutionInputWaitMode.ExternalWaitStep)
                .ConfigureAwait(false);

            Assert.Equal(AiExecutionInputWaitMode.ExternalWaitStep, state.InputWaitMode);

            var persisted = await _store.GetAsync(executionId).ConfigureAwait(false);
            Assert.NotNull(persisted);
            Assert.Equal(AiExecutionInputWaitMode.ExternalWaitStep, persisted!.InputWaitMode);

            await _store.DeleteAsync(executionId).ConfigureAwait(false);
        }

        /// <summary>
        /// Verifies that submitted input is accepted only while the durable wait is active.
        /// </summary>
        [Fact]
        public async Task SubmitHumanInputAsync_WhenWaitAlreadyReleased_ShouldRejectDuplicateSubmission()
        {
            var executionId = CreateExecutionId();

            await _service.MarkWaitingForInputAsync(
                    executionId,
                    waitingKey: "approval:once",
                    waitingStepName: "approval",
                    requestedBy: "runtime")
                .ConfigureAwait(false);

            await _service.SubmitHumanInputAsync(
                    executionId,
                    waitingKey: "approval:once",
                    input: new Dictionary<string, object?> { ["approved"] = true },
                    submittedBy: "operator")
                .ConfigureAwait(false);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _service.SubmitHumanInputAsync(
                    executionId,
                    waitingKey: "approval:once",
                    input: new Dictionary<string, object?> { ["approved"] = false },
                    submittedBy: "operator"));

            Assert.Contains("not currently accepting input", exception.Message, StringComparison.Ordinal);

            await _store.DeleteAsync(executionId).ConfigureAwait(false);
        }

        /// <summary>
        /// Verifies that submitting input with an unexpected waiting key fails.
        /// </summary>
        [Fact]
        public async Task SubmitHumanInputAsync_WhenWaitingKeyDoesNotMatch_ShouldThrow()
        {
            var executionId = CreateExecutionId();

            await _service.MarkWaitingForInputAsync(
                    executionId,
                    waitingKey: "approval:pricing",
                    waitingStepName: "human-approval",
                    reason: "approval required",
                    requestedBy: "runtime")
                .ConfigureAwait(false);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _service.SubmitHumanInputAsync(
                    executionId,
                    waitingKey: "approval:wrong",
                    input: new Dictionary<string, object?>
                    {
                        ["approved"] = true
                    },
                    submittedBy: "operator"));

            await _store.DeleteAsync(executionId).ConfigureAwait(false);
        }

        /// <summary>
        /// Verifies that an execution without control state is allowed to advance.
        /// </summary>
        [Fact]
        public async Task CheckCanAdvanceAsync_WhenStateDoesNotExist_ShouldAllowAdvance()
        {
            var executionId = CreateExecutionId();

            var decision = await _service.CheckCanAdvanceAsync(executionId).ConfigureAwait(false);

            Assert.True(decision.CanContinue);
            Assert.False(decision.ShouldStopClaiming);
            Assert.False(decision.ShouldCancel);
            Assert.False(decision.IsWaitingForInput);
            Assert.Equal(AiExecutionControlStatus.Running, decision.Status);
        }

        private static string CreateExecutionId()
        {
            return $"test-exec-control-service-{Guid.NewGuid():N}";
        }
    }
}