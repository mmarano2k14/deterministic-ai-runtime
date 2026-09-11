using FluentAssertions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.AI.Observability.Ledger;
using Multiplexed.AI.Runtime.Observability.Ledger.Mongo;
using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Multiplexed.AI.Tests.Integration.Runtime.Observability.Ledger
{
    /// <summary>
    /// Locks the distributed ordering contract that constrains decision-ledger
    /// sequence-allocation optimizations.
    /// </summary>
    public sealed class MongoAiDecisionLedgerSequenceContractTests
    {
        private const string ConnectionString = "mongodb://localhost:27017";
        private const string DatabaseName = "multiplexed_ai_tests";

        /// <summary>
        /// Verifies that independent ledger instances targeting the same durable
        /// stream still produce one unique monotonic sequence space.
        /// </summary>
        [Fact]
        public async Task AppendAsync_AcrossIndependentLedgerInstances_ShouldShareOneSequenceStream()
        {
            var collectionSuffix = Guid.NewGuid().ToString("N");
            var primaryLedger = CreateLedger(collectionSuffix, createIndexes: true);

            // Create indexes exactly once before the concurrent wave. The additional
            // ledger instances intentionally skip index creation but target the same
            // durable collections, which isolates sequence-allocation behavior.
            await primaryLedger.GetByExecutionAsync("index-warmup");

            var ledgers = Enumerable.Range(0, 4)
                .Select(_ => CreateLedger(collectionSuffix, createIndexes: false))
                .ToArray();

            const string executionId = "execution-shared-sequence-stream";
            const int appendCount = 120;

            var tasks = Enumerable.Range(1, appendCount)
                .Select(index => ledgers[index % ledgers.Length].AppendAsync(
                    CreateEntry(
                        executionId,
                        $"sequence.concurrent.{index:D3}")))
                .ToArray();

            await Task.WhenAll(tasks);

            var entries = await primaryLedger.GetByExecutionAsync(executionId);
            var expectedSequences = Enumerable.Range(1, appendCount)
                .Select(value => (long)value)
                .ToArray();

            entries.Should().HaveCount(appendCount);
            entries.Select(entry => entry.Sequence).Should().Equal(expectedSequences);
            entries.Select(entry => entry.Sequence).Should().OnlyHaveUniqueItems();
        }

        /// <summary>
        /// Verifies the completed-before-started ordering guarantee across independent
        /// ledger instances. A later append must never receive a lower durable sequence
        /// than an append that completed before it began.
        /// </summary>
        [Fact]
        public async Task AppendAsync_WhenCallsHaveHappensBeforeRelationship_ShouldPreserveSequenceOrderAcrossInstances()
        {
            var collectionSuffix = Guid.NewGuid().ToString("N");
            var ledgerA = CreateLedger(collectionSuffix, createIndexes: true);

            await ledgerA.GetByExecutionAsync("index-warmup");

            var ledgerB = CreateLedger(collectionSuffix, createIndexes: false);
            const string executionId = "execution-happens-before";

            await ledgerA.AppendAsync(CreateEntry(executionId, "sequence.order.a1"));
            await ledgerB.AppendAsync(CreateEntry(executionId, "sequence.order.b1"));
            await ledgerA.AppendAsync(CreateEntry(executionId, "sequence.order.a2"));

            var entries = await ledgerB.GetByExecutionAsync(executionId);

            entries.Select(entry => entry.EventType).Should().Equal(
                "sequence.order.a1",
                "sequence.order.b1",
                "sequence.order.a2");

            entries.Select(entry => entry.Sequence).Should().Equal(1L, 2L, 3L);
        }

        /// <summary>
        /// Verifies that public sequence-range queries observe the durable ordering key
        /// directly, so changing sequence semantics would also change query semantics.
        /// </summary>
        [Fact]
        public async Task QueryAsync_WithSequenceRange_ShouldUseDurableSequenceOrderingContract()
        {
            var collectionSuffix = Guid.NewGuid().ToString("N");
            var ledgerA = CreateLedger(collectionSuffix, createIndexes: true);

            await ledgerA.GetByExecutionAsync("index-warmup");

            var ledgerB = CreateLedger(collectionSuffix, createIndexes: false);
            const string executionId = "execution-sequence-range";

            await ledgerA.AppendAsync(CreateEntry(executionId, "sequence.range.1"));
            await ledgerB.AppendAsync(CreateEntry(executionId, "sequence.range.2"));
            await ledgerA.AppendAsync(CreateEntry(executionId, "sequence.range.3"));
            await ledgerB.AppendAsync(CreateEntry(executionId, "sequence.range.4"));
            await ledgerA.AppendAsync(CreateEntry(executionId, "sequence.range.5"));

            var entries = await ledgerA.QueryAsync(new AiDecisionLedgerQuery
            {
                ExecutionId = executionId,
                SequenceFrom = 2,
                SequenceTo = 4
            });

            entries.Select(entry => entry.Sequence).Should().Equal(2L, 3L, 4L);
            entries.Select(entry => entry.EventType).Should().Equal(
                "sequence.range.2",
                "sequence.range.3",
                "sequence.range.4");
        }

        private static MongoAiDecisionLedger CreateLedger(
            string collectionSuffix,
            bool createIndexes)
        {
            var client = new MongoClient(ConnectionString);

            return new MongoAiDecisionLedger(
                client,
                Options.Create(new MongoAiDecisionLedgerOptions
                {
                    DatabaseName = DatabaseName,
                    CollectionName = $"ai_decision_ledger_entries_{collectionSuffix}",
                    SequenceCollectionName = $"ai_decision_ledger_sequences_{collectionSuffix}",
                    CreateIndexes = createIndexes
                }));
        }

        private static AiDecisionLedgerEntry CreateEntry(
            string executionId,
            string eventType)
        {
            return new AiDecisionLedgerEntry
            {
                EntryId = Guid.NewGuid().ToString("N"),
                CorrelationContext = new AiRuntimeLedgerEventCorrelationContext
                {
                    ExecutionId = executionId
                },
                Category = AiDecisionLedgerCategory.Execution,
                EventType = eventType,
                Outcome = AiDecisionLedgerOutcome.None,
                TimestampUtc = DateTimeOffset.UtcNow
            };
        }
    }
}
