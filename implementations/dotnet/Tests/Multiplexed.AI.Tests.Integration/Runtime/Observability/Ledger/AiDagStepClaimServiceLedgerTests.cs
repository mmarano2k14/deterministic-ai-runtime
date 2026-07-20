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

namespace Multiplexed.AI.Tests.Integration.Runtime.Observability.Ledger
{
    /// <summary>
    /// Tests decision ledger recording around DAG step claiming and concurrency admission.
    /// </summary>
    public sealed class AiDagStepClaimServiceLedgerTests
    {
        /// <summary>
        /// Verifies that a denied concurrency admission records a concurrency denied ledger event.
        /// </summary>
        [Fact]
        public async Task ClaimNextAsync_WhenConcurrencyGateDenies_ShouldRecordConcurrencyDenied()
        {
            // Arrange
            var executionId = "exec-ledger-throttled";
            var pipelineKey = "test-pipeline:v1";
            var workerId = "worker-1";
            var stepName = "step-a";

            var dagStore = Substitute.For<IAiDagExecutionStore>();
            var concurrencyGate = Substitute.For<IAiConcurrencyGate>();
            var ledger = new InMemoryAiDecisionLedger();

            var stepConfig = new Dictionary<string, object?>
            {
                ["concurrency"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["maxGlobalConcurrency"] = 1
                }
            };

            var pipeline = CreatePipeline(
                stepName,
                stepConfig);

            var state = CreateReadyState(
                executionId,
                stepName,
                stepConfig);

            ConfigureReadyDagStore(
                dagStore,
                executionId,
                state,
                stepName);

            concurrencyGate.TryAcquireAsync(
                    Arg.Any<AiConcurrencyContext>(),
                    Arg.Any<AiConcurrencyDefinition>(),
                    Arg.Any<CancellationToken>())
                .Returns(AiConcurrencyDecision.Deny(
                    "Concurrency limit reached for scope 'ai:concurrency:scope:global'. Current='1', Limit='1'.",
                    TimeSpan.FromMilliseconds(100)));

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

            Assert.NotEmpty(entries);

            var entry = Assert.Single(entries, entry =>
                    entry.Category == AiDecisionLedgerCategory.Concurrency &&
                    entry.EventType == AiDecisionLedgerEvents.Concurrency.Denied &&
                    entry.Outcome == AiDecisionLedgerOutcome.Denied);

            Assert.Equal(AiDecisionLedgerCategory.Concurrency, entry.Category);
            Assert.Equal(AiDecisionLedgerEvents.Concurrency.Denied, entry.EventType);
            Assert.Equal(AiDecisionLedgerOutcome.Denied, entry.Outcome);

            Assert.Equal(executionId, entry.CorrelationContext.ExecutionId);
            Assert.Equal(pipelineKey, entry.CorrelationContext.PipelineName);
            Assert.Equal(stepName, entry.CorrelationContext.StepId);
            Assert.Equal(stepName, entry.CorrelationContext.StepKey);
            Assert.Equal(workerId, entry.CorrelationContext.WorkerId);
            Assert.Equal(workerId, entry.CorrelationContext.RuntimeInstanceId);
            Assert.Contains("Concurrency limit reached", entry.Reason);

            Assert.Contains(
                entries,
                entry =>
                    entry.Category == AiDecisionLedgerCategory.Claim &&
                    entry.EventType == AiDecisionLedgerEvents.Claim.Attempted);

            Assert.Contains(
                entries,
                entry =>
                    entry.Category == AiDecisionLedgerCategory.Claim &&
                    entry.EventType == AiDecisionLedgerEvents.Claim.Denied);

            _ = dagStore.DidNotReceive().TryClaimStepAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
        }

        /// <summary>
        /// Verifies that a successful DAG step claim records a claim acquired ledger event.
        /// </summary>
        [Fact]
        public async Task ClaimNextAsync_WhenStepClaimIsAcquired_ShouldRecordClaimAcquired()
        {
            // Arrange
            var executionId = "exec-ledger-claim-acquired";
            var pipelineKey = "test-pipeline:v1";
            var workerId = "worker-1";
            var stepName = "llm-summary";
            var claimToken = "claim-token-1";

            var dagStore = Substitute.For<IAiDagExecutionStore>();
            var concurrencyGate = Substitute.For<IAiConcurrencyGate>();
            var ledger = new InMemoryAiDecisionLedger();

            var stepConfig = new Dictionary<string, object?>
            {
                ["provider"] = "openai",
                ["model"] = "gpt-4.1",
                ["operation"] = "llm.chat",
                ["concurrency"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["maxProviderConcurrency"] = 10
                }
            };

            var pipeline = CreatePipeline(
                stepName,
                stepConfig);

            var state = CreateReadyState(
                executionId,
                stepName,
                stepConfig);

            ConfigureReadyDagStore(
                dagStore,
                executionId,
                state,
                stepName);

            concurrencyGate.TryAcquireAsync(
                    Arg.Any<AiConcurrencyContext>(),
                    Arg.Any<AiConcurrencyDefinition>(),
                    Arg.Any<CancellationToken>())
                .Returns(AiConcurrencyDecision.Allow());

            dagStore.TryClaimStepAsync(
                    executionId,
                    stepName,
                    workerId,
                    Arg.Any<CancellationToken>())
                .Returns(new AiClaimedStep
                {
                    ExecutionId = executionId,
                    StepName = stepName,
                    ClaimToken = claimToken
                });

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
            Assert.NotNull(claimed);

            var entries = await ledger.GetByExecutionAsync(executionId);

            Assert.NotEmpty(entries);

            var entry = Assert.Single(entries, entry =>
                    entry.Category == AiDecisionLedgerCategory.Claim &&
                    entry.EventType == AiDecisionLedgerEvents.Claim.Acquired &&
                    entry.Outcome == AiDecisionLedgerOutcome.Allowed &&
                    entry.CorrelationContext.ClaimToken == claimToken);

            Assert.Equal(AiDecisionLedgerCategory.Claim, entry.Category);
            Assert.Equal(AiDecisionLedgerEvents.Claim.Acquired, entry.EventType);
            Assert.Equal(AiDecisionLedgerOutcome.Allowed, entry.Outcome);

            Assert.Equal(executionId, entry.CorrelationContext.ExecutionId);
            Assert.Equal(pipelineKey, entry.CorrelationContext.PipelineName);
            Assert.Equal(stepName, entry.CorrelationContext.StepId);
            Assert.Equal(stepName, entry.CorrelationContext.StepKey);
            Assert.Equal(workerId, entry.CorrelationContext.WorkerId);
            Assert.Equal(workerId, entry.CorrelationContext.RuntimeInstanceId);
            Assert.Equal(claimToken, entry.CorrelationContext.ClaimToken);
            Assert.Equal("openai", entry.CorrelationContext.Provider);
            Assert.Equal("gpt-4.1", entry.CorrelationContext.Model);
            Assert.Equal("llm.chat", entry.CorrelationContext.Operation);

            Assert.Contains(
                entries,
                entry =>
                    entry.Category == AiDecisionLedgerCategory.Claim &&
                    entry.EventType == AiDecisionLedgerEvents.Claim.Attempted);

            Assert.Contains(
                entries,
                entry =>
                    entry.Category == AiDecisionLedgerCategory.Concurrency &&
                    entry.EventType == AiDecisionLedgerEvents.Concurrency.LeaseAcquired);
        }

        /// <summary>
        /// Verifies that losing the DAG claim race after acquiring concurrency capacity records
        /// claim denied and concurrency lease released ledger events.
        /// </summary>
        [Fact]
        public async Task ClaimNextAsync_WhenClaimFailsAfterConcurrencyLease_ShouldRecordClaimDeniedAndLeaseReleased()
        {
            // Arrange
            var executionId = "exec-ledger-claim-race";
            var pipelineKey = "test-pipeline:v1";
            var workerId = "worker-1";
            var stepName = "step-a";
            var leaseId = $"{executionId}:{stepName}:{workerId}";

            var dagStore = Substitute.For<IAiDagExecutionStore>();
            var concurrencyGate = Substitute.For<IAiConcurrencyGate>();
            var ledger = new InMemoryAiDecisionLedger();

            var stepConfig = new Dictionary<string, object?>
            {
                ["concurrency"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["maxGlobalConcurrency"] = 1
                }
            };

            var pipeline = CreatePipeline(
                stepName,
                stepConfig);

            var state = CreateReadyState(
                executionId,
                stepName,
                stepConfig);

            ConfigureReadyDagStore(
                dagStore,
                executionId,
                state,
                stepName);

            concurrencyGate.TryAcquireAsync(
                    Arg.Any<AiConcurrencyContext>(),
                    Arg.Any<AiConcurrencyDefinition>(),
                    Arg.Any<CancellationToken>())
                .Returns(AiConcurrencyDecision.Allow());

            dagStore.TryClaimStepAsync(
                    executionId,
                    stepName,
                    workerId,
                    Arg.Any<CancellationToken>())
                .Returns((AiClaimedStep?)null);

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

            Assert.NotEmpty(entries);

            var claimDenied = Assert.Single(entries, entry =>
                    entry.Category == AiDecisionLedgerCategory.Claim &&
                    entry.EventType == AiDecisionLedgerEvents.Claim.Denied &&
                    entry.Outcome == AiDecisionLedgerOutcome.Denied &&
                    entry.CorrelationContext.ClaimToken == leaseId);

            Assert.Equal(executionId, claimDenied.CorrelationContext.ExecutionId);
            Assert.Equal(pipelineKey, claimDenied.CorrelationContext.PipelineName);
            Assert.Equal(stepName, claimDenied.CorrelationContext.StepId);
            Assert.Equal(workerId, claimDenied.CorrelationContext.WorkerId);
            Assert.Equal(leaseId, claimDenied.CorrelationContext.ClaimToken);
            Assert.Contains("failed after concurrency lease", claimDenied.Reason);

            var leaseReleased = Assert.Single(entries, entry =>
                    entry.Category == AiDecisionLedgerCategory.Concurrency &&
                    entry.EventType == AiDecisionLedgerEvents.Concurrency.LeaseReleased &&
                    entry.Outcome == AiDecisionLedgerOutcome.Released);

            Assert.Equal(executionId, leaseReleased.CorrelationContext.ExecutionId);
            Assert.Equal(pipelineKey, leaseReleased.CorrelationContext.PipelineName);
            Assert.Equal(stepName, leaseReleased.CorrelationContext.StepId);
            Assert.Equal(workerId, leaseReleased.CorrelationContext.WorkerId);
            Assert.Equal(leaseId, leaseReleased.CorrelationContext.ClaimToken);
            Assert.Contains(
                "Concurrency lease released",
                leaseReleased.Reason);

            Assert.True(
                leaseReleased.Reason?.Contains(
                    "step claim",
                    StringComparison.OrdinalIgnoreCase) == true,
                $"Expected the lease-release reason to describe the failed step claim. Actual='{leaseReleased.Reason}'.");

            await concurrencyGate.Received(1).ReleaseAsync(
                Arg.Is<AiConcurrencyContext>(context =>
                    context.ExecutionId == executionId &&
                    context.PipelineKey == pipelineKey &&
                    context.StepId == stepName &&
                    context.StepKey == stepName &&
                    context.RuntimeInstanceId == workerId &&
                    context.LeaseId == leaseId),
                Arg.Any<AiConcurrencyDefinition>(),
                Arg.Any<CancellationToken>());
        }

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

        private static ResolvedAiPipeline CreatePipeline(
            string stepName,
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
                        StepKey = stepName,
                        Config = stepConfig
                    }
                ]
            };
        }

        private static AiExecutionState CreateReadyState(
            string executionId,
            string stepName,
            IReadOnlyDictionary<string, object?> stepConfig)
        {
            return new AiExecutionState
            {
                ExecutionId = executionId,
                PipelineName = "test-pipeline",
                Steps =
        {
            [stepName] = new AiStepState
            {
                StepName = stepName,
                Status = AiStepExecutionStatus.Ready,
                Config = new Dictionary<string, object?>(stepConfig)
            }
        }
            };
        }

        private static void ConfigureReadyDagStore(
            IAiDagExecutionStore dagStore,
            string executionId,
            AiExecutionState state,
            string stepName)
        {
            dagStore.RecoverTimedOutStepsAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(0);

            dagStore.GetStateAsync(
                    executionId,
                    Arg.Any<CancellationToken>())
                .Returns(state);

            dagStore.GetReadyStepsAsync(
                    executionId,
                    Arg.Any<int>(),
                    Arg.Any<CancellationToken>())
                .Returns(new[]
                {
                    new AiClaimedStep
                    {
                        ExecutionId = executionId,
                        StepName = stepName,
                        ClaimToken = "ready-token",
                    }
                });
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