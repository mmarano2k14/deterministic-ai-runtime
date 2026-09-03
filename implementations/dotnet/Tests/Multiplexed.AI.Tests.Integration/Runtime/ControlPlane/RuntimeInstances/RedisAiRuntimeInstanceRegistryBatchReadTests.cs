using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;
using Multiplexed.AI.Tests.Fixtures;
using NSubstitute;
using StackExchange.Redis;
using System.Net;
using System.Text.Json;

namespace Multiplexed.AI.Tests.Integration.Runtime.ControlPlane.RuntimeInstances
{
    public sealed class RedisAiRuntimeInstanceRegistryBatchReadTests
    {
        private const string ControlPlaneId = "perf1-control-plane";
        private const string InstanceSetKey = "ai:control-plane:perf1-control-plane:runtime-instances";

        private static readonly JsonSerializerOptions JsonOptions =
            new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task ListAsync_NonCluster_Should_Load_Indexed_Entries_With_One_MGet()
        {
            var fixture = CreateFixture(ServerType.Standalone);
            var runtimeInstanceId1 = "runtime-001";
            var runtimeInstanceId2 = "runtime-002";
            var missingRuntimeInstanceId = "runtime-missing";
            var entryKey1 = GetInstanceKey(runtimeInstanceId1);
            var entryKey2 = GetInstanceKey(runtimeInstanceId2);
            var missingEntryKey = GetInstanceKey(missingRuntimeInstanceId);

            fixture.Database
                .SetMembersAsync((RedisKey)InstanceSetKey, CommandFlags.None)
                .Returns(Task.FromResult(
                    new RedisValue[]
                    {
                        runtimeInstanceId2,
                        missingRuntimeInstanceId,
                        runtimeInstanceId1
                    }));
            fixture.Database
                .StringGetAsync(
                    Arg.Is<RedisKey[]>(keys =>
                        keys.Length == 3 &&
                        keys[0] == (RedisKey)entryKey2 &&
                        keys[1] == (RedisKey)missingEntryKey &&
                        keys[2] == (RedisKey)entryKey1),
                    CommandFlags.None)
                .Returns(Task.FromResult(
                    new RedisValue[]
                    {
                        SerializeEntry(runtimeInstanceId2),
                        RedisValue.Null,
                        SerializeEntry(runtimeInstanceId1)
                    }));

            var actual =
                await fixture.Registry.ListAsync();

            Assert.Equal(
                new[]
                {
                    runtimeInstanceId1,
                    runtimeInstanceId2
                },
                actual.Select(snapshot => snapshot.RuntimeInstanceId));
            await fixture.Database.Received(1).StringGetAsync(
                Arg.Is<RedisKey[]>(keys =>
                    keys.Length == 3 &&
                    keys[0] == (RedisKey)entryKey2 &&
                    keys[1] == (RedisKey)missingEntryKey &&
                    keys[2] == (RedisKey)entryKey1),
                CommandFlags.None);
            await fixture.Database.DidNotReceive().StringGetAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<CommandFlags>());
            await fixture.Database.Received(1).SetRemoveAsync(
                (RedisKey)InstanceSetKey,
                (RedisValue)missingRuntimeInstanceId,
                CommandFlags.None);
        }

        [Fact]
        public async Task ListAsync_Cluster_Should_Preserve_Individual_Get_Fallback()
        {
            var fixture = CreateFixture(ServerType.Cluster);
            var runtimeInstanceId1 = "runtime-001";
            var runtimeInstanceId2 = "runtime-002";
            var entryKey1 = GetInstanceKey(runtimeInstanceId1);
            var entryKey2 = GetInstanceKey(runtimeInstanceId2);

            fixture.Database
                .SetMembersAsync((RedisKey)InstanceSetKey, CommandFlags.None)
                .Returns(Task.FromResult(
                    new RedisValue[]
                    {
                        runtimeInstanceId1,
                        runtimeInstanceId2
                    }));
            fixture.Database
                .StringGetAsync((RedisKey)entryKey1, CommandFlags.None)
                .Returns(Task.FromResult<RedisValue>(
                    SerializeEntry(runtimeInstanceId1)));
            fixture.Database
                .StringGetAsync((RedisKey)entryKey2, CommandFlags.None)
                .Returns(Task.FromResult<RedisValue>(
                    SerializeEntry(runtimeInstanceId2)));

            var actual =
                await fixture.Registry.ListAsync();

            Assert.Equal(2, actual.Count);
            await fixture.Database.Received(1).StringGetAsync(
                (RedisKey)entryKey1,
                CommandFlags.None);
            await fixture.Database.Received(1).StringGetAsync(
                (RedisKey)entryKey2,
                CommandFlags.None);
            await fixture.Database.DidNotReceive().StringGetAsync(
                Arg.Any<RedisKey[]>(),
                Arg.Any<CommandFlags>());
        }

        private static RegistryFixture CreateFixture(
            ServerType serverType)
        {
            var multiplexer = Substitute.For<IConnectionMultiplexer>();
            var database = Substitute.For<IDatabase>();
            var server = Substitute.For<IServer>();
            var endpoint = new DnsEndPoint("localhost", 6379);

            multiplexer.GetDatabase().Returns(database);
            multiplexer.GetEndPoints().Returns(new EndPoint[] { endpoint });
            multiplexer.GetServer(endpoint).Returns(server);
            server.ServerType.Returns(serverType);

            var registry =
                new RedisAiRuntimeInstanceRegistry(
                    multiplexer,
                    Options.Create(new AiRuntimeInstanceRegistrationOptions()),
                    new StaticAiControlPlaneIdResolver(ControlPlaneId));

            return new RegistryFixture(
                registry,
                database);
        }

        private static RedisValue SerializeEntry(
            string runtimeInstanceId)
        {
            var entry =
                RuntimeInstanceEntry.Create(
                    new AiRuntimeInstanceRegistration
                    {
                        RuntimeInstanceId = runtimeInstanceId,
                        ControlPlaneId = ControlPlaneId,
                        HostId = "host-001",
                        PoolId = "pool-001",
                        RuntimeId = runtimeInstanceId,
                        Role = AiRuntimeInstanceRole.Runtime,
                        WorkerCount = 4,
                        MaxConcurrentRuns = 2,
                        Metadata = new Dictionary<string, string>()
                    },
                    DateTimeOffset.UtcNow);

            return JsonSerializer.Serialize(entry, JsonOptions);
        }

        private static string GetInstanceKey(
            string runtimeInstanceId)
        {
            return $"ai:control-plane:perf1-control-plane:runtime-instance:{runtimeInstanceId}";
        }

        private sealed record RegistryFixture(
            RedisAiRuntimeInstanceRegistry Registry,
            IDatabase Database);
    }
}
