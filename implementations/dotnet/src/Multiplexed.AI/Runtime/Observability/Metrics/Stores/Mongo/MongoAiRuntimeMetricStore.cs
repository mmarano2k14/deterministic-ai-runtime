using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Metrics.Store;
using Multiplexed.AI.Runtime.Observability.Performance;

namespace Multiplexed.AI.Runtime.Observability.Metrics.Stores.Mongo
{
    /// <summary>
    /// MongoDB-backed implementation of <see cref="IAiRuntimeMetricStore"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This store persists runtime metric records as append-only MongoDB documents.
    /// </para>
    ///
    /// <para>
    /// Each persisted metric includes its runtime execution correlation snapshot,
    /// allowing metrics to be queried by execution, run, correlation identifier,
    /// pipeline, runtime instance, and worker.
    /// </para>
    /// </remarks>
    public sealed class MongoAiRuntimeMetricStore : IAiRuntimeMetricStore
    {
        private readonly IMongoCollection<AiRuntimeMetricRecord> _collection;

        /// <summary>
        /// Initializes a new instance of the <see cref="MongoAiRuntimeMetricStore"/> class.
        /// </summary>
        /// <param name="options">The runtime metric store options.</param>
        public MongoAiRuntimeMetricStore(
            IOptions<AiRuntimeMetricStoreOptions> options)
        {
            var value = options?.Value ?? throw new ArgumentNullException(nameof(options));

            var client = AiMongoAttributionDiagnostics.CreateMongoClient(
                value.MongoConnectionString,
                AiMongoAttributionClientRoles.MetricStore);
            var database = client.GetDatabase(value.MongoDatabaseName);

            _collection = database.GetCollection<AiRuntimeMetricRecord>(
                value.MongoCollectionName);
        }

        /// <inheritdoc />
        public async Task AppendAsync(
            AiRuntimeMetricRecord record,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(record);

            var appendMeasurement = AiMongoAttributionDiagnostics.StartOperation(
                AiMongoAttributionOperations.MetricAppend,
                AiMongoAttributionCommands.Insert,
                requestedDocuments: 1);

            try
            {
                await _collection.InsertOneAsync(
                        record,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                appendMeasurement.Succeed();
            }
            catch (OperationCanceledException)
            {
                appendMeasurement.Cancel();
                throw;
            }
            catch
            {
                appendMeasurement.Fail();
                throw;
            }
        }
    }
}