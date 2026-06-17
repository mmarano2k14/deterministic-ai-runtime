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

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

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
            ArgumentNullException.ThrowIfNull(controlPlaneIdResolver);

            _database = connection.GetDatabase();
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
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
                await ResolveControlPlaneIdAsync(cancellationToken)
                    .ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            var createdAtUtc = entry.CreatedAtUtc == default
                ? now
                : entry.CreatedAtUtc;

            var executionContextSnapshot =
                entry.ExecutionContextSnapshot ??
                TryResolveSnapshot();

            var effectiveEntry = new AiRuntimeRunExecutionIndexEntry
            {
                RunId = entry.RunId,
                ExecutionId = entry.ExecutionId,
                RuntimeInstanceId = entry.RuntimeInstanceId,
                Status = string.IsNullOrWhiteSpace(entry.Status) ? "queued" : entry.Status,
                FailureReason = entry.FailureReason,
                ExecutionContextSnapshot = executionContextSnapshot,
                CreatedAtUtc = createdAtUtc,
                StartedAtUtc = entry.StartedAtUtc,
                CompletedAtUtc = entry.CompletedAtUtc,
                Metadata = entry.Metadata
            };

            var score =
                BuildIndexScore(createdAtUtc);

            var expireSeconds =
                GetExpireSeconds();

            var keys = BuildRegisterKeys(
                controlPlaneId,
                effectiveEntry.RunId,
                effectiveEntry.ExecutionContextSnapshot?.TenantId);

            var result = await _scripts
                .ExecuteRegisterQueuedAsync(
                    _database,
                    keys,
                    BuildRegisterValues(
                        effectiveEntry,
                        score,
                        expireSeconds))
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

            if (!await CanMutateAsync(controlPlaneId, runId, cancellationToken).ConfigureAwait(false))
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

            EnsureMutationResult(
                result,
                runId,
                "completed",
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
                "failed",
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
                "cancelled",
                "cancel");
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

        private async Task<string> ResolveControlPlaneIdAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var resolvedControlPlaneId = await _controlPlaneIdResolver
                .ResolveAsync(cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(resolvedControlPlaneId))
            {
                throw new InvalidOperationException(
                    "The resolved control-plane identifier cannot be null or empty.");
            }

            return resolvedControlPlaneId;
        }

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

        private string? TryResolveTenantId()
        {
            return TryResolveSnapshot()?.TenantId;
        }

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

        private RedisKey BuildAllIndexKey(
            string controlPlaneId)
        {
            return string.Concat(
                BuildIndexKeyPrefix(controlPlaneId),
                ":",
                AllIndexKeySegment);
        }

        private RedisKey BuildTenantAllIndexKey(
            string controlPlaneId,
            string tenantId)
        {
            return string.Concat(
                BuildTenantIndexKeyPrefix(controlPlaneId, tenantId),
                ":",
                AllIndexKeySegment);
        }

        private static void AddField(
            ICollection<RedisValue> values,
            string name,
            string? value)
        {
            values.Add(name);
            values.Add(value ?? string.Empty);
        }

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
                    $"Redis runtime run execution index entry is missing required field '{name}'.");
            }

            return value;
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

        private static double BuildIndexScore(
            DateTimeOffset createdAtUtc)
        {
            return createdAtUtc.ToUnixTimeMilliseconds();
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
