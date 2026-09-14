using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.Invocation.Durable;
using Multiplexed.AI.Runtime.Invocation.Durable.DI;
using Multiplexed.AI.Runtime.Invocation.Durable.Mongo;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Durable
{
    /// <summary>Real BSON encoding/filter code without a database; these are not Mongo integration tests.</summary>
    public sealed class AiDurableInvocationMongoContractTests
    {
        [Fact]
        public async Task Bson_Roundtrip_Preserves_Pins_Lease_Result_And_Continuation()
        {
            var (journal, _, _) = DurableInvocationTestSupport.Create();
            var record = await DurableInvocationTestSupport.CompleteAsync(journal);
            var document = DurableInvocationTestSupport.Codec<BsonDocument>("Encode", record);
            Assert.Equal(record, DurableInvocationTestSupport.Codec<AiDurableInvocationRecord>("Decode", document));
            Assert.Equal(record.Definition.Identity.TenantId, document["tenantId"].AsString);
            Assert.Equal(record.Definition.Identity.ExecutionId, document["executionId"].AsString);
            Assert.Equal(record.Definition.Identity.StepName, document["stepName"].AsString);
            Assert.Equal(record.Definition.Identity.Generation, document["generation"].AsInt32);
        }

        [Theory]
        [InlineData("tenantId")]
        [InlineData("executionId")]
        [InlineData("stepName")]
        [InlineData("tenantGroupId")]
        [InlineData("controlPlaneId")]
        [InlineData("language")]
        [InlineData("snapshotSha256")]
        [InlineData("_id")]
        public async Task Index_Projection_Or_Integrity_Tampering_Is_Detected(string field)
        {
            var (journal, _, _) = DurableInvocationTestSupport.Create();
            var record = await DurableInvocationTestSupport.CompleteAsync(journal);
            var document = DurableInvocationTestSupport.Codec<BsonDocument>("Encode", record);
            document[field] = "corrupted";
            Assert.Throws<InvalidOperationException>(() => DurableInvocationTestSupport.Codec<AiDurableInvocationRecord>("Decode", document));
        }

        [Fact]
        public async Task Replacement_Filter_Pins_Revision_Snapshot_Identity_And_Database_Clock()
        {
            var (journal, _, _) = DurableInvocationTestSupport.Create();
            var leased = await DurableInvocationTestSupport.LeaseAsync(journal);
            await journal.CompleteAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity,
                leased.Lease!, DurableInvocationTestSupport.Result());
            var completed = (await journal.GetAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity))!;
            var filter = DurableInvocationTestSupport.Codec<BsonDocument>("ReplacementFilter", leased, completed);
            Assert.Equal(leased.Revision, filter["revision"].AsInt64);
            Assert.True(filter.Contains("snapshotSha256"));
            Assert.Equal(leased.Definition.Identity.StepName, filter["stepName"].AsString);
            var condition = filter["$expr"]["$and"].AsBsonArray[0]["$gt"].AsBsonArray;
            Assert.Equal("$leaseExpiresAt", condition[0].AsString);
            Assert.Equal("$$NOW", condition[1]["$toLong"].AsString);
        }

        [Fact]
        public async Task Replacement_Lease_Uses_Database_Expiry_And_New_Lease_Bounds()
        {
            var (journal, _, clock) = DurableInvocationTestSupport.Create();
            var leased = await DurableInvocationTestSupport.LeaseAsync(journal);
            clock.Advance(TimeSpan.FromSeconds(30));
            var next = (await journal.TryAcquireLeaseAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity,
                "worker-b", TimeSpan.FromSeconds(30)))!;
            var filter = DurableInvocationTestSupport.Codec<BsonDocument>("ReplacementFilter", leased, next);
            var conditions = filter["$expr"]["$and"].AsBsonArray;
            Assert.Equal(3, conditions.Count);
            Assert.Equal("$leaseExpiresAt", conditions[0]["$lte"].AsBsonArray[0].AsString);
            Assert.Equal("$$NOW", conditions[0]["$lte"].AsBsonArray[1]["$toLong"].AsString);
            Assert.Equal(300000L, conditions[2]["$lte"].AsBsonArray[1]["$add"].AsBsonArray[1].AsInt64);
        }

        [Theory]
        [InlineData("schema")]
        [InlineData("operation")]
        [InlineData("input-hash")]
        [InlineData("result-hash")]
        [InlineData("state")]
        public async Task Corrupted_Snapshots_Fail_Before_An_Invocation_Is_Returned(string field)
        {
            var (journal, store, _) = DurableInvocationTestSupport.Create();
            await DurableInvocationTestSupport.CompleteAsync(journal);
            store.Corrupt(record => field switch
            {
                "schema" => record with { SchemaVersion = 99 },
                "operation" => record with { OperationId = "other" },
                "input-hash" => record with { InputsSha256 = "other" },
                "result-hash" => record with { ResultSha256 = "other" },
                _ => record with { Status = (AiDurableInvocationStatus)99 }
            });
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.GetAsync(
                DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity));
        }

        [Fact]
        public void Explicit_Registration_Does_Not_Install_Executable_Capabilities_Or_Hosted_Services()
        {
            var services = new ServiceCollection();
            var clock = new DurableInvocationTestSupport.Clock();
            var store = new DurableInvocationTestSupport.MemoryStore(clock);
            services.AddSingleton<IAiDurableInvocationStore>(store);
            services.AddSingleton<TimeProvider>(clock);
            services.AddAiDurableInvocationJournal();
            services.AddAiDurableInvocationJournal();
            Assert.Single(services.Where(item => item.ServiceType == typeof(AiDurableInvocationJournal)));
            Assert.DoesNotContain(services, item => item.ServiceType == typeof(IAiStep) || item.ServiceType == typeof(IAiPolicy) ||
                item.ServiceType == typeof(IAiStepInvocationAdapterFactory) || item.ServiceType == typeof(IHostedService));
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
            Assert.Same(store, provider.GetRequiredService<IAiDurableInvocationStore>());
            Assert.NotNull(provider.GetRequiredService<AiDurableInvocationJournal>());
        }

        [Fact]
        public void Missing_Mongo_Database_Is_Not_Replaced_By_An_InMemory_Production_Store()
        {
            Assert.Throws<ArgumentNullException>(() => new MongoAiDurableInvocationStore(null!,
                Options.Create(new AiDurableInvocationMongoOptions())));
        }
    }
}
