using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Multiplexed.AI.Stores.Mongo;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure
{
    /// <summary>
    /// Stores immutable runtime-pool failure observations in MongoDB so independently hosted
    /// parent pools and their control plane share one failure authority.
    /// </summary>
    public sealed class MongoAiRuntimePoolFailureJournal : IAiRuntimePoolFailureJournal
    {
        private readonly IMongoCollection<MongoAiRuntimePoolFailureDocument> collection;
        private readonly AiRuntimePoolFailureJournalMongoOptions options;
        private readonly SemaphoreSlim indexInitializationLock = new(1, 1);
        private bool indexesInitialized;

        public MongoAiRuntimePoolFailureJournal(
            IMongoDatabase database,
            IOptions<AiRuntimePoolFailureJournalMongoOptions> options)
        {
            ArgumentNullException.ThrowIfNull(database);
            ArgumentNullException.ThrowIfNull(options);

            this.options = options.Value;
            ArgumentException.ThrowIfNullOrWhiteSpace(this.options.CollectionName);

            this.collection = database.GetCollection<MongoAiRuntimePoolFailureDocument>(
                this.options.CollectionName);
        }

        public async Task<AiRuntimePoolFailureObservation> RecordAsync(
            AiRuntimePoolFailureObservation observation,
            CancellationToken cancellationToken = default)
        {
            var normalized =
                AiRuntimePoolFailureObservationNormalization.Normalize(observation);

            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await this.collection
                    .InsertOneAsync(
                        MongoAiRuntimePoolFailureDocument.FromObservation(normalized),
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                return normalized;
            }
            catch (MongoException exception) when (IsDuplicateKey(exception))
            {
                var existing =
                    await GetByFailureIdCoreAsync(
                            normalized.FailureId,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (existing is not null &&
                    AiRuntimePoolFailureObservationNormalization.AreEquivalent(
                        existing,
                        normalized))
                {
                    return existing;
                }

                throw new AiRuntimePoolFailureConflictException(
                    normalized.FailureId);
            }
        }

        public async Task<AiRuntimePoolFailureObservation?> GetByFailureIdAsync(
            string failureId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(failureId);
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

            return await GetByFailureIdCoreAsync(
                    failureId.Trim(),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public Task<IReadOnlyList<AiRuntimePoolFailureObservation>> ListByHostIdAsync(
            string hostId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(hostId);

            return QueryAsync(
                Builders<MongoAiRuntimePoolFailureDocument>.Filter.Eq(
                    document => document.Observation.HostId,
                    hostId.Trim()),
                cancellationToken);
        }

        public Task<IReadOnlyList<AiRuntimePoolFailureObservation>>
            ListByRuntimeInstanceIdAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            return QueryAsync(
                Builders<MongoAiRuntimePoolFailureDocument>.Filter.Eq(
                    document => document.Observation.RuntimeInstanceId,
                    runtimeInstanceId.Trim()),
                cancellationToken);
        }

        private async Task<AiRuntimePoolFailureObservation?> GetByFailureIdCoreAsync(
            string failureId,
            CancellationToken cancellationToken)
        {
            var document =
                await this.collection
                    .Find(
                        Builders<MongoAiRuntimePoolFailureDocument>.Filter.Eq(
                            item => item.Id,
                            failureId))
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

            return document?.Observation;
        }

        private async Task<IReadOnlyList<AiRuntimePoolFailureObservation>> QueryAsync(
            FilterDefinition<MongoAiRuntimePoolFailureDocument> filter,
            CancellationToken cancellationToken)
        {
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

            var sort =
                Builders<MongoAiRuntimePoolFailureDocument>.Sort
                    .Ascending(document => document.Observation.ObservedAtUtc)
                    .Ascending(document => document.Id);

            var documents =
                await this.collection
                    .Find(filter)
                    .Sort(sort)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

            return documents
                .Select(document => document.Observation)
                .ToArray();
        }

        private async Task EnsureIndexesAsync(CancellationToken cancellationToken)
        {
            if (this.indexesInitialized || !this.options.EnsureIndexes)
            {
                return;
            }

            await this.indexInitializationLock
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                if (this.indexesInitialized)
                {
                    return;
                }

                await MongoRuntimeResilience.ExecuteInfrastructureAsync(
                        async token =>
                        {
                            var indexes = new[]
                            {
                                new CreateIndexModel<MongoAiRuntimePoolFailureDocument>(
                                    Builders<MongoAiRuntimePoolFailureDocument>.IndexKeys
                                        .Ascending(document => document.Observation.PoolId)
                                        .Ascending(document => document.Observation.ObservedAtUtc)
                                        .Ascending(document => document.Id),
                                    new CreateIndexOptions
                                    {
                                        Name = "ix_failure_poolId_observedAt_failureId"
                                    }),
                                new CreateIndexModel<MongoAiRuntimePoolFailureDocument>(
                                    Builders<MongoAiRuntimePoolFailureDocument>.IndexKeys
                                        .Ascending(document => document.Observation.HostId)
                                        .Ascending(document => document.Observation.ObservedAtUtc)
                                        .Ascending(document => document.Id),
                                    new CreateIndexOptions
                                    {
                                        Name = "ix_failure_hostId_observedAt_failureId"
                                    }),
                                new CreateIndexModel<MongoAiRuntimePoolFailureDocument>(
                                    Builders<MongoAiRuntimePoolFailureDocument>.IndexKeys
                                        .Ascending(document => document.Observation.RuntimeInstanceId)
                                        .Ascending(document => document.Observation.ObservedAtUtc)
                                        .Ascending(document => document.Id),
                                    new CreateIndexOptions
                                    {
                                        Name = "ix_failure_runtimeInstanceId_observedAt_failureId",
                                        Sparse = true
                                    })
                            };

                            await this.collection.Indexes
                                .CreateManyAsync(indexes, token)
                                .ConfigureAwait(false);
                        },
                        "runtime-pool-failure-journal-indexes",
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                this.indexesInitialized = true;
            }
            finally
            {
                this.indexInitializationLock.Release();
            }
        }

        private static bool IsDuplicateKey(MongoException exception)
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
                return writeException.WriteError?.Category ==
                           ServerErrorCategory.DuplicateKey ||
                       writeException.WriteError?.Code == 11000 ||
                       writeException.Message.Contains(
                           "E11000",
                           StringComparison.OrdinalIgnoreCase);
            }

            return exception.Message.Contains(
                "E11000",
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
