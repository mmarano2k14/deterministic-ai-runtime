using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.AI.Abstractions.AI.Retry;
using Multiplexed.AI.Observability.Ledger;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Engine.Helpers;
using Multiplexed.AI.Runtime.Execution.Instance;
using Multiplexed.AI.Runtime.Observability.Context;
using NSubstitute;
using Xunit;
using Multiplexed.Abstractions.AI.Observability.Events;

namespace Multiplexed.AI.Tests.Integration.Runtime.Observability.Ledger
{
    /// <summary>
    /// Tests retry decision ledger recording through the shared DAG execution helper.
    /// </summary>
    public sealed class AiDagRetryLedgerHelperTests
    {
        /// <summary>
        /// Verifies that a retry decision records evaluated and scheduled events
        /// when the reloaded step state is waiting for retry.
        /// </summary>
        [Fact]
        public async Task RecordRetryLedgerEventsAsync_WhenStepIsWaitingForRetry_ShouldRecordEvaluatedAndScheduled()
        {
            var executionId = "exec-retry-scheduled";
            var pipelineKey = "test-pipeline:v1";
            var stepName = "step-a";
            var workerId = "worker-1";
            var claimToken = "claim-token-1";

            var ledger = new InMemoryAiDecisionLedger();
            var services = CreateServices(ledger);

            var nextRetryAtUtc = DateTime.UtcNow.AddSeconds(30);

            var stepState = new AiStepState
            {
                StepName = stepName,
                Status = AiStepExecutionStatus.WaitingForRetry,
                Retry = new AiRetryPolicyDefinition
                {
                    MaxRetries = 3
                },
                RetryState = new AiStepRetryState
                {
                    RetryCount = 1,
                    RetryReason = "Transient failure.",
                    NextRetryAtUtc = nextRetryAtUtc
                }
            };

            await AiDagExecutionHelpers.RecordRetryLedgerEventsAsync(
                services,
                executionId,
                pipelineKey,
                stepName,
                stepName,
                workerId,
                claimToken,
                stepState,
                "temporary failure",
                "test",
                CancellationToken.None);

            var entries = await ledger.GetByExecutionAsync(executionId);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Retry &&
                entry.EventType == AiEngineEvents.Retry.Evaluated &&
                entry.Outcome == AiDecisionLedgerOutcome.Started);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Retry &&
                entry.EventType == AiEngineEvents.Retry.Scheduled &&
                entry.Outcome == AiDecisionLedgerOutcome.Applied);

            Assert.DoesNotContain(entries, entry =>
                entry.EventType == AiEngineEvents.Retry.Denied);

            Assert.DoesNotContain(entries, entry =>
                entry.EventType == AiEngineEvents.Retry.BudgetExhausted);
        }

        /// <summary>
        /// Verifies that a retry decision records evaluated and budget exhausted events
        /// when the step is failed and the retry count reached the configured maximum.
        /// </summary>
        [Fact]
        public async Task RecordRetryLedgerEventsAsync_WhenRetryBudgetIsExhausted_ShouldRecordEvaluatedAndBudgetExhausted()
        {
            var executionId = "exec-retry-budget-exhausted";
            var pipelineKey = "test-pipeline:v1";
            var stepName = "step-a";
            var workerId = "worker-1";
            var claimToken = "claim-token-1";

            var ledger = new InMemoryAiDecisionLedger();
            var services = CreateServices(ledger);

            var stepState = new AiStepState
            {
                StepName = stepName,
                Status = AiStepExecutionStatus.Failed,
                Retry = new AiRetryPolicyDefinition
                {
                    MaxRetries = 3
                },
                RetryState = new AiStepRetryState
                {
                    RetryCount = 3,
                    RetryReason = "Retry budget exhausted."
                }
            };

            await AiDagExecutionHelpers.RecordRetryLedgerEventsAsync(
                services,
                executionId,
                pipelineKey,
                stepName,
                stepName,
                workerId,
                claimToken,
                stepState,
                "permanent failure",
                "test",
                CancellationToken.None);

            var entries = await ledger.GetByExecutionAsync(executionId);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Retry &&
                entry.EventType == AiEngineEvents.Retry.Evaluated &&
                entry.Outcome == AiDecisionLedgerOutcome.Started);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Retry &&
                entry.EventType == AiEngineEvents.Retry.BudgetExhausted &&
                entry.Outcome == AiDecisionLedgerOutcome.Denied);

            Assert.DoesNotContain(entries, entry =>
                entry.EventType == AiEngineEvents.Retry.Scheduled);
        }

        /// <summary>
        /// Verifies that a retry decision records evaluated and denied events
        /// when the step failed but the retry budget was not the reason.
        /// </summary>
        [Fact]
        public async Task RecordRetryLedgerEventsAsync_WhenRetryIsDenied_ShouldRecordEvaluatedAndDenied()
        {
            var executionId = "exec-retry-denied";
            var pipelineKey = "test-pipeline:v1";
            var stepName = "step-a";
            var workerId = "worker-1";
            var claimToken = "claim-token-1";

            var ledger = new InMemoryAiDecisionLedger();
            var services = CreateServices(ledger);

            var stepState = new AiStepState
            {
                StepName = stepName,
                Status = AiStepExecutionStatus.Failed,
                Retry = new AiRetryPolicyDefinition
                {
                    MaxRetries = 3
                },
                RetryState = new AiStepRetryState
                {
                    RetryCount = 1,
                    RetryReason = "Failure is not retryable."
                }
            };

            await AiDagExecutionHelpers.RecordRetryLedgerEventsAsync(
                services,
                executionId,
                pipelineKey,
                stepName,
                stepName,
                workerId,
                claimToken,
                stepState,
                "validation failure",
                "test",
                CancellationToken.None);

            var entries = await ledger.GetByExecutionAsync(executionId);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Retry &&
                entry.EventType == AiEngineEvents.Retry.Evaluated &&
                entry.Outcome == AiDecisionLedgerOutcome.Started);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Retry &&
                entry.EventType == AiEngineEvents.Retry.Denied &&
                entry.Outcome == AiDecisionLedgerOutcome.Denied);

            Assert.DoesNotContain(entries, entry =>
                entry.EventType == AiEngineEvents.Retry.Scheduled);

            Assert.DoesNotContain(entries, entry =>
                entry.EventType == AiEngineEvents.Retry.BudgetExhausted);
        }

        /// <summary>
        /// Verifies that retry ledger recording handles a missing reloaded step state safely.
        /// </summary>
        [Fact]
        public async Task RecordRetryLedgerEventsAsync_WhenStepStateIsMissing_ShouldRecordEvaluatedAndDenied()
        {
            var executionId = "exec-retry-missing-step-state";
            var pipelineKey = "test-pipeline:v1";
            var stepName = "step-a";
            var workerId = "worker-1";
            var claimToken = "claim-token-1";

            var ledger = new InMemoryAiDecisionLedger();
            var services = CreateServices(ledger);

            await AiDagExecutionHelpers.RecordRetryLedgerEventsAsync(
                services,
                executionId,
                pipelineKey,
                stepName,
                stepName,
                workerId,
                claimToken,
                stepState: null,
                error: "failure without state",
                failureSource: "test",
                cancellationToken: CancellationToken.None);

            var entries = await ledger.GetByExecutionAsync(executionId);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Retry &&
                entry.EventType == AiEngineEvents.Retry.Evaluated &&
                entry.Outcome == AiDecisionLedgerOutcome.Started);

            Assert.Contains(entries, entry =>
                entry.Category == AiDecisionLedgerCategory.Retry &&
                entry.EventType == AiEngineEvents.Retry.Denied &&
                entry.Outcome == AiDecisionLedgerOutcome.Denied);
        }

        private static IAiDagExecutionEngineServices CreateServices(
            InMemoryAiDecisionLedger ledger)
        {
            var services = Substitute.For<IAiDagExecutionEngineServices>();
            var observability = Substitute.For<IAiRuntimeObservability>();
            var runtimeIdentity = Substitute.For<IAiRuntimeInstanceIdentityDescriptor>();

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

            runtimeIdentity.RuntimeInstanceId.Returns("worker-1");

            observability.Ledger.Returns(recorder);

            services.ObservabilityService.Returns(observability);
            services.RuntimeInstanceIdentity.Returns(runtimeIdentity);

            return services;
        }
    }
}