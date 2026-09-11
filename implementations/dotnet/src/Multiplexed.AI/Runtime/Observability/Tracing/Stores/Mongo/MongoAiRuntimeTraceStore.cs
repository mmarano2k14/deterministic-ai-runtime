using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Observability.Tracing.Store;
using Multiplexed.AI.Runtime.Observability.Performance;
using Multiplexed.AI.Stores.Mongo;

namespace Multiplexed.AI.Runtime.Observability.Tracing.Stores.Mongo
{
    /// <summary>
    /// MongoDB-backed implementation of <see cref="IAiRuntimeTraceStore"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This store persists completed runtime trace records to MongoDB for durable
    /// diagnostics, replay support, and post-execution inspection.
    /// </para>
    /// <para>
    /// Trace storage is observational and best-effort. MongoDB writes are therefore
    /// buffered in a bounded process-local queue and emitted through bounded batches.
    /// Queue pressure never blocks runtime execution indefinitely.
    /// </para>
    /// <para>
    /// Execution reads establish a local flush barrier before querying MongoDB so
    /// records already accepted by this process are not hidden behind the batch window.
    /// Physical process termination may still lose records pending in the bounded
    /// best-effort queue, which is consistent with the existing trace contract.
    /// </para>
    /// <para>
    /// MongoDB index creation is treated as an idempotent infrastructure operation
    /// and is executed through Mongo runtime resilience helpers to tolerate transient
    /// Docker/local socket failures.
    /// </para>
    /// </remarks>
    public sealed class MongoAiRuntimeTraceStore : IAiRuntimeTraceStore, IDisposable, IAsyncDisposable
    {
        private const int BatchQueueCapacity = 1024;
        private const int MaximumBatchSize = 64;
        private static readonly TimeSpan MaximumFlushInterval = TimeSpan.FromMilliseconds(10);
        private static readonly TimeSpan GracefulShutdownTimeout = TimeSpan.FromSeconds(5);

        private readonly IMongoCollection<AiTraceRecord> _collection;
        private readonly Lazy<Task> _ensureIndexesTask;
        private readonly AiMongoBestEffortBatchWriter<AiTraceRecord> _batchWriter;

        /// <summary>
        /// Initializes a new instance of the <see cref="MongoAiRuntimeTraceStore"/> class.
        /// </summary>
        /// <param name="client">The MongoDB client.</param>
        /// <param name="options">The runtime trace store options.</param>
        public MongoAiRuntimeTraceStore(
            IMongoClient client,
            IOptions<AiRuntimeTraceStoreOptions> options)
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(options);

            var resolvedOptions = options.Value
                ?? throw new ArgumentNullException(nameof(options));

            if (string.IsNullOrWhiteSpace(resolvedOptions.MongoDatabaseName))
            {
                throw new ArgumentException(
                    "Mongo trace store database name cannot be null or whitespace.",
                    nameof(options));
            }

            if (string.IsNullOrWhiteSpace(resolvedOptions.MongoCollectionName))
            {
                throw new ArgumentException(
                    "Mongo trace store collection name cannot be null or whitespace.",
                    nameof(options));
            }

            var database = client.GetDatabase(
                resolvedOptions.MongoDatabaseName);

            _collection = database.GetCollection<AiTraceRecord>(
                resolvedOptions.MongoCollectionName);

            _ensureIndexesTask = new Lazy<Task>(
                () => EnsureIndexesAsync(CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication);

            _batchWriter = new AiMongoBestEffortBatchWriter<AiTraceRecord>(
                storeName: "trace",
                capacity: BatchQueueCapacity,
                maxBatchSize: MaximumBatchSize,
                maxFlushInterval: MaximumFlushInterval,
                flushAsync: FlushBatchAsync,
                shutdownTimeout: GracefulShutdownTimeout);
        }

        /// <inheritdoc />
        public Task AppendAsync(
            AiTraceRecord record,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(record);
            cancellationToken.ThrowIfCancellationRequested();

            _ = _batchWriter.TryEnqueue(record);

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AiTraceRecord>> GetByExecutionAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            cancellationToken.ThrowIfCancellationRequested();

            await _batchWriter
                .FlushAsync(cancellationToken)
                .ConfigureAwait(false);

            await _ensureIndexesTask.Value.ConfigureAwait(false);

            var filter = Builders<AiTraceRecord>.Filter.Or(
                Builders<AiTraceRecord>.Filter.Eq(
                    record => record.ExecutionId,
                    executionId),
                Builders<AiTraceRecord>.Filter.Eq(
                    "Correlation.Runtime.ExecutionId",
                    executionId),
                Builders<AiTraceRecord>.Filter.Eq(
                    "Correlation.Runtime.RunId",
                    executionId),
                Builders<AiTraceRecord>.Filter.Eq(
                    "Correlation.Runtime.CorrelationId",
                    executionId));

            var loadMeasurement = AiMongoAttributionDiagnostics.StartOperation(
                AiMongoAttributionOperations.TraceExecutionLoad,
                AiMongoAttributionCommands.Find);

            try
            {
                var records = await _collection
                    .Find(filter)
                    .SortBy(record => record.StartedAtUtc)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                loadMeasurement.Succeed(records.Count);
                return records;
            }
            catch (OperationCanceledException)
            {
                loadMeasurement.Cancel();
                throw;
            }
            catch
            {
                loadMeasurement.Fail();
                throw;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _batchWriter.Dispose();
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            return _batchWriter.DisposeAsync();
        }

        private async Task FlushBatchAsync(
            IReadOnlyList<AiTraceRecord> records,
            CancellationToken cancellationToken)
        {
            if (records.Count == 0)
            {
                return;
            }

            await _ensureIndexesTask.Value.ConfigureAwait(false);

            var appendMeasurement = AiMongoAttributionDiagnostics.StartOperation(
                AiMongoAttributionOperations.TraceAppend,
                AiMongoAttributionCommands.Insert,
                requestedDocuments: records.Count);

            try
            {
                await _collection
                    .InsertManyAsync(
                        records,
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

        /// <summary>
        /// Ensures MongoDB indexes used by trace lookup and replay diagnostics.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task EnsureIndexesAsync(
            CancellationToken cancellationToken)
        {
            var indexes = new[]
            {
                new CreateIndexModel<AiTraceRecord>(
                    Builders<AiTraceRecord>.IndexKeys
                        .Ascending(record => record.ExecutionId)
                        .Ascending(record => record.StartedAtUtc),
                    new CreateIndexOptions
                    {
                        Name = "ix_trace_execution_started"
                    }),

                new CreateIndexModel<AiTraceRecord>(
                    Builders<AiTraceRecord>.IndexKeys
                        .Ascending("Correlation.Runtime.ExecutionId")
                        .Ascending(record => record.StartedAtUtc),
                    new CreateIndexOptions
                    {
                        Name = "ix_trace_correlation_execution_started"
                    }),

                new CreateIndexModel<AiTraceRecord>(
                    Builders<AiTraceRecord>.IndexKeys
                        .Ascending("Correlation.Runtime.RunId")
                        .Ascending(record => record.StartedAtUtc),
                    new CreateIndexOptions
                    {
                        Name = "ix_trace_run_started"
                    }),

                new CreateIndexModel<AiTraceRecord>(
                    Builders<AiTraceRecord>.IndexKeys
                        .Ascending("Correlation.Runtime.CorrelationId")
                        .Ascending(record => record.StartedAtUtc),
                    new CreateIndexOptions
                    {
                        Name = "ix_trace_correlation_started"
                    }),

                new CreateIndexModel<AiTraceRecord>(
                    Builders<AiTraceRecord>.IndexKeys
                        .Ascending(record => record.Operation)
                        .Ascending(record => record.StartedAtUtc),
                    new CreateIndexOptions
                    {
                        Name = "ix_trace_operation_started"
                    })
            };

            await MongoRuntimeResilience.ExecuteInfrastructureAsync(
                    ct => _collection.Indexes.CreateManyAsync(
                        indexes,
                        cancellationToken: ct),
                    "mongo-runtime-trace-create-indexes",
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
