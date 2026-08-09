using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Failure
{
    /// <summary>
    /// Validates runtime-pool failure journal dependency-injection composition.
    /// </summary>
    public sealed class RuntimePoolFailureJournalServiceCollectionExtensionsTests
    {
        [Fact]
        public void AddInMemory_Should_Register_Combined_Journal_Authority()
        {
            var services = new ServiceCollection();

            services.AddInMemoryAiRuntimePoolFailureJournal();

            using var provider = services.BuildServiceProvider();
            var journal =
                provider.GetRequiredService<IAiRuntimePoolFailureJournal>();

            Assert.IsType<InMemoryAiRuntimePoolFailureJournal>(journal);
            Assert.IsAssignableFrom<IAiRuntimePoolFailureObserver>(journal);
            Assert.IsAssignableFrom<IAiRuntimePoolFailureReader>(journal);
        }

        [Fact]
        public void AddMongo_Should_Use_Existing_Database_Registration()
        {
            var services = new ServiceCollection();
            var client = new MongoClient("mongodb://localhost:27017");
            var database =
                client.GetDatabase(
                    $"runtime_pool_failure_di_{Guid.NewGuid():N}");

            services.AddSingleton(database);
            services.AddMongoAiRuntimePoolFailureJournal(options =>
            {
                options.CollectionName = "runtime_pool_failure_di_tests";
                options.EnsureIndexes = false;
            });

            using var provider = services.BuildServiceProvider();
            var journal =
                provider.GetRequiredService<IAiRuntimePoolFailureJournal>();

            Assert.IsType<MongoAiRuntimePoolFailureJournal>(journal);
        }

        [Fact]
        public void AddMongo_WithExplicitAuthority_Should_Not_Replace_Ambient_Database_Registration()
        {
            var services = new ServiceCollection();
            var client = new MongoClient("mongodb://localhost:27017");
            var ambientDatabase =
                client.GetDatabase(
                    $"runtime_pool_failure_ambient_{Guid.NewGuid():N}");

            services.AddSingleton<IMongoDatabase>(ambientDatabase);
            services.AddMongoAiRuntimePoolFailureJournal(
                "mongodb://localhost:27017",
                $"runtime_pool_failure_authority_{Guid.NewGuid():N}",
                options =>
                {
                    options.CollectionName =
                        "runtime_pool_failure_explicit_authority_tests";
                    options.EnsureIndexes = false;
                });

            using var provider = services.BuildServiceProvider();
            var journal =
                provider.GetRequiredService<IAiRuntimePoolFailureJournal>();
            var resolvedAmbientDatabase =
                provider.GetRequiredService<IMongoDatabase>();

            Assert.IsType<MongoAiRuntimePoolFailureJournal>(journal);
            Assert.Same(ambientDatabase, resolvedAmbientDatabase);
        }
    }
}
