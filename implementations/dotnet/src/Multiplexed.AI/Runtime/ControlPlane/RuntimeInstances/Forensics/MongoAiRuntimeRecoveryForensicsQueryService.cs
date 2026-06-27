using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.AI.Stores.Mongo;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// MongoDB-backed read-only query service for runtime recovery forensics.
    /// </summary>
    /// <remarks>
    /// This service is intentionally read-only. It exposes recovery evidence for MCP,
    /// dashboards, tests, and operators, but it must never be used to drive recovery.
    /// </remarks>
    public sealed class MongoAiRuntimeRecoveryForensicsQueryService : IAiRuntimeRecoveryForensicsQueryService
    {
        private const string ExecutionRecoveryFailedEventType = "execution.recovery.failed";

        private static readonly ProjectionDefinition<AiRuntimeRecoveryForensicsRecord, AiRuntimeRecoveryForensicsRecord> WithoutMongoIdProjection =
            Builders<AiRuntimeRecoveryForensicsRecord>.Projection
                .Exclude("_id");

        private readonly IMongoCollection<AiRuntimeRecoveryForensicsRecord> _collection;
        private readonly AiRuntimeRecoveryForensicsMongoOptions _options;

        /// <summary>
        /// Initializes a new instance of the <see cref="MongoAiRuntimeRecoveryForensicsQueryService"/> class.
        /// </summary>
        /// <param name="database">The MongoDB database.</param>
        /// <param name="options">The runtime recovery forensics MongoDB options.</param>
        public MongoAiRuntimeRecoveryForensicsQueryService(
            IMongoDatabase database,
            IOptions<AiRuntimeRecoveryForensicsMongoOptions> options)
        {
            ArgumentNullException.ThrowIfNull(database);
            ArgumentNullException.ThrowIfNull(options);

            _options = options.Value;
            _collection = database.GetCollection<AiRuntimeRecoveryForensicsRecord>(_options.CollectionName);
        }

        /// <inheritdoc />
        public async Task<AiRuntimeRecoveryForensicsReadModel?> GetByForensicsIdAsync(
            string forensicsId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(forensicsId);

            var filter = Builders<AiRuntimeRecoveryForensicsRecord>.Filter.Eq(
                x => x.Identity.ForensicsId,
                forensicsId);

            var record = await MongoRuntimeResilience.ExecuteInfrastructureAsync(
                    token => _collection
                        .Find(filter)
                        .Project(WithoutMongoIdProjection)
                        .FirstOrDefaultAsync(token),
                    "runtime-recovery-forensics-query-get-by-forensics-id",
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return record is null
                ? null
                : ToReadModel(record);
        }

        /// <inheritdoc />
        public async Task<AiRuntimeRecoveryForensicsQueryResult> SearchAsync(
            AiRuntimeRecoveryForensicsQuery query,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);

            var safeLimit = Math.Clamp(query.Limit, 1, 500);
            var filter = BuildFilter(query);

            var records = await MongoRuntimeResilience.ExecuteInfrastructureAsync(
                    token => _collection
                        .Find(filter)
                        .SortByDescending(x => x.CreatedAtUtc)
                        .Limit(safeLimit)
                        .Project(WithoutMongoIdProjection)
                        .ToListAsync(token),
                    "runtime-recovery-forensics-query-search",
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return new AiRuntimeRecoveryForensicsQueryResult
            {
                Limit = safeLimit,
                Items = records
                    .Select(ToReadModel)
                    .ToList()
            };
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AiRuntimeRecoveryForensicsTimelineItem>> GetTimelineAsync(
            string forensicsId,
            CancellationToken cancellationToken = default)
        {
            var model = await GetByForensicsIdAsync(
                    forensicsId,
                    cancellationToken)
                .ConfigureAwait(false);

            return model?.Timeline ?? [];
        }

        /// <summary>
        /// Builds a MongoDB filter from read-only query criteria.
        /// </summary>
        /// <param name="query">The query criteria.</param>
        /// <returns>The MongoDB filter.</returns>
        private static FilterDefinition<AiRuntimeRecoveryForensicsRecord> BuildFilter(
            AiRuntimeRecoveryForensicsQuery query)
        {
            var builder = Builders<AiRuntimeRecoveryForensicsRecord>.Filter;
            var filters = new List<FilterDefinition<AiRuntimeRecoveryForensicsRecord>>();

            if (!string.IsNullOrWhiteSpace(query.ForensicsId))
            {
                filters.Add(builder.Eq(x => x.Identity.ForensicsId, query.ForensicsId));
            }

            if (!string.IsNullOrWhiteSpace(query.ExecutionId))
            {
                filters.Add(builder.Eq(x => x.Identity.ExecutionId, query.ExecutionId));
            }

            if (!string.IsNullOrWhiteSpace(query.SharedRunId))
            {
                filters.Add(builder.Eq(x => x.Identity.SharedRunId, query.SharedRunId));
            }

            if (!string.IsNullOrWhiteSpace(query.TenantId))
            {
                filters.Add(builder.Eq(x => x.Identity.TenantId, query.TenantId));
            }

            if (!string.IsNullOrWhiteSpace(query.ControlPlaneId))
            {
                filters.Add(builder.Eq(x => x.Identity.ControlPlaneId, query.ControlPlaneId));
            }

            if (!string.IsNullOrWhiteSpace(query.RuntimeFailureIncidentId))
            {
                filters.Add(builder.Eq(x => x.Failure!.RuntimeFailureIncidentId, query.RuntimeFailureIncidentId));
            }

            if (!string.IsNullOrWhiteSpace(query.RuntimeInstanceId))
            {
                filters.Add(
                    builder.Or(
                        builder.Eq(x => x.Failure!.FailedRuntimeInstanceId, query.RuntimeInstanceId),
                        builder.Eq(x => x.Replacement!.ReplacementRuntimeInstanceId, query.RuntimeInstanceId)));
            }

            if (!string.IsNullOrWhiteSpace(query.EventType))
            {
                filters.Add(
                    builder.ElemMatch(
                        x => x.Events,
                        x => x.EventType == query.EventType));
            }

            if (query.RecentFailuresOnly)
            {
                filters.Add(
                    builder.ElemMatch(
                        x => x.Events,
                        x => x.EventType == ExecutionRecoveryFailedEventType));
            }

            if (query.CreatedFromUtc.HasValue)
            {
                filters.Add(builder.Gte(x => x.CreatedAtUtc, query.CreatedFromUtc.Value));
            }

            if (query.CreatedToUtc.HasValue)
            {
                filters.Add(builder.Lte(x => x.CreatedAtUtc, query.CreatedToUtc.Value));
            }

            return filters.Count == 0
                ? builder.Empty
                : builder.And(filters);
        }

        /// <summary>
        /// Converts a persisted forensics record into a query read model.
        /// </summary>
        /// <param name="record">The persisted record.</param>
        /// <returns>The read model.</returns>
        private static AiRuntimeRecoveryForensicsReadModel ToReadModel(
            AiRuntimeRecoveryForensicsRecord record)
        {
            return new AiRuntimeRecoveryForensicsReadModel
            {
                ForensicsId = record.Identity.ForensicsId,
                ExecutionId = record.Identity.ExecutionId,
                SharedRunId = record.Identity.SharedRunId,
                TenantId = record.Identity.TenantId,
                ControlPlaneId = record.Identity.ControlPlaneId,
                CreatedAtUtc = record.CreatedAtUtc,
                UpdatedAtUtc = record.UpdatedAtUtc,
                Timeline = record.Events
                    .OrderBy(x => x.TimestampUtc)
                    .Select(ToTimelineItem)
                    .ToList(),
                Record = record
            };
        }

        /// <summary>
        /// Converts a persisted forensics event into a timeline item.
        /// </summary>
        /// <param name="evt">The persisted event.</param>
        /// <returns>The timeline item.</returns>
        private static AiRuntimeRecoveryForensicsTimelineItem ToTimelineItem(
            AiRuntimeRecoveryForensicsEvent evt)
        {
            return new AiRuntimeRecoveryForensicsTimelineItem
            {
                EventId = evt.EventId,
                ForensicsId = evt.ForensicsId,
                TimestampUtc = evt.TimestampUtc,
                EventType = evt.EventType,
                Outcome = evt.Outcome,
                Reason = evt.Reason,
                ExecutionId = evt.ExecutionId,
                SharedRunId = evt.SharedRunId,
                LocalRunId = evt.LocalRunId,
                RuntimeInstanceId = evt.RuntimeInstanceId,
                Metadata = evt.Metadata
            };
        }
    }
}
