using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using StackExchange.Redis;
using System.Globalization;
using System.Text.Json;

namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Store
{
    /// <summary>
    /// Redis-backed implementation of the shared runtime controller run store.
    /// </summary>
    /// <remarks>
    /// This store uses:
    /// - one Redis hash per shared run
    /// - one Redis sorted set index ordered by submission time
    /// - Lua for atomic create
    /// - Lua for atomic cancel-if-non-terminal
    /// - Lua for atomic mark-dispatched updates
    ///
    /// Redis keys:
    /// - {KeyPrefix}:control-plane:{controlPlaneId}:shared-run:{sharedRunId}
    /// - {KeyPrefix}:control-plane:{controlPlaneId}:shared-runs:index
    ///
    /// IMPORTANT:
    /// - Shared run visibility is scoped by logical control-plane identifier.
    /// - Reads are defensively filtered by logical control-plane identifier to avoid returning
    ///   stale, migrated, corrupted, or foreign shared run records.
    /// - Listing self-heals the scoped index by removing missing or foreign records.
    /// - Mutating operations validate the current scoped record before executing the mutation script.
    /// </remarks>
    public sealed class RedisAiSharedRunStore : IAiSharedRunStore
    {
        private const string DefaultKeyPrefix =
            "ai";

        private const string ControlPlaneKeySegment =
            "control-plane";

        private const string SharedRunKeySegment =
            "shared-run";

        private const string SharedRunIndexSegment =
            "shared-runs:index";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly IDatabase _database;
        private readonly RedisAiSharedRunStoreOptions _options;
        private readonly RedisAiSharedRunStoreScriptCache _scripts;
        private readonly IAiControlPlaneIdResolver _controlPlaneIdResolver;

        /// <summary>
        /// Initializes a new instance of the <see cref="RedisAiSharedRunStore"/> class.
        /// </summary>
        /// <param name="connection">The Redis connection multiplexer.</param>
        /// <param name="options">The Redis shared run store options.</param>
        /// <param name="controlPlaneIdResolver">The control-plane identifier resolver.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="connection"/>, <paramref name="options"/>,
        /// or <paramref name="controlPlaneIdResolver"/> is null.
        /// </exception>
        public RedisAiSharedRunStore(
            IConnectionMultiplexer connection,
            IOptions<RedisAiSharedRunStoreOptions> options,
            IAiControlPlaneIdResolver controlPlaneIdResolver)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(controlPlaneIdResolver);

            _database = connection.GetDatabase();
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _scripts = new RedisAiSharedRunStoreScriptCache(connection);
            _controlPlaneIdResolver = controlPlaneIdResolver;
        }

        /// <inheritdoc />
        public async Task<AiSharedRunRecord> CreateAsync(
            AiSharedRunRecord record,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentException.ThrowIfNullOrWhiteSpace(record.SharedRunId);

            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(
                        record.ControlPlaneId,
                        cancellationToken)
                    .ConfigureAwait(false);

            var effectiveRecord =
                EnsureControlPlaneId(
                    record,
                    controlPlaneId);

            var runKey =
                BuildRunKey(
                    controlPlaneId,
                    effectiveRecord.SharedRunId);

            var indexKey =
                BuildIndexKey(controlPlaneId);

            var submittedAtScore =
                effectiveRecord.SubmittedAtUtc.ToUnixTimeMilliseconds();

            var expireSeconds =
                GetExpireSeconds();

            var values =
                BuildCreateValues(
                    effectiveRecord,
                    submittedAtScore,
                    expireSeconds);

            var result = await _scripts
                .ExecuteCreateAsync(
                    _database,
                    new RedisKey[]
                    {
                        runKey,
                        indexKey
                    },
                    values)
                .ConfigureAwait(false);

            var status = result.ToString();

            if (string.Equals(status, "duplicate", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Shared run '{effectiveRecord.SharedRunId}' already exists.");
            }

            if (!string.Equals(status, "created", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Unexpected Redis create result for shared run '{effectiveRecord.SharedRunId}': '{status}'.");
            }

            return effectiveRecord;
        }

        /// <inheritdoc />
        public async Task<AiSharedRunRecord?> GetAsync(
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
        public async Task<IReadOnlyList<AiSharedRunRecord>> ListAsync(
            bool includeCancelled = false,
            bool includeCompleted = false,
            bool includeFailed = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(
                        requestedControlPlaneId: null,
                        cancellationToken)
                    .ConfigureAwait(false);

            var indexKey =
                BuildIndexKey(controlPlaneId);

            var ids = await _database
                .SortedSetRangeByScoreAsync(
                    indexKey,
                    order: Order.Ascending,
                    take: _options.ListScanLimit)
                .ConfigureAwait(false);

            var records = new List<AiSharedRunRecord>();

            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var sharedRunId = id.ToString();

                if (string.IsNullOrWhiteSpace(sharedRunId))
                {
                    continue;
                }

                var rawRecord = await GetRawAsync(
                        controlPlaneId,
                        sharedRunId,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (rawRecord is null)
                {
                    await RemoveFromIndexAsync(
                            indexKey,
                            sharedRunId)
                        .ConfigureAwait(false);

                    continue;
                }

                if (!BelongsToControlPlane(
                        rawRecord.ControlPlaneId,
                        controlPlaneId))
                {
                    await RemoveFromIndexAsync(
                            indexKey,
                            sharedRunId)
                        .ConfigureAwait(false);

                    continue;
                }

                var record =
                    EnsureControlPlaneId(
                        rawRecord,
                        controlPlaneId);

                if (!includeCancelled &&
                    record.Status == AiSharedRunStatus.Cancelled)
                {
                    continue;
                }

                if (!includeCompleted &&
                    record.Status == AiSharedRunStatus.Completed)
                {
                    continue;
                }

                if (!includeFailed &&
                    record.Status == AiSharedRunStatus.Failed)
                {
                    continue;
                }

                records.Add(record);
            }

            return records
                .OrderBy(record => record.SubmittedAtUtc)
                .ThenBy(record => record.SharedRunId, StringComparer.Ordinal)
                .ToArray();
        }

        /// <inheritdoc />
        public async Task<AiSharedRunRecord?> CancelAsync(
            string sharedRunId,
            string? reason = null,
            string? requestedBy = null,
            string? source = null,
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

            var runKey =
                BuildRunKey(
                    controlPlaneId,
                    sharedRunId);

            var updatedAtUtc =
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            var cancellationReason = string.IsNullOrWhiteSpace(reason)
                ? "Shared run cancelled."
                : reason;

            var result = await _scripts
                .ExecuteCancelAsync(
                    _database,
                    new RedisKey[]
                    {
                        runKey
                    },
                    new RedisValue[]
                    {
                        cancellationReason,
                        requestedBy ?? string.Empty,
                        source ?? string.Empty,
                        updatedAtUtc
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
                $"Unexpected Redis cancel result for shared run '{sharedRunId}': '{status}'.");
        }

        /// <inheritdoc />
        public async Task<AiSharedRunRecord?> MarkDispatchedAsync(
            string sharedRunId,
            string runtimeInstanceId,
            string? localRunId = null,
            string? executionId = null,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

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

            var runKey =
                BuildRunKey(
                    controlPlaneId,
                    sharedRunId);

            var updatedAtUtc =
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            var result = await _scripts
                .ExecuteMarkDispatchedAsync(
                    _database,
                    new RedisKey[]
                    {
                        runKey
                    },
                    new RedisValue[]
                    {
                        runtimeInstanceId,
                        localRunId ?? string.Empty,
                        executionId ?? string.Empty,
                        reason ?? string.Empty,
                        updatedAtUtc
                    })
                .ConfigureAwait(false);

            var status = result.ToString();

            if (string.Equals(status, "missing", StringComparison.Ordinal))
            {
                return null;
            }

            if (string.Equals(status, "dispatched", StringComparison.Ordinal) ||
                string.Equals(status, "terminal", StringComparison.Ordinal))
            {
                return await GetAsync(
                        controlPlaneId,
                        sharedRunId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                $"Unexpected Redis mark-dispatched result for shared run '{sharedRunId}': '{status}'.");
        }

        /// <summary>
        /// Gets a shared run record from the scoped control-plane keyspace and validates that it belongs
        /// to the expected logical control-plane.
        /// </summary>
        /// <param name="controlPlaneId">The expected logical control-plane identifier.</param>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>
        /// The shared run record when found and scoped to the expected control-plane;
        /// otherwise, <c>null</c>.
        /// </returns>
        private async Task<AiSharedRunRecord?> GetAsync(
            string controlPlaneId,
            string sharedRunId,
            CancellationToken cancellationToken)
        {
            var record =
                await GetRawAsync(
                        controlPlaneId,
                        sharedRunId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!BelongsToControlPlane(
                    record?.ControlPlaneId,
                    controlPlaneId))
            {
                return null;
            }

            return record is null
                ? null
                : EnsureControlPlaneId(
                    record,
                    controlPlaneId);
        }

        /// <summary>
        /// Gets a shared run record from the scoped Redis key without applying control-plane validation.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier used to build the Redis key.</param>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The raw shared run record when found; otherwise, <c>null</c>.</returns>
        private async Task<AiSharedRunRecord?> GetRawAsync(
            string controlPlaneId,
            string sharedRunId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var runKey =
                BuildRunKey(
                    controlPlaneId,
                    sharedRunId);

            var entries = await _database
                .HashGetAllAsync(runKey)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (entries.Length == 0)
            {
                return null;
            }

            return MapRecord(entries);
        }

        /// <summary>
        /// Removes a shared run identifier from a scoped shared run index.
        /// </summary>
        /// <param name="indexKey">The scoped Redis shared run index key.</param>
        /// <param name="sharedRunId">The shared run identifier.</param>
        private Task RemoveFromIndexAsync(
            RedisKey indexKey,
            string sharedRunId)
        {
            return _database.SortedSetRemoveAsync(
                indexKey,
                sharedRunId);
        }

        /// <summary>
        /// Builds Redis script values for atomic shared run creation.
        /// </summary>
        /// <param name="record">The shared run record.</param>
        /// <param name="submittedAtScore">The submitted timestamp score.</param>
        /// <param name="expireSeconds">The optional expiration in seconds.</param>
        /// <returns>The Redis script values.</returns>
        private static RedisValue[] BuildCreateValues(
            AiSharedRunRecord record,
            long submittedAtScore,
            long expireSeconds)
        {
            var values = new List<RedisValue>
            {
                record.SharedRunId,
                submittedAtScore,
                expireSeconds
            };

            AddField(values, "sharedRunId", record.SharedRunId);
            AddField(values, "controlPlaneId", record.ControlPlaneId);
            AddField(values, "status", record.Status.ToString());
            AddField(values, "runRequestJson", Serialize(record.RunRequest));
            AddField(values, "localRunId", record.LocalRunId);
            AddField(values, "executionId", record.ExecutionId);
            AddField(values, "assignedRuntimeInstanceId", record.AssignedRuntimeInstanceId);
            AddField(values, "admissionDecisionJson", Serialize(record.AdmissionDecision));
            AddField(values, "tenantId", record.TenantId);
            AddField(values, "pipelineKey", record.PipelineKey);
            AddField(values, "correlationId", record.CorrelationId);
            AddField(values, "requestedBy", record.RequestedBy);
            AddField(values, "source", record.Source);
            AddField(values, "reason", record.Reason);
            AddField(values, "failureReason", record.FailureReason);
            AddField(values, "submittedAtUtc", record.SubmittedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            AddField(values, "updatedAtUtc", record.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            AddField(values, "metadataJson", Serialize(record.Metadata));

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
        /// Maps Redis hash entries to a shared run record.
        /// </summary>
        /// <param name="entries">The Redis hash entries.</param>
        /// <returns>The shared run record.</returns>
        private static AiSharedRunRecord MapRecord(
            IReadOnlyCollection<HashEntry> entries)
        {
            var fields = entries.ToDictionary(
                entry => entry.Name.ToString(),
                entry => entry.Value.ToString(),
                StringComparer.Ordinal);

            var sharedRunId = GetRequired(fields, "sharedRunId");
            var status = ParseStatus(GetRequired(fields, "status"));

            var runRequest = DeserializeRequired<AiRuntimePipelineRunRequest>(
                GetRequired(fields, "runRequestJson"),
                "runRequestJson");

            var admissionDecision = DeserializeOptional<AiRunAdmissionDecision>(
                GetOptional(fields, "admissionDecisionJson"));

            var metadata = DeserializeOptional<IReadOnlyDictionary<string, string>>(
                    GetOptional(fields, "metadataJson"))
                ?? new Dictionary<string, string>();

            return new AiSharedRunRecord
            {
                SharedRunId = sharedRunId,
                ControlPlaneId = GetOptional(fields, "controlPlaneId"),
                Status = status,
                RunRequest = runRequest,
                LocalRunId = GetOptional(fields, "localRunId"),
                ExecutionId = GetOptional(fields, "executionId"),
                AssignedRuntimeInstanceId = GetOptional(fields, "assignedRuntimeInstanceId"),
                AdmissionDecision = admissionDecision,
                TenantId = GetOptional(fields, "tenantId"),
                PipelineKey = GetOptional(fields, "pipelineKey"),
                CorrelationId = GetOptional(fields, "correlationId"),
                RequestedBy = GetOptional(fields, "requestedBy"),
                Source = GetOptional(fields, "source"),
                Reason = GetOptional(fields, "reason"),
                FailureReason = GetOptional(fields, "failureReason"),
                SubmittedAtUtc = ParseDateTimeOffset(GetRequired(fields, "submittedAtUtc")),
                UpdatedAtUtc = ParseDateTimeOffset(GetRequired(fields, "updatedAtUtc")),
                Metadata = metadata
            };
        }

        /// <summary>
        /// Resolves the logical control-plane identifier used to scope shared run keys.
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
        /// Ensures a shared run record carries the logical control-plane identifier.
        /// </summary>
        /// <param name="record">The shared run record.</param>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <returns>The shared run record with a control-plane identifier.</returns>
        private static AiSharedRunRecord EnsureControlPlaneId(
            AiSharedRunRecord record,
            string controlPlaneId)
        {
            if (string.Equals(
                    record.ControlPlaneId,
                    controlPlaneId,
                    StringComparison.Ordinal))
            {
                return record;
            }

            var metadata =
                new Dictionary<string, string>(
                    record.Metadata,
                    StringComparer.Ordinal)
                {
                    ["controlPlaneId"] = controlPlaneId
                };

            return new AiSharedRunRecord
            {
                SharedRunId = record.SharedRunId,
                ControlPlaneId = controlPlaneId,
                Status = record.Status,
                RunRequest = record.RunRequest,
                LocalRunId = record.LocalRunId,
                ExecutionId = record.ExecutionId,
                AssignedRuntimeInstanceId = record.AssignedRuntimeInstanceId,
                AdmissionDecision = record.AdmissionDecision,
                TenantId = record.TenantId,
                PipelineKey = record.PipelineKey,
                CorrelationId = record.CorrelationId,
                RequestedBy = record.RequestedBy,
                Source = record.Source,
                Reason = record.Reason,
                FailureReason = record.FailureReason,
                SubmittedAtUtc = record.SubmittedAtUtc,
                UpdatedAtUtc = record.UpdatedAtUtc,
                Metadata = metadata
            };
        }

        /// <summary>
        /// Determines whether a stored shared run record belongs to the expected logical control-plane.
        /// </summary>
        /// <param name="recordControlPlaneId">The control-plane identifier stored on the record.</param>
        /// <param name="expectedControlPlaneId">The expected logical control-plane identifier.</param>
        /// <returns>
        /// <c>true</c> when the record belongs to the expected control-plane, or when the
        /// record has no control-plane identifier for backward compatibility; otherwise, <c>false</c>.
        /// </returns>
        private static bool BelongsToControlPlane(
            string? recordControlPlaneId,
            string expectedControlPlaneId)
        {
            if (string.IsNullOrWhiteSpace(recordControlPlaneId))
            {
                return true;
            }

            return string.Equals(
                NormalizeKeySegment(recordControlPlaneId),
                NormalizeKeySegment(expectedControlPlaneId),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Builds the Redis hash key for a shared run inside one logical control-plane.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <returns>The Redis shared run key.</returns>
        private RedisKey BuildRunKey(
            string controlPlaneId,
            string sharedRunId)
        {
            return string.Concat(
                NormalizeBaseKeyPrefix(_options.KeyPrefix),
                ":",
                ControlPlaneKeySegment,
                ":",
                NormalizeKeySegment(controlPlaneId),
                ":",
                SharedRunKeySegment,
                ":",
                NormalizeKeySegment(sharedRunId));
        }

        /// <summary>
        /// Builds the Redis sorted-set index key for shared runs inside one logical control-plane.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <returns>The Redis shared run index key.</returns>
        private RedisKey BuildIndexKey(
            string controlPlaneId)
        {
            return string.Concat(
                NormalizeBaseKeyPrefix(_options.KeyPrefix),
                ":",
                ControlPlaneKeySegment,
                ":",
                NormalizeKeySegment(controlPlaneId),
                ":",
                SharedRunIndexSegment);
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
                return DefaultKeyPrefix;
            }

            var normalized =
                keyPrefix
                    .Trim()
                    .TrimEnd(':');

            const string sharedRunsSuffix = ":shared-runs";

            if (normalized.EndsWith(
                    sharedRunsSuffix,
                    StringComparison.Ordinal))
            {
                normalized = normalized[..^sharedRunsSuffix.Length];
            }

            return string.IsNullOrWhiteSpace(normalized)
                ? DefaultKeyPrefix
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
                    $"Redis shared run record is missing required field '{name}'.");
            }

            return value;
        }

        /// <summary>
        /// Parses a shared run status.
        /// </summary>
        /// <param name="value">The status value.</param>
        /// <returns>The parsed shared run status.</returns>
        private static AiSharedRunStatus ParseStatus(
            string value)
        {
            if (Enum.TryParse<AiSharedRunStatus>(
                    value,
                    ignoreCase: true,
                    out var status))
            {
                return status;
            }

            return AiSharedRunStatus.Unknown;
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
        /// Deserializes a required JSON value.
        /// </summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <param name="json">The JSON value.</param>
        /// <param name="fieldName">The field name used for diagnostics.</param>
        /// <returns>The deserialized value.</returns>
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
                    $"Redis shared run record field '{fieldName}' could not be deserialized.");
            }

            return value;
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