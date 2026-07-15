using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue.Redis;
using Multiplexed.Abstractions.Core.ExecutionContext;
using StackExchange.Redis;
using System.Globalization;
using System.Text.Json;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue.Redis
{
    /// <summary>
    /// Redis-backed implementation of the runtime run execution index.
    /// </summary>
    /// <remarks>
    /// This index stores the durable relationship between a local runtime queue
    /// RunId and the DAG ExecutionId created from that run.
    ///
    /// Redis keys:
    /// - {KeyPrefix}:control-plane:{controlPlaneId}:runtime-run-index:item:{runId}
    /// - {KeyPrefix}:control-plane:{controlPlaneId}:runtime-run-index:all
    /// - {KeyPrefix}:control-plane:{controlPlaneId}:tenant:{tenantId}:runtime-run-index:all
    ///
    /// Multi-tenant isolation is enforced through ExecutionContextSnapshot.TenantId.
    /// ExecutionContextSnapshot.ContextKey is volatile and must not be used as a
    /// durable Redis partition key.
    /// </remarks>
    public sealed class RedisAiRuntimeRunExecutionIndex : IAiRuntimeRunExecutionIndex
    {
        private const string DefaultKeyPrefix =
            "ai";

        private const string ControlPlaneKeySegment =
            "control-plane";

        private const string RuntimeRunIndexKeySegment =
            "runtime-run-index";

        private const string TenantKeySegment =
            "tenant";

        private const string ItemKeySegment =
            "item";

        private const string AllIndexKeySegment =
            "all";

        private const string StatusQueued =
            "queued";

        private const string StatusRunning =
            "running";

        private const string StatusCompleted =
            "completed";

        private const string StatusFailed =
            "failed";

        private const string StatusCancelled =
            "cancelled";

        private const string StatusRequeuedForRecovery =
            "requeued-for-recovery";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private const string MarkCompletedIgnoredRequeuedForRecovery = "ignored-requeued-for-recovery";

        private readonly IDatabase _database;
        private readonly RedisAiRuntimeRunExecutionIndexOptions _options;
        private readonly RedisAiRuntimeRunExecutionIndexScriptCache _scripts;
        private readonly IAiControlPlaneIdResolver _controlPlaneIdResolver;
        private readonly IExecutionContextSnapshotProvider? _executionContextSnapshotProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="RedisAiRuntimeRunExecutionIndex"/> class.
        /// </summary>
        /// <param name="connection">The Redis connection multiplexer.</param>
        /// <param name="options">The Redis runtime run execution index options.</param>
        /// <param name="controlPlaneIdResolver">The control-plane identifier resolver.</param>
        public RedisAiRuntimeRunExecutionIndex(
            IConnectionMultiplexer connection,
            IOptions<RedisAiRuntimeRunExecutionIndexOptions> options,
            IAiControlPlaneIdResolver controlPlaneIdResolver)
            : this(
                connection,
                options,
                controlPlaneIdResolver,
                executionContextSnapshotProvider: null)
        {
        }

        /// <summary>
        /// Initializes a tenant-aware instance of the <see cref="RedisAiRuntimeRunExecutionIndex"/> class.
        /// </summary>
        /// <param name="connection">The Redis connection multiplexer.</param>
        /// <param name="options">The Redis runtime run execution index options.</param>
        /// <param name="controlPlaneIdResolver">The control-plane identifier resolver.</param>
        /// <param name="executionContextSnapshotProvider">The execution context snapshot provider.</param>
        public RedisAiRuntimeRunExecutionIndex(
            IConnectionMultiplexer connection,
            IOptions<RedisAiRuntimeRunExecutionIndexOptions> options,
            IAiControlPlaneIdResolver controlPlaneIdResolver,
            IExecutionContextSnapshotProvider? executionContextSnapshotProvider)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentException.ThrowIfNullOrWhiteSpace(options?.Value?.KeyPrefix);
            ArgumentNullException.ThrowIfNull(controlPlaneIdResolver);

            _database = connection.GetDatabase();
            _options = options.Value;
            _scripts = new RedisAiRuntimeRunExecutionIndexScriptCache(connection);
            _controlPlaneIdResolver = controlPlaneIdResolver;
            _executionContextSnapshotProvider = executionContextSnapshotProvider;
        }

        /// <inheritdoc />
        public async Task RegisterQueuedAsync(
            AiRuntimeRunExecutionIndexEntry entry,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.RunId);

            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(
                        entry.Metadata,
                        cancellationToken)
                    .ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            var createdAtUtc = entry.CreatedAtUtc == default ? now : entry.CreatedAtUtc;
            var executionContextSnapshot = entry.ExecutionContextSnapshot ?? TryResolveSnapshot();

            var effectiveEntry = new AiRuntimeRunExecutionIndexEntry
            {
                RunId = entry.RunId,
                ExecutionId = entry.ExecutionId,
                RuntimeInstanceId = entry.RuntimeInstanceId,
                Status = string.IsNullOrWhiteSpace(entry.Status) ? StatusQueued : entry.Status,
                FailureReason = entry.FailureReason,
                ExecutionContextSnapshot = executionContextSnapshot,
                CreatedAtUtc = createdAtUtc,
                StartedAtUtc = entry.StartedAtUtc,
                CompletedAtUtc = entry.CompletedAtUtc,
                Metadata = entry.Metadata
            };

            var score = BuildIndexScore(createdAtUtc);
            var expireSeconds = GetExpireSeconds();
            var keys = BuildRegisterKeys(
                controlPlaneId,
                effectiveEntry.RunId,
                effectiveEntry.ExecutionContextSnapshot?.TenantId);

            var values = BuildRegisterValues(
                effectiveEntry,
                score,
                expireSeconds);

            var result = await _scripts
                .ExecuteRegisterQueuedAsync(
                    _database,
                    keys,
                    values)
                .ConfigureAwait(false);

            var status = result.ToString();

            if (string.Equals(status, "registered", StringComparison.Ordinal))
            {
                return;
            }

            if (string.Equals(status, "invalid-field-pairs", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Invalid Redis register arguments for runtime run index entry '{effectiveEntry.RunId}': field/value pairs are not balanced.");
            }

            throw new InvalidOperationException(
                $"Unexpected Redis register result for runtime run index entry '{effectiveEntry.RunId}': '{status}'.");
        }

        /// <inheritdoc />
        public async Task MarkStartedAsync(
            string runId,
            string executionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(cancellationToken)
                    .ConfigureAwait(false);

            if (!await CanMutateAsync(controlPlaneId, runId, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            var result = await _scripts
                .ExecuteMarkStartedAsync(
                    _database,
                    new RedisKey[]
                    {
                        BuildItemKey(controlPlaneId, runId)
                    },
                    new RedisValue[]
                    {
                        executionId,
                        FormatDate(DateTimeOffset.UtcNow)
                    })
                .ConfigureAwait(false);

            EnsureMutationResult(
                result,
                runId,
                "started",
                "start");
        }
        /// <inheritdoc />
        public async Task MarkCompletedAsync(
            string runId,
            string executionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(cancellationToken)
                    .ConfigureAwait(false);

            if (!await CanMutateAsync(
                    controlPlaneId,
                    runId,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return;
            }

            var result = await _scripts
                .ExecuteMarkCompletedAsync(
                    _database,
                    new RedisKey[]
                    {
                BuildItemKey(controlPlaneId, runId)
                    },
                    new RedisValue[]
                    {
                executionId,
                FormatDate(DateTimeOffset.UtcNow)
                    })
                .ConfigureAwait(false);

            var status =
                result.ToString();

            if (string.Equals(
                    status,
                    MarkCompletedIgnoredRequeuedForRecovery,
                    StringComparison.Ordinal))
            {
                return;
            }

            EnsureMutationResult(
                result,
                runId,
                StatusCompleted,
                "complete");
        }

        /// <inheritdoc />
        public async Task MarkFailedAsync(
            string runId,
            string? executionId,
            string failureReason,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);
            ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(cancellationToken)
                    .ConfigureAwait(false);

            if (!await CanMutateAsync(controlPlaneId, runId, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            var result = await _scripts
                .ExecuteMarkFailedAsync(
                    _database,
                    new RedisKey[]
                    {
                        BuildItemKey(controlPlaneId, runId)
                    },
                    new RedisValue[]
                    {
                        executionId ?? string.Empty,
                        failureReason,
                        FormatDate(DateTimeOffset.UtcNow)
                    })
                .ConfigureAwait(false);

            EnsureMutationResult(
                result,
                runId,
                StatusFailed,
                "fail");
        }

        /// <inheritdoc />
        public async Task MarkCancelledAsync(
            string runId,
            string? executionId,
            string? reason,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);

            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(cancellationToken)
                    .ConfigureAwait(false);

            if (!await CanMutateAsync(controlPlaneId, runId, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            var result = await _scripts
                .ExecuteMarkCancelledAsync(
                    _database,
                    new RedisKey[]
                    {
                        BuildItemKey(controlPlaneId, runId)
                    },
                    new RedisValue[]
                    {
                        executionId ?? string.Empty,
                        reason ?? string.Empty,
                        FormatDate(DateTimeOffset.UtcNow)
                    })
                .ConfigureAwait(false);

            EnsureMutationResult(
                result,
                runId,
                StatusCancelled,
                "cancel");
        }

        /// <inheritdoc />
        /// <inheritdoc />
        public async Task<bool> MarkRequeuedForRecoveryAsync(
            string runId,
            string executionId,
            string reason,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);

            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(cancellationToken)
                    .ConfigureAwait(false);

            var existing =
                await GetRawAsync(
                        controlPlaneId,
                        runId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (existing is null)
            {
                return false;
            }

            if (!BelongsToTenant(
                    existing,
                    TryResolveTenantId()))
            {
                return false;
            }

            if (IsTerminal(existing) &&
                !CanMarkFailedForRecovery(
                    existing,
                    executionId))
            {
                return false;
            }

            if (!CanMarkRequeuedForRecovery(
                    existing,
                    executionId))
            {
                return false;
            }

            var effectiveExecutionId =
                string.IsNullOrWhiteSpace(executionId)
                    ? existing.ExecutionId ?? string.Empty
                    : executionId;

            var now =
                DateTimeOffset.UtcNow;

            await _database
                .HashSetAsync(
                    BuildItemKey(controlPlaneId, runId),
                    new HashEntry[]
                    {
                new("executionId", effectiveExecutionId),
                new("status", StatusRequeuedForRecovery),
                new("failureReason", reason),
                new("completedAtUtc", FormatDate(now))
                    })
                .ConfigureAwait(false);

            return true;
        }

        /// <summary>
        /// Determines whether a failed runtime execution can still be marked as requeued for recovery.
        /// </summary>
        /// <param name="existing">The existing runtime execution index entry.</param>
        /// <param name="executionId">The expected execution id.</param>
        /// <returns><c>true</c> when the failed execution can be recovered; otherwise, <c>false</c>.</returns>
        private static bool CanMarkFailedForRecovery(
            AiRuntimeRunExecutionIndexEntry existing,
            string? executionId)
        {
            ArgumentNullException.ThrowIfNull(existing);

            if (!string.Equals(
                    existing.Status,
                    StatusFailed,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return ExecutionIdMatches(
                existing,
                executionId);
        }

        /// <summary>
        /// Determines whether a runtime execution index entry can be marked as requeued for recovery.
        /// </summary>
        /// <param name="existing">The existing runtime execution index entry.</param>
        /// <param name="executionId">The expected execution id.</param>
        /// <returns><c>true</c> when the entry can be marked as requeued for recovery; otherwise, <c>false</c>.</returns>
        private static bool CanMarkRequeuedForRecovery(
            AiRuntimeRunExecutionIndexEntry existing,
            string? executionId)
        {
            ArgumentNullException.ThrowIfNull(existing);

            if (string.Equals(
                    existing.Status,
                    StatusRequeuedForRecovery,
                    StringComparison.OrdinalIgnoreCase))
            {
                return ExecutionIdMatches(
                    existing,
                    executionId);
            }

            if (!ExecutionIdMatches(
                    existing,
                    executionId))
            {
                return false;
            }

            return string.Equals(existing.Status, StatusQueued, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(existing.Status, StatusRunning, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(existing.Status, StatusFailed, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Determines whether the expected execution id matches the runtime execution index entry.
        /// </summary>
        /// <param name="existing">The existing runtime execution index entry.</param>
        /// <param name="executionId">The expected execution id.</param>
        /// <returns><c>true</c> when the execution id is empty or matches the existing execution id; otherwise, <c>false</c>.</returns>
        private static bool ExecutionIdMatches(
            AiRuntimeRunExecutionIndexEntry existing,
            string? executionId)
        {
            ArgumentNullException.ThrowIfNull(existing);

            if (string.IsNullOrWhiteSpace(executionId))
            {
                return true;
            }

            return string.Equals(
                existing.ExecutionId,
                executionId,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <inheritdoc />
        public async Task<AiRuntimeRunExecutionIndexEntry?> GetAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);

            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(cancellationToken)
                    .ConfigureAwait(false);

            var entry = await GetRawAsync(
                    controlPlaneId,
                    runId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (entry is null)
            {
                return null;
            }

            if (!BelongsToTenant(
                    entry,
                    TryResolveTenantId()))
            {
                return null;
            }

            return entry;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>> ListUnfinishedByRuntimeInstanceAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(cancellationToken)
                    .ConfigureAwait(false);

            var runIds =
                await _database
                    .SortedSetRangeByRankAsync(
                        BuildAllIndexKey(controlPlaneId),
                        order: Order.Ascending)
                    .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (runIds.Length == 0)
            {
                return Array.Empty<AiRuntimeRunExecutionIndexEntry>();
            }

            var tenantId =
                TryResolveTenantId();

            var entries =
                new List<AiRuntimeRunExecutionIndexEntry>(runIds.Length);

            foreach (var runIdValue in runIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var runId =
                    runIdValue.ToString();

                if (string.IsNullOrWhiteSpace(runId))
                {
                    continue;
                }

                var entry =
                    await GetRawAsync(
                            controlPlaneId,
                            runId,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (entry is null)
                {
                    continue;
                }

                if (!string.Equals(
                        entry.RuntimeInstanceId,
                        runtimeInstanceId,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (!IsUnfinished(entry))
                {
                    continue;
                }

                if (!BelongsToTenant(
                        entry,
                        tenantId))
                {
                    continue;
                }

                entries.Add(entry);
            }

            return entries
                .OrderBy(entry => entry.CreatedAtUtc)
                .ThenBy(entry => entry.RunId, StringComparer.Ordinal)
                .ToArray();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>> ListUnfinishedAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(cancellationToken)
                    .ConfigureAwait(false);

            var runIds =
                await _database
                    .SortedSetRangeByRankAsync(
                        BuildAllIndexKey(controlPlaneId),
                        order: Order.Ascending)
                    .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (runIds.Length == 0)
            {
                return Array.Empty<AiRuntimeRunExecutionIndexEntry>();
            }

            var tenantId =
                TryResolveTenantId();

            var entries =
                new List<AiRuntimeRunExecutionIndexEntry>(runIds.Length);

            foreach (var runIdValue in runIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var runId =
                    runIdValue.ToString();

                if (string.IsNullOrWhiteSpace(runId))
                {
                    continue;
                }

                var entry =
                    await GetRawAsync(
                            controlPlaneId,
                            runId,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (entry is null)
                {
                    continue;
                }

                if (!IsUnfinished(entry))
                {
                    continue;
                }

                if (!BelongsToTenant(
                        entry,
                        tenantId))
                {
                    continue;
                }

                entries.Add(entry);
            }

            return entries
                .OrderBy(entry => entry.CreatedAtUtc)
                .ThenBy(entry => entry.RunId, StringComparer.Ordinal)
                .ToArray();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>> ListRecoverableByRuntimeInstanceAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(cancellationToken)
                    .ConfigureAwait(false);

            var runIds =
                await _database
                    .SortedSetRangeByRankAsync(
                        BuildAllIndexKey(controlPlaneId),
                        order: Order.Ascending)
                    .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (runIds.Length == 0)
            {
                return Array.Empty<AiRuntimeRunExecutionIndexEntry>();
            }

            var tenantId =
                TryResolveTenantId();

            var entries =
                new List<AiRuntimeRunExecutionIndexEntry>(runIds.Length);

            foreach (var runIdValue in runIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var runId =
                    runIdValue.ToString();

                if (string.IsNullOrWhiteSpace(runId))
                {
                    continue;
                }

                var entry =
                    await GetRawAsync(
                            controlPlaneId,
                            runId,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (entry is null)
                {
                    continue;
                }

                if (!string.Equals(
                        entry.RuntimeInstanceId,
                        runtimeInstanceId,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (!IsRecoverable(entry))
                {
                    continue;
                }

                if (!BelongsToTenant(
                        entry,
                        tenantId))
                {
                    continue;
                }

                entries.Add(entry);
            }

            return entries
                .OrderBy(entry => entry.CreatedAtUtc)
                .ThenBy(entry => entry.RunId, StringComparer.Ordinal)
                .ToArray();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>> ListRecoverableAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(cancellationToken)
                    .ConfigureAwait(false);

            var runIds =
                await _database
                    .SortedSetRangeByRankAsync(
                        BuildAllIndexKey(controlPlaneId),
                        order: Order.Ascending)
                    .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (runIds.Length == 0)
            {
                return Array.Empty<AiRuntimeRunExecutionIndexEntry>();
            }

            var tenantId =
                TryResolveTenantId();

            var entries =
                new List<AiRuntimeRunExecutionIndexEntry>(runIds.Length);

            foreach (var runIdValue in runIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var runId =
                    runIdValue.ToString();

                if (string.IsNullOrWhiteSpace(runId))
                {
                    continue;
                }

                var entry =
                    await GetRawAsync(
                            controlPlaneId,
                            runId,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (entry is null)
                {
                    continue;
                }

                if (!IsRecoverable(entry))
                {
                    continue;
                }

                if (!BelongsToTenant(
                        entry,
                        tenantId))
                {
                    continue;
                }

                entries.Add(entry);
            }

            return entries
                .OrderBy(entry => entry.CreatedAtUtc)
                .ThenBy(entry => entry.RunId, StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>
        /// Determines whether an index entry is recoverable by runtime crash recovery.
        /// </summary>
        /// <param name="entry">The runtime run index entry.</param>
        /// <returns>True when the entry can be considered by crash recovery; otherwise false.</returns>
        private static bool IsRecoverable(
            AiRuntimeRunExecutionIndexEntry entry)
        {
            return !string.Equals(entry.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(entry.Status, StatusCancelled, StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(entry.Status, StatusRequeuedForRecovery, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Determines whether the current tenant context is allowed to mutate the specified runtime run entry.
        /// </summary>
        /// <param name="controlPlaneId">The control-plane identifier.</param>
        /// <param name="runId">The local runtime run identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>True when the entry exists and belongs to the current tenant context; otherwise false.</returns>
        private async Task<bool> CanMutateAsync(
            string controlPlaneId,
            string runId,
            CancellationToken cancellationToken)
        {
            var entry = await GetRawAsync(
                    controlPlaneId,
                    runId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (entry is null)
            {
                return false;
            }

            return BelongsToTenant(
                entry,
                TryResolveTenantId());
        }

        /// <summary>
        /// Gets a runtime run execution index entry without applying tenant visibility filtering.
        /// </summary>
        /// <param name="controlPlaneId">The control-plane identifier.</param>
        /// <param name="runId">The local runtime run identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The raw index entry when it exists; otherwise null.</returns>
        private async Task<AiRuntimeRunExecutionIndexEntry?> GetRawAsync(
            string controlPlaneId,
            string runId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entries = await _database
                .HashGetAllAsync(
                    BuildItemKey(controlPlaneId, runId))
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (entries.Length == 0)
            {
                return null;
            }

            return MapEntry(entries);
        }

        /// <summary>
        /// Builds the Redis keys used by the queued-run registration script.
        /// </summary>
        /// <param name="controlPlaneId">The control-plane identifier.</param>
        /// <param name="runId">The local runtime run identifier.</param>
        /// <param name="tenantId">The optional tenant identifier.</param>
        /// <returns>The Redis keys used to register the entry and update its indexes.</returns>
        private RedisKey[] BuildRegisterKeys(
            string controlPlaneId,
            string runId,
            string? tenantId)
        {
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                return new RedisKey[]
                {
                    BuildItemKey(controlPlaneId, runId),
                    BuildAllIndexKey(controlPlaneId)
                };
            }

            return new RedisKey[]
            {
                BuildItemKey(controlPlaneId, runId),
                BuildAllIndexKey(controlPlaneId),
                BuildTenantAllIndexKey(controlPlaneId, tenantId)
            };
        }

        /// <summary>
        /// Builds the Redis argument values used by the queued-run registration script.
        /// </summary>
        /// <param name="entry">The runtime run execution index entry to serialize.</param>
        /// <param name="score">The sorted-set score used for ordered index scans.</param>
        /// <param name="expireSeconds">The optional record expiration in seconds, or 0 when disabled.</param>
        /// <returns>The Redis values passed to the registration script.</returns>
        private static RedisValue[] BuildRegisterValues(
            AiRuntimeRunExecutionIndexEntry entry,
            double score,
            long expireSeconds)
        {
            var values = new List<RedisValue>
            {
                entry.RunId,
                score.ToString(CultureInfo.InvariantCulture),
                expireSeconds
            };

            AddField(values, "runId", entry.RunId);
            AddField(values, "executionId", entry.ExecutionId);
            AddField(values, "runtimeInstanceId", entry.RuntimeInstanceId);
            AddField(values, "status", entry.Status);
            AddField(values, "failureReason", entry.FailureReason);
            AddField(values, "executionContextSnapshotJson", Serialize(entry.ExecutionContextSnapshot));
            AddField(values, "createdAtUtc", FormatDate(entry.CreatedAtUtc));
            AddField(values, "startedAtUtc", FormatOptionalDate(entry.StartedAtUtc));
            AddField(values, "completedAtUtc", FormatOptionalDate(entry.CompletedAtUtc));
            AddField(values, "metadataJson", Serialize(entry.Metadata));

            return values.ToArray();
        }

        /// <summary>
        /// Maps Redis hash entries to a runtime run execution index entry.
        /// </summary>
        /// <param name="entries">The Redis hash entries.</param>
        /// <returns>The mapped runtime run execution index entry.</returns>
        private static AiRuntimeRunExecutionIndexEntry MapEntry(
            IReadOnlyCollection<HashEntry> entries)
        {
            var fields = entries.ToDictionary(
                entry => entry.Name.ToString(),
                entry => entry.Value.ToString(),
                StringComparer.Ordinal);

            var metadata = DeserializeOptional<IReadOnlyDictionary<string, string>>(
                    GetOptional(fields, "metadataJson"))
                ?? new Dictionary<string, string>();

            var executionContextSnapshot = DeserializeOptional<ExecutionContextSnapshot>(
                GetOptional(fields, "executionContextSnapshotJson"));

            return new AiRuntimeRunExecutionIndexEntry
            {
                RunId = GetRequired(fields, "runId"),
                ExecutionId = GetOptional(fields, "executionId"),
                RuntimeInstanceId = GetOptional(fields, "runtimeInstanceId"),
                Status = GetOptional(fields, "status"),
                FailureReason = GetOptional(fields, "failureReason"),
                ExecutionContextSnapshot = executionContextSnapshot,
                CreatedAtUtc = ParseDateTimeOffset(GetRequired(fields, "createdAtUtc")),
                StartedAtUtc = ParseOptionalDateTimeOffset(GetOptional(fields, "startedAtUtc")),
                CompletedAtUtc = ParseOptionalDateTimeOffset(GetOptional(fields, "completedAtUtc")),
                Metadata = metadata
            };
        }

        /// <summary>
        /// Resolves the current logical control-plane identifier.
        /// </summary>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The resolved logical control-plane identifier.</returns>
        private Task<string> ResolveControlPlaneIdAsync(
            CancellationToken cancellationToken)
        {
            return ResolveControlPlaneIdAsync(
                metadata: null,
                cancellationToken);
        }

        /// <summary>
        /// Resolves the current logical control-plane identifier.
        /// </summary>
        /// <param name="metadata">The metadata that may contain a logical control-plane identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The resolved logical control-plane identifier.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the control-plane identifier is empty.</exception>
        private async Task<string> ResolveControlPlaneIdAsync(
            IReadOnlyDictionary<string, string>? metadata,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var resolvedControlPlaneId =
                await _controlPlaneIdResolver
                    .ResolveAsync(
                        new AiControlPlaneIdResolutionRequest
                        {
                            Metadata = metadata,
                            Source = "redis-runtime-run-execution-index",
                            AllowGeneratedFallback = false
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(resolvedControlPlaneId))
            {
                throw new InvalidOperationException(
                    "The resolved control-plane identifier cannot be null or empty.");
            }

            return resolvedControlPlaneId;
        }

        /// <summary>
        /// Attempts to resolve the current execution context snapshot.
        /// </summary>
        /// <returns>The current execution context snapshot, or null when none is available.</returns>
        private ExecutionContextSnapshot? TryResolveSnapshot()
        {
            if (_executionContextSnapshotProvider is null)
            {
                return null;
            }

            try
            {
                return _executionContextSnapshotProvider.MapToSnapshot();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>
        /// Attempts to resolve the current tenant identifier from the execution context snapshot.
        /// </summary>
        /// <returns>The current tenant identifier, or null when no tenant context is active.</returns>
        private string? TryResolveTenantId()
        {
            return TryResolveSnapshot()?.TenantId;
        }

        /// <summary>
        /// Determines whether an index entry belongs to the expected tenant.
        /// </summary>
        /// <param name="entry">The runtime run execution index entry.</param>
        /// <param name="expectedTenantId">The expected tenant identifier, or null for unscoped access.</param>
        /// <returns>True when the entry belongs to the expected tenant or access is unscoped; otherwise false.</returns>
        private static bool BelongsToTenant(
            AiRuntimeRunExecutionIndexEntry entry,
            string? expectedTenantId)
        {
            if (string.IsNullOrWhiteSpace(expectedTenantId))
            {
                return true;
            }

            var itemTenantId =
                entry.ExecutionContextSnapshot?.TenantId;

            if (string.IsNullOrWhiteSpace(itemTenantId))
            {
                return false;
            }

            return string.Equals(
                NormalizeKeySegment(itemTenantId),
                NormalizeKeySegment(expectedTenantId),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Determines whether an index entry has not reached a terminal runtime-run state.
        /// </summary>
        /// <param name="entry">The runtime run index entry.</param>
        /// <returns>True when the entry is unfinished; otherwise false.</returns>
        private static bool IsUnfinished(
            AiRuntimeRunExecutionIndexEntry entry)
        {
            return !IsTerminal(entry);
        }

        /// <summary>
        /// Determines whether an index entry has reached a terminal runtime-run state.
        /// </summary>
        /// <param name="entry">The runtime run index entry.</param>
        /// <returns>True when the entry is terminal; otherwise false.</returns>
        private static bool IsTerminal(
            AiRuntimeRunExecutionIndexEntry entry)
        {
            return string.Equals(entry.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(entry.Status, StatusFailed, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(entry.Status, StatusCancelled, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(entry.Status, StatusRequeuedForRecovery, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Builds the Redis key prefix for runtime run execution index entries owned by a control plane.
        /// </summary>
        /// <param name="controlPlaneId">The control-plane identifier.</param>
        /// <returns>The Redis key prefix.</returns>
        private string BuildIndexKeyPrefix(
            string controlPlaneId)
        {
            return string.Concat(
                NormalizeBaseKeyPrefix(_options.KeyPrefix),
                ":",
                ControlPlaneKeySegment,
                ":",
                NormalizeKeySegment(controlPlaneId),
                ":",
                RuntimeRunIndexKeySegment);
        }

        /// <summary>
        /// Builds the Redis key prefix for tenant-scoped runtime run execution indexes.
        /// </summary>
        /// <param name="controlPlaneId">The control-plane identifier.</param>
        /// <param name="tenantId">The tenant identifier.</param>
        /// <returns>The Redis tenant-scoped key prefix.</returns>
        private string BuildTenantIndexKeyPrefix(
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
                RuntimeRunIndexKeySegment);
        }

        /// <summary>
        /// Builds the Redis item key for a runtime run execution index entry.
        /// </summary>
        /// <param name="controlPlaneId">The control-plane identifier.</param>
        /// <param name="runId">The local runtime run identifier.</param>
        /// <returns>The Redis item key.</returns>
        private RedisKey BuildItemKey(
            string controlPlaneId,
            string runId)
        {
            return string.Concat(
                BuildIndexKeyPrefix(controlPlaneId),
                ":",
                ItemKeySegment,
                ":",
                NormalizeKeySegment(runId));
        }

        /// <summary>
        /// Builds the Redis sorted-set key containing all runtime run identifiers for a control plane.
        /// </summary>
        /// <param name="controlPlaneId">The control-plane identifier.</param>
        /// <returns>The Redis all-index key.</returns>
        private RedisKey BuildAllIndexKey(
            string controlPlaneId)
        {
            return string.Concat(
                BuildIndexKeyPrefix(controlPlaneId),
                ":",
                AllIndexKeySegment);
        }

        /// <summary>
        /// Builds the Redis sorted-set key containing all runtime run identifiers for a tenant.
        /// </summary>
        /// <param name="controlPlaneId">The control-plane identifier.</param>
        /// <param name="tenantId">The tenant identifier.</param>
        /// <returns>The Redis tenant all-index key.</returns>
        private RedisKey BuildTenantAllIndexKey(
            string controlPlaneId,
            string tenantId)
        {
            return string.Concat(
                BuildTenantIndexKeyPrefix(controlPlaneId, tenantId),
                ":",
                AllIndexKeySegment);
        }

        /// <summary>
        /// Adds a Redis hash field/value pair to a Redis values collection.
        /// </summary>
        /// <param name="values">The Redis values collection.</param>
        /// <param name="name">The field name.</param>
        /// <param name="value">The optional field value.</param>
        private static void AddField(
            ICollection<RedisValue> values,
            string name,
            string? value)
        {
            values.Add(name);
            values.Add(value ?? string.Empty);
        }

        /// <summary>
        /// Normalizes the configured Redis key prefix.
        /// </summary>
        /// <param name="keyPrefix">The configured key prefix.</param>
        /// <returns>The normalized base key prefix.</returns>
        private static string NormalizeBaseKeyPrefix(
            string keyPrefix)
        {
            if (string.IsNullOrWhiteSpace(keyPrefix))
            {
                return DefaultKeyPrefix;
            }

            return keyPrefix
                .Trim()
                .TrimEnd(':');
        }

        /// <summary>
        /// Normalizes a value so it can safely be used as a Redis key segment.
        /// </summary>
        /// <param name="value">The raw key segment value.</param>
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
        /// Gets an optional field value from a Redis hash field dictionary.
        /// </summary>
        /// <param name="fields">The Redis hash field dictionary.</param>
        /// <param name="name">The field name.</param>
        /// <returns>The field value, or null when the field is missing or blank.</returns>
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
        /// Gets a required field value from a Redis hash field dictionary.
        /// </summary>
        /// <param name="fields">The Redis hash field dictionary.</param>
        /// <param name="name">The field name.</param>
        /// <returns>The required field value.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the field is missing or blank.</exception>
        private static string GetRequired(
            IReadOnlyDictionary<string, string> fields,
            string name)
        {
            if (!fields.TryGetValue(name, out var value) ||
                string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    $"Redis runtime run execution index entry is missing required field '{name}'.");
            }

            return value;
        }

        /// <summary>
        /// Formats a timestamp for Redis persistence.
        /// </summary>
        /// <param name="value">The timestamp value.</param>
        /// <returns>The formatted timestamp.</returns>
        private static string FormatDate(
            DateTimeOffset value)
        {
            return value.ToString("O", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Formats an optional timestamp for Redis persistence.
        /// </summary>
        /// <param name="value">The optional timestamp value.</param>
        /// <returns>The formatted timestamp, or null when no value is available.</returns>
        private static string? FormatOptionalDate(
            DateTimeOffset? value)
        {
            return value?.ToString("O", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Parses a persisted timestamp from Redis.
        /// </summary>
        /// <param name="value">The persisted timestamp.</param>
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
        /// Parses an optional persisted timestamp from Redis.
        /// </summary>
        /// <param name="value">The optional persisted timestamp.</param>
        /// <returns>The parsed timestamp, or null when no value is available.</returns>
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
        /// Serializes a value to JSON for Redis persistence.
        /// </summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <param name="value">The value to serialize.</param>
        /// <returns>The JSON payload, or an empty string when the value is null.</returns>
        private static string Serialize<T>(
            T? value)
        {
            return value is null
                ? string.Empty
                : JsonSerializer.Serialize(value, JsonOptions);
        }

        /// <summary>
        /// Deserializes an optional JSON payload from Redis.
        /// </summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <param name="json">The optional JSON payload.</param>
        /// <returns>The deserialized value, or default when the payload is blank.</returns>
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
        /// Builds the sorted-set score used to order runtime run index entries.
        /// </summary>
        /// <param name="createdAtUtc">The entry creation timestamp.</param>
        /// <returns>The Redis sorted-set score.</returns>
        private static double BuildIndexScore(
            DateTimeOffset createdAtUtc)
        {
            return createdAtUtc.ToUnixTimeMilliseconds();
        }

        /// <summary>
        /// Gets the configured record expiration in seconds.
        /// </summary>
        /// <returns>The expiration in seconds, or 0 when expiration is disabled.</returns>
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

        /// <summary>
        /// Validates the Redis Lua mutation result.
        /// </summary>
        /// <param name="result">The Redis script result.</param>
        /// <param name="runId">The local runtime run identifier.</param>
        /// <param name="expectedStatus">The expected status returned by the script.</param>
        /// <param name="operation">The logical operation name used in error messages.</param>
        /// <exception cref="InvalidOperationException">Thrown when Redis returns an unexpected mutation result.</exception>
        private static void EnsureMutationResult(
            RedisResult result,
            string runId,
            string expectedStatus,
            string operation)
        {
            var status = result.ToString();

            if (string.Equals(status, "missing", StringComparison.Ordinal))
            {
                return;
            }

            if (string.Equals(status, expectedStatus, StringComparison.Ordinal))
            {
                return;
            }

            throw new InvalidOperationException(
                $"Unexpected Redis {operation} result for runtime run index entry '{runId}': '{status}'.");
        }
    }
}