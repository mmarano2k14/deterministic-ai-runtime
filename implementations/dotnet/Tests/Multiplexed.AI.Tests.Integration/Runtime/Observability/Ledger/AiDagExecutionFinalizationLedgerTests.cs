using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Control;
using Multiplexed.Abstractions.AI.Execution.Scheduling;
using Multiplexed.Abstractions.AI.Execution.State;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Observability.Ledger;
using Multiplexed.AI.Runtime.AI.Policies;
using Multiplexed.AI.Runtime.Execution.Context;
using Multiplexed.AI.Runtime.Execution.Convergence;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Engine.Finalization;
using Multiplexed.AI.Runtime.Execution.Engine.Models;
using Multiplexed.AI.Runtime.Execution.Engine.Retention;
using Multiplexed.AI.Runtime.Execution.Instance;
using Multiplexed.AI.Runtime.Execution.Retention;
using Multiplexed.AI.Runtime.Execution.Retention.Models;
using Multiplexed.AI.Runtime.Observability.Context;
using Multiplexed.AI.Runtime.Observability.Logging;
using Multiplexed.AI.Stores;
using Multiplexed.AI.Tests.Integration.Helpers;
using NSubstitute;
using Xunit;
using Multiplexed.Abstractions.AI.Observability.Events;

namespace Multiplexed.AI.Tests.Integration.Runtime.Observability.Ledger
{
    /// <summary>
    /// Tests decision ledger recording around DAG execution finalization.
    /// </summary>
    public sealed class AiDagExecutionFinalizationLedgerTests
    {
        /// <summary>
        /// Verifies that successful completed finalization records all finalization and terminal execution events.
        /// </summary>
        [Fact]
        public async Task PersistDistributedConvergedRecordAsync_WhenCompleted_ShouldRecordFinalizationAndExecutionEvents()
        {
            var executionId = "exec-finalization-completed";
            var runtimeInstanceId = "runtime-1";
            var expectedStepKey = "expected-step-key";

            var ledger = new InMemoryAiDecisionLedger();

            var services = CreateServices(
                ledger,
                runtimeInstanceId,
                shouldFinalize: true);

            var service = CreateService(services);

            var record = CreateRecord(
                executionId,
                AiExecutionStatus.Running);

            record.CompletedSteps.Add("step-a");

            var state = CreateState(executionId);
            var pipeline = CreatePipeline();

            var convergence = new AiDagExecutionConvergenceResult
            {
                Status = AiExecutionStatus.Completed
            };

            await service.PersistDistributedConvergedRecordAsync(
                record,
                convergence,
                expectedStepKey,
                state,
                pipeline,
                BuildExecutionContext,
                PersistAsync,
                CancellationToken.None);

            var entries = await ledger.GetByExecutionAsync(executionId);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Finalization &&
                entry.EventType == AiEngineEvents.Finalization.Started &&
                entry.Outcome == AiDecisionLedgerOutcome.Started);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Finalization &&
                entry.EventType == AiEngineEvents.Finalization.Completed &&
                entry.Outcome == AiDecisionLedgerOutcome.Completed);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Execution &&
                entry.EventType == AiEngineEvents.Execution.Finalized &&
                entry.Outcome == AiDecisionLedgerOutcome.Completed);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Execution &&
                entry.EventType == AiEngineEvents.Execution.Completed &&
                entry.Outcome == AiDecisionLedgerOutcome.Completed);

            Assert.DoesNotContain(entries, entry =>
                entry.EventType == AiEngineEvents.Finalization.CancellationOverrideApplied);

            var finalizationAndExecutionEntries = entries
                .Where(entry =>
                    entry.Category == AiDecisionLedgerCategory.Finalization ||
                    entry.Category == AiDecisionLedgerCategory.Execution)
                .ToList();

            foreach (var entry in finalizationAndExecutionEntries)
            {
                Assert.Equal(executionId, entry.CorrelationContext.ExecutionId);
                Assert.Equal("test-pipeline:v1", entry.CorrelationContext.PipelineName);
                Assert.Equal(runtimeInstanceId, entry.CorrelationContext.WorkerId);
                Assert.Equal(runtimeInstanceId, entry.CorrelationContext.RuntimeInstanceId);
            }

            await services.DagStore!.Received(1).TryFinalizeExecutionAsync(
                Arg.Is<AiDagExecutionFinalizationRequest>(request =>
                    request.ExecutionId == executionId &&
                    request.Status == AiExecutionStatus.Completed &&
                    request.ExpectedExecutionStepKey == expectedStepKey &&
                    request.WorkerId == runtimeInstanceId),
                Arg.Any<CancellationToken>());
        }

        /// <summary>
        /// Verifies that cancellation control override records the cancellation override and cancelled execution events.
        /// </summary>
        [Fact]
        public async Task PersistDistributedConvergedRecordAsync_WhenCancellationOverrideApplies_ShouldRecordOverrideAndCancelledEvents()
        {
            var executionId = "exec-finalization-cancelled";
            var runtimeInstanceId = "runtime-1";
            var expectedStepKey = "expected-step-key";

            var ledger = new InMemoryAiDecisionLedger();

            var services = CreateServices(
                ledger,
                runtimeInstanceId,
                shouldFinalize: true,
                shouldCancel: true);

            var service = CreateService(services);

            var record = CreateRecord(
                executionId,
                AiExecutionStatus.Running);

            record.CompletedSteps.Add("step-a");

            var state = CreateState(executionId);
            var pipeline = CreatePipeline();

            var convergence = new AiDagExecutionConvergenceResult
            {
                Status = AiExecutionStatus.Completed
            };

            await service.PersistDistributedConvergedRecordAsync(
                record,
                convergence,
                expectedStepKey,
                state,
                pipeline,
                BuildExecutionContext,
                PersistAsync,
                CancellationToken.None);

            var entries = await ledger.GetByExecutionAsync(executionId);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Finalization &&
                entry.EventType == AiEngineEvents.Finalization.Started &&
                entry.Outcome == AiDecisionLedgerOutcome.Started);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Finalization &&
                entry.EventType == AiEngineEvents.Finalization.CancellationOverrideApplied &&
                entry.Outcome == AiDecisionLedgerOutcome.Applied);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Finalization &&
                entry.EventType == AiEngineEvents.Finalization.Completed &&
                entry.Outcome == AiDecisionLedgerOutcome.Completed);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Execution &&
                entry.EventType == AiEngineEvents.Execution.Finalized &&
                entry.Outcome == AiDecisionLedgerOutcome.Cancelled);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Execution &&
                entry.EventType == AiEngineEvents.Execution.Cancelled &&
                entry.Outcome == AiDecisionLedgerOutcome.Cancelled);

            await services.DagStore!.Received(1).TryFinalizeExecutionAsync(
                Arg.Is<AiDagExecutionFinalizationRequest>(request =>
                    request.ExecutionId == executionId &&
                    request.Status == AiExecutionStatus.Cancelled),
                Arg.Any<CancellationToken>());
        }

        /// <summary>
        /// Verifies that losing a distributed finalization race records finalization race lost.
        /// </summary>
        [Fact]
        public async Task PersistDistributedConvergedRecordAsync_WhenFinalizationRaceIsLost_ShouldRecordFinalizationRaceLost()
        {
            var executionId = "exec-finalization-race-lost";
            var runtimeInstanceId = "runtime-1";
            var expectedStepKey = "expected-step-key";

            var ledger = new InMemoryAiDecisionLedger();

            var services = CreateServices(
                ledger,
                runtimeInstanceId,
                shouldFinalize: false);

            var service = CreateService(services);

            var record = CreateRecord(
                executionId,
                AiExecutionStatus.Running);

            record.CompletedSteps.Add("step-a");

            var refreshed = CreateRecord(
                executionId,
                AiExecutionStatus.Completed);

            var state = CreateState(executionId);
            var pipeline = CreatePipeline();

            services.DagStore!
                .GetRecordAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(refreshed);

            var convergence = new AiDagExecutionConvergenceResult
            {
                Status = AiExecutionStatus.Completed
            };

            await service.PersistDistributedConvergedRecordAsync(
                record,
                convergence,
                expectedStepKey,
                state,
                pipeline,
                BuildExecutionContext,
                PersistAsync,
                CancellationToken.None);

            var entries = await ledger.GetByExecutionAsync(executionId);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Finalization &&
                entry.EventType == AiEngineEvents.Finalization.Started &&
                entry.Outcome == AiDecisionLedgerOutcome.Started);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Finalization &&
                entry.EventType == AiEngineEvents.Finalization.RaceLost &&
                entry.Outcome == AiDecisionLedgerOutcome.Denied);

            Assert.DoesNotContain(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Finalization &&
                entry.EventType == AiEngineEvents.Finalization.Failed);

            Assert.DoesNotContain(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Finalization &&
                entry.EventType == AiEngineEvents.Finalization.Completed);

            Assert.DoesNotContain(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Execution &&
                entry.EventType == AiEngineEvents.Execution.Finalized);
        }

        private static AiDagExecutionFinalizationService CreateService(
            IAiDagExecutionEngineServices services)
        {
            var retentionCoordinator = new AiDagRetentionCoordinator(services);

            return new AiDagExecutionFinalizationService(
                services,
                retentionCoordinator);
        }

        private static IAiDagExecutionEngineServices CreateServices(
            InMemoryAiDecisionLedger ledger,
            string runtimeInstanceId,
            bool shouldFinalize,
            bool shouldCancel = false)
        {
            var services = Substitute.For<IAiDagExecutionEngineServices>();

            var dagStore = Substitute.For<IAiDagExecutionStore>();
            var observability = Substitute.For<IAiRuntimeObservability>();
            var logger = Substitute.For<IAiRuntimeLogger>();
            var runtimeIdentity = Substitute.For<IAiRuntimeInstanceIdentityDescriptor>();
            var controlGate = Substitute.For<IAiExecutionControlGate>();
            var policyEngineFactory = Substitute.For<IAiPolicyEngineFactory>();
            var retentionPolicyEngine = Substitute.For<IAiPolicyEngine, IAiRetentionEngine>();
            var retentionEngine = (IAiRetentionEngine)retentionPolicyEngine;
            var stepResolver = Substitute.For<IAiExecutionStepResolver>();

            var retentionResult = AiRetentionApplyResult.Empty(
                AiRetentionDecision.None("No-op retention for finalization ledger test."));

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

            runtimeIdentity.RuntimeInstanceId.Returns(runtimeInstanceId);

            observability.Tracer.Returns(new PassthroughAiRuntimeTracer());
            observability.Ledger.Returns(recorder);
            observability.Metrics.Returns(MetricsFactory.Create());

            controlGate.CheckBeforeAdvanceAsync(
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>())
                .Returns(new AiExecutionControlDecision
                {
                    CanContinue = !shouldCancel,
                    ShouldCancel = shouldCancel,
                    Status = shouldCancel
                        ? AiExecutionControlStatus.Cancelling
                        : AiExecutionControlStatus.Running,
                    Reason = shouldCancel
                        ? "Cancellation requested by test."
                        : null
                });

            dagStore.TryFinalizeExecutionAsync(
                    Arg.Any<AiDagExecutionFinalizationRequest>(),
                    Arg.Any<CancellationToken>())
                .Returns(shouldFinalize);

            dagStore.SaveStateAsync(
                    Arg.Any<string>(),
                    Arg.Any<AiExecutionState>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            stepResolver.WarmStepsAsync(
                    Arg.Any<string>(),
                    Arg.Any<AiExecutionState>(),
                    Arg.Any<IReadOnlyCollection<string>>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            policyEngineFactory
                .Create(
                    AiPolicyKind.Retention,
                    Arg.Any<AiStepExecutionContext>())
                .Returns((IAiPolicyEngine)retentionPolicyEngine);

            policyEngineFactory
                .Create<IAiRetentionEngine>(
                    AiPolicyKind.Retention,
                    Arg.Any<AiStepExecutionContext>())
                .Returns(retentionEngine);

            retentionEngine.ApplyAtomicAsync(
                    Arg.Any<AiRetentionContext>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(retentionResult));

            services.DagStore.Returns(dagStore);
            services.ObservabilityService.Returns(observability);
            services.Logger.Returns(logger);
            services.RuntimeInstanceIdentity.Returns(runtimeIdentity);
            services.ExecutionControlGate.Returns(controlGate);
            services.PolicyEngineFactory.Returns(policyEngineFactory);
            services.StepResolver.Returns(stepResolver);

            return services;
        }

        private static AiExecutionRecord CreateRecord(
            string executionId,
            AiExecutionStatus status)
        {
            return new AiExecutionRecord
            {
                ExecutionId = executionId,
                PipelineName = "test-pipeline",
                ExecutionMode = AiExecutionMode.Dag,
                Status = status,
                ExecutionStepKey = "current-step-key",
                CurrentStep = string.Empty,
                CurrentStepIndex = 0
            };
        }

        private static AiExecutionState CreateState(
            string executionId)
        {
            return new AiExecutionState
            {
                ExecutionId = executionId,
                PipelineName = "test-pipeline",
                Steps =
                {
                    ["step-a"] = new AiStepState
                    {
                        StepName = "step-a",
                        Status = AiStepExecutionStatus.Completed
                    }
                }
            };
        }

        private static ResolvedAiPipeline CreatePipeline()
        {
            return new ResolvedAiPipeline
            {
                Name = "test-pipeline",
                Version = "v1",
                ExecutionMode = AiExecutionMode.Dag,
                Steps =
                [
                    new ResolvedAiPipelineStep
                    {
                        Name = "step-a",
                        StepKey = "debug.pass",
                        Step = Substitute.For<IAiStep>(),
                        Config = new Dictionary<string, object?>()
                    }
                ]
            };
        }

        private static AiExecutionContext BuildExecutionContext(
            AiExecutionRecord record,
            AiExecutionState state,
            CancellationToken cancellationToken)
        {
            return new AiExecutionContext(
                record,
                state,
                Substitute.For<IServiceProvider>(),
                Substitute.For<IAiExecutionStateReader>(),
                Substitute.For<IAiExecutionStateWriter>(),
                cancellationToken);
        }

        private static Task PersistAsync(
            AiExecutionRecord record,
            string expectedStepKey,
            AiExecutionState state,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
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