using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.SharedController;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Tests.Fixtures;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.SharedController
{
    public sealed class InMemoryAiSharedRunStoreTests
    {
        [Fact]
        public async Task CreateAsync_Should_Create_Shared_Run()
        {
            var store = new InMemoryAiSharedRunStore();

            var record = CreateRecord("shared-run-1", AiSharedRunStatus.AssignedToInstance);

            var created = await store.CreateAsync(record);

            Assert.Equal("shared-run-1", created.SharedRunId);
            Assert.Equal(AiSharedRunStatus.AssignedToInstance, created.Status);
        }

        [Fact]
        public async Task CreateAsync_Should_Reject_Duplicate_SharedRunId()
        {
            var store = new InMemoryAiSharedRunStore();

            var record = CreateRecord("shared-run-1", AiSharedRunStatus.AssignedToInstance);

            await store.CreateAsync(record);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.CreateAsync(record));
        }

        [Fact]
        public async Task GetAsync_Should_Return_Record_When_Known()
        {
            var store = new InMemoryAiSharedRunStore();

            await store.CreateAsync(
                CreateRecord("shared-run-1", AiSharedRunStatus.QueuedGlobally));

            var record = await store.GetAsync("shared-run-1");

            Assert.NotNull(record);
            Assert.Equal("shared-run-1", record!.SharedRunId);
            Assert.Equal(AiSharedRunStatus.QueuedGlobally, record.Status);
        }

        [Fact]
        public async Task GetAsync_Should_Return_Null_When_Unknown()
        {
            var store = new InMemoryAiSharedRunStore();

            var record = await store.GetAsync("missing-run");

            Assert.Null(record);
        }

        [Fact]
        public async Task ListAsync_Should_Return_Records_Ordered_By_SubmittedAt_Then_Id()
        {
            var store = new InMemoryAiSharedRunStore();

            await store.CreateAsync(
                CreateRecord(
                    "shared-run-b",
                    AiSharedRunStatus.AssignedToInstance,
                    DateTimeOffset.UtcNow.AddMinutes(1)));

            await store.CreateAsync(
                CreateRecord(
                    "shared-run-a",
                    AiSharedRunStatus.AssignedToInstance,
                    DateTimeOffset.UtcNow));

            var records = await store.ListAsync();

            Assert.Equal(2, records.Count);
            Assert.Equal("shared-run-a", records[0].SharedRunId);
            Assert.Equal("shared-run-b", records[1].SharedRunId);
        }

        [Fact]
        public async Task ListAsync_Should_Exclude_Cancelled_By_Default()
        {
            var store = new InMemoryAiSharedRunStore();

            await store.CreateAsync(
                CreateRecord("shared-run-1", AiSharedRunStatus.AssignedToInstance));

            await store.CreateAsync(
                CreateRecord("shared-run-2", AiSharedRunStatus.Cancelled));

            var records = await store.ListAsync();

            Assert.Single(records);
            Assert.Equal("shared-run-1", records[0].SharedRunId);
        }

        [Fact]
        public async Task ListAsync_Should_Include_Cancelled_When_Requested()
        {
            var store = new InMemoryAiSharedRunStore();

            await store.CreateAsync(
                CreateRecord("shared-run-1", AiSharedRunStatus.AssignedToInstance));

            await store.CreateAsync(
                CreateRecord("shared-run-2", AiSharedRunStatus.Cancelled));

            var records = await store.ListAsync(includeCancelled: true);

            Assert.Equal(2, records.Count);
        }

        [Fact]
        public async Task ListAsync_Should_Exclude_Completed_By_Default()
        {
            var store = new InMemoryAiSharedRunStore();

            await store.CreateAsync(
                CreateRecord("shared-run-1", AiSharedRunStatus.AssignedToInstance));

            await store.CreateAsync(
                CreateRecord("shared-run-2", AiSharedRunStatus.Completed));

            var records = await store.ListAsync();

            Assert.Single(records);
            Assert.Equal("shared-run-1", records[0].SharedRunId);
        }

        [Fact]
        public async Task ListAsync_Should_Include_Completed_When_Requested()
        {
            var store = new InMemoryAiSharedRunStore();

            await store.CreateAsync(
                CreateRecord("shared-run-1", AiSharedRunStatus.AssignedToInstance));

            await store.CreateAsync(
                CreateRecord("shared-run-2", AiSharedRunStatus.Completed));

            var records = await store.ListAsync(includeCompleted: true);

            Assert.Equal(2, records.Count);
        }

        [Fact]
        public async Task ListAsync_Should_Exclude_Failed_By_Default()
        {
            var store = new InMemoryAiSharedRunStore();

            await store.CreateAsync(
                CreateRecord("shared-run-1", AiSharedRunStatus.AssignedToInstance));

            await store.CreateAsync(
                CreateRecord("shared-run-2", AiSharedRunStatus.Failed));

            var records = await store.ListAsync();

            Assert.Single(records);
            Assert.Equal("shared-run-1", records[0].SharedRunId);
        }

        [Fact]
        public async Task ListAsync_Should_Include_Failed_When_Requested()
        {
            var store = new InMemoryAiSharedRunStore();

            await store.CreateAsync(
                CreateRecord("shared-run-1", AiSharedRunStatus.AssignedToInstance));

            await store.CreateAsync(
                CreateRecord("shared-run-2", AiSharedRunStatus.Failed));

            var records = await store.ListAsync(includeFailed: true);

            Assert.Equal(2, records.Count);
        }

        [Fact]
        public async Task CancelAsync_Should_Mark_NonTerminal_Run_As_Cancelled()
        {
            var store = new InMemoryAiSharedRunStore();

            await store.CreateAsync(
                CreateRecord("shared-run-1", AiSharedRunStatus.QueuedGlobally));

            var cancelled = await store.CancelAsync(
                "shared-run-1",
                reason: "operator cancel",
                requestedBy: "tester",
                source: "unit-test");

            Assert.NotNull(cancelled);
            Assert.Equal(AiSharedRunStatus.Cancelled, cancelled!.Status);
            Assert.Equal("operator cancel", cancelled.FailureReason);
            Assert.Equal("operator cancel", cancelled.Reason);
            Assert.Equal("tester", cancelled.RequestedBy);
            Assert.Equal("unit-test", cancelled.Source);
        }

        [Fact]
        public async Task CancelAsync_Should_Return_Null_When_Run_Is_Unknown()
        {
            var store = new InMemoryAiSharedRunStore();

            var cancelled = await store.CancelAsync("missing-run");

            Assert.Null(cancelled);
        }

        [Theory]
        [InlineData(AiSharedRunStatus.Completed)]
        [InlineData(AiSharedRunStatus.Failed)]
        [InlineData(AiSharedRunStatus.Cancelled)]
        public async Task CancelAsync_Should_Return_Existing_Record_When_Run_Is_Terminal(
            AiSharedRunStatus terminalStatus)
        {
            var store = new InMemoryAiSharedRunStore();

            await store.CreateAsync(
                CreateRecord("shared-run-1", terminalStatus, failureReason: "existing failure"));

            var result = await store.CancelAsync(
                "shared-run-1",
                reason: "new cancel");

            Assert.NotNull(result);
            Assert.Equal(terminalStatus, result!.Status);
            Assert.Equal("existing failure", result.FailureReason);
        }

        [Fact]
        public async Task MarkDispatchedAsync_Should_Update_NonTerminal_Run()
        {
            var store = new InMemoryAiSharedRunStore();

            await store.CreateAsync(
                CreateRecord("shared-run-1", AiSharedRunStatus.AssignedToInstance));

            var updated = await store.MarkDispatchedAsync(
                "shared-run-1",
                runtimeInstanceId: "runtime-1",
                localRunId: "local-run-1",
                executionId: "execution-1",
                reason: "dispatch succeeded");

            Assert.NotNull(updated);
            Assert.Equal(AiSharedRunStatus.Dispatched, updated!.Status);
            Assert.Equal("runtime-1", updated.AssignedRuntimeInstanceId);
            Assert.Equal("local-run-1", updated.LocalRunId);
            Assert.Equal("execution-1", updated.ExecutionId);
            Assert.Equal("dispatch succeeded", updated.Reason);
        }

        [Fact]
        public async Task MarkDispatchedAsync_Should_Preserve_First_Durable_Ownership()
        {
            var store = new InMemoryAiSharedRunStore();

            await store.CreateAsync(
                CreateRecord(
                    "shared-run-1",
                    AiSharedRunStatus.AssignedToInstance));

            await store.MarkDispatchedAsync(
                "shared-run-1",
                runtimeInstanceId: "runtime-first",
                localRunId: "local-first",
                executionId: "execution-first",
                reason: "first dispatch");

            var second = await store.MarkDispatchedAsync(
                "shared-run-1",
                runtimeInstanceId: "runtime-second",
                localRunId: "local-second",
                executionId: "execution-second",
                reason: "delayed duplicate dispatch");

            Assert.NotNull(second);
            Assert.Equal(AiSharedRunStatus.Dispatched, second!.Status);
            Assert.Equal(
                "runtime-first",
                second.AssignedRuntimeInstanceId);
            Assert.Equal("local-first", second.LocalRunId);
            Assert.Equal("execution-first", second.ExecutionId);
            Assert.Equal("first dispatch", second.Reason);
        }

        [Fact]
        public async Task MarkDispatchedAsync_Should_Return_Null_When_Run_Is_Unknown()
        {
            var store = new InMemoryAiSharedRunStore();

            var updated = await store.MarkDispatchedAsync(
                "missing-run",
                runtimeInstanceId: "runtime-1");

            Assert.Null(updated);
        }

        /// <summary>
        /// Verifies that dispatch ownership metadata is durably persisted in the shared run store.
        /// </summary>
        [Fact]
        public async Task MarkDispatchedAsync_Should_Persist_Dispatch_Ownership_Metadata()
        {
            var store = new InMemoryAiSharedRunStore();

            await store.CreateAsync(
                CreateRecord("shared-run-1", AiSharedRunStatus.AssignedToInstance));

            await store.MarkDispatchedAsync(
                "shared-run-1",
                runtimeInstanceId: "runtime-1",
                localRunId: "local-run-1",
                executionId: "execution-1",
                reason: "dispatch succeeded");

            var stored = await store.GetAsync("shared-run-1");
            var records = await store.ListAsync();

            Assert.NotNull(stored);
            Assert.Equal(AiSharedRunStatus.Dispatched, stored!.Status);
            Assert.Equal("runtime-1", stored.AssignedRuntimeInstanceId);
            Assert.Equal("local-run-1", stored.LocalRunId);
            Assert.Equal("execution-1", stored.ExecutionId);
            Assert.Equal("dispatch succeeded", stored.Reason);
            Assert.Contains(records, record => record.SharedRunId == "shared-run-1" && record.Status == AiSharedRunStatus.Dispatched);
        }

        private static AiSharedRunRecord CreateRecord(
            string sharedRunId,
            AiSharedRunStatus status,
            DateTimeOffset? submittedAtUtc = null,
            string? failureReason = null)
        {
            var now = submittedAtUtc ?? DateTimeOffset.UtcNow;

            return new AiSharedRunRecord
            {
                SharedRunId = sharedRunId,
                Status = status,
                RunRequest = new AiRuntimePipelineRunRequest
                {
                    PipelineName = "pipeline-1"
                },
                ExecutionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create(
                    tenantId: "tenant-1"),
                PipelineKey = "pipeline-1",
                FailureReason = failureReason,
                SubmittedAtUtc = now,
                UpdatedAtUtc = now
            };
        }
    }
}