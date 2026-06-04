using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.Execution.Control;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.AI.Observability.Ledger;
using Multiplexed.AI.Runtime.Execution.Control;
using Multiplexed.AI.Runtime.Execution.Instance;
using Multiplexed.AI.Runtime.Observability.Context;
using NSubstitute;
using Xunit;

namespace Multiplexed.AI.Tests.Integration.Runtime.Observability.Ledger
{
    /// <summary>
    /// Tests decision ledger recording for direct execution control commands.
    /// </summary>
    public sealed class AiExecutionControlServiceLedgerTests
    {
        /// <summary>
        /// Verifies that pause requests are recorded in the decision ledger.
        /// </summary>
        [Fact]
        public async Task PauseExecutionAsync_ShouldRecordPauseRequestedLedgerEvent()
        {
            var executionId = "exec-control-pause";
            var ledger = new InMemoryAiDecisionLedger();

            var service = CreateService(
                ledger,
                out _);

            var state = await service.PauseExecutionAsync(
                executionId,
                reason: "Pause requested by test.",
                requestedBy: "tester",
                CancellationToken.None);

            Assert.Equal(AiExecutionControlStatus.Pausing, state.Status);

            var entries = await ledger.GetByExecutionAsync(executionId);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Control &&
                entry.EventType == AiDecisionLedgerEvents.Control.PauseRequested &&
                entry.Outcome == AiDecisionLedgerOutcome.Applied &&
                entry.CorrelationContext.StepKey == "_control");
        }

        /// <summary>
        /// Verifies that resume requests are recorded in the decision ledger.
        /// </summary>
        [Fact]
        public async Task ResumeExecutionAsync_ShouldRecordResumeRequestedLedgerEvent()
        {
            var executionId = "exec-control-resume";
            var ledger = new InMemoryAiDecisionLedger();

            var service = CreateService(
                ledger,
                out _);

            var state = await service.ResumeExecutionAsync(
                executionId,
                requestedBy: "tester",
                CancellationToken.None);

            Assert.Equal(AiExecutionControlStatus.Resuming, state.Status);

            var entries = await ledger.GetByExecutionAsync(executionId);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Control &&
                entry.EventType == AiDecisionLedgerEvents.Control.ResumeRequested &&
                entry.Outcome == AiDecisionLedgerOutcome.Applied &&
                entry.CorrelationContext.StepKey == "_control");
        }

        /// <summary>
        /// Verifies that cancellation requests are recorded in the decision ledger.
        /// </summary>
        [Fact]
        public async Task CancelExecutionAsync_ShouldRecordCancelRequestedLedgerEvent()
        {
            var executionId = "exec-control-cancel";
            var ledger = new InMemoryAiDecisionLedger();

            var service = CreateService(
                ledger,
                out _);

            var state = await service.CancelExecutionAsync(
                executionId,
                reason: "Cancel requested by test.",
                requestedBy: "tester",
                CancellationToken.None);

            Assert.Equal(AiExecutionControlStatus.Cancelling, state.Status);

            var entries = await ledger.GetByExecutionAsync(executionId);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Control &&
                entry.EventType == AiDecisionLedgerEvents.Control.CancelRequested &&
                entry.Outcome == AiDecisionLedgerOutcome.Applied &&
                entry.CorrelationContext.StepKey == "_control");
        }

        /// <summary>
        /// Verifies that waiting-for-input transitions record requested and waiting human-input events.
        /// </summary>
        [Fact]
        public async Task MarkWaitingForInputAsync_ShouldRecordHumanInputRequestedAndWaitingEvents()
        {
            var executionId = "exec-human-input-requested";
            var waitingKey = "approval";
            var waitingStepKey = "approve-step";
            var ledger = new InMemoryAiDecisionLedger();

            var service = CreateService(
                ledger,
                out _);

            var state = await service.MarkWaitingForInputAsync(
                executionId,
                waitingKey,
                waitingStepKey,
                reason: "Waiting for approval.",
                requestedBy: "runtime",
                CancellationToken.None);

            Assert.Equal(AiExecutionControlStatus.WaitingForInput, state.Status);
            Assert.Equal("approve-step", state.WaitingStepName);
            Assert.Equal("approval", state.WaitingKey);

            var entries = await ledger.GetByExecutionAsync(executionId);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.HumanInput &&
                entry.EventType == AiDecisionLedgerEvents.HumanInput.Requested &&
                entry.Outcome == AiDecisionLedgerOutcome.Applied &&
                entry.CorrelationContext.StepKey == waitingStepKey);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.HumanInput &&
                entry.EventType == AiDecisionLedgerEvents.HumanInput.Waiting &&
                entry.Outcome == AiDecisionLedgerOutcome.Applied &&
                entry.CorrelationContext.StepKey == waitingStepKey);
        }

        /// <summary>
        /// Verifies that submitted human input is recorded in the decision ledger.
        /// </summary>
        [Fact]
        public async Task SubmitHumanInputAsync_ShouldRecordHumanInputSubmittedLedgerEvent()
        {
            var executionId = "exec-human-input-submitted";
            var waitingKey = "approval";
            var waitingStepKey = "approve-step";
            var ledger = new InMemoryAiDecisionLedger();

            var service = CreateService(
                ledger,
                out _);

            await service.MarkWaitingForInputAsync(
                executionId,
                waitingKey,
                waitingStepKey,
                reason: "Waiting for approval.",
                requestedBy: "runtime",
                CancellationToken.None);

            var state = await service.SubmitHumanInputAsync(
                executionId,
                waitingKey,
                new Dictionary<string, object?>
                {
                    ["approved"] = true
                },
                submittedBy: "tester",
                CancellationToken.None);

            Assert.Equal(AiExecutionControlStatus.Resuming, state.Status);
            Assert.Equal(AiExecutionControlAction.SubmitInput, state.PendingAction);

            var entries = await ledger.GetByExecutionAsync(executionId);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.HumanInput &&
                entry.EventType == AiDecisionLedgerEvents.HumanInput.Submitted &&
                entry.Outcome == AiDecisionLedgerOutcome.Applied &&
                entry.CorrelationContext.StepKey == waitingStepKey);
        }

        /// <summary>
        /// Verifies that effective paused transitions are recorded in the decision ledger.
        /// </summary>
        [Fact]
        public async Task MarkPausedAsync_ShouldRecordPausedLedgerEvent()
        {
            var executionId = "exec-control-paused";
            var ledger = new InMemoryAiDecisionLedger();

            var service = CreateService(
                ledger,
                out _);

            await service.PauseExecutionAsync(
                executionId,
                reason: "Pause requested.",
                requestedBy: "tester",
                CancellationToken.None);

            var state = await service.MarkPausedAsync(
                executionId,
                requestedBy: "worker-1",
                CancellationToken.None);

            Assert.Equal(AiExecutionControlStatus.Paused, state.Status);

            var entries = await ledger.GetByExecutionAsync(executionId);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Control &&
                entry.EventType == AiDecisionLedgerEvents.Control.Paused &&
                entry.Outcome == AiDecisionLedgerOutcome.Applied &&
                entry.CorrelationContext.StepKey == "_control");
        }

        /// <summary>
        /// Verifies that effective resumed/running transitions are recorded in the decision ledger.
        /// </summary>
        [Fact]
        public async Task MarkRunningAsync_ShouldRecordResumedLedgerEvent()
        {
            var executionId = "exec-control-resumed";
            var ledger = new InMemoryAiDecisionLedger();

            var service = CreateService(
                ledger,
                out _);

            await service.ResumeExecutionAsync(
                executionId,
                requestedBy: "tester",
                CancellationToken.None);

            var state = await service.MarkRunningAsync(
                executionId,
                requestedBy: "worker-1",
                CancellationToken.None);

            Assert.Equal(AiExecutionControlStatus.Running, state.Status);

            var entries = await ledger.GetByExecutionAsync(executionId);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Control &&
                entry.EventType == AiDecisionLedgerEvents.Control.Resumed &&
                entry.Outcome == AiDecisionLedgerOutcome.Applied &&
                entry.CorrelationContext.StepKey == "_control");
        }

       

        private static AiExecutionControlService CreateService(
            InMemoryAiDecisionLedger ledger,
            out InMemoryExecutionControlStore store)
        {
            store = new InMemoryExecutionControlStore();

            var observability = Substitute.For<IAiRuntimeObservability>();

            IAiRuntimeInstanceIdentityDescriptor runtimeInstanceIdentity =
            new DefaultAiRuntimeInstanceIdentity();

            IAiRuntimeCorrelationAccessor correlationAccessor =
                new AsyncLocalAiRuntimeCorrelationAccessor(runtimeInstanceIdentity);

            var recorder = new DefaultAiDecisionLedgerRecorder(
                ledger,
                correlationAccessor,
                Options.Create(new AiDecisionLedgerRecorderOptions
                {
                    WriteMode = AiDecisionLedgerWriteMode.Strict,
                    StorageMode = AiDecisionLedgerStorageMode.InMemory
                }),
                NullLogger<DefaultAiDecisionLedgerRecorder>.Instance);

            observability.Ledger.Returns(recorder);

            return new AiExecutionControlService(
                store,
                observability);
        }

        private sealed class InMemoryExecutionControlStore : IAiExecutionControlStore
        {
            private readonly Dictionary<string, AiExecutionControlState> _states =
                new(StringComparer.Ordinal);

            public Task<AiExecutionControlState?> GetAsync(
                string executionId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                _states.TryGetValue(
                    executionId,
                    out var state);

                return Task.FromResult(state);
            }

            public Task<bool> TryCreateAsync(
                AiExecutionControlState state,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_states.ContainsKey(state.ExecutionId))
                {
                    return Task.FromResult(false);
                }

                _states[state.ExecutionId] = Clone(state);

                return Task.FromResult(true);
            }

            public Task<bool> TryUpdateAsync(
                AiExecutionControlState state,
                int expectedVersion,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!_states.TryGetValue(
                        state.ExecutionId,
                        out var existing))
                {
                    return Task.FromResult(false);
                }

                if (existing.Version != expectedVersion)
                {
                    return Task.FromResult(false);
                }

                _states[state.ExecutionId] = Clone(state);

                return Task.FromResult(true);
            }

            private static AiExecutionControlState Clone(
                AiExecutionControlState state)
            {
                return new AiExecutionControlState
                {
                    ExecutionId = state.ExecutionId,
                    Status = state.Status,
                    PendingAction = state.PendingAction,
                    Reason = state.Reason,
                    RequestedBy = state.RequestedBy,
                    WaitingKey = state.WaitingKey,
                    WaitingStepName = state.WaitingStepName,
                    Input = new Dictionary<string, object?>(state.Input, StringComparer.Ordinal),
                    Version = state.Version,
                    UpdatedAtUtc = state.UpdatedAtUtc,
                    PauseRequestedAtUtc = state.PauseRequestedAtUtc,
                    PausedAtUtc = state.PausedAtUtc,
                    ResumeRequestedAtUtc = state.ResumeRequestedAtUtc,
                    CancellationRequestedAtUtc = state.CancellationRequestedAtUtc,
                    CancelledAtUtc = state.CancelledAtUtc,
                    WaitingStartedAtUtc = state.WaitingStartedAtUtc,
                    InputReceivedAtUtc = state.InputReceivedAtUtc
                };
            }

            public Task SetAsync(
                AiExecutionControlState state,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(state);

                _states[state.ExecutionId] = Clone(state);

                return Task.CompletedTask;
            }

            public Task<bool> TryUpdateAsync(
                AiExecutionControlState state,
                long expectedVersion,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(state);

                if (!_states.TryGetValue(
                        state.ExecutionId,
                        out var existing))
                {
                    return Task.FromResult(false);
                }

                if (existing.Version != expectedVersion)
                {
                    return Task.FromResult(false);
                }

                _states[state.ExecutionId] = Clone(state);

                return Task.FromResult(true);
            }

            public Task<bool> DeleteAsync(
                string executionId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(executionId))
                {
                    return Task.FromResult(false);
                }

                return Task.FromResult(_states.Remove(executionId));
            }
        }
    }
}