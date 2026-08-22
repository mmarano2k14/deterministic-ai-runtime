using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.Concurrency;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Scheduling;
using Multiplexed.Abstractions.AI.Execution.State;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Observability.Ledger;
using Multiplexed.AI.Runtime.AI.Concurrency;
using Multiplexed.AI.Runtime.AI.Policies;
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
    /// Tests decision ledger recording around DAG step timeout recovery.
    /// </summary>
    public sealed class AiDagRecoveryLedgerTests2
    {
        /// <summary>
        /// Verifies that single-step claiming records recovery ledger events with recovered step names.
        /// </summary>
        [Fact]
        public async Task ClaimNextAsync_WhenTimedOutStepsAreRecovered_ShouldRecordRecoveryLedgerEventsWithStepNames()
        {
            // Arrange
            var executionId = "exec-recovery-ledger";
            var pipelineKey = "test-pipeline:v1";
            var workerId = "worker-1";
            var stepName = "step-a";
            var stepKey = "debug.pass";

            var dagStore = Substitute.For<IAiDagExecutionStore>();
            var concurrencyGate = Substitute.For<IAiConcurrencyGate>();
            var ledger = new InMemoryAiDecisionLedger();

            var stepConfig = new Dictionary<string, object?>();

            var pipeline = CreatePipeline(
                stepName,
                stepKey,
                stepConfig);

            var beforeState = CreateRecoveryState(
                executionId,
                stepName,
                stepConfig,
                AiStepExecutionStatus.Running,
                recoveryCount: 0);

            var afterState = CreateRecoveryState(
                executionId,
                stepName,
                stepConfig,
                AiStepExecutionStatus.Ready,
                recoveryCount: 1);

            ConfigureRecoveryDagStore(
                dagStore,
                executionId,
                beforeState,
                afterState);

            var services = CreateEngineServices(
                dagStore,
                concurrencyGate,
                ledger);

            var service = new AiDagStepClaimService(services);

            // Act
            var claimed = await service.ClaimNextAsync(
                executionId,
                pipeline,
                pipelineKey,
                workerId);

            // Assert
            Assert.Null(claimed);

            var entries = await ledger.GetByExecutionAsync(executionId);

            AssertRecoveryLedgerEvents(
                entries,
                executionId,
                pipelineKey,
                workerId,
                stepName,
                stepKey);
        }

        /// <summary>
        /// Verifies that batch claiming records recovery ledger events with recovered step names.
        /// </summary>
        [Fact]
        public async Task ClaimBatchAsync_WhenTimedOutStepsAreRecovered_ShouldRecordRecoveryLedgerEventsWithStepNames()
        {
            // Arrange
            var executionId = "exec-batch-recovery-ledger";
            var pipelineKey = "test-pipeline:v1";
            var workerId = "worker-1";
            var stepName = "step-a";
            var stepKey = "debug.pass";

            var dagStore = Substitute.For<IAiDagExecutionStore>();
            var concurrencyGate = Substitute.For<IAiConcurrencyGate>();
            var ledger = new InMemoryAiDecisionLedger();

            var stepConfig = new Dictionary<string, object?>();

            var pipeline = CreatePipeline(
                stepName,
                stepKey,
                stepConfig);

            var beforeState = CreateRecoveryState(
                executionId,
                stepName,
                stepConfig,
                AiStepExecutionStatus.Running,
                recoveryCount: 0);

            var afterState = CreateRecoveryState(
                executionId,
                stepName,
                stepConfig,
                AiStepExecutionStatus.Ready,
                recoveryCount: 1);

            ConfigureRecoveryDagStore(
                dagStore,
                executionId,
                beforeState,
                afterState);

            var services = CreateEngineServices(
                dagStore,
                concurrencyGate,
                ledger);

            var service = new AiDagStepClaimService(services);

            // Act
            var claimed = await service.ClaimBatchAsync(
                executionId,
                pipeline,
                pipelineKey,
                workerId,
                maxSteps: 4);

            // Assert
            Assert.Empty(claimed);

            var entries = await ledger.GetByExecutionAsync(executionId);

            AssertRecoveryLedgerEvents(
                entries,
                executionId,
                pipelineKey,
                workerId,
                stepName,
                stepKey);
        }

        /// <summary>
        /// Asserts that recovery ledger events were recorded with the expected DAG step name
        /// and executable step key.
        /// </summary>
        private static void AssertRecoveryLedgerEvents(
            IReadOnlyCollection<AiDecisionLedgerEntry> entries,
            string executionId,
            string pipelineKey,
            string workerId,
            string stepName,
            string stepKey)
        {
            Assert.NotEmpty(entries);

            Assert.Contains(
                entries,
                entry =>
                    entry.Category == AiDecisionLedgerCategory.Claim &&
                    entry.EventType == AiEngineEvents.Claim.Attempted &&
                    entry.Outcome == AiDecisionLedgerOutcome.Started);

            var recoveryDetected = Assert.Single(entries, entry =>
                    entry.Category == AiDecisionLedgerCategory.Recovery &&
                    entry.EventType == AiEngineEvents.Recovery.Detected);

            Assert.Equal(executionId, recoveryDetected.CorrelationContext.ExecutionId);
            Assert.Equal(pipelineKey, recoveryDetected.CorrelationContext.PipelineName);
            Assert.Equal(workerId, recoveryDetected.CorrelationContext.WorkerId);
            Assert.Equal(workerId, recoveryDetected.CorrelationContext.RuntimeInstanceId);
            Assert.Equal("1", recoveryDetected?.Metadata["recovered.count"] );
            Assert.Equal(stepName, recoveryDetected?.Metadata["recovered.steps"]);

            var recoveryApplied = Assert.Single(entries, entry =>
                    entry.Category == AiDecisionLedgerCategory.Recovery &&
                    entry.EventType == AiEngineEvents.Recovery.Applied);

            Assert.Equal(AiDecisionLedgerOutcome.Applied, recoveryApplied.Outcome);
            Assert.Equal(executionId, recoveryApplied.CorrelationContext.ExecutionId);
            Assert.Equal(pipelineKey, recoveryApplied.CorrelationContext.PipelineName);
            Assert.Equal(workerId, recoveryApplied.CorrelationContext.WorkerId);
            Assert.Equal(workerId, recoveryApplied.CorrelationContext.RuntimeInstanceId);
            Assert.Equal("1", recoveryApplied.Metadata["recovered.count"]);
            Assert.Equal(stepName, recoveryApplied.Metadata["recovered.steps"]);

            var stepRecovered = Assert.Single(entries, entry =>
                    entry.Category == AiDecisionLedgerCategory.Recovery &&
                    entry.EventType == AiEngineEvents.Recovery.StepRecovered);

            Assert.Equal(AiDecisionLedgerOutcome.Applied, stepRecovered.Outcome);
            Assert.Equal(executionId, stepRecovered.CorrelationContext.ExecutionId);
            Assert.Equal(pipelineKey, stepRecovered.CorrelationContext.PipelineName);

            // StepId is the DAG step instance name.
            // StepKey is the executable step key.
            Assert.Equal(stepName, stepRecovered.CorrelationContext.StepId);
            Assert.Equal(stepKey, stepRecovered.CorrelationContext.StepKey);

            Assert.Equal(workerId, stepRecovered.CorrelationContext.WorkerId);
            Assert.Equal(workerId, stepRecovered.CorrelationContext.RuntimeInstanceId);
            Assert.Equal(stepName, stepRecovered.Metadata["step.name"]);
            Assert.Equal("1", stepRecovered.Metadata["recovered.count"]);

            Assert.Contains(
                entries,
                entry =>
                    entry.Category == AiDecisionLedgerCategory.Claim &&
                    entry.EventType == AiEngineEvents.Claim.Denied &&
                    entry.Outcome == AiDecisionLedgerOutcome.Denied);
        }

        /// <summary>
        /// Creates engine services for the recovery ledger tests.
        /// </summary>
        private static IAiDagExecutionEngineServices CreateEngineServices(
            IAiDagExecutionStore dagStore,
            IAiConcurrencyGate concurrencyGate,
            InMemoryAiDecisionLedger ledger,
            IAiPolicyEngineFactory? policyEngineFactory = null)
        {
            var services = Substitute.For<IAiDagExecutionEngineServices>();

            var logger = Substitute.For<IAiRuntimeLogger>();
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

            services.DagStore.Returns(dagStore);
            services.ConcurrencyGate.Returns(concurrencyGate);
            services.PolicyEngineFactory.Returns(
                policyEngineFactory ?? Substitute.For<IAiPolicyEngineFactory>());

            services.Services.Returns(Substitute.For<IServiceProvider>());
            services.StateReader.Returns(Substitute.For<IAiExecutionStateReader>());
            services.StateWriter.Returns(Substitute.For<IAiExecutionStateWriter>());

            services.Logger.Returns(logger);

            services.ObservabilityService.Returns(observability);
            observability.Tracer.Returns(new PassthroughAiRuntimeTracer());
            observability.Ledger.Returns(recorder);

            return services;
        }

        /// <summary>
        /// Creates a resolved pipeline with a distinct DAG step name and executable step key.
        /// </summary>
        private static ResolvedAiPipeline CreatePipeline(
            string stepName,
            string stepKey,
            IReadOnlyDictionary<string, object?> stepConfig)
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
                        Name = stepName,
                        StepKey = stepKey,
                        Config = stepConfig
                    }
                ]
            };
        }

        /// <summary>
        /// Creates a state used before or after timeout recovery.
        /// </summary>
        private static AiExecutionState CreateRecoveryState(
            string executionId,
            string stepName,
            IReadOnlyDictionary<string, object?> stepConfig,
            AiStepExecutionStatus status,
            int recoveryCount)
        {
            return new AiExecutionState
            {
                ExecutionId = executionId,
                PipelineName = "test-pipeline",
                Steps = new Dictionary<string, AiStepState>
                {
                    [stepName] = new AiStepState
                    {
                        StepName = stepName,
                        Status = status,
                        RecoveryCount = recoveryCount,
                        Config = new Dictionary<string, object?>(stepConfig)
                    }
                }
            };
        }

        /// <summary>
        /// Configures the DAG store to simulate a timeout recovery and no ready step claim afterwards.
        /// </summary>
        private static void ConfigureRecoveryDagStore(
            IAiDagExecutionStore dagStore,
            string executionId,
            AiExecutionState beforeState,
            AiExecutionState afterState)
        {
            dagStore.GetStateAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(
                    beforeState,
                    afterState,
                    afterState,
                    afterState);

            dagStore.RecoverTimedOutStepsAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(1);

            dagStore.GetReadyStepsAsync(
                    executionId,
                    Arg.Any<int>(),
                    Arg.Any<CancellationToken>())
                .Returns(Array.Empty<AiClaimedStep>());
        }

        /// <summary>
        /// Tracer used by tests to execute traced actions without side effects.
        /// </summary>
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