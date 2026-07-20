using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Execution;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.Execution.Control;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.Execution;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Tests.Fixtures;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.Execution
{
    public sealed class AiExecutionControlPlaneTests
    {
        [Fact]
        public async Task PauseAsync_Should_Call_Control_Service_And_Return_State()
        {
            var service = new FakeExecutionControlService();
            var controlPlane = CreateControlPlane(service);

            var result = await controlPlane.PauseAsync(new AiExecutionControlPlaneRequest
            {
                ExecutionId = "execution-1",
                Operation = AiExecutionControlPlaneOperation.Pause,
                RequestedBy = "tester",
                Reason = "maintenance"
            });

            Assert.True(result.Success);
            Assert.Equal(AiExecutionControlPlaneOperation.Pause, result.Operation);
            Assert.NotNull(result.State);
            Assert.Equal(AiExecutionControlStatus.Pausing, result.State.Status);
            Assert.Equal("execution-1", service.LastExecutionId);
            Assert.Equal("maintenance", service.LastReason);
            Assert.Equal("tester", service.LastRequestedBy);
        }

        [Fact]
        public async Task ResumeAsync_Should_Call_Control_Service_And_Return_State()
        {
            var service = new FakeExecutionControlService();
            var controlPlane = CreateControlPlane(service);

            var result = await controlPlane.ResumeAsync(new AiExecutionControlPlaneRequest
            {
                ExecutionId = "execution-1",
                Operation = AiExecutionControlPlaneOperation.Resume,
                RequestedBy = "tester"
            });

            Assert.True(result.Success);
            Assert.NotNull(result.State);
            Assert.Equal(AiExecutionControlStatus.Resuming, result.State.Status);
            Assert.Equal("execution-1", service.LastExecutionId);
            Assert.Equal("tester", service.LastRequestedBy);
        }

        [Fact]
        public async Task CancelAsync_Should_Call_Control_Service_And_Return_State()
        {
            var service = new FakeExecutionControlService();
            var controlPlane = CreateControlPlane(service);

            var result = await controlPlane.CancelAsync(new AiExecutionControlPlaneRequest
            {
                ExecutionId = "execution-1",
                Operation = AiExecutionControlPlaneOperation.Cancel,
                RequestedBy = "tester",
                Reason = "operator requested"
            });

            Assert.True(result.Success);
            Assert.NotNull(result.State);
            Assert.Equal(AiExecutionControlStatus.Cancelling, result.State.Status);
            Assert.Equal("operator requested", service.LastReason);
        }

        [Fact]
        public async Task SubmitHumanInputAsync_Should_Require_WaitingKey()
        {
            var service = new FakeExecutionControlService();
            var controlPlane = CreateControlPlane(service);

            var result = await controlPlane.SubmitHumanInputAsync(new AiExecutionControlPlaneRequest
            {
                ExecutionId = "execution-1",
                Operation = AiExecutionControlPlaneOperation.SubmitHumanInput,
                Input = new Dictionary<string, object?>
                {
                    ["answer"] = "yes"
                }
            });

            Assert.False(result.Success);
            Assert.Contains("WaitingKey is required", result.FailureReason);
        }

        [Fact]
        public async Task SubmitHumanInputAsync_Should_Call_Control_Service()
        {
            var service = new FakeExecutionControlService();
            var controlPlane = CreateControlPlane(service);

            var result = await controlPlane.SubmitHumanInputAsync(new AiExecutionControlPlaneRequest
            {
                ExecutionId = "execution-1",
                Operation = AiExecutionControlPlaneOperation.SubmitHumanInput,
                WaitingKey = "approval",
                RequestedBy = "tester",
                Input = new Dictionary<string, object?>
                {
                    ["approved"] = true
                }
            });

            Assert.True(result.Success);
            Assert.NotNull(result.State);
            Assert.Equal(AiExecutionControlStatus.Resuming, result.State.Status);
            Assert.Equal("approval", service.LastWaitingKey);
            Assert.NotNull(service.LastInput);
            Assert.True((bool)service.LastInput["approved"]!);
        }

        [Fact]
        public async Task GetStatusAsync_Should_Call_GetStateAsync()
        {
            var service = new FakeExecutionControlService();
            var controlPlane = CreateControlPlane(service);

            var result = await controlPlane.GetStatusAsync(new AiExecutionControlPlaneRequest
            {
                ExecutionId = "execution-1",
                Operation = AiExecutionControlPlaneOperation.GetStatus
            });

            Assert.True(result.Success);
            Assert.NotNull(result.State);
            Assert.Equal(AiExecutionControlStatus.Running, result.State.Status);
            Assert.True(service.GetStateCalled);
        }

        [Fact]
        public async Task ExecuteAsync_Should_Dispatch_By_Operation()
        {
            var service = new FakeExecutionControlService();
            var controlPlane = CreateControlPlane(service);

            var result = await controlPlane.ExecuteAsync(new AiExecutionControlPlaneRequest
            {
                ExecutionId = "execution-1",
                Operation = AiExecutionControlPlaneOperation.Cancel,
                Reason = "test cancel"
            });

            Assert.True(result.Success);
            Assert.Equal(AiExecutionControlPlaneOperation.Cancel, result.Operation);
            Assert.Equal(AiExecutionControlStatus.Cancelling, result.State?.Status);
        }

        [Fact]
        public async Task PauseAsync_Should_Return_Failure_When_Disabled()
        {
            var service = new FakeExecutionControlService();

            var controlPlane = CreateControlPlane(
                service,
                new AiExecutionControlPlaneOptions
                {
                    EnablePause = false
                });

            var result = await controlPlane.PauseAsync(new AiExecutionControlPlaneRequest
            {
                ExecutionId = "execution-1",
                Operation = AiExecutionControlPlaneOperation.Pause
            });

            Assert.False(result.Success);
            Assert.Contains("disabled", result.FailureReason);
        }

        [Fact]
        public async Task PauseAsync_Should_Record_Started_And_Completed_Events()
        {
            var service = new FakeExecutionControlService();
            var observer = new CapturingControlPlaneObserver();

            var controlPlane = new AiExecutionControlPlane(
                service,
                Options.Create(new AiExecutionControlPlaneOptions()),
                observer);

            var result = await controlPlane.PauseAsync(new AiExecutionControlPlaneRequest
            {
                ExecutionId = "execution-1",
                Operation = AiExecutionControlPlaneOperation.Pause,
                CorrelationId = "correlation-1",
                Source = "unit-test",
                RequestedBy = "tester"
            });

            Assert.True(result.Success);
            Assert.Equal(2, observer.Events.Count);

            Assert.Equal(AiControlPlaneEventType.OperationStarted, observer.Events[0].EventType);
            Assert.Equal(AiControlPlaneEventType.OperationCompleted, observer.Events[1].EventType);

            Assert.All(observer.Events, controlPlaneEvent =>
            {
                Assert.Equal(AiControlPlaneArea.ExecutionControl, controlPlaneEvent.Area);
                Assert.Equal("Pause", controlPlaneEvent.Operation);
                Assert.Equal("execution-1", controlPlaneEvent.Correlation.ExecutionId);
                Assert.Equal("correlation-1", controlPlaneEvent.Correlation.CorrelationId);
            });
        }

        [Fact]
        public void AddAiControlPlane_Should_Register_Execution_ControlPlane()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiControlPlane();

            var descriptor = services.SingleOrDefault(service =>
                service.ServiceType == typeof(IAiExecutionControlPlane));

            Assert.NotNull(descriptor);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        }

        private static AiExecutionControlPlane CreateControlPlane(
            IAiExecutionControlService controlService,
            AiExecutionControlPlaneOptions? options = null)
        {
            return new AiExecutionControlPlane(
                controlService,
                Options.Create(options ?? new AiExecutionControlPlaneOptions()),
                new NoopAiControlPlaneObserver());
        }

        /// <summary>
        /// Fake execution control service used by execution control-plane tests.
        /// </summary>
        private sealed class FakeExecutionControlService : IAiExecutionControlService
        {
            public string? LastExecutionId { get; private set; }

            public string? LastReason { get; private set; }

            public string? LastRequestedBy { get; private set; }

            public string? LastRecoveryOwnerId { get; private set; }

            public string? LastWaitingKey { get; private set; }

            public string? LastWaitingStepName { get; private set; }

            public IReadOnlyDictionary<string, object?>? LastInput { get; private set; }

            public bool GetStateCalled { get; private set; }

            public bool PauseForRecoveryCalled { get; private set; }

            public bool ResumeFromRecoveryCalled { get; private set; }

            /// <inheritdoc />
            public Task<AiExecutionControlState> PauseExecutionAsync(
                string executionId,
                string? reason = null,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                LastExecutionId = executionId;
                LastReason = reason;
                LastRequestedBy = requestedBy;

                return Task.FromResult(CreateState(
                    executionId,
                    AiExecutionControlStatus.Pausing,
                    AiExecutionControlAction.Pause,
                    requestedBy,
                    reason));
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> ResumeExecutionAsync(
                string executionId,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                LastExecutionId = executionId;
                LastRequestedBy = requestedBy;

                return Task.FromResult(CreateState(
                    executionId,
                    AiExecutionControlStatus.Resuming,
                    AiExecutionControlAction.Resume,
                    requestedBy,
                    reason: null));
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> PauseExecutionForRecoveryAsync(
                string executionId,
                string recoveryOwnerId,
                string? reason = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                LastExecutionId = executionId;
                LastReason = reason;
                LastRequestedBy = recoveryOwnerId;
                LastRecoveryOwnerId = recoveryOwnerId;
                PauseForRecoveryCalled = true;

                return Task.FromResult(CreateState(
                    executionId,
                    AiExecutionControlStatus.Pausing,
                    AiExecutionControlAction.Pause,
                    recoveryOwnerId,
                    reason));
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> ResumeExecutionFromRecoveryAsync(
                string executionId,
                string recoveryOwnerId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                LastExecutionId = executionId;
                LastRequestedBy = recoveryOwnerId;
                LastRecoveryOwnerId = recoveryOwnerId;
                ResumeFromRecoveryCalled = true;

                return Task.FromResult(CreateState(
                    executionId,
                    AiExecutionControlStatus.Resuming,
                    AiExecutionControlAction.Resume,
                    recoveryOwnerId,
                    reason: null));
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> CancelExecutionAsync(
                string executionId,
                string? reason = null,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                LastExecutionId = executionId;
                LastReason = reason;
                LastRequestedBy = requestedBy;

                return Task.FromResult(CreateState(
                    executionId,
                    AiExecutionControlStatus.Cancelling,
                    AiExecutionControlAction.Cancel,
                    requestedBy,
                    reason));
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> MarkWaitingForInputAsync(
                string executionId,
                string waitingKey,
                string? waitingStepName = null,
                string? reason = null,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                LastExecutionId = executionId;
                LastWaitingKey = waitingKey;
                LastWaitingStepName = waitingStepName;
                LastReason = reason;
                LastRequestedBy = requestedBy;

                var state = CreateState(
                    executionId,
                    AiExecutionControlStatus.WaitingForInput,
                    AiExecutionControlAction.WaitForInput,
                    requestedBy,
                    reason);

                state.WaitingKey = waitingKey;
                state.WaitingStepName = waitingStepName;

                return Task.FromResult(state);
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> SubmitHumanInputAsync(
                string executionId,
                string waitingKey,
                IReadOnlyDictionary<string, object?> input,
                string? submittedBy = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(input);

                LastExecutionId = executionId;
                LastWaitingKey = waitingKey;
                LastInput = input;
                LastRequestedBy = submittedBy;

                var state = CreateState(
                    executionId,
                    AiExecutionControlStatus.Resuming,
                    AiExecutionControlAction.SubmitInput,
                    submittedBy,
                    reason: null);

                state.WaitingKey = waitingKey;
                state.Input = new Dictionary<string, object?>(
                    input,
                    StringComparer.Ordinal);

                return Task.FromResult(state);
            }

            /// <inheritdoc />
            public Task<AiExecutionControlDecision> CheckCanAdvanceAsync(
                string executionId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                LastExecutionId = executionId;

                return Task.FromResult(new AiExecutionControlDecision
                {
                    CanContinue = true,
                    Status = AiExecutionControlStatus.Running
                });
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> MarkPausedAsync(
                string executionId,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                LastExecutionId = executionId;
                LastRequestedBy = requestedBy;

                return Task.FromResult(CreateState(
                    executionId,
                    AiExecutionControlStatus.Paused,
                    AiExecutionControlAction.None,
                    requestedBy,
                    reason: null));
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> MarkRunningAsync(
                string executionId,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                LastExecutionId = executionId;
                LastRequestedBy = requestedBy;

                return Task.FromResult(CreateState(
                    executionId,
                    AiExecutionControlStatus.Running,
                    AiExecutionControlAction.None,
                    requestedBy,
                    reason: null));
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> MarkCancelledAsync(
                string executionId,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                LastExecutionId = executionId;
                LastRequestedBy = requestedBy;

                return Task.FromResult(CreateState(
                    executionId,
                    AiExecutionControlStatus.Cancelled,
                    AiExecutionControlAction.None,
                    requestedBy,
                    reason: null));
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState?> GetStateAsync(
                string executionId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                LastExecutionId = executionId;
                GetStateCalled = true;

                return Task.FromResult<AiExecutionControlState?>(CreateState(
                    executionId,
                    AiExecutionControlStatus.Running,
                    AiExecutionControlAction.None,
                    requestedBy: null,
                    reason: null));
            }

            private static AiExecutionControlState CreateState(
                string executionId,
                AiExecutionControlStatus status,
                AiExecutionControlAction pendingAction,
                string? requestedBy,
                string? reason)
            {
                return new AiExecutionControlState
                {
                    ExecutionId = executionId,
                    Status = status,
                    PendingAction = pendingAction,
                    RequestedBy = requestedBy,
                    Reason = reason,
                    Version = 1,
                    UpdatedAtUtc = DateTime.UtcNow
                };
            }
        }

        private sealed class CapturingControlPlaneObserver : IAiControlPlaneObserver
        {
            public List<AiControlPlaneEvent> Events { get; } = new();

            public Task RecordAsync(
                AiControlPlaneEvent controlPlaneEvent,
                CancellationToken cancellationToken = default)
            {
                Events.Add(controlPlaneEvent);

                return Task.CompletedTask;
            }
        }


    }
}