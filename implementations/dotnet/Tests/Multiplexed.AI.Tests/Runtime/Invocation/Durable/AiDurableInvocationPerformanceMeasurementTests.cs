using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.AI.Runtime.Invocation.Durable;
using Multiplexed.AI.Runtime.Invocation.Durable.Mongo;
using Xunit.Abstractions;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Durable
{
    [CollectionDefinition(CollectionName, DisableParallelization = true)]
    public sealed class AiDurableInvocationPerformanceMeasurementCollection
    {
        public const string CollectionName = "AiDurableInvocationPerformanceMeasurement";
    }

    /// <summary>
    /// Measurement-only performance probes. They establish reproducible baselines and
    /// deliberately avoid performance thresholds so that optimization decisions remain
    /// evidence-driven across different machines and MongoDB deployments.
    /// </summary>
    [Collection(AiDurableInvocationPerformanceMeasurementCollection.CollectionName)]
    public sealed class AiDurableInvocationPerformanceMeasurementTests
    {
        private readonly ITestOutputHelper _output;

        public AiDurableInvocationPerformanceMeasurementTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void Temporary_Mongo_Database_Names_Stay_Within_Server_Limit()
        {
            const string prefix = "sdk_invocation_index_experiment";
            const int maxMongoDatabaseNameLength = 63;
            const int guidSuffixLength = 32;
            var expectedPrefixLength = Math.Min(prefix.Length, maxMongoDatabaseNameLength - guidSuffixLength - 1);
            var expectedPrefix = prefix[..expectedPrefixLength];

            var first = CreateTemporaryDatabaseName(prefix);
            var second = CreateTemporaryDatabaseName(prefix);

            Assert.InRange(first.Length, 1, maxMongoDatabaseNameLength);
            Assert.InRange(second.Length, 1, maxMongoDatabaseNameLength);
            Assert.StartsWith(expectedPrefix + "_", first, StringComparison.Ordinal);
            Assert.StartsWith(expectedPrefix + "_", second, StringComparison.Ordinal);
            Assert.True(Guid.TryParseExact(first[^guidSuffixLength..], "N", out _));
            Assert.True(Guid.TryParseExact(second[^guidSuffixLength..], "N", out _));
            Assert.NotEqual(first, second);
        }

        [Fact]
        [Trait("Category", "InvocationPerformanceMeasurement")]
        public async Task Codec_Benchmark_Reports_Encode_And_Decode_Costs()
        {
            var record = await CreateCompletedRecordAsync("codec", 0).ConfigureAwait(false);
            var codecType = typeof(MongoAiDurableInvocationStore).Assembly.GetType(
                "Multiplexed.AI.Runtime.Invocation.Durable.Mongo.AiDurableInvocationMongoCodec", throwOnError: true)!;
            var encode = codecType.GetMethod("Encode", BindingFlags.Static | BindingFlags.NonPublic)!
                .CreateDelegate<Func<AiDurableInvocationRecord, BsonDocument>>();
            var decode = codecType.GetMethod("Decode", BindingFlags.Static | BindingFlags.NonPublic)!
                .CreateDelegate<Func<BsonDocument, AiDurableInvocationRecord>>();

            var iterations = ReadPositiveInt("MULTIPLEXED_INVOCATION_CODEC_BENCHMARK_ITERATIONS", 10_000, 1_000_000);
            var warmup = Math.Min(1_000, Math.Max(100, iterations / 10));
            BsonDocument? encoded = null;
            AiDurableInvocationRecord? decoded = null;

            for (var index = 0; index < warmup; index++)
            {
                encoded = encode(record);
                decoded = decode(encoded);
            }
            Assert.Equal(record, decoded);

            var stableDocument = encode(record);
            Assert.Equal(record, decode(stableDocument));

            WriteMetric("codec.iterations", iterations);
            WriteMetric("codec.snapshot.bytes", stableDocument["snapshot"].AsString.Length);
            WriteMetric("codec.bson.bytes", stableDocument.ToBson().Length);

            var encodeResult = MeasureResult.From(iterations, record, encode);
            var decodeResult = MeasureResult.From(iterations, stableDocument, decode);
            WriteResult("encode", encodeResult);
            WriteResult("decode", decodeResult);
            WriteMetric("codec.decode_to_encode.time_ratio", decodeResult.NanosecondsPerOperation / encodeResult.NanosecondsPerOperation);
            WriteMetric("codec.decode_to_encode.allocation_ratio", SafeRatio(decodeResult.BytesPerOperation, encodeResult.BytesPerOperation));
        }

        [DurableInvocationMongoFact]
        [Trait("Category", "InvocationPerformanceMeasurement")]
        public async Task Mongo_Query_Explain_Reports_Dispatch_And_Continuation_Plans()
        {
            var connectionString = Environment.GetEnvironmentVariable(DurableInvocationMongoFactAttribute.ConnectionVariable)!;
            var commands = new ConcurrentQueue<BsonDocument>();
            var settings = MongoClientSettings.FromConnectionString(connectionString);
            settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(evt =>
            {
                if (string.Equals(evt.CommandName, "find", StringComparison.Ordinal))
                    commands.Enqueue(evt.Command.DeepClone().AsBsonDocument);
            });

            var client = new MongoClient(settings);
            var databaseName = CreateTemporaryDatabaseName("sdk_invocation_perf");
            var database = client.GetDatabase(databaseName);
            const string collectionName = "invocations";
            var collection = database.GetCollection<BsonDocument>(collectionName);

            try
            {
                var documentCount = ReadPositiveInt("MULTIPLEXED_INVOCATION_EXPLAIN_DOCUMENTS", 2_000, 50_000);
                var documents = new List<BsonDocument>(documentCount);
                var dispatchCount = documentCount / 2;
                for (var index = 0; index < dispatchCount; index++)
                    documents.Add(DurableInvocationTestSupport.Codec<BsonDocument>("Encode", CreatePreparedRecord("dispatch", index)));
                for (var index = dispatchCount; index < documentCount; index++)
                    documents.Add(DurableInvocationTestSupport.Codec<BsonDocument>("Encode",
                        await CreateCompletedRecordAsync("continuation", index).ConfigureAwait(false)));

                await collection.InsertManyAsync(documents, new InsertManyOptions { IsOrdered = false }).ConfigureAwait(false);

                var store = new MongoAiDurableInvocationStore(database,
                    Options.Create(new AiDurableInvocationMongoOptions { CollectionName = collectionName }));

                while (commands.TryDequeue(out _)) { }
                var dispatch = await store.ListDispatchPageAsync(
                    DurableInvocationTestSupport.Scope, "python", DateTimeOffset.UtcNow, 100).ConfigureAwait(false);
                Assert.NotEmpty(dispatch);
                var dispatchPreparedCommand = TakeFindByStatus(
                    commands, collectionName, AiDurableInvocationStatus.Prepared);
                var dispatchExpiredLeaseCommand = TakeFindByStatus(
                    commands, collectionName, AiDurableInvocationStatus.Leased);

                while (commands.TryDequeue(out _)) { }
                var continuation = await store.ListContinuationPageAsync(
                    DurableInvocationTestSupport.Scope, 100).ConfigureAwait(false);
                Assert.NotEmpty(continuation);
                var continuationCommand = TakeLastFind(commands, collectionName);

                Assert.Equal("ix_durable_invocation_dispatch_prepared_v2", CapturedHint(dispatchPreparedCommand));
                Assert.Equal("ix_durable_invocation_dispatch", CapturedHint(dispatchExpiredLeaseCommand));
                Assert.Equal("ix_durable_invocation_continuation_v2", CapturedHint(continuationCommand));

                // The production dispatch call above must remain decodable, so lease-distribution
                // probe documents are inserted only after command capture. They exercise the exact
                // captured expired-lease filter/hint without becoming runtime records.
                var queryNow = DateTimeOffset.UtcNow;
                var leaseProbeCount = Math.Max(1_000, documentCount / 4);
                var leaseProbeDocuments = new List<BsonDocument>(leaseProbeCount * 2);
                for (var index = 0; index < leaseProbeCount; index++)
                {
                    leaseProbeDocuments.Add(SyntheticDispatchDocument(
                        1_000_000 + index, AiDurableInvocationStatus.Leased,
                        queryNow.AddHours(-4).AddMilliseconds(index), queryNow.AddMinutes(-30)));
                    leaseProbeDocuments.Add(SyntheticDispatchDocument(
                        2_000_000 + index, AiDurableInvocationStatus.Leased,
                        queryNow.AddHours(-3).AddMilliseconds(index), queryNow.AddMinutes(30)));
                }
                await collection.InsertManyAsync(leaseProbeDocuments,
                    new InsertManyOptions { IsOrdered = false }).ConfigureAwait(false);

                var dispatchPreparedExplain = await ExplainAsync(database, dispatchPreparedCommand).ConfigureAwait(false);
                var dispatchExpiredLeaseExplain = await ExplainAsync(database, dispatchExpiredLeaseCommand).ConfigureAwait(false);
                var continuationExplain = await ExplainAsync(database, continuationCommand).ConfigureAwait(false);

                WriteMetric("mongo.dispatch.prepared.hint", CapturedHint(dispatchPreparedCommand));
                WriteMetric("mongo.dispatch.expired_leased.hint", CapturedHint(dispatchExpiredLeaseCommand));
                WriteMetric("mongo.continuation.hint", CapturedHint(continuationCommand));
                WriteMetric("mongo.dispatch.expired_leased.probe_expired", leaseProbeCount);
                WriteMetric("mongo.dispatch.expired_leased.probe_live", leaseProbeCount);
                WriteExplain("dispatch.prepared", dispatchPreparedExplain);
                WriteExplain("dispatch.expired_leased", dispatchExpiredLeaseExplain);
                WriteExplain("continuation", continuationExplain);

                Assert.True(dispatchPreparedExplain.Contains("executionStats"));
                Assert.True(dispatchExpiredLeaseExplain.Contains("executionStats"));
                Assert.True(continuationExplain.Contains("executionStats"));
            }
            finally
            {
                await client.DropDatabaseAsync(databaseName).ConfigureAwait(false);
            }
        }

        [DurableInvocationMongoFact]
        [Trait("Category", "InvocationPerformanceMeasurement")]
        public async Task Mongo_Index_Candidate_Experiment_Reports_Hinted_Dispatch_And_Continuation_Plans()
        {
            var connectionString = Environment.GetEnvironmentVariable(DurableInvocationMongoFactAttribute.ConnectionVariable)!;
            var commands = new ConcurrentQueue<BsonDocument>();
            var settings = MongoClientSettings.FromConnectionString(connectionString);
            settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(evt =>
            {
                if (string.Equals(evt.CommandName, "find", StringComparison.Ordinal))
                    commands.Enqueue(evt.Command.DeepClone().AsBsonDocument);
            });

            var client = new MongoClient(settings);
            var databaseName = CreateTemporaryDatabaseName("sdk_invocation_index_experiment");
            var database = client.GetDatabase(databaseName);
            const string collectionName = "invocations";
            var collection = database.GetCollection<BsonDocument>(collectionName);

            try
            {
                var documentCount = ReadPositiveInt("MULTIPLEXED_INVOCATION_EXPLAIN_DOCUMENTS", 10_000, 50_000);
                var documents = new List<BsonDocument>(documentCount);
                var dispatchCount = documentCount / 2;
                for (var index = 0; index < dispatchCount; index++)
                    documents.Add(DurableInvocationTestSupport.Codec<BsonDocument>("Encode", CreatePreparedRecord("dispatch-index", index)));
                for (var index = dispatchCount; index < documentCount; index++)
                    documents.Add(DurableInvocationTestSupport.Codec<BsonDocument>("Encode",
                        await CreateCompletedRecordAsync("continuation-index", index).ConfigureAwait(false)));

                await collection.InsertManyAsync(documents, new InsertManyOptions { IsOrdered = false }).ConfigureAwait(false);

                var store = new MongoAiDurableInvocationStore(database,
                    Options.Create(new AiDurableInvocationMongoOptions { CollectionName = collectionName }));

                // Force production index creation. Candidate experiments retain the historical
                // single-query OR shape explicitly so measurements remain comparable after H5.
                while (commands.TryDequeue(out _)) { }
                Assert.NotEmpty(await store.ListDispatchPageAsync(
                    DurableInvocationTestSupport.Scope, "python", DateTimeOffset.UtcNow, 100).ConfigureAwait(false));
                var dispatchCommand = LegacyDispatchFindCommand(collectionName, DateTimeOffset.UtcNow);

                while (commands.TryDequeue(out _)) { }
                Assert.NotEmpty(await store.ListContinuationPageAsync(
                    DurableInvocationTestSupport.Scope, 100).ConfigureAwait(false));
                var continuationCommand = TakeLastFind(commands, collectionName);

                const string dispatchSortFirst = "ix_perf_dispatch_sort_first";
                const string dispatchExpiryFirst = "ix_perf_dispatch_expiry_first";
                const string continuationSortFirst = "ix_perf_continuation_sort_first";
                const string continuationFilterFirst = "ix_perf_continuation_filter_first";
                const string continuationStatusFirst = "ix_perf_continuation_status_first";

                await collection.Indexes.CreateManyAsync(new[]
                {
                    new CreateIndexModel<BsonDocument>(new BsonDocument
                    {
                        { "controlPlaneId", 1 }, { "tenantId", 1 }, { "tenantGroupId", 1 },
                        { "language", 1 }, { "status", 1 }, { "updatedAt", 1 }, { "_id", 1 }, { "leaseExpiresAt", 1 }
                    }, new CreateIndexOptions { Name = dispatchSortFirst, Collation = OrdinalCollation() }),
                    new CreateIndexModel<BsonDocument>(new BsonDocument
                    {
                        { "controlPlaneId", 1 }, { "tenantId", 1 }, { "tenantGroupId", 1 },
                        { "language", 1 }, { "status", 1 }, { "leaseExpiresAt", 1 }, { "updatedAt", 1 }, { "_id", 1 }
                    }, new CreateIndexOptions { Name = dispatchExpiryFirst, Collation = OrdinalCollation() }),
                    new CreateIndexModel<BsonDocument>(new BsonDocument
                    {
                        { "controlPlaneId", 1 }, { "tenantId", 1 }, { "tenantGroupId", 1 },
                        { "updatedAt", 1 }, { "_id", 1 }, { "status", 1 }, { "continuationStatus", 1 }
                    }, new CreateIndexOptions { Name = continuationSortFirst, Collation = OrdinalCollation() }),
                    new CreateIndexModel<BsonDocument>(new BsonDocument
                    {
                        { "controlPlaneId", 1 }, { "tenantId", 1 }, { "tenantGroupId", 1 },
                        { "status", 1 }, { "continuationStatus", 1 }, { "updatedAt", 1 }, { "_id", 1 }
                    }, new CreateIndexOptions { Name = continuationFilterFirst, Collation = OrdinalCollation() }),
                    new CreateIndexModel<BsonDocument>(new BsonDocument
                    {
                        { "controlPlaneId", 1 }, { "tenantId", 1 }, { "tenantGroupId", 1 },
                        { "continuationStatus", 1 }, { "status", 1 }, { "updatedAt", 1 }, { "_id", 1 }
                    }, new CreateIndexOptions { Name = continuationStatusFirst, Collation = OrdinalCollation() })
                }).ConfigureAwait(false);

                WriteMetric("mongo.index_experiment.documents", documentCount);
                WriteMetric("mongo.index_experiment.limit", 100);

                await ExplainCandidateAsync(database, "dispatch.current", dispatchCommand,
                    "ix_durable_invocation_dispatch").ConfigureAwait(false);
                await ExplainCandidateAsync(database, "dispatch.sort_first", dispatchCommand,
                    dispatchSortFirst).ConfigureAwait(false);
                await ExplainCandidateAsync(database, "dispatch.expiry_first", dispatchCommand,
                    dispatchExpiryFirst).ConfigureAwait(false);

                await ExplainCandidateAsync(database, "continuation.current", continuationCommand,
                    "ix_durable_invocation_continuation").ConfigureAwait(false);
                await ExplainCandidateAsync(database, "continuation.sort_first", continuationCommand,
                    continuationSortFirst).ConfigureAwait(false);
                await ExplainCandidateAsync(database, "continuation.filter_first", continuationCommand,
                    continuationFilterFirst).ConfigureAwait(false);
                await ExplainCandidateAsync(database, "continuation.status_first", continuationCommand,
                    continuationStatusFirst).ConfigureAwait(false);
            }
            finally
            {
                await client.DropDatabaseAsync(databaseName).ConfigureAwait(false);
            }
        }

        [DurableInvocationMongoFact]
        [Trait("Category", "InvocationPerformanceMeasurement")]
        public async Task Mongo_Dispatch_Index_H3_Reports_Current_And_SortFirst_Across_Lease_Distributions()
        {
            var connectionString = Environment.GetEnvironmentVariable(DurableInvocationMongoFactAttribute.ConnectionVariable)!;
            var commands = new ConcurrentQueue<BsonDocument>();
            var settings = MongoClientSettings.FromConnectionString(connectionString);
            settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(evt =>
            {
                if (string.Equals(evt.CommandName, "find", StringComparison.Ordinal))
                    commands.Enqueue(evt.Command.DeepClone().AsBsonDocument);
            });

            var client = new MongoClient(settings);
            var databaseName = CreateTemporaryDatabaseName("sdk_invocation_dispatch_h3");
            var database = client.GetDatabase(databaseName);
            const string collectionName = "invocations";
            var collection = database.GetCollection<BsonDocument>(collectionName);

            try
            {
                var documentCount = ReadPositiveInt("MULTIPLEXED_INVOCATION_H3_DOCUMENTS", 10_000, 50_000);
                if (documentCount < 1_000)
                    throw new InvalidOperationException("MULTIPLEXED_INVOCATION_H3_DOCUMENTS must be at least 1000.");

                var queryNow = DateTimeOffset.Parse("2026-09-21T00:00:00+00:00");
                var seed = DurableInvocationTestSupport.Codec<BsonDocument>(
                    "Encode", CreatePreparedRecord("dispatch-h3-seed", 0));
                await collection.InsertOneAsync(seed).ConfigureAwait(false);

                var store = new MongoAiDurableInvocationStore(database,
                    Options.Create(new AiDurableInvocationMongoOptions { CollectionName = collectionName }));

                // Force production-index creation. H3 intentionally keeps the historical
                // single-query OR shape as its comparison baseline.
                while (commands.TryDequeue(out _)) { }
                Assert.NotEmpty(await store.ListDispatchPageAsync(
                    DurableInvocationTestSupport.Scope, "python", queryNow, 100).ConfigureAwait(false));
                var dispatchCommand = LegacyDispatchFindCommand(collectionName, queryNow);
                await collection.DeleteManyAsync(Builders<BsonDocument>.Filter.Empty).ConfigureAwait(false);

                const string dispatchSortFirst = "ix_perf_dispatch_sort_first";
                await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument
                {
                    { "controlPlaneId", 1 }, { "tenantId", 1 }, { "tenantGroupId", 1 },
                    { "language", 1 }, { "status", 1 }, { "updatedAt", 1 }, { "_id", 1 }, { "leaseExpiresAt", 1 }
                }, new CreateIndexOptions { Name = dispatchSortFirst, Collation = OrdinalCollation() })).ConfigureAwait(false);

                WriteMetric("mongo.dispatch_h3.documents", documentCount);
                WriteMetric("mongo.dispatch_h3.limit", 100);
                WriteMetric("mongo.dispatch_h3.query_now", queryNow.ToString("O"));

                await MeasureDispatchDistributionAsync(
                    collection, database, dispatchCommand, dispatchSortFirst,
                    "prepared_dense", BuildPreparedDense(documentCount, queryNow)).ConfigureAwait(false);

                await MeasureDispatchDistributionAsync(
                    collection, database, dispatchCommand, dispatchSortFirst,
                    "live_lease_heavy", BuildLiveLeaseHeavy(documentCount, queryNow)).ConfigureAwait(false);

                await MeasureDispatchDistributionAsync(
                    collection, database, dispatchCommand, dispatchSortFirst,
                    "realistic_mix", BuildRealisticMix(documentCount, queryNow)).ConfigureAwait(false);
            }
            finally
            {
                await client.DropDatabaseAsync(databaseName).ConfigureAwait(false);
            }
        }


        [DurableInvocationMongoFact]
        [Trait("Category", "InvocationPerformanceMeasurement")]
        public async Task Mongo_Dispatch_Index_H4_Reports_Hybrid_Branch_Strategy_Across_Lease_Distributions()
        {
            var connectionString = Environment.GetEnvironmentVariable(DurableInvocationMongoFactAttribute.ConnectionVariable)!;
            var commands = new ConcurrentQueue<BsonDocument>();
            var settings = MongoClientSettings.FromConnectionString(connectionString);
            settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(evt =>
            {
                if (string.Equals(evt.CommandName, "find", StringComparison.Ordinal))
                    commands.Enqueue(evt.Command.DeepClone().AsBsonDocument);
            });

            var client = new MongoClient(settings);
            var databaseName = CreateTemporaryDatabaseName("sdk_invocation_dispatch_h4");
            var database = client.GetDatabase(databaseName);
            const string collectionName = "invocations";
            var collection = database.GetCollection<BsonDocument>(collectionName);

            try
            {
                var documentCount = ReadPositiveInt("MULTIPLEXED_INVOCATION_H4_DOCUMENTS", 10_000, 50_000);
                if (documentCount < 1_000)
                    throw new InvalidOperationException("MULTIPLEXED_INVOCATION_H4_DOCUMENTS must be at least 1000.");

                var queryNow = DateTimeOffset.Parse("2026-09-21T00:00:00+00:00");
                var seed = DurableInvocationTestSupport.Codec<BsonDocument>(
                    "Encode", CreatePreparedRecord("dispatch-h4-seed", 0));
                await collection.InsertOneAsync(seed).ConfigureAwait(false);

                var store = new MongoAiDurableInvocationStore(database,
                    Options.Create(new AiDurableInvocationMongoOptions { CollectionName = collectionName }));

                // Create the production indexes. H4 keeps the pre-H5 single-query OR shape
                // explicitly as the historical baseline for semantic/performance comparison.
                while (commands.TryDequeue(out _)) { }
                Assert.NotEmpty(await store.ListDispatchPageAsync(
                    DurableInvocationTestSupport.Scope, "python", queryNow, 100).ConfigureAwait(false));
                var currentCommand = LegacyDispatchFindCommand(collectionName, queryNow);
                await collection.DeleteManyAsync(Builders<BsonDocument>.Filter.Empty).ConfigureAwait(false);

                const string preparedSortIndex = "ix_perf_dispatch_h4_prepared_sort";
                await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument
                {
                    { "controlPlaneId", 1 }, { "tenantId", 1 }, { "tenantGroupId", 1 },
                    { "language", 1 }, { "status", 1 }, { "updatedAt", 1 }, { "_id", 1 }
                }, new CreateIndexOptions { Name = preparedSortIndex, Collation = OrdinalCollation() })).ConfigureAwait(false);

                var preparedCommand = DispatchBranchFindCommand(
                    collectionName, AiDurableInvocationStatus.Prepared, queryNow, includeExpiryPredicate: false);
                var expiredLeaseCommand = DispatchBranchFindCommand(
                    collectionName, AiDurableInvocationStatus.Leased, queryNow, includeExpiryPredicate: true);

                WriteMetric("mongo.dispatch_h4.documents", documentCount);
                WriteMetric("mongo.dispatch_h4.limit", 100);
                WriteMetric("mongo.dispatch_h4.query_now", queryNow.ToString("O"));
                WriteMetric("mongo.dispatch_h4.strategy",
                    "prepared=ordered-index;expired-leased=current-expiry-index;merge=(updatedAt,_id)");

                await MeasureDispatchHybridDistributionAsync(
                    collection, database, currentCommand, preparedCommand, expiredLeaseCommand,
                    preparedSortIndex, "prepared_dense", BuildPreparedDense(documentCount, queryNow), queryNow).ConfigureAwait(false);

                await MeasureDispatchHybridDistributionAsync(
                    collection, database, currentCommand, preparedCommand, expiredLeaseCommand,
                    preparedSortIndex, "live_lease_heavy", BuildLiveLeaseHeavy(documentCount, queryNow), queryNow).ConfigureAwait(false);

                await MeasureDispatchHybridDistributionAsync(
                    collection, database, currentCommand, preparedCommand, expiredLeaseCommand,
                    preparedSortIndex, "realistic_mix", BuildRealisticMix(documentCount, queryNow), queryNow).ConfigureAwait(false);
            }
            finally
            {
                await client.DropDatabaseAsync(databaseName).ConfigureAwait(false);
            }
        }

        private async Task MeasureDispatchHybridDistributionAsync(
            IMongoCollection<BsonDocument> collection,
            IMongoDatabase database,
            BsonDocument currentCommand,
            BsonDocument preparedCommand,
            BsonDocument expiredLeaseCommand,
            string preparedSortIndex,
            string distribution,
            DispatchDistribution dataset,
            DateTimeOffset queryNow)
        {
            await collection.DeleteManyAsync(Builders<BsonDocument>.Filter.Empty).ConfigureAwait(false);
            await collection.InsertManyAsync(dataset.Documents, new InsertManyOptions { IsOrdered = false }).ConfigureAwait(false);

            WriteMetric($"mongo.dispatch_h4.{distribution}.prepared", dataset.PreparedCount);
            WriteMetric($"mongo.dispatch_h4.{distribution}.expired_leased", dataset.ExpiredLeaseCount);
            WriteMetric($"mongo.dispatch_h4.{distribution}.live_leased", dataset.LiveLeaseCount);

            var current = await ExplainMetricsAsync(
                database, $"dispatch_h4.{distribution}.current", currentCommand,
                "ix_durable_invocation_dispatch").ConfigureAwait(false);
            var prepared = await ExplainMetricsAsync(
                database, $"dispatch_h4.{distribution}.hybrid.prepared", preparedCommand,
                preparedSortIndex).ConfigureAwait(false);
            var expired = await ExplainMetricsAsync(
                database, $"dispatch_h4.{distribution}.hybrid.expired_leased", expiredLeaseCommand,
                "ix_durable_invocation_dispatch").ConfigureAwait(false);

            var hybridKeys = prepared.KeysExamined + expired.KeysExamined;
            var hybridDocs = prepared.DocsExamined + expired.DocsExamined;
            var hybridTime = prepared.ExecutionTimeMilliseconds + expired.ExecutionTimeMilliseconds;
            var hybridReturned = Math.Min(100, prepared.Returned + expired.Returned);

            WriteMetric($"mongo.dispatch_h4.{distribution}.hybrid.n_returned", hybridReturned);
            WriteMetric($"mongo.dispatch_h4.{distribution}.hybrid.execution_time_ms_sum", hybridTime);
            WriteMetric($"mongo.dispatch_h4.{distribution}.hybrid.keys_examined", hybridKeys);
            WriteMetric($"mongo.dispatch_h4.{distribution}.hybrid.docs_examined", hybridDocs);
            WriteMetric($"mongo.dispatch_h4.{distribution}.hybrid.blocking_sort",
                prepared.BlockingSort || expired.BlockingSort);
            WriteMetric($"mongo.dispatch_h4.{distribution}.hybrid.keys_per_returned",
                SafeRatio(hybridKeys, hybridReturned));
            WriteMetric($"mongo.dispatch_h4.{distribution}.hybrid.docs_per_returned",
                SafeRatio(hybridDocs, hybridReturned));
            WriteMetric($"mongo.dispatch_h4.{distribution}.hybrid_vs_current.keys_ratio",
                SafeRatio(hybridKeys, current.KeysExamined));
            WriteMetric($"mongo.dispatch_h4.{distribution}.hybrid_vs_current.docs_ratio",
                SafeRatio(hybridDocs, current.DocsExamined));

            var currentIds = await ExecuteDispatchCurrentAsync(collection, queryNow).ConfigureAwait(false);
            var hybridIds = await ExecuteDispatchHybridAsync(collection, queryNow).ConfigureAwait(false);
            Assert.Equal(currentIds, hybridIds);
            WriteMetric($"mongo.dispatch_h4.{distribution}.semantic_equivalence", true);
        }

        private static async Task<IReadOnlyList<string>> ExecuteDispatchCurrentAsync(
            IMongoCollection<BsonDocument> collection,
            DateTimeOffset queryNow)
        {
            var filter = DispatchCommonFilter()
                & Builders<BsonDocument>.Filter.Eq("language", "python")
                & (Builders<BsonDocument>.Filter.Eq("status", (int)AiDurableInvocationStatus.Prepared)
                   | (Builders<BsonDocument>.Filter.Eq("status", (int)AiDurableInvocationStatus.Leased)
                      & Builders<BsonDocument>.Filter.Lte("leaseExpiresAt", queryNow.ToUnixTimeMilliseconds())));
            var sort = Builders<BsonDocument>.Sort.Ascending("updatedAt").Ascending("_id");
            var docs = await collection.Find(filter).Sort(sort).Limit(100).ToListAsync().ConfigureAwait(false);
            return docs.Select(document => document["_id"].AsString).ToArray();
        }

        private static async Task<IReadOnlyList<string>> ExecuteDispatchHybridAsync(
            IMongoCollection<BsonDocument> collection,
            DateTimeOffset queryNow)
        {
            var sort = Builders<BsonDocument>.Sort.Ascending("updatedAt").Ascending("_id");
            var common = DispatchCommonFilter() & Builders<BsonDocument>.Filter.Eq("language", "python");

            var prepared = await collection.Find(common
                    & Builders<BsonDocument>.Filter.Eq("status", (int)AiDurableInvocationStatus.Prepared))
                .Sort(sort).Limit(100).ToListAsync().ConfigureAwait(false);

            var expired = await collection.Find(common
                    & Builders<BsonDocument>.Filter.Eq("status", (int)AiDurableInvocationStatus.Leased)
                    & Builders<BsonDocument>.Filter.Lte("leaseExpiresAt", queryNow.ToUnixTimeMilliseconds()))
                .Sort(sort).Limit(100).ToListAsync().ConfigureAwait(false);

            return prepared.Concat(expired)
                .OrderBy(document => document["updatedAt"].ToInt64())
                .ThenBy(document => document["_id"].AsString, StringComparer.Ordinal)
                .Take(100)
                .Select(document => document["_id"].AsString)
                .ToArray();
        }

        private static FilterDefinition<BsonDocument> DispatchCommonFilter()
        {
            var filter = Builders<BsonDocument>.Filter;
            return filter.Eq("controlPlaneId", DurableInvocationTestSupport.Scope.ControlPlaneId)
                & filter.Eq("tenantId", DurableInvocationTestSupport.Scope.TenantId)
                & filter.Eq("tenantGroupId", DurableInvocationTestSupport.Scope.TenantGroupId);
        }

        private static BsonDocument LegacyDispatchFindCommand(
            string collectionName, DateTimeOffset queryNow)
        {
            var filter = new BsonDocument
            {
                { "controlPlaneId", DurableInvocationTestSupport.Scope.ControlPlaneId },
                { "tenantId", DurableInvocationTestSupport.Scope.TenantId },
                { "tenantGroupId", DurableInvocationTestSupport.Scope.TenantGroupId },
                { "language", "python" },
                { "$or", new BsonArray
                    {
                        new BsonDocument("status", (int)AiDurableInvocationStatus.Prepared),
                        new BsonDocument
                        {
                            { "status", (int)AiDurableInvocationStatus.Leased },
                            { "leaseExpiresAt", new BsonDocument("$lte", queryNow.ToUnixTimeMilliseconds()) }
                        }
                    }
                }
            };
            return new BsonDocument
            {
                { "find", collectionName },
                { "filter", filter },
                { "sort", new BsonDocument { { "updatedAt", 1 }, { "_id", 1 } } },
                { "limit", 100 },
                { "collation", new BsonDocument { { "locale", "simple" } } }
            };
        }

        private static BsonDocument DispatchBranchFindCommand(
            string collectionName,
            AiDurableInvocationStatus status,
            DateTimeOffset queryNow,
            bool includeExpiryPredicate)
        {
            var filter = new BsonDocument
            {
                { "controlPlaneId", DurableInvocationTestSupport.Scope.ControlPlaneId },
                { "tenantId", DurableInvocationTestSupport.Scope.TenantId },
                { "tenantGroupId", DurableInvocationTestSupport.Scope.TenantGroupId },
                { "language", "python" },
                { "status", (int)status }
            };
            if (includeExpiryPredicate)
                filter["leaseExpiresAt"] = new BsonDocument("$lte", queryNow.ToUnixTimeMilliseconds());

            return new BsonDocument
            {
                { "find", collectionName },
                { "filter", filter },
                { "sort", new BsonDocument { { "updatedAt", 1 }, { "_id", 1 } } },
                { "limit", 100 },
                { "collation", new BsonDocument { { "locale", "simple" } } }
            };
        }

        private async Task<ExplainMetrics> ExplainMetricsAsync(
            IMongoDatabase database, string name, BsonDocument command, string hint)
        {
            var explain = await ExplainAsync(database, command, hint).ConfigureAwait(false);
            WriteExplain(name, explain);
            var stats = explain["executionStats"].AsBsonDocument;
            var plan = explain["queryPlanner"].AsBsonDocument["winningPlan"].AsBsonDocument;
            var stages = CollectStages(plan);
            var metrics = new ExplainMetrics(
                stats.GetValue("nReturned", 0).ToInt64(),
                stats.GetValue("executionTimeMillis", 0).ToInt64(),
                stats.GetValue("totalKeysExamined", 0).ToInt64(),
                stats.GetValue("totalDocsExamined", 0).ToInt64(),
                stages.Contains("SORT", StringComparer.Ordinal));
            WriteMetric($"mongo.{name}.hint", hint);
            WriteMetric($"mongo.{name}.blocking_sort", metrics.BlockingSort);
            WriteMetric($"mongo.{name}.keys_per_returned", SafeRatio(metrics.KeysExamined, metrics.Returned));
            WriteMetric($"mongo.{name}.docs_per_returned", SafeRatio(metrics.DocsExamined, metrics.Returned));
            return metrics;
        }

        private sealed record ExplainMetrics(
            long Returned,
            long ExecutionTimeMilliseconds,
            long KeysExamined,
            long DocsExamined,
            bool BlockingSort);

        private async Task MeasureDispatchDistributionAsync(
            IMongoCollection<BsonDocument> collection,
            IMongoDatabase database,
            BsonDocument dispatchCommand,
            string dispatchSortFirst,
            string distribution,
            DispatchDistribution dataset)
        {
            await collection.DeleteManyAsync(Builders<BsonDocument>.Filter.Empty).ConfigureAwait(false);
            await collection.InsertManyAsync(dataset.Documents, new InsertManyOptions { IsOrdered = false }).ConfigureAwait(false);

            WriteMetric($"mongo.dispatch_h3.{distribution}.prepared", dataset.PreparedCount);
            WriteMetric($"mongo.dispatch_h3.{distribution}.expired_leased", dataset.ExpiredLeaseCount);
            WriteMetric($"mongo.dispatch_h3.{distribution}.live_leased", dataset.LiveLeaseCount);

            await ExplainCandidateAsync(database, $"dispatch_h3.{distribution}.current", dispatchCommand,
                "ix_durable_invocation_dispatch").ConfigureAwait(false);
            await ExplainCandidateAsync(database, $"dispatch_h3.{distribution}.sort_first", dispatchCommand,
                dispatchSortFirst).ConfigureAwait(false);
        }

        private static DispatchDistribution BuildPreparedDense(int documentCount, DateTimeOffset queryNow)
        {
            var baseUpdatedAt = queryNow.AddHours(-12);
            var documents = new List<BsonDocument>(documentCount);
            for (var index = 0; index < documentCount; index++)
                documents.Add(SyntheticDispatchDocument(index, AiDurableInvocationStatus.Prepared,
                    baseUpdatedAt.AddMilliseconds(index), null));
            return new DispatchDistribution(documents, documentCount, 0, 0);
        }

        private static DispatchDistribution BuildLiveLeaseHeavy(int documentCount, DateTimeOffset queryNow)
        {
            var liveCount = Math.Max(0, documentCount - Math.Max(100, documentCount / 10));
            var expiredCount = documentCount - liveCount;
            var baseUpdatedAt = queryNow.AddHours(-12);
            var documents = new List<BsonDocument>(documentCount);
            for (var index = 0; index < documentCount; index++)
            {
                var live = index < liveCount;
                documents.Add(SyntheticDispatchDocument(index, AiDurableInvocationStatus.Leased,
                    baseUpdatedAt.AddMilliseconds(index),
                    live ? queryNow.AddMinutes(30) : queryNow.AddMinutes(-30)));
            }
            return new DispatchDistribution(documents, 0, expiredCount, liveCount);
        }

        private static DispatchDistribution BuildRealisticMix(int documentCount, DateTimeOffset queryNow)
        {
            var baseUpdatedAt = queryNow.AddHours(-12);
            var documents = new List<BsonDocument>(documentCount);
            var prepared = 0;
            var expired = 0;
            var live = 0;
            for (var index = 0; index < documentCount; index++)
            {
                var bucket = index % 10;
                if (bucket < 4)
                {
                    prepared++;
                    documents.Add(SyntheticDispatchDocument(index, AiDurableInvocationStatus.Prepared,
                        baseUpdatedAt.AddMilliseconds(index), null));
                }
                else if (bucket < 7)
                {
                    expired++;
                    documents.Add(SyntheticDispatchDocument(index, AiDurableInvocationStatus.Leased,
                        baseUpdatedAt.AddMilliseconds(index), queryNow.AddMinutes(-30)));
                }
                else
                {
                    live++;
                    documents.Add(SyntheticDispatchDocument(index, AiDurableInvocationStatus.Leased,
                        baseUpdatedAt.AddMilliseconds(index), queryNow.AddMinutes(30)));
                }
            }
            return new DispatchDistribution(documents, prepared, expired, live);
        }

        private static BsonDocument SyntheticDispatchDocument(
            int index,
            AiDurableInvocationStatus status,
            DateTimeOffset updatedAt,
            DateTimeOffset? leaseExpiresAt)
        {
            var id = $"h3-{index:D8}";
            return new BsonDocument
            {
                { "_id", id },
                { "tenantId", DurableInvocationTestSupport.Scope.TenantId },
                { "executionId", $"h3-execution-{index:D8}" },
                { "stepName", "analyze" },
                { "generation", 0 },
                { "tenantGroupId", DurableInvocationTestSupport.Scope.TenantGroupId },
                { "controlPlaneId", DurableInvocationTestSupport.Scope.ControlPlaneId },
                { "language", "python" },
                { "status", (int)status },
                { "leaseExpiresAt", leaseExpiresAt is null
                    ? BsonNull.Value
                    : new BsonInt64(leaseExpiresAt.Value.ToUnixTimeMilliseconds()) },
                { "updatedAt", updatedAt.ToUnixTimeMilliseconds() }
            };
        }

        private sealed record DispatchDistribution(
            IReadOnlyList<BsonDocument> Documents,
            int PreparedCount,
            int ExpiredLeaseCount,
            int LiveLeaseCount);

        private async Task ExplainCandidateAsync(
            IMongoDatabase database, string name, BsonDocument capturedFind, string hint)
        {
            var explain = await ExplainAsync(database, capturedFind, hint).ConfigureAwait(false);
            WriteExplain(name, explain);
            var stats = explain["executionStats"].AsBsonDocument;
            var plan = explain["queryPlanner"].AsBsonDocument["winningPlan"].AsBsonDocument;
            var returned = stats.GetValue("nReturned", 0).ToInt64();
            var keys = stats.GetValue("totalKeysExamined", 0).ToInt64();
            var docs = stats.GetValue("totalDocsExamined", 0).ToInt64();
            var stages = CollectStages(plan);
            WriteMetric($"mongo.{name}.hint", hint);
            WriteMetric($"mongo.{name}.blocking_sort", stages.Contains("SORT", StringComparer.Ordinal));
            WriteMetric($"mongo.{name}.keys_per_returned", SafeRatio(keys, returned));
            WriteMetric($"mongo.{name}.docs_per_returned", SafeRatio(docs, returned));
        }

        private static Collation OrdinalCollation() => new("simple");

        private static async Task<AiDurableInvocationRecord> CreateCompletedRecordAsync(string prefix, int index)
        {
            var clock = new DurableInvocationTestSupport.Clock();
            clock.Advance(TimeSpan.FromMilliseconds(index));
            var store = new DurableInvocationTestSupport.MemoryStore(clock);
            var journal = new AiDurableInvocationJournal(store, clock);
            var definition = Definition(prefix, index);
            await journal.PrepareAsync(definition).ConfigureAwait(false);
            var leased = (await journal.TryAcquireLeaseAsync(definition.Scope, definition.Identity,
                "worker-" + index, TimeSpan.FromMinutes(2)).ConfigureAwait(false))!;
            Assert.Equal(AiDurableInvocationCompletionStatus.Accepted,
                await journal.CompleteAsync(definition.Scope, definition.Identity, leased.Lease!,
                    DurableInvocationTestSupport.Result()).ConfigureAwait(false));
            return (await journal.GetAsync(definition.Scope, definition.Identity).ConfigureAwait(false))!;
        }

        private static AiDurableInvocationRecord CreatePreparedRecord(string prefix, int index)
        {
            var clock = new DurableInvocationTestSupport.Clock();
            clock.Advance(TimeSpan.FromMilliseconds(index));
            var journal = new AiDurableInvocationJournal(new DurableInvocationTestSupport.MemoryStore(clock), clock);
            return journal.PrepareAsync(Definition(prefix, index)).GetAwaiter().GetResult();
        }

        private static AiDurableInvocationDefinition Definition(string prefix, int index)
        {
            var identity = new AiDurableInvocationIdentity(
                DurableInvocationTestSupport.Scope.TenantId,
                $"{prefix}-execution-{index:D8}",
                "analyze");
            return DurableInvocationTestSupport.Definition() with { Identity = identity };
        }

        private static string CapturedHint(BsonDocument command)
        {
            Assert.True(command.TryGetValue("hint", out var hint), "Captured find command must contain an explicit index hint.");
            Assert.True(hint.IsString, "Captured find command hint must be an index name.");
            return hint.AsString;
        }

        private static BsonDocument TakeFindByStatus(
            ConcurrentQueue<BsonDocument> commands, string collectionName, AiDurableInvocationStatus status)
        {
            var matching = commands.Where(command => command.TryGetValue("find", out var name) &&
                string.Equals(name.AsString, collectionName, StringComparison.Ordinal) &&
                command.TryGetValue("filter", out var filterValue) && filterValue.IsBsonDocument &&
                filterValue.AsBsonDocument.TryGetValue("status", out var statusValue) &&
                statusValue.IsInt32 && statusValue.AsInt32 == (int)status).ToArray();
            return Assert.Single(matching);
        }

        private static BsonDocument TakeLastFind(ConcurrentQueue<BsonDocument> commands, string collectionName)
        {
            var matching = commands.Where(command => command.TryGetValue("find", out var name) &&
                string.Equals(name.AsString, collectionName, StringComparison.Ordinal)).ToArray();
            return Assert.Single(matching);
        }

        private static async Task<BsonDocument> ExplainAsync(
            IMongoDatabase database, BsonDocument capturedFind, string? hint = null)
        {
            var find = new BsonDocument();
            foreach (var name in new[] { "find", "filter", "sort", "limit", "collation" })
                if (capturedFind.TryGetValue(name, out var value)) find[name] = value.DeepClone();
            if (!string.IsNullOrWhiteSpace(hint))
                find["hint"] = hint;
            else if (capturedFind.TryGetValue("hint", out var capturedHint))
                find["hint"] = capturedHint.DeepClone();
            return await database.RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { "explain", find },
                { "verbosity", "executionStats" }
            }).ConfigureAwait(false);
        }

        private void WriteExplain(string name, BsonDocument explain)
        {
            var stats = explain["executionStats"].AsBsonDocument;
            var plan = explain["queryPlanner"].AsBsonDocument["winningPlan"].AsBsonDocument;
            WriteMetric($"mongo.{name}.n_returned", stats.GetValue("nReturned", 0).ToInt64());
            WriteMetric($"mongo.{name}.execution_time_ms", stats.GetValue("executionTimeMillis", 0).ToInt64());
            WriteMetric($"mongo.{name}.keys_examined", stats.GetValue("totalKeysExamined", 0).ToInt64());
            WriteMetric($"mongo.{name}.docs_examined", stats.GetValue("totalDocsExamined", 0).ToInt64());
            WriteMetric($"mongo.{name}.plan_stages", string.Join(",", CollectStages(plan)));
            _output.WriteLine($"mongo.{name}.winning_plan={plan.ToJson()}");
        }

        private static IReadOnlyList<string> CollectStages(BsonValue value)
        {
            var stages = new List<string>();
            Visit(value, stages);
            return stages;

            static void Visit(BsonValue current, List<string> target)
            {
                if (current is BsonDocument document)
                {
                    if (document.TryGetValue("stage", out var stage) && stage.IsString)
                        target.Add(stage.AsString);
                    foreach (var element in document.Elements) Visit(element.Value, target);
                }
                else if (current is BsonArray array)
                    foreach (var item in array) Visit(item, target);
            }
        }

        private static string CreateTemporaryDatabaseName(string prefix)
        {
            const int maxMongoDatabaseNameLength = 63;
            var suffix = Guid.NewGuid().ToString("N");
            var maxPrefixLength = maxMongoDatabaseNameLength - suffix.Length - 1;
            var safePrefix = prefix.Length <= maxPrefixLength ? prefix : prefix[..maxPrefixLength];
            return $"{safePrefix}_{suffix}";
        }

        private static int ReadPositiveInt(string name, int fallback, int maximum)
        {
            var text = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            if (!int.TryParse(text, out var value) || value <= 0 || value > maximum)
                throw new InvalidOperationException($"{name} must be an integer between 1 and {maximum}.");
            return value;
        }

        private static void ForceCollection()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private void WriteResult(string name, MeasureResult result)
        {
            WriteMetric($"codec.{name}.ns_per_op", result.NanosecondsPerOperation);
            WriteMetric($"codec.{name}.bytes_per_op", result.BytesPerOperation);
            WriteMetric($"codec.{name}.ops_per_second", result.OperationsPerSecond);
        }

        private void WriteMetric(string name, object value) => _output.WriteLine($"{name}={value}");

        private static double SafeRatio(double numerator, double denominator) => denominator == 0 ? 0 : numerator / denominator;

        private sealed record MeasureResult(double NanosecondsPerOperation, double BytesPerOperation, double OperationsPerSecond)
        {
            internal static MeasureResult From<TInput, TResult>(int iterations,
                TInput input, Func<TInput, TResult> operation)
            {
                ForceCollection();
                var allocationStart = GC.GetAllocatedBytesForCurrentThread();
                var stopwatch = Stopwatch.StartNew();
                TResult? last = default;
                for (var index = 0; index < iterations; index++) last = operation(input);
                stopwatch.Stop();
                var allocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
                GC.KeepAlive(last);
                var seconds = stopwatch.Elapsed.TotalSeconds;
                return new MeasureResult(
                    stopwatch.Elapsed.TotalMilliseconds * 1_000_000d / iterations,
                    (double)allocated / iterations,
                    seconds <= 0 ? 0 : iterations / seconds);
            }
        }
    }
}
