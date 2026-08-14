using FluentAssertions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.AI.Observability.Ledger;
using Multiplexed.AI.Runtime.Observability.Ledger.Mongo;
using Multiplexed.AI.Stores.Mongo;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Multiplexed.AI.Tests.Integration.Runtime.Observability.Ledger
{
    /// <summary>
    /// Tests the MongoDB-backed decision ledger implementation.
    /// </summary>
    public sealed class MongoAiDecisionLedgerTests
    {
        private const string ConnectionString = "mongodb://localhost:27017";
        private const string DatabaseName = "multiplexed_ai_tests";

        /// <summary>
        /// Verifies that MongoDB-backed ledger entries can be appended and read by execution identifier.
        /// </summary>
        [Fact]
        public async Task GetByExecutionAsync_ShouldReturnEntriesForExecutionOnly()
        {
            var collectionSuffix = Guid.NewGuid().ToString("N");
            var ledger = CreateLedger(collectionSuffix);

            await ledger.AppendAsync(CreateEntry("execution-1", AiDecisionLedgerEvents.Execution.Started));
            await ledger.AppendAsync(CreateEntry("execution-2", AiDecisionLedgerEvents.Execution.Started));
            await ledger.AppendAsync(CreateEntry("execution-1", AiDecisionLedgerEvents.Execution.Completed));

            var entries = await ledger.GetByExecutionAsync("execution-1");

            entries.Should().HaveCount(2);
            entries.Should().OnlyContain(entry => entry.CorrelationContext.ExecutionId == "execution-1");
            entries[0].Sequence.Should().Be(1);
            entries[1].Sequence.Should().Be(2);
            entries[0].EventType.Should().Be(AiDecisionLedgerEvents.Execution.Started);
            entries[1].EventType.Should().Be(AiDecisionLedgerEvents.Execution.Completed);
        }

        /// <summary>
        /// Verifies that MongoDB sequence assignment is monotonic per execution.
        /// </summary>
        [Fact]
        public async Task AppendAsync_ShouldAssignSequencePerExecution()
        {
            var collectionSuffix = Guid.NewGuid().ToString("N");
            var ledger = CreateLedger(collectionSuffix);

            await ledger.AppendAsync(CreateEntry("execution-1", AiDecisionLedgerEvents.Step.Started));
            await ledger.AppendAsync(CreateEntry("execution-1", AiDecisionLedgerEvents.Step.Completed));
            await ledger.AppendAsync(CreateEntry("execution-2", AiDecisionLedgerEvents.Step.Started));

            var executionOneEntries = await ledger.GetByExecutionAsync("execution-1");
            var executionTwoEntries = await ledger.GetByExecutionAsync("execution-2");

            executionOneEntries.Select(entry => entry.Sequence).Should().Equal(1, 2);
            executionTwoEntries.Select(entry => entry.Sequence).Should().Equal(1);
        }

        /// <summary>
        /// Verifies that MongoDB query filters return matching ledger entries.
        /// </summary>
        [Fact]
        public async Task QueryAsync_ShouldApplyFilters()
        {
            var collectionSuffix = Guid.NewGuid().ToString("N");
            var ledger = CreateLedger(collectionSuffix);

            await ledger.AppendAsync(CreateEntry(
                "execution-1",
                AiDecisionLedgerEvents.Concurrency.Denied,
                AiDecisionLedgerCategory.Concurrency,
                AiDecisionLedgerOutcome.Denied,
                stepId: "step-1",
                policyKey: "provider-limit",
                workerId: "worker-1"));

            await ledger.AppendAsync(CreateEntry(
                "execution-1",
                AiDecisionLedgerEvents.Step.Completed,
                AiDecisionLedgerCategory.Step,
                AiDecisionLedgerOutcome.Completed,
                stepId: "step-2",
                workerId: "worker-2"));

            var entries = await ledger.QueryAsync(new AiDecisionLedgerQuery
            {
                ExecutionId = "execution-1",
                Category = AiDecisionLedgerCategory.Concurrency,
                Outcome = AiDecisionLedgerOutcome.Denied,
                PolicyKey = "provider-limit",
                WorkerId = "worker-1"
            });

            entries.Should().ContainSingle();
            entries[0].EventType.Should().Be(AiDecisionLedgerEvents.Concurrency.Denied);
            entries[0].CorrelationContext.StepId.Should().Be("step-1");
            entries[0].CorrelationContext.PolicyKey.Should().Be("provider-limit");
            entries[0].CorrelationContext.WorkerId.Should().Be("worker-1");
        }

        /// <summary>
        /// Verifies that operation-scoped ledger queries use the dedicated operation/timestamp index.
        /// </summary>
        [Fact]
        public async Task QueryAsync_ByOperation_ShouldCreateOperationTimestampIndex()
        {
            var collectionSuffix = Guid.NewGuid().ToString("N");
            var collectionName = $"ai_decision_ledger_entries_{collectionSuffix}";
            var ledger = CreateLedger(collectionSuffix);
            var timestampUtc = DateTimeOffset.UtcNow;

            await ledger.AppendAsync(new AiDecisionLedgerEntry
            {
                EntryId = Guid.NewGuid().ToString("N"),
                CorrelationContext = new AiRuntimeLedgerEventCorrelationContext
                {
                    ExecutionId = "execution-operation-index",
                    Operation = "runtime-host-creation"
                },
                Category = AiDecisionLedgerCategory.Scaling,
                EventType = "control.scaling.runtime-host-creation.succeeded",
                Outcome = AiDecisionLedgerOutcome.Succeeded,
                TimestampUtc = timestampUtc
            });

            var entries = await ledger.QueryAsync(new AiDecisionLedgerQuery
            {
                Operation = "runtime-host-creation",
                TimestampFromUtc = timestampUtc.AddMinutes(-1),
                TimestampToUtc = timestampUtc.AddMinutes(1)
            });

            entries.Should().ContainSingle();

            var client = new MongoClient(ConnectionString);
            var collection = client
                .GetDatabase(DatabaseName)
                .GetCollection<MongoDB.Bson.BsonDocument>(collectionName);

            using var indexCursor = await collection.Indexes.ListAsync();
            var indexes = await indexCursor.ToListAsync();
            var operationTimestampIndex = indexes.Single(index =>
                string.Equals(
                    index["name"].AsString,
                    "ix_operation_timestamp",
                    StringComparison.Ordinal));

            var indexKeys = operationTimestampIndex["key"].AsBsonDocument;

            indexKeys["Operation"].AsInt32.Should().Be(1);
            indexKeys["TimestampUtc"].AsInt32.Should().Be(1);
        }

        /// <summary>
        /// Verifies that MongoDB preserves the runtime correlation context.
        /// </summary>
        [Fact]
        public async Task GetByExecutionAsync_ShouldRoundTripCorrelationContext()
        {
            var collectionSuffix = Guid.NewGuid().ToString("N");
            var ledger = CreateLedger(collectionSuffix);

            var entry = new AiDecisionLedgerEntry
            {
                EntryId = Guid.NewGuid().ToString("N"),
                CorrelationContext = new AiRuntimeLedgerEventCorrelationContext
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
                },
                Category = AiDecisionLedgerCategory.Retry,
                EventType = AiDecisionLedgerEvents.Retry.Scheduled,
                Outcome = AiDecisionLedgerOutcome.Applied,
                TimestampUtc = DateTimeOffset.UtcNow,
                Reason = "Retry scheduled after transient failure.",
                Metadata = new Dictionary<string, string>
                {
                    ["retry.count"] = "1",
                    ["retry.delay.ms"] = "500"
                }
            };

            await ledger.AppendAsync(entry);

            var entries = await ledger.GetByExecutionAsync("execution-1");

            entries.Should().ContainSingle();

            var stored = entries[0];
            var correlation = stored.CorrelationContext;

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

            stored.Category.Should().Be(AiDecisionLedgerCategory.Retry);
            stored.EventType.Should().Be(AiDecisionLedgerEvents.Retry.Scheduled);
            stored.Outcome.Should().Be(AiDecisionLedgerOutcome.Applied);
            stored.Reason.Should().Be("Retry scheduled after transient failure.");
            stored.Metadata.Should().ContainKey("retry.count");
            stored.Metadata.Should().ContainKey("retry.delay.ms");
        }

        /// <summary>
        /// Verifies that MongoDB assigns unique monotonic sequences under concurrent appends.
        /// </summary>
        [Fact]
        public async Task AppendAsync_WhenConcurrent_ShouldAssignUniqueMonotonicSequences()
        {
            var collectionSuffix = Guid.NewGuid().ToString("N");
            var ledger = CreateLedger(collectionSuffix);

            var tasks = Enumerable.Range(1, 50)
                .Select(index => ledger.AppendAsync(CreateEntry(
                    "execution-concurrent",
                    AiDecisionLedgerEvents.Step.Completed,
                    AiDecisionLedgerCategory.Step,
                    AiDecisionLedgerOutcome.Completed,
                    stepId: $"step-{index}",
                    workerId: $"worker-{index % 5}")))
                .ToArray();

            await Task.WhenAll(tasks);

            var entries = await ledger.GetByExecutionAsync("execution-concurrent");

            entries.Should().HaveCount(50);
            entries.Select(entry => entry.Sequence).Should().BeEquivalentTo(Enumerable.Range(1, 50).Select(value => (long)value));
            entries.OrderBy(entry => entry.Sequence).Select(entry => entry.Sequence).Should().Equal(Enumerable.Range(1, 50).Select(value => (long)value));
        }

        private static MongoAiDecisionLedger CreateLedger(
            string collectionSuffix)
        {
            var client = new MongoClient(ConnectionString);

            return new MongoAiDecisionLedger(
                client,
                Options.Create(new MongoAiDecisionLedgerOptions
                {
                    DatabaseName = DatabaseName,
                    CollectionName = $"ai_decision_ledger_entries_{collectionSuffix}",
                    SequenceCollectionName = $"ai_decision_ledger_sequences_{collectionSuffix}",
                    CreateIndexes = true
                }));
        }

        private static AiDecisionLedgerEntry CreateEntry(
            string executionId,
            string eventType,
            AiDecisionLedgerCategory category = AiDecisionLedgerCategory.Execution,
            AiDecisionLedgerOutcome outcome = AiDecisionLedgerOutcome.None,
            string? stepId = null,
            string? policyKey = null,
            string? workerId = null)
        {
            return new AiDecisionLedgerEntry
            {
                EntryId = Guid.NewGuid().ToString("N"),
                CorrelationContext = new AiRuntimeLedgerEventCorrelationContext
                {
                    ExecutionId = executionId,
                    StepId = stepId,
                    PolicyKey = policyKey,
                    WorkerId = workerId
                },
                Category = category,
                EventType = eventType,
                Outcome = outcome,
                TimestampUtc = DateTimeOffset.UtcNow
            };
        }
    }
}