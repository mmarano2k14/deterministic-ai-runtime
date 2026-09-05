using Multiplexed.AI.Stores.Mongo;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Xunit;

namespace Multiplexed.AI.Tests.Runtime.Observability.Performance
{
    public sealed class AiMongoClientFactoryTests
    {
        [Fact]
        public void Same_ConnectionString_Should_Reuse_Exact_Client_Instance()
        {
            var connectionString =
                $"mongodb://localhost:27017/?appName=perf2-pool-reuse-{Guid.NewGuid():N}";

            var first = AiMongoClientFactory.GetOrCreate(connectionString);
            var second = AiMongoClientFactory.GetOrCreate(connectionString);

            Assert.Same(first, second);
        }

        [Fact]
        public void Leading_And_Trailing_Whitespace_Should_Reuse_Exact_Client_Instance()
        {
            var connectionString =
                $"mongodb://localhost:27017/?appName=perf2-pool-trim-{Guid.NewGuid():N}";

            var first = AiMongoClientFactory.GetOrCreate(connectionString);
            var second = AiMongoClientFactory.GetOrCreate($"  {connectionString}  ");

            Assert.Same(first, second);
        }

        [Fact]
        public void Different_ConnectionConfiguration_Should_Remain_Isolated()
        {
            var identity = Guid.NewGuid().ToString("N");
            var first = AiMongoClientFactory.GetOrCreate(
                $"mongodb://localhost:27017/?appName=perf2-pool-a-{identity}");
            var second = AiMongoClientFactory.GetOrCreate(
                $"mongodb://localhost:27017/?appName=perf2-pool-b-{identity}");

            Assert.NotSame(first, second);
        }

        [Fact]
        public void Concurrent_Requests_Should_Converge_On_One_Client_Instance()
        {
            var connectionString =
                $"mongodb://localhost:27017/?appName=perf2-pool-concurrency-{Guid.NewGuid():N}";
            var clients = new ConcurrentBag<MongoDB.Driver.MongoClient>();

            Parallel.For(
                0,
                128,
                _ => clients.Add(AiMongoClientFactory.GetOrCreate(connectionString)));

            var expected = AiMongoClientFactory.GetOrCreate(connectionString);
            Assert.All(clients, client => Assert.Same(expected, client));
        }
    }
}
