using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using StackExchange.Redis;

namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Provides a Redis-backed implementation of <see cref="IAiRuntimeScaleOutRequestStore" />.
    /// </summary>
    /// <remarks>
    /// This store persists live runtime scale-out coordination state in Redis.
    /// It makes <c>RequestScaleOut</c> decisions observable by MCP tools, diagnostics,
    /// dashboards, and future scaler adapters without coupling the runtime core to Kubernetes.
    /// </remarks>
    public sealed class RedisAiRuntimeScaleOutRequestStore : IAiRuntimeScaleOutRequestStore
    {
        /// <summary>
        /// JSON serializer options used for metadata persistence.
        /// </summary>
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        /// <summary>
        /// The Redis connection multiplexer.
        /// </summary>
        private readonly IConnectionMultiplexer connection;

        /// <summary>
        /// Executes Lua scripts using cached SHA values with automatic NOSCRIPT reload.
        /// </summary>
        private readonly RedisAiRuntimeScaleOutRequestStoreScriptCache scriptCache;

        /// <summary>
        /// Redis-specific and inherited scale-out request store options.
        /// </summary>
        private readonly RedisAiRuntimeScaleOutRequestStoreOptions options;

        /// <summary>
        /// Initializes a new instance of the <see cref="RedisAiRuntimeScaleOutRequestStore" /> class.
        /// </summary>
        /// <param name="connection">The Redis connection multiplexer.</param>
        /// <param name="options">The Redis scale-out request store options.</param>
        /// <param name="scriptCache">The Redis Lua script cache.</param>
        public RedisAiRuntimeScaleOutRequestStore(
            IConnectionMultiplexer connection,
            IOptions<RedisAiRuntimeScaleOutRequestStoreOptions>? options = null,
            RedisAiRuntimeScaleOutRequestStoreScriptCache? scriptCache = null)
        {
            this.connection =
                connection
                ?? throw new ArgumentNullException(nameof(connection));

            this.options =
                options?.Value
                ?? new RedisAiRuntimeScaleOutRequestStoreOptions();

            this.scriptCache =
                scriptCache
                ?? new RedisAiRuntimeScaleOutRequestStoreScriptCache(connection);
        }

        /// <inheritdoc />
        public async Task<AiRuntimeScaleOutRequestRecord> CreateAsync(
            AiRuntimeScaleOutRequestRecord request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            cancellationToken.ThrowIfCancellationRequested();

            var normalized =
                this.NormalizeForCreate(request);

            var database =
                this.GetDatabase();

            var hashEntries =
                ToHashEntries(normalized);

            var values =
                CreateScriptValues(
                    normalized,
                    hashEntries,
                    this.options);

            var result =
                await this.scriptCache
                    .ExecuteCreateAsync(
                        database,
                        new RedisKey[]
                        {
                            this.GetRequestKey(normalized.ControlPlaneId, normalized.RequestId),
                            this.GetAllIndexKey(normalized.ControlPlaneId),
                            this.GetPendingIndexKey(normalized.ControlPlaneId),
                            this.GetSharedRunKey(normalized.ControlPlaneId, normalized.SharedRunId),
                            this.GetDedupKey(normalized),
                            this.GetRequestControlPlaneIndexKey(normalized.RequestId)
                        },
                        values)
                    .ConfigureAwait(false);

            var createdRequestId =
                result.ToString();

            if (!string.IsNullOrWhiteSpace(createdRequestId) &&
                !string.Equals(createdRequestId, normalized.RequestId, StringComparison.Ordinal))
            {
                var existing =
                    await this
                        .GetAsync(
                            createdRequestId,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (existing is not null)
                {
                    return existing;
                }
            }

            var created =
                await this
                    .GetAsync(
                        normalized.RequestId,
                        cancellationToken)
                    .ConfigureAwait(false);

            return created ?? normalized;
        }

        /// <inheritdoc />
        public async Task<AiRuntimeScaleOutRequestRecord?> GetAsync(
            string requestId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

            cancellationToken.ThrowIfCancellationRequested();

            var database =
                this.GetDatabase();

            var requestKey =
                await this
                    .FindRequestKeyAsync(
                        database,
                        requestId)
                    .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(requestKey))
            {
                return null;
            }

            var entries =
                await database
                    .HashGetAllAsync(requestKey)
                    .ConfigureAwait(false);

            if (entries.Length == 0)
            {
                return null;
            }

            return FromHashEntries(entries);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyCollection<AiRuntimeScaleOutRequestRecord>> ListAsync(
            AiRuntimeScaleOutRequestQuery query,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);

            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(query.ControlPlaneId))
            {
                return Array.Empty<AiRuntimeScaleOutRequestRecord>();
            }

            var database =
                this.GetDatabase();

            var requestIds =
                await database
                    .SortedSetRangeByRankAsync(
                        this.GetAllIndexKey(query.ControlPlaneId),
                        start: 0,
                        stop: this.GetIndexScanStop(query),
                        order: Order.Descending)
                    .ConfigureAwait(false);

            return await this
                .LoadAndFilterAsync(
                    database,
                    query,
                    requestIds,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyCollection<AiRuntimeScaleOutRequestRecord>> ListPendingAsync(
            AiRuntimeScaleOutRequestQuery query,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);

            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(query.ControlPlaneId))
            {
                return Array.Empty<AiRuntimeScaleOutRequestRecord>();
            }

            var database =
                this.GetDatabase();

            var requestIds =
                await database
                    .SortedSetRangeByRankAsync(
                        this.GetPendingIndexKey(query.ControlPlaneId),
                        start: 0,
                        stop: this.GetIndexScanStop(query),
                        order: Order.Ascending)
                    .ConfigureAwait(false);

            var pendingQuery =
                CopyQuery(query);

            if (pendingQuery.Statuses.Count == 0)
            {
                pendingQuery.Statuses.Add(AiRuntimeScaleOutRequestStatus.Pending);
            }

            return await this
                .LoadAndFilterAsync(
                    database,
                    pendingQuery,
                    requestIds,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task<bool> MarkObservedAsync(
            string requestId,
            string observedBy,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
            ArgumentException.ThrowIfNullOrWhiteSpace(observedBy);

            return this.TransitionAsync(
                requestId,
                AiRuntimeScaleOutRequestStatus.Observed,
                "observedAtUtc",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["observedBy"] = observedBy
                },
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<bool> MarkFulfilledAsync(
            string requestId,
            string fulfilledBy,
            string? runtimeInstanceId = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
            ArgumentException.ThrowIfNullOrWhiteSpace(fulfilledBy);

            var fields =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["fulfilledBy"] = fulfilledBy
                };

            if (!string.IsNullOrWhiteSpace(runtimeInstanceId))
            {
                fields["fulfilledRuntimeInstanceId"] =
                    runtimeInstanceId;
            }

            return this.TransitionAsync(
                requestId,
                AiRuntimeScaleOutRequestStatus.Fulfilled,
                "fulfilledAtUtc",
                fields,
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<bool> MarkRejectedAsync(
            string requestId,
            string rejectedBy,
            string reason,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
            ArgumentException.ThrowIfNullOrWhiteSpace(rejectedBy);
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);

            return this.TransitionAsync(
                requestId,
                AiRuntimeScaleOutRequestStatus.Rejected,
                "rejectedAtUtc",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["rejectedBy"] = rejectedBy,
                    ["rejectionReason"] = reason
                },
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<bool> MarkExpiredAsync(
            string requestId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

            return this.TransitionAsync(
                requestId,
                AiRuntimeScaleOutRequestStatus.Expired,
                "expiredAtUtc",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<bool> MarkCancelledAsync(
            string requestId,
            string cancelledBy,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
            ArgumentException.ThrowIfNullOrWhiteSpace(cancelledBy);

            return this.TransitionAsync(
                requestId,
                AiRuntimeScaleOutRequestStatus.Cancelled,
                "cancelledAtUtc",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["cancelledBy"] = cancelledBy
                },
                cancellationToken);
        }

        /// <summary>
        /// Transitions a scale-out request to a new lifecycle status.
        /// </summary>
        /// <param name="requestId">The scale-out request identifier.</param>
        /// <param name="targetStatus">The target lifecycle status.</param>
        /// <param name="timestampField">The hash field that receives the transition timestamp.</param>
        /// <param name="additionalFields">Additional hash fields to update.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><see langword="true" /> when the transition was applied; otherwise, <see langword="false" />.</returns>
        private async Task<bool> TransitionAsync(
            string requestId,
            AiRuntimeScaleOutRequestStatus targetStatus,
            string timestampField,
            IReadOnlyDictionary<string, string> additionalFields,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var database =
                this.GetDatabase();

            var requestKey =
                await this
                    .FindRequestKeyAsync(
                        database,
                        requestId)
                    .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(requestKey))
            {
                return false;
            }

            var controlPlaneId =
                await database
                    .HashGetAsync(
                        requestKey,
                        "controlPlaneId")
                    .ConfigureAwait(false);

            if (controlPlaneId.IsNullOrEmpty)
            {
                return false;
            }

            var values =
                new List<RedisValue>
                {
                    targetStatus.ToString(),
                    timestampField,
                    FormatDate(DateTimeOffset.UtcNow)
                };

            foreach (var pair in additionalFields)
            {
                values.Add(pair.Key);
                values.Add(pair.Value);
            }

            var result =
                await this.scriptCache
                    .ExecuteTransitionAsync(
                        database,
                        new RedisKey[]
                        {
                            requestKey,
                            this.GetPendingIndexKey(controlPlaneId.ToString())
                        },
                        values.ToArray())
                    .ConfigureAwait(false);

            return string.Equals(
                result.ToString(),
                "updated",
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Loads request records by id and applies query filters.
        /// </summary>
        /// <param name="database">The Redis database.</param>
        /// <param name="query">The query filters.</param>
        /// <param name="requestIds">The candidate request identifiers.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The matching request records.</returns>
        private async Task<IReadOnlyCollection<AiRuntimeScaleOutRequestRecord>> LoadAndFilterAsync(
            IDatabase database,
            AiRuntimeScaleOutRequestQuery query,
            IReadOnlyCollection<RedisValue> requestIds,
            CancellationToken cancellationToken)
        {
            var results =
                new List<AiRuntimeScaleOutRequestRecord>();

            var maxResults =
                GetMaxResults(
                    query,
                    this.options);

            foreach (var redisRequestId in requestIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (redisRequestId.IsNullOrEmpty)
                {
                    continue;
                }

                var requestId =
                    redisRequestId.ToString();

                var requestKey =
                    this.GetRequestKey(
                        query.ControlPlaneId!,
                        requestId);

                var entries =
                    await database
                        .HashGetAllAsync(requestKey)
                        .ConfigureAwait(false);

                if (entries.Length == 0)
                {
                    await this
                        .RemoveMissingIndexReferencesAsync(
                            database,
                            query.ControlPlaneId!,
                            requestId)
                        .ConfigureAwait(false);

                    continue;
                }

                var record =
                    FromHashEntries(entries);

                if (!MatchesQuery(record, query))
                {
                    continue;
                }

                results.Add(record);

                if (results.Count >= maxResults)
                {
                    break;
                }
            }

            return results;
        }

        /// <summary>
        /// Removes stale index references for a request hash that no longer exists.
        /// </summary>
        /// <param name="database">The Redis database.</param>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <param name="requestId">The scale-out request identifier.</param>
        /// <returns>A task representing the asynchronous cleanup operation.</returns>
        private async Task RemoveMissingIndexReferencesAsync(
            IDatabase database,
            string controlPlaneId,
            string requestId)
        {
            await database
                .SortedSetRemoveAsync(
                    this.GetAllIndexKey(controlPlaneId),
                    requestId)
                .ConfigureAwait(false);

            await database
                .SortedSetRemoveAsync(
                    this.GetPendingIndexKey(controlPlaneId),
                    requestId)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Normalizes and validates a request before creating it in Redis.
        /// </summary>
        /// <param name="request">The request to normalize.</param>
        /// <returns>The normalized request.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when required request identity fields are missing.
        /// </exception>
        private AiRuntimeScaleOutRequestRecord NormalizeForCreate(
            AiRuntimeScaleOutRequestRecord request)
        {
            var now =
                DateTimeOffset.UtcNow;

            var normalized =
                Clone(request);

            if (string.IsNullOrWhiteSpace(normalized.RequestId))
            {
                normalized.RequestId =
                    Guid.NewGuid().ToString("N");
            }

            if (string.IsNullOrWhiteSpace(normalized.ControlPlaneId))
            {
                throw new ArgumentException(
                    "Scale-out request control-plane id is required.",
                    nameof(request));
            }

            if (string.IsNullOrWhiteSpace(normalized.SharedRunId))
            {
                throw new ArgumentException(
                    "Scale-out request shared run id is required.",
                    nameof(request));
            }

            normalized.Status =
                AiRuntimeScaleOutRequestStatus.Pending;

            if (normalized.CreatedAtUtc == default)
            {
                normalized.CreatedAtUtc =
                    now;
            }

            if (normalized.ExpiresAtUtc is null &&
                this.options.DefaultTtl > TimeSpan.Zero)
            {
                normalized.ExpiresAtUtc =
                    normalized.CreatedAtUtc.Add(this.options.DefaultTtl);
            }

            return normalized;
        }

        /// <summary>
        /// Gets the Redis database used by this store.
        /// </summary>
        /// <returns>The configured Redis database.</returns>
        private IDatabase GetDatabase()
        {
            return this.connection.GetDatabase(this.options.Database ?? -1);
        }

        /// <summary>
        /// Finds the Redis hash key for a request identifier.
        /// </summary>
        /// <param name="database">The Redis database.</param>
        /// <param name="requestId">The scale-out request identifier.</param>
        /// <returns>The Redis key string when found; otherwise, <see langword="null" />.</returns>
        private async Task<string?> FindRequestKeyAsync(
            IDatabase database,
            string requestId)
        {
            var controlPlaneIndexKey =
                this.GetRequestControlPlaneIndexKey(requestId);

            var controlPlaneId =
                await database
                    .StringGetAsync(controlPlaneIndexKey)
                    .ConfigureAwait(false);

            if (controlPlaneId.IsNullOrEmpty)
            {
                return null;
            }

            return this
                .GetRequestKey(
                    controlPlaneId.ToString(),
                    requestId)
                .ToString();
        }

        /// <summary>
        /// Gets the Redis key for a request hash.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <param name="requestId">The scale-out request identifier.</param>
        /// <returns>The Redis request hash key.</returns>
        private RedisKey GetRequestKey(
            string controlPlaneId,
            string requestId)
        {
            return $"{this.options.KeyPrefix}:{{{controlPlaneId}}}:scaleout:request:{requestId}";
        }

        /// <summary>
        /// Gets the Redis key for the all-requests sorted-set index.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <returns>The Redis all-requests index key.</returns>
        private RedisKey GetAllIndexKey(
            string controlPlaneId)
        {
            return $"{this.options.KeyPrefix}:{{{controlPlaneId}}}:scaleout:all";
        }

        /// <summary>
        /// Gets the Redis key for the pending-requests sorted-set index.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <returns>The Redis pending-requests index key.</returns>
        private RedisKey GetPendingIndexKey(
            string controlPlaneId)
        {
            return $"{this.options.KeyPrefix}:{{{controlPlaneId}}}:scaleout:pending";
        }

        /// <summary>
        /// Gets the Redis key used to resolve a scale-out request by shared run id.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <returns>The Redis shared run lookup key.</returns>
        private RedisKey GetSharedRunKey(
            string controlPlaneId,
            string sharedRunId)
        {
            return $"{this.options.KeyPrefix}:{{{controlPlaneId}}}:scaleout:sharedrun:{sharedRunId}";
        }

        /// <summary>
        /// Gets the Redis key used to resolve the control-plane id of a request id.
        /// </summary>
        /// <param name="requestId">The scale-out request identifier.</param>
        /// <returns>The Redis request-to-control-plane lookup key.</returns>
        private RedisKey GetRequestControlPlaneIndexKey(
            string requestId)
        {
            return $"{this.options.KeyPrefix}:scaleout:request-control-plane:{requestId}";
        }

        /// <summary>
        /// Gets the Redis key used to deduplicate equivalent pending scale-out requests.
        /// </summary>
        /// <param name="request">The scale-out request record.</param>
        /// <returns>The Redis deduplication key.</returns>
        private RedisKey GetDedupKey(
            AiRuntimeScaleOutRequestRecord request)
        {
            var tenant =
                NormalizeKeyPart(request.TenantId);

            var pipeline =
                NormalizeKeyPart(request.PipelineKey);

            var reason =
                NormalizeKeyPart(request.Reason);

            var provider =
                NormalizeKeyPart(request.ProviderHint);

            return $"{this.options.KeyPrefix}:{{{request.ControlPlaneId}}}:scaleout:dedup:{tenant}:{pipeline}:{reason}:{provider}";
        }

        /// <summary>
        /// Gets the inclusive Redis sorted-set scan stop index for a query.
        /// </summary>
        /// <param name="query">The query filters.</param>
        /// <returns>The inclusive sorted-set stop index.</returns>
        private long GetIndexScanStop(
            AiRuntimeScaleOutRequestQuery query)
        {
            var requested =
                query.MaxResults > 0
                    ? Math.Max(query.MaxResults, this.options.DefaultIndexScanLimit)
                    : this.options.DefaultIndexScanLimit;

            return Math.Max(
                0,
                requested - 1);
        }

        /// <summary>
        /// Gets the effective maximum result count for a query.
        /// </summary>
        /// <param name="query">The query options.</param>
        /// <param name="options">The store options.</param>
        /// <returns>The effective maximum result count.</returns>
        private static int GetMaxResults(
            AiRuntimeScaleOutRequestQuery query,
            RedisAiRuntimeScaleOutRequestStoreOptions options)
        {
            if (query.MaxResults <= 0)
            {
                return options.MaxListResults;
            }

            return Math.Min(
                query.MaxResults,
                options.MaxListResults);
        }

        /// <summary>
        /// Converts a scale-out request record into Redis hash entries.
        /// </summary>
        /// <param name="request">The request record.</param>
        /// <returns>The Redis hash entries.</returns>
        private static HashEntry[] ToHashEntries(
            AiRuntimeScaleOutRequestRecord request)
        {
            return new[]
            {
                new HashEntry("requestId", request.RequestId),
                new HashEntry("controlPlaneId", request.ControlPlaneId),
                new HashEntry("sharedRunId", request.SharedRunId),
                new HashEntry("tenantId", request.TenantId ?? string.Empty),
                new HashEntry("tenantGroupId", request.TenantGroupId ?? string.Empty),
                new HashEntry("pipelineKey", request.PipelineKey ?? string.Empty),

                new HashEntry("isolationMode", request.IsolationMode.ToString()),
                new HashEntry("preferDedicatedCapacity", request.PreferDedicatedCapacity.ToString(CultureInfo.InvariantCulture)),
                new HashEntry("allowSharedFallback", request.AllowSharedFallback.ToString(CultureInfo.InvariantCulture)),
                new HashEntry("maxRuntimeInstances", request.MaxRuntimeInstances?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                new HashEntry("runtimeInstanceIdPrefix", request.RuntimeInstanceIdPrefix ?? string.Empty),
                new HashEntry("workerCountPerInstance", request.WorkerCountPerInstance?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                new HashEntry("maxConcurrentRunsPerInstance", request.MaxConcurrentRunsPerInstance?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                new HashEntry("localQueueCapacity", request.LocalQueueCapacity?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),

                new HashEntry("status", request.Status.ToString()),
                new HashEntry("reason", request.Reason ?? string.Empty),
                new HashEntry("visibleInstanceCount", FormatInt(request.VisibleInstanceCount)),
                new HashEntry("availableInstanceCount", FormatInt(request.AvailableInstanceCount)),
                new HashEntry("currentInstanceCount", FormatInt(request.CurrentInstanceCount)),
                new HashEntry("maxInstanceCount", request.MaxInstanceCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                new HashEntry("requestedTargetInstanceCount", FormatInt(request.RequestedTargetInstanceCount)),
                new HashEntry("providerHint", request.ProviderHint ?? string.Empty),
                new HashEntry("requestedBy", request.RequestedBy ?? string.Empty),
                new HashEntry("source", request.Source ?? string.Empty),
                new HashEntry("correlationId", request.CorrelationId ?? string.Empty),
                new HashEntry("createdAtUtc", FormatDate(request.CreatedAtUtc)),
                new HashEntry("observedAtUtc", FormatNullableDate(request.ObservedAtUtc)),
                new HashEntry("fulfilledAtUtc", FormatNullableDate(request.FulfilledAtUtc)),
                new HashEntry("rejectedAtUtc", FormatNullableDate(request.RejectedAtUtc)),
                new HashEntry("expiredAtUtc", FormatNullableDate(request.ExpiredAtUtc)),
                new HashEntry("cancelledAtUtc", FormatNullableDate(request.CancelledAtUtc)),
                new HashEntry("expiresAtUtc", FormatNullableDate(request.ExpiresAtUtc)),
                new HashEntry("fulfilledRuntimeInstanceId", request.FulfilledRuntimeInstanceId ?? string.Empty),
                new HashEntry("observedBy", request.ObservedBy ?? string.Empty),
                new HashEntry("fulfilledBy", request.FulfilledBy ?? string.Empty),
                new HashEntry("rejectedBy", request.RejectedBy ?? string.Empty),
                new HashEntry("rejectionReason", request.RejectionReason ?? string.Empty),
                new HashEntry("metadata", JsonSerializer.Serialize(request.Metadata, JsonOptions))
            };
        }

        /// <summary>
        /// Converts Redis hash entries into a scale-out request record.
        /// </summary>
        /// <param name="entries">The Redis hash entries.</param>
        /// <returns>The scale-out request record.</returns>
        private static AiRuntimeScaleOutRequestRecord FromHashEntries(
            HashEntry[] entries)
        {
            var fields =
                entries.ToDictionary(
                    entry => entry.Name.ToString(),
                    entry => entry.Value.ToString(),
                    StringComparer.OrdinalIgnoreCase);

            var metadata =
                ParseMetadata(GetString(fields, "metadata"));

            var cancelledBy =
                GetString(fields, "cancelledBy");

            if (!string.IsNullOrWhiteSpace(cancelledBy))
            {
                metadata["cancelledBy"] =
                    cancelledBy;
            }

            return new AiRuntimeScaleOutRequestRecord
            {
                RequestId = GetString(fields, "requestId") ?? string.Empty,
                ControlPlaneId = GetString(fields, "controlPlaneId") ?? string.Empty,
                SharedRunId = GetString(fields, "sharedRunId") ?? string.Empty,
                TenantId = EmptyToNull(GetString(fields, "tenantId")),
                TenantGroupId = EmptyToNull(GetString(fields, "tenantGroupId")),
                PipelineKey = EmptyToNull(GetString(fields, "pipelineKey")),

                IsolationMode = ParseIsolationMode(GetString(fields, "isolationMode")),
                PreferDedicatedCapacity = ParseBool(GetString(fields, "preferDedicatedCapacity")),
                AllowSharedFallback = ParseBool(GetString(fields, "allowSharedFallback")),
                MaxRuntimeInstances = ParseNullableInt(GetString(fields, "maxRuntimeInstances")),
                RuntimeInstanceIdPrefix = EmptyToNull(GetString(fields, "runtimeInstanceIdPrefix")),
                WorkerCountPerInstance = ParseNullableInt(GetString(fields, "workerCountPerInstance")),
                MaxConcurrentRunsPerInstance = ParseNullableInt(GetString(fields, "maxConcurrentRunsPerInstance")),
                LocalQueueCapacity = ParseNullableInt(GetString(fields, "localQueueCapacity")),

                Status = ParseStatus(GetString(fields, "status")),
                Reason = GetString(fields, "reason") ?? string.Empty,
                VisibleInstanceCount = ParseInt(GetString(fields, "visibleInstanceCount")),
                AvailableInstanceCount = ParseInt(GetString(fields, "availableInstanceCount")),
                CurrentInstanceCount = ParseInt(GetString(fields, "currentInstanceCount")),
                MaxInstanceCount = ParseNullableInt(GetString(fields, "maxInstanceCount")),
                RequestedTargetInstanceCount = ParseInt(GetString(fields, "requestedTargetInstanceCount")),
                ProviderHint = EmptyToNull(GetString(fields, "providerHint")),
                RequestedBy = EmptyToNull(GetString(fields, "requestedBy")),
                Source = EmptyToNull(GetString(fields, "source")),
                CorrelationId = EmptyToNull(GetString(fields, "correlationId")),
                CreatedAtUtc = ParseDate(GetString(fields, "createdAtUtc")) ?? DateTimeOffset.MinValue,
                ObservedAtUtc = ParseDate(GetString(fields, "observedAtUtc")),
                FulfilledAtUtc = ParseDate(GetString(fields, "fulfilledAtUtc")),
                RejectedAtUtc = ParseDate(GetString(fields, "rejectedAtUtc")),
                ExpiredAtUtc = ParseDate(GetString(fields, "expiredAtUtc")),
                CancelledAtUtc = ParseDate(GetString(fields, "cancelledAtUtc")),
                ExpiresAtUtc = ParseDate(GetString(fields, "expiresAtUtc")),
                FulfilledRuntimeInstanceId = EmptyToNull(GetString(fields, "fulfilledRuntimeInstanceId")),
                ObservedBy = EmptyToNull(GetString(fields, "observedBy")),
                FulfilledBy = EmptyToNull(GetString(fields, "fulfilledBy")),
                RejectedBy = EmptyToNull(GetString(fields, "rejectedBy")),
                RejectionReason = EmptyToNull(GetString(fields, "rejectionReason")),
                Metadata = metadata
            };
        }

        /// <summary>
        /// Creates Redis script values for the atomic create operation.
        /// </summary>
        /// <param name="request">The scale-out request record.</param>
        /// <param name="hashEntries">The hash entries to persist.</param>
        /// <param name="options">The store options.</param>
        /// <returns>The Redis script argument values.</returns>
        private static RedisValue[] CreateScriptValues(
            AiRuntimeScaleOutRequestRecord request,
            IReadOnlyCollection<HashEntry> hashEntries,
            RedisAiRuntimeScaleOutRequestStoreOptions options)
        {
            var values =
                new List<RedisValue>
                {
                    request.RequestId,
                    request.SharedRunId,
                    request.CreatedAtUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                    GetExpirationSeconds(request, options).ToString(CultureInfo.InvariantCulture),
                    GetDeduplicationSeconds(options).ToString(CultureInfo.InvariantCulture),
                    options.EnableDeduplication ? "1" : "0",
                    request.ControlPlaneId
                };

            foreach (var entry in hashEntries)
            {
                values.Add(entry.Name);
                values.Add(entry.Value);
            }

            return values.ToArray();
        }

        /// <summary>
        /// Gets the record expiration in seconds.
        /// </summary>
        /// <param name="request">The scale-out request record.</param>
        /// <param name="options">The store options.</param>
        /// <returns>The expiration duration in seconds.</returns>
        private static long GetExpirationSeconds(
            AiRuntimeScaleOutRequestRecord request,
            RedisAiRuntimeScaleOutRequestStoreOptions options)
        {
            if (request.ExpiresAtUtc is not null)
            {
                var remaining =
                    request.ExpiresAtUtc.Value - DateTimeOffset.UtcNow;

                return remaining <= TimeSpan.Zero
                    ? 1
                    : Math.Max(
                        1,
                        Convert.ToInt64(Math.Ceiling(remaining.TotalSeconds)));
            }

            return options.DefaultTtl > TimeSpan.Zero
                ? Math.Max(
                    1,
                    Convert.ToInt64(Math.Ceiling(options.DefaultTtl.TotalSeconds)))
                : 0;
        }

        /// <summary>
        /// Gets the deduplication expiration in seconds.
        /// </summary>
        /// <param name="options">The store options.</param>
        /// <returns>The deduplication expiration duration in seconds.</returns>
        private static long GetDeduplicationSeconds(
            RedisAiRuntimeScaleOutRequestStoreOptions options)
        {
            return options.DeduplicationWindow > TimeSpan.Zero
                ? Math.Max(
                    1,
                    Convert.ToInt64(Math.Ceiling(options.DeduplicationWindow.TotalSeconds)))
                : 0;
        }

        /// <summary>
        /// Determines whether a request matches a query.
        /// </summary>
        /// <param name="request">The request record.</param>
        /// <param name="query">The query filters.</param>
        /// <returns><see langword="true" /> when the request matches the query; otherwise, <see langword="false" />.</returns>
        private static bool MatchesQuery(
            AiRuntimeScaleOutRequestRecord request,
            AiRuntimeScaleOutRequestQuery query)
        {
            if (!string.IsNullOrWhiteSpace(query.ControlPlaneId) &&
                !string.Equals(request.ControlPlaneId, query.ControlPlaneId, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(query.TenantId) &&
                !string.Equals(request.TenantId, query.TenantId, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(query.PipelineKey) &&
                !string.Equals(request.PipelineKey, query.PipelineKey, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(query.SharedRunId) &&
                !string.Equals(request.SharedRunId, query.SharedRunId, StringComparison.Ordinal))
            {
                return false;
            }

            if (query.Statuses.Count > 0 &&
                !query.Statuses.Contains(request.Status))
            {
                return false;
            }

            if (!query.IncludeExpired &&
                request.Status is AiRuntimeScaleOutRequestStatus.Expired)
            {
                return false;
            }

            if (query.CreatedAfterUtc is not null &&
                request.CreatedAtUtc < query.CreatedAfterUtc.Value)
            {
                return false;
            }

            if (query.CreatedBeforeUtc is not null &&
                request.CreatedAtUtc > query.CreatedBeforeUtc.Value)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Creates a defensive copy of a scale-out request record.
        /// </summary>
        /// <param name="request">The request to clone.</param>
        /// <returns>The cloned request.</returns>
        private static AiRuntimeScaleOutRequestRecord Clone(
            AiRuntimeScaleOutRequestRecord request)
        {
            return new AiRuntimeScaleOutRequestRecord
            {
                RequestId = request.RequestId,
                ControlPlaneId = request.ControlPlaneId,
                SharedRunId = request.SharedRunId,
                TenantId = request.TenantId,
                TenantGroupId = request.TenantGroupId,
                PipelineKey = request.PipelineKey,

                IsolationMode = request.IsolationMode,
                PreferDedicatedCapacity = request.PreferDedicatedCapacity,
                AllowSharedFallback = request.AllowSharedFallback,
                MaxRuntimeInstances = request.MaxRuntimeInstances,
                RuntimeInstanceIdPrefix = request.RuntimeInstanceIdPrefix,
                WorkerCountPerInstance = request.WorkerCountPerInstance,
                MaxConcurrentRunsPerInstance = request.MaxConcurrentRunsPerInstance,
                LocalQueueCapacity = request.LocalQueueCapacity,

                Status = request.Status,
                Reason = request.Reason,
                VisibleInstanceCount = request.VisibleInstanceCount,
                AvailableInstanceCount = request.AvailableInstanceCount,
                CurrentInstanceCount = request.CurrentInstanceCount,
                MaxInstanceCount = request.MaxInstanceCount,
                RequestedTargetInstanceCount = request.RequestedTargetInstanceCount,
                ProviderHint = request.ProviderHint,
                RequestedBy = request.RequestedBy,
                Source = request.Source,
                CorrelationId = request.CorrelationId,
                CreatedAtUtc = request.CreatedAtUtc,
                ObservedAtUtc = request.ObservedAtUtc,
                FulfilledAtUtc = request.FulfilledAtUtc,
                RejectedAtUtc = request.RejectedAtUtc,
                ExpiredAtUtc = request.ExpiredAtUtc,
                CancelledAtUtc = request.CancelledAtUtc,
                ExpiresAtUtc = request.ExpiresAtUtc,
                FulfilledRuntimeInstanceId = request.FulfilledRuntimeInstanceId,
                ObservedBy = request.ObservedBy,
                FulfilledBy = request.FulfilledBy,
                RejectedBy = request.RejectedBy,
                RejectionReason = request.RejectionReason,
                Metadata = new Dictionary<string, string>(
                    request.Metadata ?? new Dictionary<string, string>(),
                    StringComparer.OrdinalIgnoreCase)
            };
        }

        /// <summary>
        /// Copies a scale-out request query.
        /// </summary>
        /// <param name="query">The query to copy.</param>
        /// <returns>The copied query.</returns>
        private static AiRuntimeScaleOutRequestQuery CopyQuery(
            AiRuntimeScaleOutRequestQuery query)
        {
            return new AiRuntimeScaleOutRequestQuery
            {
                ControlPlaneId = query.ControlPlaneId,
                TenantId = query.TenantId,
                PipelineKey = query.PipelineKey,
                SharedRunId = query.SharedRunId,
                Statuses = new HashSet<AiRuntimeScaleOutRequestStatus>(query.Statuses),
                MaxResults = query.MaxResults,
                IncludeExpired = query.IncludeExpired,
                CreatedAfterUtc = query.CreatedAfterUtc,
                CreatedBeforeUtc = query.CreatedBeforeUtc
            };
        }

        /// <summary>
        /// Normalizes a value for safe Redis key composition.
        /// </summary>
        /// <param name="value">The key value.</param>
        /// <returns>The normalized key part.</returns>
        private static string NormalizeKeyPart(
            string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "_";
            }

            return Convert.ToHexString(
                Encoding.UTF8.GetBytes(value));
        }

        /// <summary>
        /// Formats an integer value using invariant culture.
        /// </summary>
        /// <param name="value">The integer value.</param>
        /// <returns>The formatted integer.</returns>
        private static string FormatInt(
            int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Formats a UTC timestamp.
        /// </summary>
        /// <param name="value">The timestamp value.</param>
        /// <returns>The formatted timestamp.</returns>
        private static string FormatDate(
            DateTimeOffset value)
        {
            return value.ToString("O", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Formats a nullable UTC timestamp.
        /// </summary>
        /// <param name="value">The nullable timestamp value.</param>
        /// <returns>The formatted timestamp or an empty string.</returns>
        private static string FormatNullableDate(
            DateTimeOffset? value)
        {
            return value?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty;
        }

        /// <summary>
        /// Gets a string value from a dictionary.
        /// </summary>
        /// <param name="fields">The field dictionary.</param>
        /// <param name="name">The field name.</param>
        /// <returns>The field value when present; otherwise, <see langword="null" />.</returns>
        private static string? GetString(
            IReadOnlyDictionary<string, string> fields,
            string name)
        {
            return fields.TryGetValue(name, out var value)
                ? value
                : null;
        }

        /// <summary>
        /// Converts an empty string to <see langword="null" />.
        /// </summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><see langword="null" /> when empty; otherwise, the original value.</returns>
        private static string? EmptyToNull(
            string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : value;
        }

        /// <summary>
        /// Parses a boolean value.
        /// </summary>
        /// <param name="value">The value to parse.</param>
        /// <returns>The parsed boolean, or <see langword="false" /> when parsing fails.</returns>
        private static bool ParseBool(
            string? value)
        {
            return bool.TryParse(
                    value,
                    out var parsed) &&
                parsed;
        }

        /// <summary>
        /// Parses an integer value.
        /// </summary>
        /// <param name="value">The value to parse.</param>
        /// <returns>The parsed integer, or zero when parsing fails.</returns>
        private static int ParseInt(
            string? value)
        {
            return int.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsed)
                ? parsed
                : 0;
        }

        /// <summary>
        /// Parses a nullable integer value.
        /// </summary>
        /// <param name="value">The value to parse.</param>
        /// <returns>The parsed integer, or <see langword="null" /> when parsing fails.</returns>
        private static int? ParseNullableInt(
            string? value)
        {
            return int.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsed)
                ? parsed
                : null;
        }

        /// <summary>
        /// Parses a UTC timestamp.
        /// </summary>
        /// <param name="value">The value to parse.</param>
        /// <returns>The parsed timestamp, or <see langword="null" /> when parsing fails.</returns>
        private static DateTimeOffset? ParseDate(
            string? value)
        {
            return DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsed)
                ? parsed
                : null;
        }

        /// <summary>
        /// Parses a scale-out request status.
        /// </summary>
        /// <param name="value">The status value.</param>
        /// <returns>The parsed status, or <see cref="AiRuntimeScaleOutRequestStatus.Pending" /> when parsing fails.</returns>
        private static AiRuntimeScaleOutRequestStatus ParseStatus(
            string? value)
        {
            return Enum.TryParse<AiRuntimeScaleOutRequestStatus>(
                    value,
                    ignoreCase: true,
                    out var parsed)
                ? parsed
                : AiRuntimeScaleOutRequestStatus.Pending;
        }

        /// <summary>
        /// Parses a runtime instance isolation mode.
        /// </summary>
        /// <param name="value">The isolation mode value.</param>
        /// <returns>The parsed isolation mode, or <see cref="AiRuntimeInstanceIsolationMode.Shared" /> when parsing fails.</returns>
        private static AiRuntimeInstanceIsolationMode ParseIsolationMode(
            string? value)
        {
            return Enum.TryParse<AiRuntimeInstanceIsolationMode>(
                    value,
                    ignoreCase: true,
                    out var parsed)
                ? parsed
                : AiRuntimeInstanceIsolationMode.Shared;
        }

        /// <summary>
        /// Parses persisted metadata JSON.
        /// </summary>
        /// <param name="value">The JSON value.</param>
        /// <returns>The parsed metadata dictionary.</returns>
        private static Dictionary<string, string> ParseMetadata(
            string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                var parsed =
                    JsonSerializer.Deserialize<Dictionary<string, string>>(
                        value,
                        JsonOptions);

                return parsed is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(
                        parsed,
                        StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
