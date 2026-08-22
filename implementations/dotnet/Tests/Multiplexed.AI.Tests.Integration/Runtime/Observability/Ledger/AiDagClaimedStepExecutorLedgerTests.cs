using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.Concurrency;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Payloads;
using Multiplexed.Abstractions.AI.Execution.Scheduling;
using Multiplexed.Abstractions.AI.Execution.State;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Observability.Ledger;
using Multiplexed.AI.Runtime.Execution.Context;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Engine.Steps;
using Multiplexed.AI.Runtime.Execution.Instance;
using Multiplexed.AI.Runtime.Observability.Context;
using Multiplexed.AI.Runtime.Observability.Logging;
using NSubstitute;
using Xunit;
using Multiplexed.Abstractions.AI.Observability.Events;

namespace Multiplexed.AI.Tests.Integration.Runtime.Observability.Ledger
{
    /// <summary>
    /// Tests decision ledger recording around claimed DAG step execution.
    /// </summary>
    public sealed class AiDagClaimedStepExecutorLedgerTests
    {
        /// <summary>
        /// Verifies that successful claimed-step execution records step start and completion.
        /// Concurrency lease release remains owned by the calling batch or distributed runner.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_WhenStepSucceeds_ShouldRecordStepStartedAndCompletedWithoutReleasingLease()
        {
            // Arrange
            var executionId = "exec-step-ledger-success";
            var pipelineName = "test-pipeline";
            var pipelineVersion = "v1";
            var pipelineKey = $"{pipelineName}:{pipelineVersion}";
            var stepName = "step-a";
            var stepKey = "debug.pass";
            var runtimeInstanceId = "runtime-1";
            var claimToken = "claim-token-1";

            var ledger = new InMemoryAiDecisionLedger();

            var services = CreateServices(
                ledger,
                runtimeInstanceId);

            var step = Substitute.For<IAiStep>();

            step.ExecuteAsync(
                    Arg.Any<AiStepExecutionContext>(),
                    Arg.Any<CancellationToken>())
                .Returns(CreateSuccessResult());

            var record = new AiExecutionRecord
            {
                ExecutionId = executionId,
                PipelineName = pipelineName,
                ExecutionMode = AiExecutionMode.Dag
            };

            var state = new AiExecutionState
            {
                ExecutionId = executionId,
                PipelineName = pipelineName,
                Steps =
                {
                    [stepName] = new AiStepState
                    {
                        StepName = stepName,
                        Status = AiStepExecutionStatus.Running,
                        Config = new Dictionary<string, object?>
                        {
                            ["provider"] = "openai",
                            ["model"] = "gpt-4.1",
                            ["operation"] = "llm.chat"
                        }
                    }
                }
            };

            var resolvedPipeline = new ResolvedAiPipeline
            {
                Name = pipelineName,
                Version = pipelineVersion,
                ExecutionMode = AiExecutionMode.Dag,
                Steps =
                [
                    new ResolvedAiPipelineStep
                    {
                        Name = stepName,
                        StepKey = stepKey,
                        Step = step,
                        Config = new Dictionary<string, object?>()
                    }
                ]
            };

            var claimedStep = new AiClaimedStep
            {
                ExecutionId = executionId,
                StepName = stepName,
                ClaimToken = claimToken
            };

            var executor = new AiDagClaimedStepExecutor(services);

            // Act
            var result = await executor.ExecuteAsync(
                record,
                state,
                resolvedPipeline,
                claimedStep,
                BuildExecutionContext);

            // Assert
            Assert.True(result.Success);

            var entries = await ledger.GetByExecutionAsync(executionId);

            Assert.Equal(2, entries.Count);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Step &&
                entry.EventType == AiEngineEvents.Step.Started &&
                entry.Outcome == AiDecisionLedgerOutcome.Started);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Step &&
                entry.EventType == AiEngineEvents.Step.Completed &&
                entry.Outcome == AiDecisionLedgerOutcome.Completed);

            Assert.DoesNotContain(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Concurrency &&
                entry.EventType == AiEngineEvents.Concurrency.LeaseReleased);

            foreach (var entry in entries)
            {
                Assert.Equal(executionId, entry.CorrelationContext.ExecutionId);
                Assert.Equal(pipelineKey, entry.CorrelationContext.PipelineName);
                Assert.Equal(stepName, entry.CorrelationContext.StepId);
                Assert.Equal(stepKey, entry.CorrelationContext.StepKey);
                Assert.Equal(runtimeInstanceId, entry.CorrelationContext.RuntimeInstanceId);
                Assert.Equal(claimToken, entry.CorrelationContext.ClaimToken);
                Assert.Equal("openai", entry.CorrelationContext.Provider);
                Assert.Equal("gpt-4.1", entry.CorrelationContext.Model);
                Assert.Equal("llm.chat", entry.CorrelationContext.Operation);
            }

            _ = services.ConcurrencyGate.DidNotReceive().ReleaseAsync(
                Arg.Any<AiConcurrencyContext>(),
                Arg.Any<AiConcurrencyDefinition>(),
                Arg.Any<CancellationToken>());
        }

        /// <summary>
        /// Verifies that a step exception converted into a failed result records
        /// step start and step failure without releasing caller-owned concurrency capacity.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_WhenStepThrows_ShouldRecordStepStartedAndFailedWithoutReleasingLease()
        {
            // Arrange
            var executionId = "exec-step-ledger-failure";
            var pipelineName = "test-pipeline";
            var pipelineVersion = "v1";
            var stepName = "step-a";
            var stepKey = "debug.fail";
            var runtimeInstanceId = "runtime-1";
            var claimToken = "claim-token-1";

            var ledger = new InMemoryAiDecisionLedger();

            var services = CreateServices(
                ledger,
                runtimeInstanceId);

            var step = Substitute.For<IAiStep>();

            step.ExecuteAsync(
                    Arg.Any<AiStepExecutionContext>(),
                    Arg.Any<CancellationToken>())
                .Returns<Task<AiStepResult>>(_ => throw new InvalidOperationException("Boom."));

            var record = new AiExecutionRecord
            {
                ExecutionId = executionId,
                PipelineName = pipelineName,
                ExecutionMode = AiExecutionMode.Dag
            };

            var state = new AiExecutionState
            {
                ExecutionId = executionId,
                PipelineName = pipelineName,
                Steps =
                {
                    [stepName] = new AiStepState
                    {
                        StepName = stepName,
                        Status = AiStepExecutionStatus.Running
                    }
                }
            };

            var resolvedPipeline = new ResolvedAiPipeline
            {
                Name = pipelineName,
                Version = pipelineVersion,
                ExecutionMode = AiExecutionMode.Dag,
                Steps =
                [
                    new ResolvedAiPipelineStep
                    {
                        Name = stepName,
                        StepKey = stepKey,
                        Step = step,
                        Config = new Dictionary<string, object?>()
                    }
                ]
            };

            var claimedStep = new AiClaimedStep
            {
                ExecutionId = executionId,
                StepName = stepName,
                ClaimToken = claimToken
            };

            var executor = new AiDagClaimedStepExecutor(services);

            // Act
            var result = await executor.ExecuteAsync(
                record,
                state,
                resolvedPipeline,
                claimedStep,
                BuildExecutionContext);

            // Assert
            Assert.False(result.Success);

            var entries = await ledger.GetByExecutionAsync(executionId);

            Assert.Equal(2, entries.Count);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Step &&
                entry.EventType == AiEngineEvents.Step.Started &&
                entry.Outcome == AiDecisionLedgerOutcome.Started);

            var failed = Assert.Single(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Step &&
                entry.EventType == AiEngineEvents.Step.Failed &&
                entry.Outcome == AiDecisionLedgerOutcome.Failed);

            Assert.Equal("Boom.", failed.Reason);
            Assert.NotNull(failed.Metadata);
            Assert.Equal("InvalidOperationException", failed.Metadata!["exception.type"]);

            Assert.DoesNotContain(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Concurrency &&
                entry.EventType == AiEngineEvents.Concurrency.LeaseReleased);

            _ = services.ConcurrencyGate.DidNotReceive().ReleaseAsync(
                Arg.Any<AiConcurrencyContext>(),
                Arg.Any<AiConcurrencyDefinition>(),
                Arg.Any<CancellationToken>());
        }

        private static IAiDagExecutionEngineServices CreateServices(
            InMemoryAiDecisionLedger ledger,
            string runtimeInstanceId)
        {
            var services = Substitute.For<IAiDagExecutionEngineServices>();

            var observability = Substitute.For<IAiRuntimeObservability>();
            var logger = Substitute.For<IAiRuntimeLogger>();
            var runtimeInstanceIdentity = Substitute.For<IAiRuntimeInstanceIdentityDescriptor>();
            var concurrencyGate = Substitute.For<IAiConcurrencyGate>();
            var payloadCompactor = Substitute.For<IAiStepResultPayloadCompactor>();



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

            runtimeInstanceIdentity.RuntimeInstanceId.Returns(runtimeInstanceId);

            observability.Tracer.Returns(new PassthroughAiRuntimeTracer());
            observability.Ledger.Returns(recorder);

            services.ObservabilityService.Returns(observability);
            services.Logger.Returns(logger);
            services.RuntimeInstanceIdentity.Returns(runtimeInstanceIdentity);
            services.ConcurrencyGate.Returns(concurrencyGate);
            services.PayloadCompactor.Returns(payloadCompactor);

            return services;
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

        private static AiStepResult CreateSuccessResult()
        {
            return new AiStepResult
            {
                Success = true
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