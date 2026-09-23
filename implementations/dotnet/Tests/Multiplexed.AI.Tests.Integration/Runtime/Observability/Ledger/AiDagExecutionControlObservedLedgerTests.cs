using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.Concurrency;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Control;
using Multiplexed.Abstractions.AI.Execution.Scheduling;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.AI.Observability.Ledger;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Engine.Steps;
using Multiplexed.AI.Runtime.Execution.Instance;
using Multiplexed.AI.Runtime.Observability.Context;
using Multiplexed.AI.Runtime.Observability.Logging;
using Multiplexed.AI.Stores;
using NSubstitute;
using Xunit;
using Multiplexed.Abstractions.AI.Observability.Events;

namespace Multiplexed.AI.Tests.Integration.Runtime.Observability.Ledger
{
    /// <summary>
    /// Tests decision ledger recording when DAG workers observe execution control states.
    /// </summary>
    public sealed class AiDagExecutionControlObservedLedgerTests
    {
        /// <summary>
        /// Verifies that a DAG worker records a cancel-observed ledger event when execution control requests cancellation.
        /// </summary>
        [Fact]
        public async Task ClaimNextAsync_WhenCancelIsObserved_ShouldRecordCancelObservedLedgerEvent()
        {
            var executionId = "exec-control-cancel-observed";
            var pipelineKey = "test-pipeline:v1";
            var workerId = "worker-1";

            var ledger = new InMemoryAiDecisionLedger();

            var services = CreateServices(
                ledger,
                new AiExecutionControlDecision
                {
                    CanContinue = false,
                    ShouldCancel = true,
                    Status = AiExecutionControlStatus.Cancelling,
                    Reason = "Cancellation requested by test."
                });

            var service = new AiDagStepClaimService(services);

            var claimed = await service.ClaimNextAsync(
                executionId,
                CreatePipeline(),
                pipelineKey,
                workerId,
                CancellationToken.None);

            Assert.Null(claimed);

            var entries = await ledger.GetByExecutionAsync(executionId);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Control &&
                entry.EventType == AiEngineEvents.Control.CancelObserved &&
                entry.Outcome == AiDecisionLedgerOutcome.Blocked &&
                entry.CorrelationContext.ExecutionId == executionId &&
                entry.CorrelationContext.PipelineName == pipelineKey &&
                entry.CorrelationContext.StepKey == "_control" &&
                entry.CorrelationContext.WorkerId == workerId &&
                entry.CorrelationContext.RuntimeInstanceId == workerId);

            await services.DagStore!.DidNotReceive().RecoverTimedOutStepsAsync(
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
        }

        /// <summary>
        /// Verifies that a DAG worker records a human-input waiting ledger event when execution control blocks for input.
        /// </summary>
        [Fact]
        public async Task ClaimNextAsync_WhenWaitingForHumanInputIsObserved_ShouldRecordHumanInputWaitingLedgerEvent()
        {
            var executionId = "exec-human-input-waiting-observed";
            var pipelineKey = "test-pipeline:v1";
            var workerId = "worker-1";

            var ledger = new InMemoryAiDecisionLedger();

            var services = CreateServices(
                ledger,
                new AiExecutionControlDecision
                {
                    CanContinue = false,
                    ShouldCancel = false,
                    ShouldStopClaiming = true,
                    IsWaitingForInput = true,
                    Status = AiExecutionControlStatus.WaitingForInput,
                    Reason = "Waiting for approval."
                });

            var service = new AiDagStepClaimService(services);

            var claimed = await service.ClaimNextAsync(
                executionId,
                CreatePipeline(),
                pipelineKey,
                workerId,
                CancellationToken.None);

            Assert.Null(claimed);

            var entries = await ledger.GetByExecutionAsync(executionId);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.HumanInput &&
                entry.EventType == AiEngineEvents.HumanInput.Waiting &&
                entry.Outcome == AiDecisionLedgerOutcome.Blocked &&
                entry.CorrelationContext.ExecutionId == executionId &&
                entry.CorrelationContext.PipelineName == pipelineKey &&
                entry.CorrelationContext.StepKey == "_human_input" &&
                entry.CorrelationContext.WorkerId == workerId &&
                entry.CorrelationContext.RuntimeInstanceId == workerId);

            await services.DagStore!.DidNotReceive().RecoverTimedOutStepsAsync(
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
        }

        /// <summary>
        /// Verifies that no control observation ledger event is recorded when execution control allows advancement.
        /// </summary>
        [Fact]
        public async Task ClaimNextAsync_WhenControlAllowsAdvance_ShouldNotRecordControlBlockedLedgerEvents()
        {
            var executionId = "exec-control-allowed";
            var pipelineKey = "test-pipeline:v1";
            var workerId = "worker-1";

            var ledger = new InMemoryAiDecisionLedger();

            var services = CreateServices(
                ledger,
                new AiExecutionControlDecision
                {
                    CanContinue = true,
                    ShouldCancel = false,
                    Status = AiExecutionControlStatus.Running
                });

            var state = new AiExecutionState
            {
                ExecutionId = executionId,
                PipelineName = "test-pipeline"
            };

            state.Steps["step-a"] = new AiStepState
            {
                StepName = "step-a",
                Status = AiStepExecutionStatus.Ready
            };

            services.DagStore!
                .GetStateAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(state);

            services.DagStore!
                .RecoverTimedOutStepsAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(0);

            services.DagStore!
                .TryClaimStepAsync(
                    executionId,
                    "step-a",
                    workerId,
                    Arg.Any<CancellationToken>())
                .Returns((AiClaimedStep?)null);

            var service = new AiDagStepClaimService(services);

            var claimed = await service.ClaimNextAsync(
                executionId,
                CreatePipeline(),
                pipelineKey,
                workerId,
                CancellationToken.None);

            Assert.Null(claimed);

            var entries = await ledger.GetByExecutionAsync(executionId);

            Assert.DoesNotContain(entries, entry =>
                entry.EventType == AiEngineEvents.Control.CancelObserved);

            Assert.DoesNotContain(entries, entry =>
                entry.EventType == AiEngineEvents.HumanInput.Waiting);
        }

        /// <summary>
        /// Verifies that a pause arriving after the initial claim gate but before the durable
        /// single-step ownership transfer prevents the claim.
        /// </summary>
        [Fact]
        public async Task ClaimNextAsync_WhenPauseArrivesAfterInitialGate_ShouldNotClaimReadyStep()
        {
            var executionId = "exec-control-late-pause-single";
            var pipelineKey = "test-pipeline:v1";
            var workerId = "worker-1";
            var ledger = new InMemoryAiDecisionLedger();

            var running = AiExecutionControlDecision.Continue(AiExecutionControlStatus.Running);
            var pausing = AiExecutionControlDecision.StopClaiming(
                AiExecutionControlStatus.Pausing,
                "Pause requested while claim preparation was in flight.");

            var services = CreateServices(ledger, running);
            var dagStore = services.DagStore!;
            var controlGate = services.ExecutionControlGate;
            var gateCalls = 0;

            controlGate
                .CheckBeforeAdvanceAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    gateCalls++;
                    return Task.FromResult(gateCalls < 3 ? running : pausing);
                });

            var state = new AiExecutionState
            {
                ExecutionId = executionId,
                PipelineName = "test-pipeline"
            };
            state.Steps["step-a"] = new AiStepState
            {
                StepName = "step-a",
                Status = AiStepExecutionStatus.Ready
            };

            dagStore
                .RecoverTimedOutStepsAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(0);
            dagStore
                .GetStateAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(state);

            var service = new AiDagStepClaimService(services);

            var claimed = await service.ClaimNextAsync(
                executionId,
                CreatePipeline(),
                pipelineKey,
                workerId,
                CancellationToken.None);

            Assert.Null(claimed);
            Assert.True(gateCalls >= 3);
            await services.ExecutionControlService.Received(1).MarkPausedAsync(
                executionId,
                workerId,
                Arg.Any<CancellationToken>());
            await dagStore.DidNotReceive().TryClaimStepAsync(
                executionId,
                "step-a",
                workerId,
                Arg.Any<CancellationToken>());
        }

        /// <summary>
        /// Verifies that a pause arriving after the initial batch gate but before the durable
        /// step ownership transfer prevents the batch claim.
        /// </summary>
        [Fact]
        public async Task ClaimBatchAsync_WhenPauseArrivesAfterInitialGate_ShouldNotClaimReadyStep()
        {
            var executionId = "exec-control-late-pause-batch";
            var pipelineKey = "test-pipeline:v1";
            var workerId = "worker-1";
            var ledger = new InMemoryAiDecisionLedger();

            var running = AiExecutionControlDecision.Continue(AiExecutionControlStatus.Running);
            var pausing = AiExecutionControlDecision.StopClaiming(
                AiExecutionControlStatus.Pausing,
                "Pause requested while batch claim preparation was in flight.");

            var services = CreateServices(ledger, running);
            var dagStore = services.DagStore!;
            var controlGate = services.ExecutionControlGate;
            var gateCalls = 0;

            controlGate
                .CheckBeforeAdvanceAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    gateCalls++;
                    return Task.FromResult(gateCalls < 3 ? running : pausing);
                });

            var state = new AiExecutionState
            {
                ExecutionId = executionId,
                PipelineName = "test-pipeline"
            };
            state.Steps["step-a"] = new AiStepState
            {
                StepName = "step-a",
                Status = AiStepExecutionStatus.Ready
            };

            dagStore
                .RecoverTimedOutStepsAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(0);
            dagStore
                .GetStateAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(state);

            var service = new AiDagStepClaimService(services);

            var claimed = await service.ClaimBatchAsync(
                executionId,
                CreatePipeline(),
                pipelineKey,
                workerId,
                maxSteps: 4,
                cancellationToken: CancellationToken.None);

            Assert.Empty(claimed);
            Assert.True(gateCalls >= 3);
            await services.ExecutionControlService.Received(1).MarkPausedAsync(
                executionId,
                workerId,
                Arg.Any<CancellationToken>());
            await dagStore.DidNotReceive().TryClaimStepAsync(
                executionId,
                "step-a",
                workerId,
                Arg.Any<CancellationToken>());
        }

        private static IAiDagExecutionEngineServices CreateServices(
            InMemoryAiDecisionLedger ledger,
            AiExecutionControlDecision controlDecision)
        {
            var services = Substitute.For<IAiDagExecutionEngineServices>();
            var dagStore = Substitute.For<IAiDagExecutionStore>();
            var observability = Substitute.For<IAiRuntimeObservability>();
            var runtimeMetrics = Substitute.For<IAiRuntimeMetrics>();
            var logger = Substitute.For<IAiRuntimeLogger>();
            var controlGate = Substitute.For<IAiExecutionControlGate>();
            var controlService = Substitute.For<IAiExecutionControlService>();
            var concurrencyGate = Substitute.For<IAiConcurrencyGate>();

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

            observability.Tracer.Returns(new PassthroughAiRuntimeTracer());
            observability.Ledger.Returns(recorder);
            observability.Metrics.Returns(runtimeMetrics);

            controlGate
                .CheckBeforeAdvanceAsync(
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>())
                .Returns(controlDecision);

            controlService
                .MarkPausedAsync(
                    Arg.Any<string>(),
                    Arg.Any<string?>(),
                    Arg.Any<CancellationToken>())
                .Returns(callInfo => Task.FromResult(new AiExecutionControlState
                {
                    ExecutionId = callInfo.ArgAt<string>(0),
                    Status = AiExecutionControlStatus.Paused,
                    PendingAction = AiExecutionControlAction.Pause,
                    RequestedBy = callInfo.ArgAt<string?>(1),
                    PausedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                }));

            controlService
                .MarkRunningAsync(
                    Arg.Any<string>(),
                    Arg.Any<string?>(),
                    Arg.Any<CancellationToken>())
                .Returns(callInfo => Task.FromResult(new AiExecutionControlState
                {
                    ExecutionId = callInfo.ArgAt<string>(0),
                    Status = AiExecutionControlStatus.Running,
                    PendingAction = AiExecutionControlAction.None,
                    RequestedBy = callInfo.ArgAt<string?>(1),
                    UpdatedAtUtc = DateTime.UtcNow
                }));

            concurrencyGate
                .TryAcquireAsync(
                    Arg.Any<AiConcurrencyContext>(),
                    Arg.Any<AiConcurrencyDefinition>(),
                    Arg.Any<CancellationToken>())
                .Returns(AiConcurrencyDecision.Allow());

            services.DagStore.Returns(dagStore);
            services.ObservabilityService.Returns(observability);
            services.Logger.Returns(logger);
            services.ExecutionControlGate.Returns(controlGate);
            services.ExecutionControlService.Returns(controlService);
            services.ConcurrencyGate.Returns(concurrencyGate);

            return services;
        }

        private static ResolvedAiPipeline CreatePipeline()
        {
            return new ResolvedAiPipeline
            {
                Name = "test-pipeline",
                Version = "v1",
                ExecutionMode = AiExecutionMode.Dag,
                Config = new Dictionary<string, object?>(),
                Steps =
                [
                    new ResolvedAiPipelineStep
                    {
                        Name = "step-a",
                        StepKey = "debug.pass",
                        Config = new Dictionary<string, object?>(),
                        DependsOn = Array.Empty<string>()
                    }
                ]
            };
        }

        private sealed class PassthroughAiRuntimeTracer : IAiRuntimeTracer
        {
            public IAiTraceScope StartExecution(AiExecutionTraceContext context)
            {
                return Substitute.For<IAiTraceScope>();
            }

            public IAiTraceScope StartResolver(AiResolverTraceContext context)
            {
                return Substitute.For<IAiTraceScope>();
            }

            public IAiTraceScope StartRetention(AiRetentionTraceContext context)
            {
                return Substitute.For<IAiTraceScope>();
            }

            public IAiTraceScope StartStep(AiStepTraceContext context)
            {
                return Substitute.For<IAiTraceScope>();
            }

            public IAiTraceScope StartStorage(AiStorageTraceContext context)
            {
                return Substitute.For<IAiTraceScope>();
            }

            public Task<TResult> TraceStorageAsync<TResult>(
                AiStorageTraceContext context,
                Func<IAiTraceScope, Task<TResult>> action,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var scope = Substitute.For<IAiTraceScope>();

                return action(scope);
            }

            public Task<TResult> TraceRetentionAsync<TResult>(
                AiRetentionTraceContext context,
                Func<IAiTraceScope, Task<TResult>> action,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var scope = Substitute.For<IAiTraceScope>();

                return action(scope);
            }

            public Task<TResult> TraceStepAsync<TResult>(
                AiStepTraceContext context,
                Func<Task<TResult>> action,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return action();
            }
        }
    }
}
