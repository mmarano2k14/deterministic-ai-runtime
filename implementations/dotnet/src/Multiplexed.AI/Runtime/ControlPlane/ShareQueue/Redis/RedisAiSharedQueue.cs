using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Redis;
using StackExchange.Redis;
using System.Globalization;
using System.Text.Json;

namespace Multiplexed.AI.Runtime.ControlPlane.ShareQueue.Redis
{
    /// <summary>
    /// Redis-backed implementation of the shared/global queue.
    /// </summary>
    /// <remarks>
    /// This implementation uses:
    /// - one Redis hash per shared queue item
    /// - one Redis sorted set for pending items
    /// - one Redis sorted set for all queue items
    /// - Lua scripts for atomic enqueue, claim, dispatch, requeue, and cancel operations
    ///
    /// Redis keys:
    /// - ai:control-plane:{controlPlaneId}:shared-queue:item:{sharedRunId}
    /// - ai:control-plane:{controlPlaneId}:shared-queue:pending
    /// - ai:control-plane:{controlPlaneId}:shared-queue:all
    /// </remarks>
    public sealed class RedisAiSharedQueue : IAiSharedQueue
    {
        private const string ControlPlaneKeySegment =
            "control-plane";

        private const string SharedQueueKeySegment =
            "shared-queue";

        private const string ItemKeySegment =
            "item";

        private const string PendingIndexKeySegment =
            "pending";

        private const string AllIndexKeySegment =
            "all";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly IDatabase _database;
        private readonly RedisAiSharedQueueOptions _options;
        private readonly RedisAiSharedQueueScriptCache _scripts;
        private readonly IAiControlPlaneIdResolver _controlPlaneIdResolver;

        /// <summary>
        /// Initializes a new instance of the <see cref="RedisAiSharedQueue"/> class.
        /// </summary>
        /// <param name="connection">The Redis connection multiplexer.</param>
        /// <param name="options">The Redis shared queue options.</param>
        /// <param name="controlPlaneIdResolver">The control-plane identifier resolver.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="connection"/>, <paramref name="options"/>,
        /// or <paramref name="controlPlaneIdResolver"/> is null.
        /// </exception>
        public RedisAiSharedQueue(
            IConnectionMultiplexer connection,
            IOptions<RedisAiSharedQueueOptions> options,
            IAiControlPlaneIdResolver controlPlaneIdResolver)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(controlPlaneIdResolver);

            _database = connection.GetDatabase();
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _scripts = new RedisAiSharedQueueScriptCache(connection);
            _controlPlaneIdResolver = controlPlaneIdResolver;
        }

        /// <inheritdoc />
        public async Task<AiSharedQueueItem> EnqueueAsync(
            AiSharedQueueItem item,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.SharedRunId);

            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(
                        item.ControlPlaneId,
                        cancellationToken)
                    .ConfigureAwait(false);

            var effectiveItem =
                EnsureControlPlaneId(
                    item,
                    controlPlaneId);

            var itemKey =
                BuildItemKey(
                    controlPlaneId,
                    effectiveItem.SharedRunId);

            var pendingIndexKey =
                BuildPendingIndexKey(controlPlaneId);

            var allIndexKey =
                BuildAllIndexKey(controlPlaneId);

            var score =
                BuildQueueScore(effectiveItem);

            var expireSeconds =
                GetExpireSeconds();

            var result = await _scripts
                .ExecuteEnqueueAsync(
                    _database,
                    new RedisKey[]
                    {
                        itemKey,
                        pendingIndexKey,
                        allIndexKey
                    },
                    BuildEnqueueValues(
                        effectiveItem,
                        score,
                        expireSeconds))
                .ConfigureAwait(false);

            var status = result.ToString();

            if (string.Equals(status, "duplicate", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Shared queue item '{effectiveItem.SharedRunId}' already exists.");
            }

            if (!string.Equals(status, "enqueued", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Unexpected Redis enqueue result for shared queue item '{effectiveItem.SharedRunId}': '{status}'.");
            }

            return effectiveItem;
        }

        /// <inheritdoc />
        public async Task<AiSharedQueueItem?> GetAsync(
            string sharedRunId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);

            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(
                        requestedControlPlaneId: null,
                        cancellationToken)
                    .ConfigureAwait(false);

            return await GetAsync(
                    controlPlaneId,
                    sharedRunId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AiSharedQueueItem>> ListAsync(
            bool includeTerminal = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(
                        requestedControlPlaneId: null,
                        cancellationToken)
                    .ConfigureAwait(false);

            var ids = await _database
                .SortedSetRangeByScoreAsync(
                    BuildAllIndexKey(controlPlaneId),
                    order: Order.Ascending,
                    take: _options.ListScanLimit)
                .ConfigureAwait(false);

            var items = new List<AiSharedQueueItem>();

            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var sharedRunId = id.ToString();

                if (string.IsNullOrWhiteSpace(sharedRunId))
                {
                    continue;
                }

                var item = await GetAsync(
                        controlPlaneId,
                        sharedRunId,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (item is null)
                {
                    continue;
                }

                if (!includeTerminal &&
                    IsTerminal(item.Status))
                {
                    continue;
                }

                items.Add(item);
            }

            return items
                .OrderBy(item => item.Priority)
                .ThenBy(item => item.EnqueuedAtUtc)
                .ThenBy(item => item.SharedRunId, StringComparer.Ordinal)
                .ToArray();
        }

        /// <inheritdoc />
        public async Task<AiSharedQueueItem?> ClaimNextAsync(
            AiSharedQueueClaimRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.RuntimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(
                        requestedControlPlaneId: null,
                        cancellationToken)
                    .ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            var claimTtl = request.ClaimTtl <= TimeSpan.Zero
                ? TimeSpan.FromSeconds(30)
                : request.ClaimTtl;

            var claimToken = Guid.NewGuid().ToString("N");

            var result = await _scripts
                .ExecuteClaimNextAsync(
                    _database,
                    new RedisKey[]
                    {
                        BuildPendingIndexKey(controlPlaneId)
                    },
                    new RedisValue[]
                    {
                        request.RuntimeInstanceId,
                        request.WorkerId ?? string.Empty,
                        claimToken,
                        FormatDate(now),
                        FormatDate(now.Add(claimTtl)),
                        request.TenantId ?? string.Empty,
                        request.PipelineKey ?? string.Empty,
                        request.Reason ?? string.Empty,
                        BuildQueueKeyPrefix(controlPlaneId),
                        Math.Max(1, _options.ListScanLimit)
                    })
                .ConfigureAwait(false);

            var sharedRunId = result.ToString();

            if (string.IsNullOrWhiteSpace(sharedRunId))
            {
                return null;
            }

            return await GetAsync(
                    controlPlaneId,
                    sharedRunId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<AiSharedQueueItem?> MarkDispatchedAsync(
            string sharedRunId,
            string claimToken,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);

            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(
                        requestedControlPlaneId: null,
                        cancellationToken)
                    .ConfigureAwait(false);

            var result = await _scripts
                .ExecuteMarkDispatchedAsync(
                    _database,
                    new RedisKey[]
                    {
                        BuildItemKey(
                            controlPlaneId,
                            sharedRunId)
                    },
                    new RedisValue[]
                    {
                        claimToken,
                        FormatDate(DateTimeOffset.UtcNow),
                        reason ?? string.Empty
                    })
                .ConfigureAwait(false);

            var status = result.ToString();

            if (string.Equals(status, "missing", StringComparison.Ordinal) ||
                string.Equals(status, "not-owner", StringComparison.Ordinal))
            {
                return null;
            }

            if (string.Equals(status, "dispatched", StringComparison.Ordinal))
            {
                return await GetAsync(
                        controlPlaneId,
                        sharedRunId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                $"Unexpected Redis dispatch result for shared queue item '{sharedRunId}': '{status}'.");
        }

        /// <inheritdoc />
        public async Task<AiSharedQueueItem?> RequeueAsync(
            string sharedRunId,
            string claimToken,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);

            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(
                        requestedControlPlaneId: null,
                        cancellationToken)
                    .ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;

            var result = await _scripts
                .ExecuteRequeueAsync(
                    _database,
                    new RedisKey[]
                    {
                        BuildItemKey(
                            controlPlaneId,
                            sharedRunId),
                        BuildPendingIndexKey(controlPlaneId)
                    },
                    new RedisValue[]
                    {
                        sharedRunId,
                        claimToken,
                        BuildQueueScoreFromParts(
                            priority: 0,
                            enqueuedAtUtc: now),
                        FormatDate(now),
                        reason ?? string.Empty
                    })
                .ConfigureAwait(false);

            var status = result.ToString();

            if (string.Equals(status, "missing", StringComparison.Ordinal) ||
                string.Equals(status, "not-owner", StringComparison.Ordinal))
            {
                return null;
            }

            if (string.Equals(status, "requeued", StringComparison.Ordinal))
            {
                return await GetAsync(
                        controlPlaneId,
                        sharedRunId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                $"Unexpected Redis requeue result for shared queue item '{sharedRunId}': '{status}'.");
        }

        /// <inheritdoc />
        public async Task<AiSharedQueueItem?> CancelAsync(
            string sharedRunId,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);

            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(
                        requestedControlPlaneId: null,
                        cancellationToken)
                    .ConfigureAwait(false);

            var result = await _scripts
                .ExecuteCancelAsync(
                    _database,
                    new RedisKey[]
                    {
                        BuildItemKey(
                            controlPlaneId,
                            sharedRunId),
                        BuildPendingIndexKey(controlPlaneId)
                    },
                    new RedisValue[]
                    {
                        sharedRunId,
                        FormatDate(DateTimeOffset.UtcNow),
                        reason ?? "Shared queue item cancelled."
                    })
                .ConfigureAwait(false);

            var status = result.ToString();

            if (string.Equals(status, "missing", StringComparison.Ordinal))
            {
                return null;
            }

            if (string.Equals(status, "cancelled", StringComparison.Ordinal) ||
                string.Equals(status, "terminal", StringComparison.Ordinal))
            {
                return await GetAsync(
                        controlPlaneId,
                        sharedRunId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                $"Unexpected Redis cancel result for shared queue item '{sharedRunId}': '{status}'.");
        }

        /// <summary>
        /// Gets a shared queue item from the scoped control-plane keyspace.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The shared queue item, or <c>null</c> when not found.</returns>
        private async Task<AiSharedQueueItem?> GetAsync(
            string controlPlaneId,
            string sharedRunId,
            CancellationToken cancellationToken)
        {
            var entries = await _database
                .HashGetAllAsync(
                    BuildItemKey(
                        controlPlaneId,
                        sharedRunId))
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (entries.Length == 0)
            {
                return null;
            }

            var item =
                MapItem(entries);

            return EnsureControlPlaneId(
                item,
                controlPlaneId);
        }

        /// <summary>
        /// Builds Redis script values for enqueue.
        /// </summary>
        /// <param name="item">The shared queue item.</param>
        /// <param name="score">The queue ordering score.</param>
        /// <param name="expireSeconds">The optional expiration in seconds.</param>
        /// <returns>The Redis script values.</returns>
        private static RedisValue[] BuildEnqueueValues(
            AiSharedQueueItem item,
            double score,
            long expireSeconds)
        {
            var values = new List<RedisValue>
            {
                item.SharedRunId,
                score.ToString(CultureInfo.InvariantCulture),
                expireSeconds
            };

            AddField(values, "sharedRunId", item.SharedRunId);
            AddField(values, "controlPlaneId", item.ControlPlaneId);
            AddField(values, "status", item.Status.ToString());
            AddField(values, "tenantId", item.TenantId);
            AddField(values, "pipelineKey", item.PipelineKey);
            AddField(values, "priority", item.Priority.ToString(CultureInfo.InvariantCulture));
            AddField(values, "claimedByRuntimeInstanceId", item.ClaimedByRuntimeInstanceId);
            AddField(values, "claimedByWorkerId", item.ClaimedByWorkerId);
            AddField(values, "claimToken", item.ClaimToken);
            AddField(values, "enqueuedAtUtc", FormatDate(item.EnqueuedAtUtc));
            AddField(values, "updatedAtUtc", FormatDate(item.UpdatedAtUtc));
            AddField(values, "claimedAtUtc", FormatOptionalDate(item.ClaimedAtUtc));
            AddField(values, "claimExpiresAtUtc", FormatOptionalDate(item.ClaimExpiresAtUtc));
            AddField(values, "reason", item.Reason);
            AddField(values, "metadataJson", Serialize(item.Metadata));

            return values.ToArray();
        }

        /// <summary>
        /// Adds a Redis hash field pair to a script argument list.
        /// </summary>
        /// <param name="values">The script argument list.</param>
        /// <param name="name">The hash field name.</param>
        /// <param name="value">The hash field value.</param>
        private static void AddField(
            ICollection<RedisValue> values,
            string name,
            string? value)
        {
            values.Add(name);
            values.Add(value ?? string.Empty);
        }

        /// <summary>
        /// Maps Redis hash entries to a shared queue item.
        /// </summary>
        /// <param name="entries">The Redis hash entries.</param>
        /// <returns>The shared queue item.</returns>
        private static AiSharedQueueItem MapItem(
            IReadOnlyCollection<HashEntry> entries)
        {
            var fields = entries.ToDictionary(
                entry => entry.Name.ToString(),
                entry => entry.Value.ToString(),
                StringComparer.Ordinal);

            var metadata = DeserializeOptional<IReadOnlyDictionary<string, string>>(
                    GetOptional(fields, "metadataJson"))
                ?? new Dictionary<string, string>();

            return new AiSharedQueueItem
            {
                SharedRunId = GetRequired(fields, "sharedRunId"),
                ControlPlaneId = GetOptional(fields, "controlPlaneId"),
                Status = ParseStatus(GetRequired(fields, "status")),
                TenantId = GetOptional(fields, "tenantId"),
                PipelineKey = GetOptional(fields, "pipelineKey"),
                Priority = ParseInt(GetOptional(fields, "priority")),
                ClaimedByRuntimeInstanceId = GetOptional(fields, "claimedByRuntimeInstanceId"),
                ClaimedByWorkerId = GetOptional(fields, "claimedByWorkerId"),
                ClaimToken = GetOptional(fields, "claimToken"),
                EnqueuedAtUtc = ParseDateTimeOffset(GetRequired(fields, "enqueuedAtUtc")),
                UpdatedAtUtc = ParseDateTimeOffset(GetRequired(fields, "updatedAtUtc")),
                ClaimedAtUtc = ParseOptionalDateTimeOffset(GetOptional(fields, "claimedAtUtc")),
                ClaimExpiresAtUtc = ParseOptionalDateTimeOffset(GetOptional(fields, "claimExpiresAtUtc")),
                Reason = GetOptional(fields, "reason"),
                Metadata = metadata
            };
        }

        /// <summary>
        /// Resolves the logical control-plane identifier used to scope shared queue keys.
        /// </summary>
        /// <param name="requestedControlPlaneId">The preferred control-plane identifier when already known.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The resolved logical control-plane identifier.</returns>
        private async Task<string> ResolveControlPlaneIdAsync(
            string? requestedControlPlaneId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(requestedControlPlaneId))
            {
                return requestedControlPlaneId;
            }

            var resolvedControlPlaneId =
                await _controlPlaneIdResolver
                    .ResolveAsync(cancellationToken)
                    .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(resolvedControlPlaneId))
            {
                throw new InvalidOperationException(
                    "The resolved control-plane identifier cannot be null or empty.");
            }

            return resolvedControlPlaneId;
        }

        /// <summary>
        /// Ensures a shared queue item carries the logical control-plane identifier.
        /// </summary>
        /// <param name="item">The shared queue item.</param>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <returns>The shared queue item with a control-plane identifier.</returns>
        private static AiSharedQueueItem EnsureControlPlaneId(
            AiSharedQueueItem item,
            string controlPlaneId)
        {
            if (string.Equals(
                    item.ControlPlaneId,
                    controlPlaneId,
                    StringComparison.Ordinal))
            {
                return item;
            }

            var metadata =
                new Dictionary<string, string>(
                    item.Metadata,
                    StringComparer.Ordinal)
                {
                    ["controlPlaneId"] = controlPlaneId
                };

            return new AiSharedQueueItem
            {
                SharedRunId = item.SharedRunId,
                ControlPlaneId = controlPlaneId,
                Status = item.Status,
                TenantId = item.TenantId,
                PipelineKey = item.PipelineKey,
                Priority = item.Priority,
                ClaimedByRuntimeInstanceId = item.ClaimedByRuntimeInstanceId,
                ClaimedByWorkerId = item.ClaimedByWorkerId,
                ClaimToken = item.ClaimToken,
                EnqueuedAtUtc = item.EnqueuedAtUtc,
                UpdatedAtUtc = item.UpdatedAtUtc,
                ClaimedAtUtc = item.ClaimedAtUtc,
                ClaimExpiresAtUtc = item.ClaimExpiresAtUtc,
                Reason = item.Reason,
                Metadata = metadata
            };
        }

        /// <summary>
        /// Builds the Redis key prefix for one logical control-plane shared queue.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <returns>The Redis shared queue key prefix.</returns>
        private string BuildQueueKeyPrefix(
            string controlPlaneId)
        {
            return string.Concat(
                NormalizeBaseKeyPrefix(_options.KeyPrefix),
                ":",
                ControlPlaneKeySegment,
                ":",
                NormalizeKeySegment(controlPlaneId),
                ":",
                SharedQueueKeySegment);
        }

        /// <summary>
        /// Builds the Redis hash key for a shared queue item inside one logical control-plane.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <returns>The Redis shared queue item key.</returns>
        private RedisKey BuildItemKey(
            string controlPlaneId,
            string sharedRunId)
        {
            return string.Concat(
                BuildQueueKeyPrefix(controlPlaneId),
                ":",
                ItemKeySegment,
                ":",
                NormalizeKeySegment(sharedRunId));
        }

        /// <summary>
        /// Builds the Redis pending sorted-set key for one logical control-plane shared queue.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <returns>The Redis pending index key.</returns>
        private RedisKey BuildPendingIndexKey(
            string controlPlaneId)
        {
            return string.Concat(
                BuildQueueKeyPrefix(controlPlaneId),
                ":",
                PendingIndexKeySegment);
        }

        /// <summary>
        /// Builds the Redis all-items sorted-set key for one logical control-plane shared queue.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <returns>The Redis all-items index key.</returns>
        private RedisKey BuildAllIndexKey(
            string controlPlaneId)
        {
            return string.Concat(
                BuildQueueKeyPrefix(controlPlaneId),
                ":",
                AllIndexKeySegment);
        }

        /// <summary>
        /// Normalizes the configured Redis key prefix into a base prefix.
        /// </summary>
        /// <param name="keyPrefix">The configured Redis key prefix.</param>
        /// <returns>The normalized Redis base key prefix.</returns>
        private static string NormalizeBaseKeyPrefix(
            string keyPrefix)
        {
            if (string.IsNullOrWhiteSpace(keyPrefix))
            {
                return "ai";
            }

            var normalized =
                keyPrefix
                    .Trim()
                    .TrimEnd(':');

            const string sharedQueueSuffix = ":shared-queue";

            if (normalized.EndsWith(
                    sharedQueueSuffix,
                    StringComparison.Ordinal))
            {
                normalized = normalized[..^sharedQueueSuffix.Length];
            }

            return string.IsNullOrWhiteSpace(normalized)
                ? "ai"
                : normalized;
        }

        /// <summary>
        /// Normalizes a value so it can be used as a stable Redis key segment.
        /// </summary>
        /// <param name="value">The value to normalize.</param>
        /// <returns>The normalized Redis key segment.</returns>
        private static string NormalizeKeySegment(
            string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);

            return value
                .Trim()
                .Replace(" ", "-", StringComparison.Ordinal)
                .Replace("\\", "/", StringComparison.Ordinal);
        }

        /// <summary>
        /// Gets an optional field value.
        /// </summary>
        /// <param name="fields">The field dictionary.</param>
        /// <param name="name">The field name.</param>
        /// <returns>The field value, or <c>null</c> when empty or missing.</returns>
        private static string? GetOptional(
            IReadOnlyDictionary<string, string> fields,
            string name)
        {
            if (!fields.TryGetValue(name, out var value) ||
                string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return value;
        }

        /// <summary>
        /// Gets a required field value.
        /// </summary>
        /// <param name="fields">The field dictionary.</param>
        /// <param name="name">The field name.</param>
        /// <returns>The required field value.</returns>
        private static string GetRequired(
            IReadOnlyDictionary<string, string> fields,
            string name)
        {
            if (!fields.TryGetValue(name, out var value) ||
                string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    $"Redis shared queue item is missing required field '{name}'.");
            }

            return value;
        }

        /// <summary>
        /// Parses a shared queue item status.
        /// </summary>
        /// <param name="value">The status value.</param>
        /// <returns>The parsed queue item status.</returns>
        private static AiSharedQueueItemStatus ParseStatus(
            string value)
        {
            if (Enum.TryParse<AiSharedQueueItemStatus>(
                    value,
                    ignoreCase: true,
                    out var status))
            {
                return status;
            }

            return AiSharedQueueItemStatus.Unknown;
        }

        /// <summary>
        /// Parses an integer value.
        /// </summary>
        /// <param name="value">The raw integer value.</param>
        /// <returns>The parsed integer value, or <c>0</c> when missing or invalid.</returns>
        private static int ParseInt(
            string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return 0;
            }

            return int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed)
                    ? parsed
                    : 0;
        }

        /// <summary>
        /// Formats a timestamp as round-trip ISO-8601.
        /// </summary>
        /// <param name="value">The timestamp value.</param>
        /// <returns>The formatted timestamp.</returns>
        private static string FormatDate(
            DateTimeOffset value)
        {
            return value.ToString("O", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Formats an optional timestamp as round-trip ISO-8601.
        /// </summary>
        /// <param name="value">The optional timestamp value.</param>
        /// <returns>The formatted timestamp, or <c>null</c>.</returns>
        private static string? FormatOptionalDate(
            DateTimeOffset? value)
        {
            return value?.ToString("O", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Parses an ISO-8601 timestamp.
        /// </summary>
        /// <param name="value">The timestamp value.</param>
        /// <returns>The parsed timestamp.</returns>
        private static DateTimeOffset ParseDateTimeOffset(
            string value)
        {
            return DateTimeOffset.Parse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
        }

        /// <summary>
        /// Parses an optional ISO-8601 timestamp.
        /// </summary>
        /// <param name="value">The optional timestamp value.</param>
        /// <returns>The parsed timestamp, or <c>null</c>.</returns>
        private static DateTimeOffset? ParseOptionalDateTimeOffset(
            string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return ParseDateTimeOffset(value);
        }

        /// <summary>
        /// Serializes a value to JSON.
        /// </summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <param name="value">The value.</param>
        /// <returns>The serialized JSON, or an empty string when value is null.</returns>
        private static string Serialize<T>(
            T? value)
        {
            return value is null
                ? string.Empty
                : JsonSerializer.Serialize(value, JsonOptions);
        }

        /// <summary>
        /// Deserializes an optional JSON value.
        /// </summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <param name="json">The JSON value.</param>
        /// <returns>The deserialized value, or <c>null</c>.</returns>
        private static T? DeserializeOptional<T>(
            string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return default;
            }

            return JsonSerializer.Deserialize<T>(
                json,
                JsonOptions);
        }

        /// <summary>
        /// Determines whether a queue item status is terminal.
        /// </summary>
        /// <param name="status">The queue item status.</param>
        /// <returns><c>true</c> when the status is terminal; otherwise, <c>false</c>.</returns>
        private static bool IsTerminal(
            AiSharedQueueItemStatus status)
        {
            return status is
                AiSharedQueueItemStatus.Completed or
                AiSharedQueueItemStatus.Failed or
                AiSharedQueueItemStatus.Cancelled or
                AiSharedQueueItemStatus.Dispatched;
        }

        /// <summary>
        /// Builds a Redis sorted set score for queue ordering.
        /// </summary>
        /// <param name="item">The shared queue item.</param>
        /// <returns>The queue ordering score.</returns>
        private static double BuildQueueScore(
            AiSharedQueueItem item)
        {
            return BuildQueueScoreFromParts(
                item.Priority,
                item.EnqueuedAtUtc);
        }

        /// <summary>
        /// Builds a Redis sorted set score from priority and enqueue timestamp.
        /// </summary>
        /// <param name="priority">The item priority.</param>
        /// <param name="enqueuedAtUtc">The enqueue timestamp.</param>
        /// <returns>The queue ordering score.</returns>
        private static double BuildQueueScoreFromParts(
            int priority,
            DateTimeOffset enqueuedAtUtc)
        {
            var timestamp = enqueuedAtUtc.ToUnixTimeMilliseconds();

            return (priority * 1_000_000_000_000d) + timestamp;
        }

        /// <summary>
        /// Gets record expiration in seconds.
        /// </summary>
        /// <returns>The expiration in seconds, or <c>0</c> when disabled.</returns>
        private long GetExpireSeconds()
        {
            if (!_options.EnableRecordExpiration ||
                _options.RecordExpiration is null)
            {
                return 0;
            }

            return Math.Max(
                1,
                (long)_options.RecordExpiration.Value.TotalSeconds);
        }
    }
}