using FluentAssertions;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.AI.Runtime.Execution.Normalization;
using Multiplexed.AI.Runtime.Observability.Logging;
using Multiplexed.AI.Stores.Cache.Redis;
using NSubstitute;
using StackExchange.Redis;
using System.Net;
using System.Text.Json;

namespace Multiplexed.AI.Tests.Integration.Runtime.Execution
{
    public sealed class RedisDagStoreStateReaderBatchReadTests
    {
        [Fact]
        public async Task GetStateAsync_NonCluster_Should_Load_Record_And_StateBlob_With_One_MGet()
        {
            var fixture = CreateFixture(ServerType.Standalone);
            var executionId = Guid.NewGuid().ToString("N");
            var recordKey = fixture.KeyBuilder.GetExecutionRecordKey(executionId);
            var stateKey = recordKey + ":state";
            var stepIndexKey = fixture.KeyBuilder.GetDagStepIdsKey(executionId);
            var record = CreateRecord(executionId);
            var state = CreateState(executionId);

            fixture.Database
                .StringGetAsync(
                    Arg.Is<RedisKey[]>(keys =>
                        keys.Length == 2 &&
                        keys[0] == (RedisKey)recordKey &&
                        keys[1] == (RedisKey)stateKey),
                    CommandFlags.None)
                .Returns(Task.FromResult(
                    new RedisValue[]
                    {
                        JsonSerializer.Serialize(record, fixture.Services.JsonOptions),
                        JsonSerializer.Serialize(state, fixture.Services.JsonOptions)
                    }));
            fixture.Database
                .SetMembersAsync(stepIndexKey, CommandFlags.None)
                .Returns(Task.FromResult(Array.Empty<RedisValue>()));

            var actual = await fixture.Services.StateReader.GetStateAsync(executionId);

            actual.Should().NotBeNull();
            actual!.ExecutionId.Should().Be(executionId);
            actual.PipelineName.Should().Be(state.PipelineName);

            await fixture.Database.Received(1).StringGetAsync(
                Arg.Is<RedisKey[]>(keys =>
                    keys.Length == 2 &&
                    keys[0] == (RedisKey)recordKey &&
                    keys[1] == (RedisKey)stateKey),
                CommandFlags.None);
            await fixture.Database.DidNotReceive().StringGetAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<CommandFlags>());
        }

        [Fact]
        public async Task GetStateAsync_Cluster_Should_Preserve_Individual_Get_Fallback()
        {
            var fixture = CreateFixture(ServerType.Cluster);
            var executionId = Guid.NewGuid().ToString("N");
            var recordKey = fixture.KeyBuilder.GetExecutionRecordKey(executionId);
            var stateKey = recordKey + ":state";
            var stepIndexKey = fixture.KeyBuilder.GetDagStepIdsKey(executionId);
            var record = CreateRecord(executionId);
            var state = CreateState(executionId);

            fixture.Database
                .StringGetAsync(recordKey, CommandFlags.None)
                .Returns(Task.FromResult<RedisValue>(
                    JsonSerializer.Serialize(record, fixture.Services.JsonOptions)));
            fixture.Database
                .StringGetAsync(stateKey, CommandFlags.None)
                .Returns(Task.FromResult<RedisValue>(
                    JsonSerializer.Serialize(state, fixture.Services.JsonOptions)));
            fixture.Database
                .SetMembersAsync(stepIndexKey, CommandFlags.None)
                .Returns(Task.FromResult(Array.Empty<RedisValue>()));

            var actual = await fixture.Services.StateReader.GetStateAsync(executionId);

            actual.Should().NotBeNull();
            actual!.ExecutionId.Should().Be(executionId);
            actual.PipelineName.Should().Be(state.PipelineName);

            await fixture.Database.Received(1).StringGetAsync(
                (RedisKey)recordKey,
                CommandFlags.None);
            await fixture.Database.Received(1).StringGetAsync(
                (RedisKey)stateKey,
                CommandFlags.None);
            await fixture.Database.DidNotReceive().StringGetAsync(
                Arg.Any<RedisKey[]>(),
                Arg.Any<CommandFlags>());
        }

        private static ReaderFixture CreateFixture(
            ServerType serverType)
        {
            var multiplexer = Substitute.For<IConnectionMultiplexer>();
            var database = Substitute.For<IDatabase>();
            var server = Substitute.For<IServer>();
            var endpoint = new DnsEndPoint("localhost", 6379);
            var keyBuilder = new TestExecutionKeyBuilder();

            multiplexer.GetDatabase().Returns(database);
            multiplexer.GetEndPoints().Returns(new EndPoint[] { endpoint });
            multiplexer.GetServer(endpoint).Returns(server);
            server.ServerType.Returns(serverType);

            var services = new RedisDagStoreServices(
                multiplexer,
                keyBuilder,
                Substitute.For<IAiRuntimeLogger>(),
                Substitute.For<IAiRuntimeMetrics>(),
                Substitute.For<IAiStepResultNormalizerPipeline>());

            return new ReaderFixture(
                services,
                database,
                keyBuilder);
        }

        private static AiExecutionRecord CreateRecord(
            string executionId)
        {
            return new AiExecutionRecord
            {
                ExecutionId = executionId,
                PipelineName = "perf1-record-state-mget",
                ExecutionMode = AiExecutionMode.Dag,
                CompletedSteps = new List<string>()
            };
        }

        private static AiExecutionState CreateState(
            string executionId)
        {
            return new AiExecutionState
            {
                ExecutionId = executionId,
                PipelineName = "perf1-record-state-mget"
            };
        }

        private sealed record ReaderFixture(
            RedisDagStoreServices Services,
            IDatabase Database,
            TestExecutionKeyBuilder KeyBuilder);

        private sealed class TestExecutionKeyBuilder : IAiExecutionKeyBuilder
        {
            public string GetExecutionRecordKey(string executionId)
                => $"perf1:test:record:{executionId}";

            public string GetExecutionStateKey(string executionId)
                => $"perf1:test:state:{executionId}";

            public string GetDagStepIdsKey(string executionId)
                => $"perf1:test:steps:{executionId}";

            public string GetDagStepKey(string executionId, string stepId)
                => $"{GetDagStepKeyPrefix(executionId)}{stepId}";

            public string GetDagClaimKey(string executionId, string stepId)
                => $"perf1:test:claim:{executionId}:{stepId}";

            public string GetDagLeaseKey(string executionId, string stepId)
                => $"perf1:test:lease:{executionId}:{stepId}";

            public string GetDagInFlightKey(string executionId)
                => $"perf1:test:inflight:{executionId}";

            public string GetDagMetaKey(string executionId)
                => $"perf1:test:meta:{executionId}";

            public string GetDagStepKeyPrefix(string executionId)
                => $"perf1:test:step:{executionId}:";
        }
    }
}
