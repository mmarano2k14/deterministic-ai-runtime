using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.AI.Observability.Ledger;
using Multiplexed.AI.Runtime.Execution.Instance;
using Multiplexed.AI.Runtime.Observability.Context;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Multiplexed.AI.Tests.Runtime.Observability.Ledger
{
    /// <summary>
    /// Tests the in-memory decision ledger implementation.
    /// </summary>
    public sealed class InMemoryAiDecisionLedgerTests
    {
        /// <summary>
        /// Verifies that appended entries can be read by execution identifier.
        /// </summary>
        [Fact]
        public async Task GetByExecutionAsync_ShouldReturnEntriesForExecutionOnly()
        {
            var ledger = new InMemoryAiDecisionLedger();

            await ledger.AppendAsync(CreateEntry("execution-1", AiDecisionLedgerEvents.Execution.Started));
            await ledger.AppendAsync(CreateEntry("execution-2", AiDecisionLedgerEvents.Execution.Started));
            await ledger.AppendAsync(CreateEntry("execution-1", AiDecisionLedgerEvents.Execution.Completed));

            var entries = await ledger.GetByExecutionAsync("execution-1");

            entries.Should().HaveCount(2);
            entries.Should().OnlyContain(entry => entry.CorrelationContext.ExecutionId == "execution-1");
            entries[0].EventType.Should().Be(AiDecisionLedgerEvents.Execution.Started);
            entries[1].EventType.Should().Be(AiDecisionLedgerEvents.Execution.Completed);
        }

        /// <summary>
        /// Verifies that sequence numbers are assigned per execution stream.
        /// </summary>
        [Fact]
        public async Task AppendAsync_ShouldAssignSequencePerExecution()
        {
            var ledger = new InMemoryAiDecisionLedger();

            await ledger.AppendAsync(CreateEntry("execution-1", AiDecisionLedgerEvents.Step.Started));
            await ledger.AppendAsync(CreateEntry("execution-1", AiDecisionLedgerEvents.Step.Completed));
            await ledger.AppendAsync(CreateEntry("execution-2", AiDecisionLedgerEvents.Step.Started));

            var executionOneEntries = await ledger.GetByExecutionAsync("execution-1");
            var executionTwoEntries = await ledger.GetByExecutionAsync("execution-2");

            executionOneEntries[0].Sequence.Should().Be(1);
            executionOneEntries[1].Sequence.Should().Be(2);
            executionTwoEntries[0].Sequence.Should().Be(1);
        }

        /// <summary>
        /// Verifies that query filters return matching ledger entries.
        /// </summary>
        [Fact]
        public async Task QueryAsync_ShouldApplyFilters()
        {
            var ledger = new InMemoryAiDecisionLedger();

            await ledger.AppendAsync(CreateEntry(
                "execution-1",
                AiDecisionLedgerEvents.Concurrency.Denied,
                AiDecisionLedgerCategory.Concurrency,
                AiDecisionLedgerOutcome.Denied,
                stepId: "step-1",
                policyKey: "provider-limit"));

            await ledger.AppendAsync(CreateEntry(
                "execution-1",
                AiDecisionLedgerEvents.Step.Completed,
                AiDecisionLedgerCategory.Step,
                AiDecisionLedgerOutcome.Completed,
                stepId: "step-2"));

            var entries = await ledger.QueryAsync(new AiDecisionLedgerQuery
            {
                ExecutionId = "execution-1",
                Category = AiDecisionLedgerCategory.Concurrency,
                Outcome = AiDecisionLedgerOutcome.Denied,
                PolicyKey = "provider-limit"
            });

            entries.Should().ContainSingle();
            entries[0].EventType.Should().Be(AiDecisionLedgerEvents.Concurrency.Denied);
            entries[0].CorrelationContext.StepId.Should().Be("step-1");
        }

        /// <summary>
        /// Verifies that the default recorder builds ledger entries from the runtime correlation context.
        /// </summary>
        [Fact]
        public async Task DefaultRecorder_ShouldBuildEntryFromCorrelationContext()
        {
            var ledger = new InMemoryAiDecisionLedger();

            IAiRuntimeInstanceIdentityDescriptor runtimeInstanceIdentity =new DefaultAiRuntimeInstanceIdentity();

            IAiRuntimeCorrelationAccessor correlationAccessor =
                new AsyncLocalAiRuntimeCorrelationAccessor(runtimeInstanceIdentity);

            var recorder = new DefaultAiDecisionLedgerRecorder(
                ledger,
                correlationAccessor,
                Options.Create(new AiDecisionLedgerRecorderOptions
                {
                    WriteMode = AiDecisionLedgerWriteMode.Strict
                }),
                NullLogger<DefaultAiDecisionLedgerRecorder>.Instance);

            var context = new AiRuntimeLedgerEventCorrelationContext
            {
                ExecutionId = "execution-1",
                RunId = "run-1",
                PipelineName = "pipeline-a",
                PipelineVersion = "1.0.0",
                StepId = "step-1",
                StepKey = "rag.retrieve",
                RuntimeInstanceId = "runtime-a",
                WorkerId = "worker-1",
                ClaimToken = "claim-token-1",
                PolicyKey = "retry.default",
                Provider = "openai",
                Model = "gpt-test",
                Operation = "completion",
                InputPayloadRef = "payload-input-1",
                OutputPayloadRef = "payload-output-1",
                HumanInputRef = "human-input-1",
                PromptRef = "prompt-1",
                TraceId = "trace-1",
                CorrelationId = "correlation-1"
            };

            await recorder.RecordAsync(
                context,
                AiDecisionLedgerCategory.Retry,
                AiDecisionLedgerEvents.Retry.Scheduled,
                AiDecisionLedgerOutcome.Applied,
                "Retry scheduled after transient failure.",
                new Dictionary<string, string>
                {
                    ["retry.count"] = "1",
                    ["retry.delay.ms"] = "500"
                });

            var entries = await ledger.GetByExecutionAsync("execution-1");

            entries.Should().ContainSingle();

            var entry = entries[0];
            var correlation = entry.CorrelationContext;

            correlation.ExecutionId.Should().Be("execution-1");
            correlation.RunId.Should().Be("run-1");
            correlation.PipelineName.Should().Be("pipeline-a");
            correlation.PipelineVersion.Should().Be("1.0.0");
            correlation.StepId.Should().Be("step-1");
            correlation.StepKey.Should().Be("rag.retrieve");
            correlation.RuntimeInstanceId.Should().Be("runtime-a");
            correlation.WorkerId.Should().Be("worker-1");
            correlation.ClaimToken.Should().Be("claim-token-1");
            correlation.PolicyKey.Should().Be("retry.default");
            correlation.Provider.Should().Be("openai");
            correlation.Model.Should().Be("gpt-test");
            correlation.Operation.Should().Be("completion");
            correlation.InputPayloadRef.Should().Be("payload-input-1");
            correlation.OutputPayloadRef.Should().Be("payload-output-1");
            correlation.HumanInputRef.Should().Be("human-input-1");
            correlation.PromptRef.Should().Be("prompt-1");
            correlation.TraceId.Should().Be("trace-1");
            correlation.CorrelationId.Should().Be("correlation-1");

            entry.Category.Should().Be(AiDecisionLedgerCategory.Retry);
            entry.EventType.Should().Be(AiDecisionLedgerEvents.Retry.Scheduled);
            entry.Outcome.Should().Be(AiDecisionLedgerOutcome.Applied);
            entry.Reason.Should().Be("Retry scheduled after transient failure.");
            entry.Metadata.Should().ContainKey("retry.count");
            entry.Metadata.Should().ContainKey("retry.delay.ms");
        }

        /// <summary>
        /// Verifies that disabled recorder mode does not append entries.
        /// </summary>
        [Fact]
        public async Task DefaultRecorder_WhenDisabled_ShouldNotAppendEntry()
        {
            var ledger = new InMemoryAiDecisionLedger();

            IAiRuntimeInstanceIdentityDescriptor runtimeInstanceIdentity =
                new DefaultAiRuntimeInstanceIdentity();

            IAiRuntimeCorrelationAccessor correlationAccessor =
                new AsyncLocalAiRuntimeCorrelationAccessor(
                    runtimeInstanceIdentity);

            var recorder = new DefaultAiDecisionLedgerRecorder(
                ledger,
                correlationAccessor,
                Options.Create(new AiDecisionLedgerRecorderOptions
                {
                    WriteMode = AiDecisionLedgerWriteMode.Disabled
                }),
                NullLogger<DefaultAiDecisionLedgerRecorder>.Instance);

            await recorder.RecordAsync(
                new AiRuntimeLedgerEventCorrelationContext
                {
                    ExecutionId = "execution-1"
                },
                AiDecisionLedgerCategory.Execution,
                AiDecisionLedgerEvents.Execution.Started,
                AiDecisionLedgerOutcome.Started);

            var entries = await ledger.GetByExecutionAsync("execution-1");

            entries.Should().BeEmpty();
        }

        private static AiDecisionLedgerEntry CreateEntry(
            string executionId,
            string eventType,
            AiDecisionLedgerCategory category = AiDecisionLedgerCategory.Execution,
            AiDecisionLedgerOutcome outcome = AiDecisionLedgerOutcome.None,
            string? stepId = null,
            string? policyKey = null)
        {
            return new AiDecisionLedgerEntry
            {
                EntryId = Guid.NewGuid().ToString("N"),
                CorrelationContext = new AiRuntimeLedgerEventCorrelationContext
                {
                    ExecutionId = executionId,
                    StepId = stepId,
                    PolicyKey = policyKey
                },
                Category = category,
                EventType = eventType,
                Outcome = outcome,
                TimestampUtc = DateTimeOffset.UtcNow
            };
        }
    }
}