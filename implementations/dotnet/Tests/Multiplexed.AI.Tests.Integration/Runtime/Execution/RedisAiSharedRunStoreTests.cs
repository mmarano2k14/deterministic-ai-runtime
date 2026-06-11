using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.SharedController;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Tests.Fixtures;
using StackExchange.Redis;

namespace Multiplexed.AI.Tests.Integration.Runtime.Execution
{
    public sealed class RedisAiSharedRunStoreTests : IAsyncLifetime
    {
        private readonly string _keyPrefix =
            $"test:ai:shared-runs:{Guid.NewGuid():N}";

        private IConnectionMultiplexer? _connection;

        public async Task InitializeAsync()
        {
            _connection = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
        }

        public async Task DisposeAsync()
        {
            if (_connection is null)
            {
                return;
            }

            var database = _connection.GetDatabase();

            var server = _connection
                .GetServer(_connection.GetEndPoints().First());

            var keys = server.Keys(
                    database: database.Database,
                    pattern: $"{_keyPrefix}*")
                .ToArray();

            if (keys.Length > 0)
            {
                await database.KeyDeleteAsync(keys);
            }

            await _connection.CloseAsync();
            await _connection.DisposeAsync();
        }

        [Fact]
        public async Task CreateAsync_Should_Create_Record()
        {
            var store = CreateStore();

            var record = CreateRecord(
                "shared-run-1",
                AiSharedRunStatus.AssignedToInstance);

            var created = await store.CreateAsync(record);

            Assert.Equal("shared-run-1", created.SharedRunId);
            Assert.Equal(AiSharedRunStatus.AssignedToInstance, created.Status);

            var loaded = await store.GetAsync("shared-run-1");

            Assert.NotNull(loaded);
            Assert.Equal("shared-run-1", loaded!.SharedRunId);
            Assert.Equal(AiSharedRunStatus.AssignedToInstance, loaded.Status);
            Assert.Equal("pipeline-1", loaded.RunRequest.PipelineName);
        }

        [Fact]
        public async Task CreateAsync_Should_Reject_Duplicate_Atomically()
        {
            var store = CreateStore();

            var record = CreateRecord(
                "shared-run-1",
                AiSharedRunStatus.AssignedToInstance);

            await store.CreateAsync(record);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.CreateAsync(record));
        }

        [Fact]
        public async Task GetAsync_Should_Return_Null_When_Record_Is_Missing()
        {
            var store = CreateStore();

            var loaded = await store.GetAsync("missing-run");

            Assert.Null(loaded);
        }

        [Fact]
        public async Task ListAsync_Should_Return_Records_From_ZSet_Index()
        {
            var store = CreateStore();

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
            var store = CreateStore();

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
            var store = CreateStore();

            await store.CreateAsync(
                CreateRecord("shared-run-1", AiSharedRunStatus.AssignedToInstance));

            await store.CreateAsync(
                CreateRecord("shared-run-2", AiSharedRunStatus.Cancelled));

            var records = await store.ListAsync(includeCancelled: true);

            Assert.Equal(2, records.Count);
        }

        [Fact]
        public async Task CancelAsync_Should_Cancel_NonTerminal_Record_Atomically()
        {
            var store = CreateStore();

            await store.CreateAsync(
                CreateRecord("shared-run-1", AiSharedRunStatus.QueuedGlobally));

            var cancelled = await store.CancelAsync(
                "shared-run-1",
                reason: "operator cancel",
                requestedBy: "tester",
                source: "unit-test");

            Assert.NotNull(cancelled);
            Assert.Equal(AiSharedRunStatus.Cancelled, cancelled!.Status);
            Assert.Equal("operator cancel", cancelled.Reason);
            Assert.Equal("operator cancel", cancelled.FailureReason);
            Assert.Equal("tester", cancelled.RequestedBy);
            Assert.Equal("unit-test", cancelled.Source);

            var loaded = await store.GetAsync("shared-run-1");

            Assert.NotNull(loaded);
            Assert.Equal(AiSharedRunStatus.Cancelled, loaded!.Status);
        }

        [Theory]
        [InlineData(AiSharedRunStatus.Completed)]
        [InlineData(AiSharedRunStatus.Failed)]
        [InlineData(AiSharedRunStatus.Cancelled)]
        public async Task CancelAsync_Should_Return_Terminal_Record_Without_Changing_Status(
            AiSharedRunStatus terminalStatus)
        {
            var store = CreateStore();

            await store.CreateAsync(
                CreateRecord(
                    "shared-run-1",
                    terminalStatus,
                    failureReason: "existing failure"));

            var result = await store.CancelAsync(
                "shared-run-1",
                reason: "new cancel",
                requestedBy: "tester");

            Assert.NotNull(result);
            Assert.Equal(terminalStatus, result!.Status);
            Assert.Equal("existing failure", result.FailureReason);
        }

        [Fact]
        public async Task CancelAsync_Should_Return_Null_When_Record_Is_Missing()
        {
            var store = CreateStore();

            var cancelled = await store.CancelAsync("missing-run");

            Assert.Null(cancelled);
        }

        [Fact]
        public async Task CreateAsync_Should_Preserve_Metadata()
        {
            var store = CreateStore();

            var record = CreateRecord(
                "shared-run-1",
                AiSharedRunStatus.AssignedToInstance,
                metadata: new Dictionary<string, string>
                {
                    ["tenant"] = "tenant-1",
                    ["priority"] = "high"
                });

            await store.CreateAsync(record);

            var loaded = await store.GetAsync("shared-run-1");

            Assert.NotNull(loaded);
            Assert.Equal("tenant-1", loaded!.Metadata["tenant"]);
            Assert.Equal("high", loaded.Metadata["priority"]);
        }

        [Fact]
        public async Task CreateAsync_Should_Allow_Only_One_Concurrent_Create_For_Same_SharedRunId()
        {
            var store = CreateStore();

            var tasks = Enumerable.Range(0, 20)
                .Select(index =>
                    Task.Run(async () =>
                    {
                        try
                        {
                            var created = await store.CreateAsync(
                                CreateRecord(
                                    "shared-run-concurrent",
                                    AiSharedRunStatus.AssignedToInstance,
                                    metadata: new Dictionary<string, string>
                                    {
                                        ["attempt"] = index.ToString()
                                    }));

                            return (Success: true, Record: created, Exception: (Exception?)null);
                        }
                        catch (Exception exception)
                        {
                            return (Success: false, Record: (AiSharedRunRecord?)null, Exception: exception);
                        }
                    }))
                .ToArray();

            var results = await Task.WhenAll(tasks);

            var successful = results
                .Where(result => result.Success)
                .ToArray();

            var failed = results
                .Where(result => !result.Success)
                .ToArray();

            Assert.Single(successful);
            Assert.Equal(19, failed.Length);

            Assert.All(failed, result =>
            {
                Assert.IsType<InvalidOperationException>(result.Exception);
                Assert.Contains("already exists", result.Exception!.Message);
            });

            var loaded = await store.GetAsync("shared-run-concurrent");

            Assert.NotNull(loaded);
            Assert.Equal("shared-run-concurrent", loaded!.SharedRunId);
        }

        [Fact]
        public async Task CancelAsync_Should_Be_Safe_When_Called_Concurrently()
        {
            var store = CreateStore();

            await store.CreateAsync(
                CreateRecord(
                    "shared-run-cancel-concurrent",
                    AiSharedRunStatus.QueuedGlobally));

            var tasks = Enumerable.Range(0, 20)
                .Select(index =>
                    Task.Run(() =>
                        store.CancelAsync(
                            "shared-run-cancel-concurrent",
                            reason: $"cancel-{index}",
                            requestedBy: $"tester-{index}",
                            source: "unit-test")))
                .ToArray();

            var results = await Task.WhenAll(tasks);

            Assert.All(results, result =>
            {
                Assert.NotNull(result);
                Assert.Equal(AiSharedRunStatus.Cancelled, result!.Status);
            });

            var loaded = await store.GetAsync("shared-run-cancel-concurrent");

            Assert.NotNull(loaded);
            Assert.Equal(AiSharedRunStatus.Cancelled, loaded!.Status);
            Assert.False(string.IsNullOrWhiteSpace(loaded.FailureReason));
            Assert.StartsWith("cancel-", loaded.FailureReason);
        }

        [Fact]
        public async Task MarkDispatchedAsync_Should_Update_NonTerminal_Run()
        {
            var store = CreateStore();

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
        public async Task MarkDispatchedAsync_Should_Return_Null_When_Run_Is_Unknown()
        {
            var store = CreateStore();

            var updated = await store.MarkDispatchedAsync(
                "missing-run",
                runtimeInstanceId: "runtime-1");

            Assert.Null(updated);
        }

        private RedisAiSharedRunStore CreateStore()
        {
            if (_connection is null)
            {
                throw new InvalidOperationException("Redis connection was not initialized.");
            }

            return new RedisAiSharedRunStore(
                _connection,
                Options.Create(new RedisAiSharedRunStoreOptions
                {
                    KeyPrefix = _keyPrefix,
                    ListScanLimit = 100
                }), new StaticAiControlPlaneIdResolver("test-control-plane"));
        }

        private static AiSharedRunRecord CreateRecord(
            string sharedRunId,
            AiSharedRunStatus status,
            DateTimeOffset? submittedAtUtc = null,
            string? failureReason = null,
            IReadOnlyDictionary<string, string>? metadata = null)
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
                FailureReason = failureReason,
                SubmittedAtUtc = now,
                UpdatedAtUtc = now,
                Metadata = metadata ?? new Dictionary<string, string>()
            };
        }
    }
}