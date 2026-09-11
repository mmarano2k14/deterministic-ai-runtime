using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Multiplexed.AI.DI.Persistence.Mongo;
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

        [Fact]
        public void Snapshot_Registration_Should_Not_Dispose_Process_Shared_Client_With_First_ServiceProvider()
        {
            var identity = Guid.NewGuid().ToString("N");
            var connectionString =
                $"mongodb://localhost:27017/?appName=mongo-client-lifetime-{identity}";
            var databaseName = $"mongo_client_lifetime_{identity}";

            MongoClient firstClient;

            var firstServices = new ServiceCollection();
            firstServices.AddLogging();
            firstServices.AddMongoAiExecutionSnapshots<object>(options =>
            {
                options.ConnectionString = connectionString;
                options.DatabaseName = databaseName;
                options.CollectionName = "snapshots";
            });

            using (var firstProvider = firstServices.BuildServiceProvider())
            {
                firstClient = Assert.IsType<MongoClient>(
                    firstProvider.GetRequiredService<IMongoClient>());

                _ = firstProvider.GetRequiredService<IMongoDatabase>();
            }

            // The process-shared client is externally owned by AiMongoClientFactory. Disposing one
            // application ServiceProvider must not poison the static cache for the next test host.
            var cachedAfterFirstHost = AiMongoClientFactory.GetOrCreate(connectionString);
            Assert.Same(firstClient, cachedAfterFirstHost);

            var secondServices = new ServiceCollection();
            secondServices.AddLogging();
            secondServices.AddMongoAiExecutionSnapshots<object>(options =>
            {
                options.ConnectionString = connectionString;
                options.DatabaseName = databaseName;
                options.CollectionName = "snapshots";
            });

            using var secondProvider = secondServices.BuildServiceProvider();
            var secondClient = secondProvider.GetRequiredService<IMongoClient>();
            var secondDatabase = secondProvider.GetRequiredService<IMongoDatabase>();

            Assert.Same(firstClient, secondClient);
            Assert.Equal(databaseName, secondDatabase.DatabaseNamespace.DatabaseName);
        }

        [Fact]
        public void External_Instance_Registration_Should_Survive_ServiceProvider_Disposal()
        {
            var connectionString =
                $"mongodb://localhost:27017/?appName=mongo-client-external-owner-{Guid.NewGuid():N}";

            var sharedClient = AiMongoClientFactory.GetOrCreate(connectionString);
            var services = new ServiceCollection();
            services.AddSingleton<IMongoClient>(sharedClient);

            using (var provider = services.BuildServiceProvider())
            {
                Assert.Same(sharedClient, provider.GetRequiredService<IMongoClient>());
            }

            // GetDatabase does not require a live server connection; it does throw immediately when
            // the MongoClient itself has already been disposed.
            var database = sharedClient.GetDatabase("lifetime-regression");
            Assert.Equal("lifetime-regression", database.DatabaseNamespace.DatabaseName);
        }
    }
}
