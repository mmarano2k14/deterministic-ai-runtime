using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Lifecycle;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Lifecycle
{
    /// <summary>
    /// Tests runtime lifecycle journal dependency-injection registration.
    /// </summary>
    public sealed class AiRuntimeLifecycleJournalServiceCollectionExtensionsTests
    {
        [Fact]
        public void AddInMemoryAiRuntimeLifecycleJournal_Should_Register_One_Journal()
        {
            var services = new ServiceCollection();

            services.AddInMemoryAiRuntimeLifecycleJournal();

            var descriptor = Assert.Single(
                services.Where(service =>
                    service.ServiceType == typeof(IAiRuntimeLifecycleJournal)));

            Assert.Equal(typeof(InMemoryAiRuntimeLifecycleJournal), descriptor.ImplementationType);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        }

        [Fact]
        public void AddMongoAiRuntimeLifecycleJournal_Should_Use_Existing_Database_Registration()
        {
            var services = new ServiceCollection();
            var client = new MongoClient("mongodb://localhost:27017");
            var database = client.GetDatabase($"runtime_lifecycle_di_{Guid.NewGuid():N}");

            services.AddSingleton(database);
            services.AddMongoAiRuntimeLifecycleJournal(options =>
            {
                options.CollectionName = "runtime_lifecycle_di_tests";
                options.EnsureIndexes = false;
            });

            using var provider = services.BuildServiceProvider();
            var journal = provider.GetRequiredService<IAiRuntimeLifecycleJournal>();

            Assert.IsType<MongoAiRuntimeLifecycleJournal>(journal);
        }
    }
}
