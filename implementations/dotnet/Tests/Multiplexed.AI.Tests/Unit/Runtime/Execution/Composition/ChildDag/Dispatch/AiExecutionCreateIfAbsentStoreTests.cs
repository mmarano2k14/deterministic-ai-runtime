using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.AI.Runtime.Execution;
using Multiplexed.AI.Stores.Creation;
using Multiplexed.AI.Stores.Memory;

namespace Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Dispatch
{
    public sealed class AiExecutionCreateIfAbsentStoreTests
    {
        [Fact]
        public async Task TryCreateIfAbsentAsync_Should_Preserve_First_Exact_Execution()
        {
            IAiExecutionCreateIfAbsentStore store = new MemoryAiExecutionStore();
            var firstRecord = CreateRecord("child-execution-1", "child-analysis");
            var firstState = CreateState("child-execution-1", "child-analysis", "first");
            var conflictingRecord = CreateRecord("child-execution-1", "other-pipeline");
            var conflictingState = CreateState("child-execution-1", "other-pipeline", "second");

            var first = await store.TryCreateIfAbsentAsync(firstRecord, firstState);
            var second = await store.TryCreateIfAbsentAsync(conflictingRecord, conflictingState);

            Assert.True(first);
            Assert.False(second);

            var readableStore = Assert.IsType<MemoryAiExecutionStore>(store);
            var persistedRecord = await readableStore.GetRecordAsync("child-execution-1");
            var persistedState = await readableStore.GetStateAsync("child-execution-1");

            Assert.NotNull(persistedRecord);
            Assert.NotNull(persistedState);
            Assert.Equal("child-analysis", persistedRecord!.PipelineName);
            Assert.Equal("definition-hash", persistedRecord.PipelineDefinitionSnapshot?.ContentHash);
            Assert.Equal("child-analysis", persistedState!.PipelineName);
            Assert.Equal("first", persistedState.Data["value"]);
        }

        [Fact]
        public async Task TryCreateIfAbsentAsync_Should_Allow_Only_One_Concurrent_Winner()
        {
            var store = new MemoryAiExecutionStore();
            var attempts = Enumerable.Range(0, 16)
                .Select(index => store.TryCreateIfAbsentAsync(
                    CreateRecord("child-execution-race", "child-analysis"),
                    CreateState("child-execution-race", "child-analysis", index.ToString())))
                .ToArray();

            var results = await Task.WhenAll(attempts);

            Assert.Equal(1, results.Count(created => created));
            Assert.NotNull(await store.GetRecordAsync("child-execution-race"));
            Assert.NotNull(await store.GetStateAsync("child-execution-race"));
        }

        private static AiExecutionRecord CreateRecord(string executionId, string pipelineName)
        {
            return new AiExecutionRecord
            {
                ExecutionId = executionId,
                PipelineName = pipelineName,
                PipelineDefinitionSnapshot = AiStoredPayload.Inline(
                    "{}",
                    contentType: "application/json",
                    contentHash: "definition-hash"),
                ExecutionMode = AiExecutionMode.Dag,
                ContextKey = $"execution-{executionId}",
                Status = AiExecutionStatus.Pending,
                Steps = ["analyze"]
            };
        }

        private static AiExecutionState CreateState(
            string executionId,
            string pipelineName,
            string value)
        {
            return new AiExecutionState
            {
                ExecutionId = executionId,
                PipelineName = pipelineName,
                Data = new Dictionary<string, object?>
                {
                    ["value"] = value
                }
            };
        }
    }
}
