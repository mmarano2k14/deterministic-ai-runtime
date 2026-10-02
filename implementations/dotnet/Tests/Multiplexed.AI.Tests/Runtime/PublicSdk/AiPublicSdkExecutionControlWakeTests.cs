using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Execution;
using Multiplexed.Abstractions.AI.ControlPlane.Replay;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Control;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.McpServer.PublicSdk;
using Multiplexed.AI.Runtime.Execution.Control;
using Multiplexed.AI.Sdk.Contracts.Control;
using Multiplexed.AI.Stores;
using Multiplexed.AI.Tests.Runtime.Invocation.Durable.Dag;
using Multiplexed.AI.Tests.Runtime.Publication;

namespace Multiplexed.AI.Tests.Runtime.PublicSdk
{
    public sealed class AiPublicSdkExecutionControlWakeTests
    {
        [Fact]
        public async Task Resume_Waiting_Published_Execution_Submits_One_Deterministic_Physical_Wake()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync();
            var run = await fixture.CreateAsync(publication);
            await SetExecutionStatusAsync(fixture, run.ExecutionId, AiExecutionStatus.Waiting);

            var record = await fixture.Store.GetRecordAsync(run.ExecutionId)
                ?? throw new InvalidOperationException("Execution record was not created.");
            var controller = new RecordingSharedRuntimeController(SourceRun(record));
            var control = new StubExecutionControlPlane(
                State(run.ExecutionId, AiExecutionControlAction.Resume, version: 12));
            var boundary = CreateBoundary(fixture, controller, control);

            var response = await fixture.AsAsync(() => boundary.ResumeAsync(
                run.ExecutionId,
                new AiSdkExecutionControlRequest { Reason = "test-resume" }));

            Assert.True(response.Accepted);
            var wake = Assert.Single(controller.Submissions);
            Assert.Equal($"sdk-control-wake-{run.ExecutionId}-resume-12", wake.RequestedSharedRunId);
            Assert.Equal(run.ExecutionId, wake.RunRequest?.RequestedExecutionId);
            Assert.Equal("runtime-a", wake.PreferredRuntimeInstanceId);
            Assert.Equal("true", wake.Metadata["control.wake"]);
            Assert.Equal("Resume", wake.Metadata["control.wake.action"]);
            Assert.Equal("12", wake.Metadata["control.wake.version"]);
        }

        [Fact]
        public async Task Resume_Running_Published_Execution_Does_Not_Submit_Another_Physical_Run()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync();
            var run = await fixture.CreateAsync(publication);
            await SetExecutionStatusAsync(fixture, run.ExecutionId, AiExecutionStatus.Running);

            var record = await fixture.Store.GetRecordAsync(run.ExecutionId)
                ?? throw new InvalidOperationException("Execution record was not created.");
            var controller = new RecordingSharedRuntimeController(SourceRun(record));
            var control = new StubExecutionControlPlane(
                State(run.ExecutionId, AiExecutionControlAction.Resume, version: 13));
            var boundary = CreateBoundary(fixture, controller, control);

            var response = await fixture.AsAsync(() => boundary.ResumeAsync(
                run.ExecutionId,
                new AiSdkExecutionControlRequest()));

            Assert.True(response.Accepted);
            Assert.Empty(controller.Submissions);
            Assert.Equal(0, controller.GetRunCalls);
        }

        [Fact]
        public async Task SubmitInput_Waiting_Published_Execution_Submits_Wake_For_Same_Execution()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync();
            var run = await fixture.CreateAsync(publication);
            await SetExecutionStatusAsync(fixture, run.ExecutionId, AiExecutionStatus.Waiting);

            var record = await fixture.Store.GetRecordAsync(run.ExecutionId)
                ?? throw new InvalidOperationException("Execution record was not created.");
            var controller = new RecordingSharedRuntimeController(SourceRun(record));
            var state = State(run.ExecutionId, AiExecutionControlAction.SubmitInput, version: 21);
            state.WaitingKey = "approval:test";
            state.InputReceivedAtUtc = DateTime.UtcNow;
            var control = new StubExecutionControlPlane(state);
            var boundary = CreateBoundary(fixture, controller, control);

            var response = await fixture.AsAsync(() => boundary.SubmitInputAsync(
                run.ExecutionId,
                new AiSdkExecutionInputSubmissionRequest
                {
                    WaitingKey = "approval:test",
                    Input = JsonSerializer.SerializeToElement(new { approved = true })
                }));

            Assert.True(response.Accepted);
            var wake = Assert.Single(controller.Submissions);
            Assert.Equal($"sdk-control-wake-{run.ExecutionId}-submitinput-21", wake.RequestedSharedRunId);
            Assert.Equal(run.ExecutionId, wake.RunRequest?.RequestedExecutionId);
            Assert.Equal("SubmitInput", wake.Metadata["control.wake.action"]);
        }

        [Fact]
        public async Task SubmitInput_Parked_Waiting_Step_Submits_Exact_ExternalWait_Continuation()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync();
            var run = await fixture.CreateAsync(publication);
            await SetExecutionStatusAsync(fixture, run.ExecutionId, AiExecutionStatus.Waiting);
            await SetStepStatusAsync(fixture, run.ExecutionId, "first", AiStepExecutionStatus.WaitingForExternal);

            var record = await fixture.Store.GetRecordAsync(run.ExecutionId)
                ?? throw new InvalidOperationException("Execution record was not created.");
            var controller = new RecordingSharedRuntimeController(SourceRun(record));
            var state = State(run.ExecutionId, AiExecutionControlAction.SubmitInput, version: 31);
            state.WaitingKey = "approval:published";
            state.WaitingStepName = "first";
            state.InputWaitMode = AiExecutionInputWaitMode.ExternalWaitStep;
            state.InputReceivedAtUtc = DateTime.UtcNow;
            var control = new StubExecutionControlPlane(state);
            var boundary = CreateBoundary(fixture, controller, control);

            var response = await fixture.AsAsync(() => boundary.SubmitInputAsync(
                run.ExecutionId,
                new AiSdkExecutionInputSubmissionRequest
                {
                    WaitingKey = "approval:published",
                    WaitingStepName = "first",
                    Input = JsonSerializer.SerializeToElement(new { approved = true })
                }));

            Assert.True(response.Accepted);
            var wake = Assert.Single(controller.Submissions);
            var hash = InputContinuationHash(run.ExecutionId, "first", "approval:published");
            Assert.Equal($"sdk-input-continuation-{hash}", wake.RequestedSharedRunId);
            Assert.Equal(AiSharedRuntimeSubmitMode.QueueFirst, wake.SubmitModeOverride);
            Assert.Null(wake.RunRequest?.RequestedExecutionId);
            var continuation = Assert.IsType<AiRuntimeExternalWaitContinuation>(wake.RunRequest?.ExternalWaitContinuation);
            Assert.Equal(run.ExecutionId, continuation.ExecutionId);
            Assert.Equal("first", continuation.StepName);
            Assert.Equal($"sdk-input-continuation:{hash}", continuation.ContinuationId);
            Assert.Equal("public-sdk-input-continuation", wake.Source);
            Assert.Equal(0, controller.GetRunCalls);
        }

        [Fact]
        public async Task SubmitInput_ExternalWaitStep_StillRunning_Submits_Durable_Exact_Continuation()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync();
            var run = await fixture.CreateAsync(publication);
            await SetStepAndExecutionStatusAsync(
                fixture,
                run.ExecutionId,
                "first",
                AiStepExecutionStatus.Running,
                AiExecutionStatus.Running);

            var record = await fixture.Store.GetRecordAsync(run.ExecutionId)
                ?? throw new InvalidOperationException("Execution record was not created.");
            var controller = new RecordingSharedRuntimeController(SourceRun(record));
            var state = State(run.ExecutionId, AiExecutionControlAction.SubmitInput, version: 32);
            state.WaitingKey = "approval:early";
            state.WaitingStepName = "first";
            state.InputWaitMode = AiExecutionInputWaitMode.ExternalWaitStep;
            state.InputReceivedAtUtc = DateTime.UtcNow;
            var boundary = CreateBoundary(
                fixture,
                controller,
                new StubExecutionControlPlane(state));

            var response = await fixture.AsAsync(() => boundary.SubmitInputAsync(
                run.ExecutionId,
                new AiSdkExecutionInputSubmissionRequest
                {
                    WaitingKey = "approval:early",
                    WaitingStepName = "first",
                    Input = JsonSerializer.SerializeToElement(new { approved = true })
                }));

            Assert.True(response.Accepted);
            var wake = Assert.Single(controller.Submissions);
            var hash = InputContinuationHash(run.ExecutionId, "first", "approval:early");
            Assert.Equal($"sdk-input-continuation-{hash}", wake.RequestedSharedRunId);
            Assert.Equal(AiSharedRuntimeSubmitMode.QueueFirst, wake.SubmitModeOverride);
            Assert.Null(wake.RunRequest?.RequestedExecutionId);
            var continuation = Assert.IsType<AiRuntimeExternalWaitContinuation>(
                wake.RunRequest?.ExternalWaitContinuation);
            Assert.Equal(run.ExecutionId, continuation.ExecutionId);
            Assert.Equal("first", continuation.StepName);
            Assert.Equal(0, controller.GetRunCalls);
        }

        private static string InputContinuationHash(string executionId, string stepName, string waitingKey)
        {
            var identity = string.Concat(executionId, "\n", stepName, "\n", waitingKey);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        }

        private static async Task SetStepAndExecutionStatusAsync(
            PublicationTestSupport.Fixture fixture,
            string executionId,
            string stepName,
            AiStepExecutionStatus stepStatus,
            AiExecutionStatus executionStatus)
        {
            var record = await fixture.Store.GetRecordAsync(executionId)
                ?? throw new InvalidOperationException("Execution record was not created.");
            var state = await fixture.Store.GetStateAsync(executionId)
                ?? throw new InvalidOperationException("Execution state was not created.");
            if (!state.Steps.TryGetValue(stepName, out var step))
            {
                step = new AiStepState { StepName = stepName };
                state.Steps[stepName] = step;
            }

            step.Status = stepStatus;
            record.Status = executionStatus;
            await fixture.Store.CreateAsync(record, state);
        }

        private static async Task SetStepStatusAsync(
            PublicationTestSupport.Fixture fixture,
            string executionId,
            string stepName,
            AiStepExecutionStatus status)
        {
            var record = await fixture.Store.GetRecordAsync(executionId)
                ?? throw new InvalidOperationException("Execution record was not created.");
            var state = await fixture.Store.GetStateAsync(executionId)
                ?? throw new InvalidOperationException("Execution state was not created.");
            if (!state.Steps.TryGetValue(stepName, out var step))
            {
                step = new AiStepState { StepName = stepName };
                state.Steps[stepName] = step;
            }
            step.Status = status;
            record.Status = AiExecutionStatus.Waiting;
            await fixture.Store.CreateAsync(record, state);
        }

        private static async Task SetExecutionStatusAsync(
            PublicationTestSupport.Fixture fixture,
            string executionId,
            AiExecutionStatus status)
        {
            var record = await fixture.Store.GetRecordAsync(executionId)
                ?? throw new InvalidOperationException("Execution record was not created.");
            var state = await fixture.Store.GetStateAsync(executionId)
                ?? throw new InvalidOperationException("Execution state was not created.");
            record.Status = status;
            await fixture.Store.CreateAsync(record, state);
        }

        private static AiSharedRunRecord SourceRun(AiExecutionRecord record) => new()
        {
            SharedRunId = "sdk-" + record.ExecutionId,
            Status = AiSharedRunStatus.Dispatched,
            RunRequest = new AiRuntimePipelineRunRequest
            {
                PipelineName = record.PipelineName!,
                RequestedExecutionId = record.ExecutionId,
                PipelineDefinitionSnapshot = record.PipelineDefinitionSnapshot,
                Input = "{\"amount\":1}"
            },
            ExecutionContextSnapshot = record.ExecutionContextSnapshot!,
            LocalRunId = "local-a",
            ExecutionId = record.ExecutionId,
            AssignedRuntimeInstanceId = "runtime-a",
            PipelineKey = record.PipelineName,
            SubmittedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, string> { ["source"] = "original-sdk-run" }
        };

        private static AiExecutionControlState State(
            string executionId,
            AiExecutionControlAction action,
            long version) => new()
        {
            ExecutionId = executionId,
            Status = AiExecutionControlStatus.Resuming,
            PendingAction = action,
            Version = version,
            UpdatedAtUtc = DateTime.UtcNow,
            ResumeRequestedAtUtc = DateTime.UtcNow
        };

        private static AiPublicSdkBoundary CreateBoundary(
            PublicationTestSupport.Fixture fixture,
            IAiSharedRuntimeController controller,
            IAiExecutionControlPlane executionControl)
        {
            var cancellationControl = DagTestProxy.Create<IAiExecutionControlService>((method, _) =>
                throw new NotSupportedException($"Unexpected execution-control call: {method.Name}"));
            var ledger = DagTestProxy.Create<IAiDecisionLedger>((method, _) =>
                throw new NotSupportedException($"Unexpected decision-ledger call: {method.Name}"));
            var replay = DagTestProxy.Create<IAiReplayControlPlane>((method, _) =>
                throw new NotSupportedException($"Unexpected replay call: {method.Name}"));

            var dagStore = DagTestProxy.Create<IAiDagExecutionStore>((method, args) => method.Name switch
            {
                nameof(IAiDagExecutionStore.GetRecordAsync) => fixture.Store.GetRecordAsync(
                    (string)args![0]!,
                    (CancellationToken)args[1]!),
                nameof(IAiDagExecutionStore.GetStateAsync) => fixture.Store.GetStateAsync(
                    (string)args![0]!,
                    (CancellationToken)args[1]!),
                _ => throw new NotSupportedException($"Unexpected DAG-store call: {method.Name}")
            });

            return new AiPublicSdkBoundary(
                fixture.Publisher,
                fixture.Runs,
                controller,
                dagStore,
                new AiDagExecutionCancellationCoordinator(dagStore, cancellationControl),
                new AccessorSnapshotProvider(fixture.Accessor),
                fixture.ControlPlane,
                ledger,
                Options.Create(new AiPublicSdkExecutionWatchOptions()),
                executionControl,
                replay);
        }

        private sealed class RecordingSharedRuntimeController : IAiSharedRuntimeController
        {
            private readonly AiSharedRunRecord _sourceRun;

            internal RecordingSharedRuntimeController(AiSharedRunRecord sourceRun)
            {
                _sourceRun = sourceRun;
            }

            internal int GetRunCalls { get; private set; }
            internal List<AiSharedRuntimeControllerRequest> Submissions { get; } = new();

            public Task<AiSharedRuntimeControllerResult> ExecuteAsync(
                AiSharedRuntimeControllerRequest request,
                CancellationToken cancellationToken = default) =>
                request.Operation switch
                {
                    AiSharedRuntimeControllerOperation.GetRun => GetRunAsync(request, cancellationToken),
                    AiSharedRuntimeControllerOperation.SubmitRun => SubmitRunAsync(request, cancellationToken),
                    _ => throw new NotSupportedException()
                };

            public Task<AiSharedRuntimeControllerResult> SubmitRunAsync(
                AiSharedRuntimeControllerRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Submissions.Add(request);
                return Task.FromResult(new AiSharedRuntimeControllerResult
                {
                    Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                    Success = true,
                    SharedRunId = request.RequestedSharedRunId,
                    ExecutionId = request.RunRequest?.RequestedExecutionId,
                    Run = new AiSharedRunRecord
                    {
                        SharedRunId = request.RequestedSharedRunId!,
                        Status = AiSharedRunStatus.Submitted,
                        RunRequest = request.RunRequest!,
                        ExecutionContextSnapshot = request.RunRequest?.ExecutionContextSnapshot ?? _sourceRun.ExecutionContextSnapshot,
                        PipelineKey = request.PipelineKey,
                        SubmittedAtUtc = DateTimeOffset.UtcNow,
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        Metadata = request.Metadata
                    },
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow
                });
            }

            public Task<AiSharedRuntimeControllerResult> GetRunAsync(
                AiSharedRuntimeControllerRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                GetRunCalls++;
                return Task.FromResult(new AiSharedRuntimeControllerResult
                {
                    Operation = AiSharedRuntimeControllerOperation.GetRun,
                    Success = true,
                    SharedRunId = request.SharedRunId,
                    ExecutionId = _sourceRun.ExecutionId,
                    Run = _sourceRun,
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow
                });
            }

            public Task<AiSharedRuntimeControllerResult> ListRunsAsync(
                AiSharedRuntimeControllerRequest request,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<AiSharedRuntimeControllerResult> CancelRunAsync(
                AiSharedRuntimeControllerRequest request,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
        }

        private sealed class StubExecutionControlPlane : IAiExecutionControlPlane
        {
            private readonly AiExecutionControlState _state;

            internal StubExecutionControlPlane(AiExecutionControlState state)
            {
                _state = state;
            }

            public Task<AiExecutionControlPlaneResult> ExecuteAsync(
                AiExecutionControlPlaneRequest request,
                CancellationToken cancellationToken = default) =>
                request.Operation switch
                {
                    AiExecutionControlPlaneOperation.Resume => ResumeAsync(request, cancellationToken),
                    AiExecutionControlPlaneOperation.SubmitHumanInput => SubmitHumanInputAsync(request, cancellationToken),
                    _ => throw new NotSupportedException()
                };

            public Task<AiExecutionControlPlaneResult> PauseAsync(
                AiExecutionControlPlaneRequest request,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<AiExecutionControlPlaneResult> ResumeAsync(
                AiExecutionControlPlaneRequest request,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(Success(request, AiExecutionControlPlaneOperation.Resume));

            public Task<AiExecutionControlPlaneResult> CancelAsync(
                AiExecutionControlPlaneRequest request,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<AiExecutionControlPlaneResult> SubmitHumanInputAsync(
                AiExecutionControlPlaneRequest request,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(Success(request, AiExecutionControlPlaneOperation.SubmitHumanInput));

            public Task<AiExecutionControlPlaneResult> GetStatusAsync(
                AiExecutionControlPlaneRequest request,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            private AiExecutionControlPlaneResult Success(
                AiExecutionControlPlaneRequest request,
                AiExecutionControlPlaneOperation operation) => new()
                {
                    ExecutionId = request.ExecutionId,
                    Operation = operation,
                    Success = true,
                    State = _state,
                    CorrelationId = request.CorrelationId,
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow
                };
        }

        private sealed class AccessorSnapshotProvider : IExecutionContextSnapshotProvider
        {
            private readonly Multiplexed.Rbac.Core.ExecutionContext.IExecutionContextAccessor _accessor;

            internal AccessorSnapshotProvider(
                Multiplexed.Rbac.Core.ExecutionContext.IExecutionContextAccessor accessor)
            {
                _accessor = accessor;
            }

            public ExecutionContextSnapshot MapToSnapshot()
            {
                var identity = _accessor.Current
                    ?? throw new InvalidOperationException("No test RBAC context is active.");
                return new ExecutionContextSnapshot
                {
                    ContextKey = identity.ContextKey,
                    Project = identity.Project,
                    UserId = identity.UserId,
                    TenantId = identity.TenantId,
                    TenantGroupId = identity.TenantGroupId,
                    CurrentNamespace = identity.CurrentNamespace,
                    Namespaces = identity.Namespaces.Select(entry => new NamespaceEntry
                    {
                        Name = entry.Name,
                        Trns = new HashSet<string>(entry.Trns, StringComparer.Ordinal)
                    }).ToList(),
                    InFlightCount = identity.InFlightCount,
                    TtlSeconds = identity.TtlSeconds
                };
            }
        }
    }
}
