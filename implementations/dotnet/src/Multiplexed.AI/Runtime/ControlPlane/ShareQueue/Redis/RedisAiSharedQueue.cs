using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Redis;
using Multiplexed.Abstractions.Core.ExecutionContext;
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
    /// - {KeyPrefix}:control-plane:{controlPlaneId}:shared-queue:item:{sharedRunId}
    /// - {KeyPrefix}:control-plane:{controlPlaneId}:shared-queue:pending
    /// - {KeyPrefix}:control-plane:{controlPlaneId}:shared-queue:all
    ///
    /// Redis hash fields:
    /// - executionContextSnapshotJson stores the durable audit/context snapshot.
    /// - ExecutionContextSnapshot.TenantId is the tenant boundary used by claim filtering.
    /// - ExecutionContextSnapshot.ContextKey is volatile and must not be used as a durable key.
    ///
    /// IMPORTANT:
    /// - Shared queue visibility is scoped by logical control-plane identifier.
    /// - Reads are defensively filtered by logical control-plane identifier to avoid returning
    ///   stale, migrated, corrupted, or foreign queue items.
    /// - Listing self-heals the scoped indexes by removing missing or foreign items.
    /// - Claiming validates the claimed item before returning it to the pump.
    /// - Mutating operations validate the current scoped item before executing mutation scripts.
    /// </remarks>
    public sealed class RedisAiSharedQueue : IAiSharedQueue
    {
        private const string DefaultKeyPrefix =
            "ai";

        private const string ControlPlaneKeySegment =
            "control-plane";

        private const string SharedQueueKeySegment =
            "shared-queue";

        private const string TenantKeySegment =
            "tenant";

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
        private readonly IExecutionContextSnapshotProvider? _executionContextSnapshotProvider;

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
            : this(
                connection,
                options,
                controlPlaneIdResolver,
                executionContextSnapshotProvider: null)
        {
        }

        /// <summary>
        /// Initializes a new tenant-aware instance of the <see cref="RedisAiSharedQueue"/> class.
        /// </summary>
        /// <param name="connection">The Redis connection multiplexer.</param>
        /// <param name="options">The Redis shared queue options.</param>
        /// <param name="controlPlaneIdResolver">The control-plane identifier resolver.</param>
        /// <param name="executionContextSnapshotProvider">The execution context snapshot provider.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="connection"/>, <paramref name="options"/>,
        /// or <paramref name="controlPlaneIdResolver"/> is null.
        /// </exception>
        public RedisAiSharedQueue(
            IConnectionMultiplexer connection,
            IOptions<RedisAiSharedQueueOptions> options,
            IAiControlPlaneIdResolver controlPlaneIdResolver,
            IExecutionContextSnapshotProvider? executionContextSnapshotProvider)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(controlPlaneIdResolver);

            _database = connection.GetDatabase();
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _scripts = new RedisAiSharedQueueScriptCache(connection);
            _controlPlaneIdResolver = controlPlaneIdResolver;
            _executionContextSnapshotProvider = executionContextSnapshotProvider;
        }

        /// <inheritdoc />
        public async Task<AiSharedQueueItem> EnqueueAsync(
            AiSharedQueueItem item,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.SharedRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.ExecutionContextSnapshot.TenantId);

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

            var tenantPendingIndexKey =
                BuildTenantPendingIndexKey(
                    controlPlaneId,
                    effectiveItem.ExecutionContextSnapshot.TenantId);

            var tenantAllIndexKey =
                BuildTenantAllIndexKey(
                    controlPlaneId,
                    effectiveItem.ExecutionContextSnapshot.TenantId);

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
                        allIndexKey,
                        tenantPendingIndexKey,
                        tenantAllIndexKey
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

            if (string.Equals(status, "invalid-field-pairs", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Invalid Redis enqueue arguments for shared queue item '{effectiveItem.SharedRunId}': field/value pairs are not balanced.");
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

            var tenantId =
                TryResolveTenantId();

            var allIndexKey = string.IsNullOrWhiteSpace(tenantId)
                ? BuildAllIndexKey(controlPlaneId)
                : BuildTenantAllIndexKey(
                    controlPlaneId,
                    tenantId);

            var ids = await _database
                .SortedSetRangeByScoreAsync(
                    allIndexKey,
                    order: Order.Ascending,
                    take: _options.ListScanLimit)
                .ConfigureAwait(false);

            if (ids.Length == 0 &&
                !string.IsNullOrWhiteSpace(tenantId))
            {
                ids = await _database
                    .SortedSetRangeByScoreAsync(
                        BuildAllIndexKey(controlPlaneId),
                        order: Order.Ascending,
                        take: _options.ListScanLimit)
                    .ConfigureAwait(false);
            }

            var items = new List<AiSharedQueueItem>();

            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var sharedRunId = id.ToString();

                if (string.IsNullOrWhiteSpace(sharedRunId))
                {
                    continue;
                }

                var rawItem = await GetRawAsync(
                        controlPlaneId,
                        sharedRunId,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (rawItem is null)
                {
                    await CleanupItemAsync(
                            controlPlaneId,
                            sharedRunId,
                            tenantId: null,
                            deleteItem: true)
                        .ConfigureAwait(false);

                    continue;
                }

                if (!BelongsToControlPlane(
                        rawItem.ControlPlaneId,
                        controlPlaneId))
                {
                    await CleanupItemAsync(
                            controlPlaneId,
                            sharedRunId,
                            tenantId: rawItem.ExecutionContextSnapshot.TenantId,
                            deleteItem: true)
                        .ConfigureAwait(false);

                    continue;
                }

                var item =
                    EnsureControlPlaneId(
                        rawItem,
                        controlPlaneId);

                if (!BelongsToTenant(
                        item,
                        tenantId))
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

            var effectiveTenantId =
                string.IsNullOrWhiteSpace(request.TenantId)
                    ? null
                    : request.TenantId;

            var pendingIndexKey = string.IsNullOrWhiteSpace(effectiveTenantId)
                ? BuildPendingIndexKey(controlPlaneId)
                : BuildTenantPendingIndexKey(
                    controlPlaneId,
                    effectiveTenantId);

            var claimKeys = string.IsNullOrWhiteSpace(effectiveTenantId)
                ? new RedisKey[]
                {
                    BuildPendingIndexKey(controlPlaneId)
                }
                : new RedisKey[]
                {
                    pendingIndexKey,
                    BuildPendingIndexKey(controlPlaneId),
                    BuildTenantPendingIndexKey(
                        controlPlaneId,
                        effectiveTenantId)
                };

            var result = await _scripts
                .ExecuteClaimNextAsync(
                    _database,
                    claimKeys,
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

            if (string.IsNullOrWhiteSpace(sharedRunId) &&
                !string.IsNullOrWhiteSpace(effectiveTenantId))
            {
                result = await _scripts
                    .ExecuteClaimNextAsync(
                        _database,
                        new RedisKey[]
                        {
                            BuildPendingIndexKey(controlPlaneId),
                            BuildPendingIndexKey(controlPlaneId),
                            BuildTenantPendingIndexKey(
                                controlPlaneId,
                                effectiveTenantId)
                        },
                        new RedisValue[]
                        {
                            request.RuntimeInstanceId,
                            request.WorkerId ?? string.Empty,
                            claimToken,
                            FormatDate(now),
                            FormatDate(now.Add(claimTtl)),
                            effectiveTenantId,
                            request.PipelineKey ?? string.Empty,
                            request.Reason ?? string.Empty,
                            BuildQueueKeyPrefix(controlPlaneId),
                            Math.Max(1, _options.ListScanLimit)
                        })
                    .ConfigureAwait(false);

                sharedRunId = result.ToString();
            }

            if (string.IsNullOrWhiteSpace(sharedRunId))
            {
                return null;
            }

            var rawItem = await GetRawAsync(
                    controlPlaneId,
                    sharedRunId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (rawItem is null)
            {
                await CleanupItemAsync(
                        controlPlaneId,
                        sharedRunId,
                        tenantId: null,
                        deleteItem: true)
                    .ConfigureAwait(false);

                return null;
            }

            if (!BelongsToControlPlane(
                    rawItem.ControlPlaneId,
                    controlPlaneId))
            {
                await CleanupItemAsync(
                        controlPlaneId,
                        sharedRunId,
                        tenantId: rawItem.ExecutionContextSnapshot.TenantId,
                        deleteItem: true)
                    .ConfigureAwait(false);

                return null;
            }

            var item = EnsureControlPlaneId(
                rawItem,
                controlPlaneId);

            if (!BelongsToTenant(
                    item,
                    effectiveTenantId))
            {
                return null;
            }

            return item;
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

            var existing = await GetAsync(
                    controlPlaneId,
                    sharedRunId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                return null;
            }

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

            var existing = await GetAsync(
                    controlPlaneId,
                    sharedRunId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            var score = BuildQueueScoreFromParts(
                priority: 0,
                enqueuedAtUtc: now);

            var requeueKeys = string.IsNullOrWhiteSpace(existing.ExecutionContextSnapshot.TenantId)
                ? new RedisKey[]
                {
                    BuildItemKey(
                        controlPlaneId,
                        sharedRunId),
                    BuildPendingIndexKey(controlPlaneId)
                }
                : new RedisKey[]
                {
                    BuildItemKey(
                        controlPlaneId,
                        sharedRunId),
                    BuildPendingIndexKey(controlPlaneId),
                    BuildTenantPendingIndexKey(
                        controlPlaneId,
                        existing.ExecutionContextSnapshot.TenantId)
                };

            var result = await _scripts
                .ExecuteRequeueAsync(
                    _database,
                    requeueKeys,
                    new RedisValue[]
                    {
                        sharedRunId,
                        claimToken,
                        score,
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
        public Task<AiSharedQueueItem?> RequeueDispatchedAsync(
            string sharedRunId,
            string claimToken,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            return RequeueDispatchedAsync(
                sharedRunId,
                claimToken,
                reason,
                metadata: null,
                cancellationToken);
        }

        /// <inheritdoc />
        public async Task<AiSharedQueueItem?> RequeueDispatchedAsync(
            string sharedRunId,
            string claimToken,
            string? reason,
            IReadOnlyDictionary<string, string>? metadata,
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

            var existing = await GetAsync(
                    controlPlaneId,
                    sharedRunId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                return null;
            }

            if (existing.Status != AiSharedQueueItemStatus.Dispatched)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;

            var score = BuildQueueScoreFromParts(
                existing.Priority,
                existing.EnqueuedAtUtc);

            var tenantPendingIndexKey = string.IsNullOrWhiteSpace(existing.ExecutionContextSnapshot.TenantId)
                ? (RedisKey)string.Empty
                : BuildTenantPendingIndexKey(
                    controlPlaneId,
                    existing.ExecutionContextSnapshot.TenantId);

            var mergedMetadata =
                MergeMetadata(
                    existing.Metadata,
                    metadata);

            var metadataJson =
                Serialize(mergedMetadata);

            var result = await _scripts
                .ExecuteRequeueDispatchedAsync(
                    _database,
                    new RedisKey[]
                    {
                        BuildItemKey(
                            controlPlaneId,
                            sharedRunId),
                        BuildPendingIndexKey(controlPlaneId),
                        tenantPendingIndexKey
                    },
                    new RedisValue[]
                    {
                        sharedRunId,
                        claimToken,
                        score,
                        FormatDate(now),
                        reason ?? string.Empty,
                        metadataJson
                    })
                .ConfigureAwait(false);

            var status = result.ToString();

            if (string.Equals(status, "missing", StringComparison.Ordinal) ||
                string.Equals(status, "not-owner", StringComparison.Ordinal) ||
                string.Equals(status, "not-dispatched", StringComparison.Ordinal))
            {
                return null;
            }

            if (string.Equals(status, "requeued-dispatched", StringComparison.Ordinal))
            {
                return await GetAsync(
                        controlPlaneId,
                        sharedRunId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                $"Unexpected Redis requeue-dispatched result for shared queue item '{sharedRunId}': '{status}'.");
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

            var existing = await GetAsync(
                    controlPlaneId,
                    sharedRunId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                return null;
            }

            var cancelKeys = string.IsNullOrWhiteSpace(existing.ExecutionContextSnapshot.TenantId)
                ? new RedisKey[]
                {
                    BuildItemKey(
                        controlPlaneId,
                        sharedRunId),
                    BuildPendingIndexKey(controlPlaneId)
                }
                : new RedisKey[]
                {
                    BuildItemKey(
                        controlPlaneId,
                        sharedRunId),
                    BuildPendingIndexKey(controlPlaneId),
                    BuildTenantPendingIndexKey(
                        controlPlaneId,
                        existing.ExecutionContextSnapshot.TenantId)
                };

            var result = await _scripts
                .ExecuteCancelAsync(
                    _database,
                    cancelKeys,
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

        private async Task<AiSharedQueueItem?> GetAsync(
            string controlPlaneId,
            string sharedRunId,
            CancellationToken cancellationToken)
        {
            var item = await GetRawAsync(
                    controlPlaneId,
                    sharedRunId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!BelongsToControlPlane(
                    item?.ControlPlaneId,
                    controlPlaneId))
            {
                return null;
            }

            if (item is null)
            {
                return null;
            }

            var effectiveItem = EnsureControlPlaneId(
                item,
                controlPlaneId);

            if (!BelongsToTenant(
                    effectiveItem,
                    TryResolveTenantId()))
            {
                return null;
            }

            return effectiveItem;
        }

        private async Task<AiSharedQueueItem?> GetRawAsync(
            string controlPlaneId,
            string sharedRunId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

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

            return MapItem(entries);
        }

        private async Task CleanupItemAsync(
            string controlPlaneId,
            string sharedRunId,
            string? tenantId,
            bool deleteItem)
        {
            var batch = _database.CreateBatch();

            var removePendingTask = batch.SortedSetRemoveAsync(
                BuildPendingIndexKey(controlPlaneId),
                sharedRunId);

            var removeAllTask = batch.SortedSetRemoveAsync(
                BuildAllIndexKey(controlPlaneId),
                sharedRunId);

            Task<bool>? removeTenantPendingTask = null;
            Task<bool>? removeTenantAllTask = null;

            if (!string.IsNullOrWhiteSpace(tenantId))
            {
                removeTenantPendingTask = batch.SortedSetRemoveAsync(
                    BuildTenantPendingIndexKey(
                        controlPlaneId,
                        tenantId),
                    sharedRunId);

                removeTenantAllTask = batch.SortedSetRemoveAsync(
                    BuildTenantAllIndexKey(
                        controlPlaneId,
                        tenantId),
                    sharedRunId);
            }

            Task<bool>? deleteItemTask = null;

            if (deleteItem)
            {
                deleteItemTask = batch.KeyDeleteAsync(
                    BuildItemKey(
                        controlPlaneId,
                        sharedRunId));
            }

            batch.Execute();

            await removePendingTask.ConfigureAwait(false);
            await removeAllTask.ConfigureAwait(false);

            if (removeTenantPendingTask is not null)
            {
                await removeTenantPendingTask.ConfigureAwait(false);
            }

            if (removeTenantAllTask is not null)
            {
                await removeTenantAllTask.ConfigureAwait(false);
            }

            if (deleteItemTask is not null)
            {
                await deleteItemTask.ConfigureAwait(false);
            }
        }

        private async Task AddToTenantIndexesAsync(
            RedisKey tenantPendingIndexKey,
            RedisKey tenantAllIndexKey,
            string sharedRunId,
            double score,
            long expireSeconds)
        {
            var batch = _database.CreateBatch();

            var addTenantPendingTask = batch.SortedSetAddAsync(
                tenantPendingIndexKey,
                sharedRunId,
                score);

            var addTenantAllTask = batch.SortedSetAddAsync(
                tenantAllIndexKey,
                sharedRunId,
                score);

            Task<bool>? expireTenantPendingTask = null;
            Task<bool>? expireTenantAllTask = null;

            if (expireSeconds > 0)
            {
                var expiry = TimeSpan.FromSeconds(expireSeconds);

                expireTenantPendingTask = batch.KeyExpireAsync(
                    tenantPendingIndexKey,
                    expiry);

                expireTenantAllTask = batch.KeyExpireAsync(
                    tenantAllIndexKey,
                    expiry);
            }

            batch.Execute();

            await addTenantPendingTask.ConfigureAwait(false);
            await addTenantAllTask.ConfigureAwait(false);

            if (expireTenantPendingTask is not null)
            {
                await expireTenantPendingTask.ConfigureAwait(false);
            }

            if (expireTenantAllTask is not null)
            {
                await expireTenantAllTask.ConfigureAwait(false);
            }
        }

        private Task AddToTenantPendingIndexAsync(
            string controlPlaneId,
            string tenantId,
            string sharedRunId,
            double score)
        {
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                return Task.CompletedTask;
            }

            return _database.SortedSetAddAsync(
                BuildTenantPendingIndexKey(
                    controlPlaneId,
                    tenantId),
                sharedRunId,
                score);
        }

        private Task RemoveFromTenantPendingIndexAsync(
            string controlPlaneId,
            string tenantId,
            string sharedRunId)
        {
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                return Task.CompletedTask;
            }

            return _database.SortedSetRemoveAsync(
                BuildTenantPendingIndexKey(
                    controlPlaneId,
                    tenantId),
                sharedRunId);
        }

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
            AddField(values, "executionContextSnapshotJson", Serialize(item.ExecutionContextSnapshot));
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

        private static void AddField(
            ICollection<RedisValue> values,
            string name,
            string? value)
        {
            values.Add(name);
            values.Add(value ?? string.Empty);
        }

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

            var executionContextSnapshot =
                DeserializeRequired<ExecutionContextSnapshot>(
                    GetRequired(fields, "executionContextSnapshotJson"),
                    "executionContextSnapshotJson");

            return new AiSharedQueueItem
            {
                SharedRunId = GetRequired(fields, "sharedRunId"),
                ControlPlaneId = GetOptional(fields, "controlPlaneId"),
                Status = ParseStatus(GetRequired(fields, "status")),
                ExecutionContextSnapshot = executionContextSnapshot,
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

        private static IReadOnlyDictionary<string, string> MergeMetadata(
            IReadOnlyDictionary<string, string> existingMetadata,
            IReadOnlyDictionary<string, string>? metadata)
        {
            if (metadata is null ||
                metadata.Count == 0)
            {
                return existingMetadata;
            }

            var merged =
                new Dictionary<string, string>(
                    existingMetadata,
                    StringComparer.OrdinalIgnoreCase);

            foreach (var pair in metadata)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                {
                    continue;
                }

                merged[pair.Key] = pair.Value ?? string.Empty;
            }

            return merged;
        }

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
                ExecutionContextSnapshot = item.ExecutionContextSnapshot,
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

        private static bool BelongsToControlPlane(
            string? itemControlPlaneId,
            string expectedControlPlaneId)
        {
            if (string.IsNullOrWhiteSpace(itemControlPlaneId))
            {
                return true;
            }

            return string.Equals(
                NormalizeKeySegment(itemControlPlaneId),
                NormalizeKeySegment(expectedControlPlaneId),
                StringComparison.Ordinal);
        }

        private static bool BelongsToTenant(
            AiSharedQueueItem item,
            string? expectedTenantId)
        {
            if (string.IsNullOrWhiteSpace(expectedTenantId))
            {
                return true;
            }

            var itemTenantId =
                item.ExecutionContextSnapshot.TenantId;

            if (string.IsNullOrWhiteSpace(itemTenantId))
            {
                return false;
            }

            return string.Equals(
                NormalizeKeySegment(itemTenantId),
                NormalizeKeySegment(expectedTenantId),
                StringComparison.Ordinal);
        }

        private string? TryResolveTenantId()
        {
            if (_executionContextSnapshotProvider is null)
            {
                return null;
            }

            try
            {
                var snapshot =
                    _executionContextSnapshotProvider
                        .MapToSnapshot();

                return string.IsNullOrWhiteSpace(snapshot.TenantId)
                    ? null
                    : snapshot.TenantId;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

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

        private string BuildTenantQueueKeyPrefix(
            string controlPlaneId,
            string tenantId)
        {
            return string.Concat(
                NormalizeBaseKeyPrefix(_options.KeyPrefix),
                ":",
                ControlPlaneKeySegment,
                ":",
                NormalizeKeySegment(controlPlaneId),
                ":",
                TenantKeySegment,
                ":",
                NormalizeKeySegment(tenantId),
                ":",
                SharedQueueKeySegment);
        }

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

        private RedisKey BuildPendingIndexKey(
            string controlPlaneId)
        {
            return string.Concat(
                BuildQueueKeyPrefix(controlPlaneId),
                ":",
                PendingIndexKeySegment);
        }

        private RedisKey BuildAllIndexKey(
            string controlPlaneId)
        {
            return string.Concat(
                BuildQueueKeyPrefix(controlPlaneId),
                ":",
                AllIndexKeySegment);
        }

        private RedisKey BuildTenantPendingIndexKey(
            string controlPlaneId,
            string tenantId)
        {
            return string.Concat(
                BuildTenantQueueKeyPrefix(
                    controlPlaneId,
                    tenantId),
                ":",
                PendingIndexKeySegment);
        }

        private RedisKey BuildTenantAllIndexKey(
            string controlPlaneId,
            string tenantId)
        {
            return string.Concat(
                BuildTenantQueueKeyPrefix(
                    controlPlaneId,
                    tenantId),
                ":",
                AllIndexKeySegment);
        }

        private static string NormalizeBaseKeyPrefix(
            string keyPrefix)
        {
            if (string.IsNullOrWhiteSpace(keyPrefix))
            {
                return DefaultKeyPrefix;
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
                ? DefaultKeyPrefix
                : normalized;
        }

        private static string NormalizeKeySegment(
            string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);

            return value
                .Trim()
                .Replace(" ", "-", StringComparison.Ordinal)
                .Replace("\\", "/", StringComparison.Ordinal);
        }

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

        private static string FormatDate(
            DateTimeOffset value)
        {
            return value.ToString("O", CultureInfo.InvariantCulture);
        }

        private static string? FormatOptionalDate(
            DateTimeOffset? value)
        {
            return value?.ToString("O", CultureInfo.InvariantCulture);
        }

        private static DateTimeOffset ParseDateTimeOffset(
            string value)
        {
            return DateTimeOffset.Parse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
        }

        private static DateTimeOffset? ParseOptionalDateTimeOffset(
            string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return ParseDateTimeOffset(value);
        }

        private static string Serialize<T>(
            T? value)
        {
            return value is null
                ? string.Empty
                : JsonSerializer.Serialize(value, JsonOptions);
        }

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

        private static T DeserializeRequired<T>(
            string json,
            string fieldName)
        {
            var value = JsonSerializer.Deserialize<T>(
                json,
                JsonOptions);

            if (value is null)
            {
                throw new InvalidOperationException(
                    $"Redis shared queue item field '{fieldName}' could not be deserialized.");
            }

            return value;
        }

        private static bool IsTerminal(
            AiSharedQueueItemStatus status)
        {
            return status is
                AiSharedQueueItemStatus.Completed or
                AiSharedQueueItemStatus.Failed or
                AiSharedQueueItemStatus.Cancelled or
                AiSharedQueueItemStatus.Dispatched;
        }

        private static double BuildQueueScore(
            AiSharedQueueItem item)
        {
            return BuildQueueScoreFromParts(
                item.Priority,
                item.EnqueuedAtUtc);
        }

        private static double BuildQueueScoreFromParts(
            int priority,
            DateTimeOffset enqueuedAtUtc)
        {
            var timestamp = enqueuedAtUtc.ToUnixTimeMilliseconds();

            return (priority * 1_000_000_000_000d) + timestamp;
        }

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