using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.AI.Observability.Ledger;
using Multiplexed.AI.Runtime.Observability.Performance;

namespace Multiplexed.AI.Runtime.Observability.Ledger.Mongo
{
    /// <summary>
    /// Provides a MongoDB-backed append-only implementation of <see cref="IAiDecisionLedger"/>.
    /// </summary>
    /// <remarks>
    /// This ledger stores durable audit entries by execution identifier.
    /// It is not the source of truth for execution state.
    /// Sequence numbers are assigned atomically per execution using a MongoDB counter document.
    /// </remarks>
    public sealed class MongoAiDecisionLedger : IAiDecisionLedger
    {
        private readonly IMongoCollection<MongoAiDecisionLedgerEntryDocument> _entries;
        private readonly IMongoCollection<MongoAiDecisionLedgerSequenceDocument> _sequences;
        private readonly MongoAiDecisionLedgerOptions _options;
        private readonly Lazy<Task> _indexCreationTask;

        /// <summary>
        /// Initializes a new instance of the <see cref="MongoAiDecisionLedger"/> class.
        /// </summary>
        /// <param name="mongoClient">The MongoDB client.</param>
        /// <param name="options">The Mongo decision ledger options.</param>
        public MongoAiDecisionLedger(
            IMongoClient mongoClient,
            IOptions<MongoAiDecisionLedgerOptions> options)
        {
            ArgumentNullException.ThrowIfNull(mongoClient);
            ArgumentNullException.ThrowIfNull(options);

            _options = options.Value;

            if (string.IsNullOrWhiteSpace(_options.DatabaseName))
            {
                throw new ArgumentException(
                    "Mongo decision ledger database name is required.",
                    nameof(options));
            }

            if (string.IsNullOrWhiteSpace(_options.CollectionName))
            {
                throw new ArgumentException(
                    "Mongo decision ledger collection name is required.",
                    nameof(options));
            }

            if (string.IsNullOrWhiteSpace(_options.SequenceCollectionName))
            {
                throw new ArgumentException(
                    "Mongo decision ledger sequence collection name is required.",
                    nameof(options));
            }

            var database = mongoClient.GetDatabase(_options.DatabaseName);

            _entries = database.GetCollection<MongoAiDecisionLedgerEntryDocument>(
                _options.CollectionName);

            _sequences = database.GetCollection<MongoAiDecisionLedgerSequenceDocument>(
                _options.SequenceCollectionName);

            _indexCreationTask = new Lazy<Task>(
                () => _options.CreateIndexes
                    ? CreateIndexesAsync(CancellationToken.None)
                    : Task.CompletedTask);
        }

        /// <inheritdoc />
        public async Task AppendAsync(
            AiDecisionLedgerEntry entry,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(entry.CorrelationContext);
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.EntryId);
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.CorrelationContext.ExecutionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.EventType);

            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

            var sequence = await GetNextSequenceAsync(
                entry.CorrelationContext.ExecutionId,
                cancellationToken).ConfigureAwait(false);

            var document = MongoAiDecisionLedgerEntryDocument.FromEntry(entry, sequence);

            var appendMeasurement = AiMongoAttributionDiagnostics.StartOperation(
                AiMongoAttributionOperations.LedgerEntryAppend,
                AiMongoAttributionCommands.Insert,
                requestedDocuments: 1);

            try
            {
                await _entries.InsertOneAsync(
                    document,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
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

        /// <inheritdoc />
        public async Task<IReadOnlyList<AiDecisionLedgerEntry>> GetByExecutionAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

            var filter = Builders<MongoAiDecisionLedgerEntryDocument>.Filter.Eq(
                document => document.ExecutionId,
                executionId);

            var loadMeasurement = AiMongoAttributionDiagnostics.StartOperation(
                AiMongoAttributionOperations.LedgerExecutionLoad,
                AiMongoAttributionCommands.Find);

            try
            {
                var documents = await _entries
                    .Find(filter)
                    .SortBy(document => document.Sequence)
                    .ThenBy(document => document.TimestampUtc)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                loadMeasurement.Succeed(documents.Count);

                return documents.ConvertAll(document => document.ToEntry());
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
        public async Task<IReadOnlyList<AiDecisionLedgerEntry>> QueryAsync(
            AiDecisionLedgerQuery query,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);

            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

            var filter = BuildFilter(query);

            IFindFluent<MongoAiDecisionLedgerEntryDocument, MongoAiDecisionLedgerEntryDocument> find = _entries
                .Find(filter)
                .SortBy(document => document.ExecutionId)
                .ThenBy(document => document.Sequence)
                .ThenBy(document => document.TimestampUtc);

            if (query.Limit is > 0)
            {
                find = find.Limit(query.Limit.Value);
            }

            var queryMeasurement = AiMongoAttributionDiagnostics.StartOperation(
                AiMongoAttributionOperations.LedgerQuery,
                AiMongoAttributionCommands.Find);

            try
            {
                var documents = await find
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                queryMeasurement.Succeed(documents.Count);

                return documents.ConvertAll(document => document.ToEntry());
            }
            catch (OperationCanceledException)
            {
                queryMeasurement.Cancel();
                throw;
            }
            catch
            {
                queryMeasurement.Fail();
                throw;
            }
        }

        private async Task EnsureIndexesAsync(
            CancellationToken cancellationToken)
        {
            if (!_options.CreateIndexes)
            {
                return;
            }

            await _indexCreationTask.Value.ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
        }

        private async Task<long> GetNextSequenceAsync(
            string executionId,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            var filter = Builders<MongoAiDecisionLedgerSequenceDocument>.Filter
                .Eq(document => document.Id, executionId);

            var update = Builders<MongoAiDecisionLedgerSequenceDocument>.Update
                .Inc(document => document.CurrentSequence, 1)
                .SetOnInsert(document => document.Id, executionId)
                .SetOnInsert(document => document.ExecutionId, executionId);

            var options = new FindOneAndUpdateOptions<MongoAiDecisionLedgerSequenceDocument>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After
            };

            var sequenceMeasurement = AiMongoAttributionDiagnostics.StartOperation(
                AiMongoAttributionOperations.LedgerSequenceNext,
                AiMongoAttributionCommands.FindAndModify,
                requestedDocuments: 1);

            try
            {
                var sequence = await _sequences
                    .FindOneAndUpdateAsync(
                        filter,
                        update,
                        options,
                        cancellationToken)
                    .ConfigureAwait(false);
                sequenceMeasurement.Succeed(sequence is null ? 0 : 1);

                return sequence.CurrentSequence;
            }
            catch (OperationCanceledException)
            {
                sequenceMeasurement.Cancel();
                throw;
            }
            catch (MongoException exception) when (IsDuplicateKey(exception))
            {
                sequenceMeasurement.Fail(duplicateKeyRetry: true);

                // Concurrent upsert race:
                // another worker inserted the sequence document between our find and insert.
                // Retry the same atomic increment now that the document exists.
                var retryOptions = new FindOneAndUpdateOptions<MongoAiDecisionLedgerSequenceDocument>
                {
                    IsUpsert = false,
                    ReturnDocument = ReturnDocument.After
                };

                var retryMeasurement = AiMongoAttributionDiagnostics.StartOperation(
                    AiMongoAttributionOperations.LedgerSequenceNext,
                    AiMongoAttributionCommands.FindAndModify,
                    requestedDocuments: 1);

                try
                {
                    var sequence = await _sequences
                        .FindOneAndUpdateAsync(
                            filter,
                            update,
                            retryOptions,
                            cancellationToken)
                        .ConfigureAwait(false);
                    retryMeasurement.Succeed(sequence is null ? 0 : 1);

                    if (sequence is null)
                    {
                        // Extremely defensive fallback:
                        // if the document disappeared between duplicate-key and retry,
                        // restart the safe path.
                        return await GetNextSequenceAsync(
                                executionId,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    return sequence.CurrentSequence;
                }
                catch (OperationCanceledException)
                {
                    retryMeasurement.Cancel();
                    throw;
                }
                catch
                {
                    retryMeasurement.Fail();
                    throw;
                }
            }
            catch
            {
                sequenceMeasurement.Fail();
                throw;
            }
        }

        private static bool IsDuplicateKey(
            MongoException exception)
        {
            if (exception is MongoCommandException commandException)
            {
                return commandException.Code == 11000 ||
                       string.Equals(
                           commandException.CodeName,
                           "DuplicateKey",
                           StringComparison.OrdinalIgnoreCase) ||
                       commandException.Message.Contains(
                           "E11000",
                           StringComparison.OrdinalIgnoreCase);
            }

            if (exception is MongoWriteException writeException)
            {
                return writeException.WriteError?.Category == ServerErrorCategory.DuplicateKey ||
                       writeException.WriteError?.Code == 11000 ||
                       writeException.Message.Contains(
                           "E11000",
                           StringComparison.OrdinalIgnoreCase);
            }

            return exception.Message.Contains(
                "E11000",
                StringComparison.OrdinalIgnoreCase);
        }

        private async Task CreateIndexesAsync(
            CancellationToken cancellationToken)
        {
            var entryIndexModels = new[]
            {
                new CreateIndexModel<MongoAiDecisionLedgerEntryDocument>(
                    Builders<MongoAiDecisionLedgerEntryDocument>
                        .IndexKeys
                        .Ascending(document => document.ExecutionId)
                        .Ascending(document => document.Sequence),
                    new CreateIndexOptions
                    {
                        Unique = true,
                        Name = "ux_execution_id_sequence"
                    }),

                new CreateIndexModel<MongoAiDecisionLedgerEntryDocument>(
                    Builders<MongoAiDecisionLedgerEntryDocument>
                        .IndexKeys
                        .Ascending(document => document.ExecutionId)
                        .Ascending(document => document.TimestampUtc),
                    new CreateIndexOptions
                    {
                        Name = "ix_execution_id_timestamp"
                    }),

                new CreateIndexModel<MongoAiDecisionLedgerEntryDocument>(
                    Builders<MongoAiDecisionLedgerEntryDocument>
                        .IndexKeys
                        .Ascending(document => document.ExecutionId)
                        .Ascending(document => document.Category),
                    new CreateIndexOptions
                    {
                        Name = "ix_execution_id_category"
                    }),

                new CreateIndexModel<MongoAiDecisionLedgerEntryDocument>(
                    Builders<MongoAiDecisionLedgerEntryDocument>
                        .IndexKeys
                        .Ascending(document => document.ExecutionId)
                        .Ascending(document => document.StepId),
                    new CreateIndexOptions
                    {
                        Name = "ix_execution_id_step_id"
                    }),

                new CreateIndexModel<MongoAiDecisionLedgerEntryDocument>(
                    Builders<MongoAiDecisionLedgerEntryDocument>
                        .IndexKeys
                        .Ascending(document => document.CorrelationId),
                    new CreateIndexOptions
                    {
                        Name = "ix_correlation_id"
                    }),

                new CreateIndexModel<MongoAiDecisionLedgerEntryDocument>(
                    Builders<MongoAiDecisionLedgerEntryDocument>
                        .IndexKeys
                        .Ascending(document => document.TraceId),
                    new CreateIndexOptions
                    {
                        Name = "ix_trace_id"
                    }),

                new CreateIndexModel<MongoAiDecisionLedgerEntryDocument>(
                    Builders<MongoAiDecisionLedgerEntryDocument>
                        .IndexKeys
                        .Ascending(document => document.RuntimeInstanceId),
                    new CreateIndexOptions
                    {
                        Name = "ix_runtime_instance_id"
                    }),

                new CreateIndexModel<MongoAiDecisionLedgerEntryDocument>(
                    Builders<MongoAiDecisionLedgerEntryDocument>
                        .IndexKeys
                        .Ascending(document => document.WorkerId),
                    new CreateIndexOptions
                    {
                        Name = "ix_worker_id"
                    }),

                new CreateIndexModel<MongoAiDecisionLedgerEntryDocument>(
                    Builders<MongoAiDecisionLedgerEntryDocument>
                        .IndexKeys
                        .Ascending(document => document.PolicyKey),
                    new CreateIndexOptions
                    {
                        Name = "ix_policy_key"
                    }),

                new CreateIndexModel<MongoAiDecisionLedgerEntryDocument>(
                    Builders<MongoAiDecisionLedgerEntryDocument>
                        .IndexKeys
                        .Ascending(document => document.Operation)
                        .Ascending(document => document.TimestampUtc),
                    new CreateIndexOptions
                    {
                        Name = "ix_operation_timestamp"
                    })
            };

            await _entries.Indexes
                .CreateManyAsync(entryIndexModels, cancellationToken)
                .ConfigureAwait(false);

            var sequenceIndexModels = new[]
            {
                new CreateIndexModel<MongoAiDecisionLedgerSequenceDocument>(
                    Builders<MongoAiDecisionLedgerSequenceDocument>
                        .IndexKeys
                        .Ascending(document => document.ExecutionId),
                    new CreateIndexOptions
                    {
                        Unique = true,
                        Name = "ux_execution_id"
                    })
            };

            await _sequences.Indexes
                .CreateManyAsync(sequenceIndexModels, cancellationToken)
                .ConfigureAwait(false);
        }

        private static FilterDefinition<MongoAiDecisionLedgerEntryDocument> BuildFilter(
            AiDecisionLedgerQuery query)
        {
            var builder = Builders<MongoAiDecisionLedgerEntryDocument>.Filter;
            var filter = builder.Empty;

            if (!string.IsNullOrWhiteSpace(query.ExecutionId))
            {
                filter &= builder.Eq(document => document.ExecutionId, query.ExecutionId);
            }

            if (!string.IsNullOrWhiteSpace(query.RunId))
            {
                filter &= builder.Eq(document => document.RunId, query.RunId);
            }

            if (!string.IsNullOrWhiteSpace(query.StepId))
            {
                filter &= builder.Eq(document => document.StepId, query.StepId);
            }

            if (!string.IsNullOrWhiteSpace(query.StepKey))
            {
                filter &= builder.Eq(document => document.StepKey, query.StepKey);
            }

            if (query.Category.HasValue)
            {
                filter &= builder.Eq(document => document.Category, query.Category.Value);
            }

            if (!string.IsNullOrWhiteSpace(query.EventType))
            {
                filter &= builder.Eq(document => document.EventType, query.EventType);
            }

            if (query.Outcome.HasValue)
            {
                filter &= builder.Eq(document => document.Outcome, query.Outcome.Value);
            }

            if (!string.IsNullOrWhiteSpace(query.RuntimeInstanceId))
            {
                filter &= builder.Eq(document => document.RuntimeInstanceId, query.RuntimeInstanceId);
            }

            if (!string.IsNullOrWhiteSpace(query.WorkerId))
            {
                filter &= builder.Eq(document => document.WorkerId, query.WorkerId);
            }

            if (!string.IsNullOrWhiteSpace(query.PolicyKey))
            {
                filter &= builder.Eq(document => document.PolicyKey, query.PolicyKey);
            }

            if (!string.IsNullOrWhiteSpace(query.Provider))
            {
                filter &= builder.Eq(document => document.Provider, query.Provider);
            }

            if (!string.IsNullOrWhiteSpace(query.Model))
            {
                filter &= builder.Eq(document => document.Model, query.Model);
            }

            if (!string.IsNullOrWhiteSpace(query.Operation))
            {
                filter &= builder.Eq(document => document.Operation, query.Operation);
            }

            if (!string.IsNullOrWhiteSpace(query.CorrelationId))
            {
                filter &= builder.Eq(document => document.CorrelationId, query.CorrelationId);
            }

            if (!string.IsNullOrWhiteSpace(query.TraceId))
            {
                filter &= builder.Eq(document => document.TraceId, query.TraceId);
            }

            if (query.SequenceFrom.HasValue)
            {
                filter &= builder.Gte(document => document.Sequence, query.SequenceFrom.Value);
            }

            if (query.SequenceTo.HasValue)
            {
                filter &= builder.Lte(document => document.Sequence, query.SequenceTo.Value);
            }

            if (query.TimestampFromUtc.HasValue)
            {
                filter &= builder.Gte(document => document.TimestampUtc, query.TimestampFromUtc.Value);
            }

            if (query.TimestampToUtc.HasValue)
            {
                filter &= builder.Lte(document => document.TimestampUtc, query.TimestampToUtc.Value);
            }

            return filter;
        }
    }
}