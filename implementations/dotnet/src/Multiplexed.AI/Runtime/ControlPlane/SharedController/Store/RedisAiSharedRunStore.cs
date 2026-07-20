using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.Abstractions.Core.ExecutionContext;
using StackExchange.Redis;
using System.Globalization;
using System.Text.Json;

namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Store
{
    /// <summary>
    /// Redis-backed implementation of the shared runtime controller run store.
    /// </summary>
    /// <remarks>
    /// This store is scoped by logical control-plane id and now defensively filters
    /// reads/lists by the active tenant when an execution-context snapshot is available.
    /// Tenant identity comes from <see cref="ExecutionContextSnapshot.TenantId"/>.
    /// </remarks>
    public sealed class RedisAiSharedRunStore : IAiSharedRunStore
    {
        private const string DefaultKeyPrefix = "ai";
        private const string ControlPlaneKeySegment = "control-plane";
        private const string SharedRunKeySegment = "shared-run";
        private const string SharedRunIndexSegment = "shared-runs:index";
        private const string TenantKeySegment = "tenant";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly IDatabase _database;
        private readonly RedisAiSharedRunStoreOptions _options;
        private readonly RedisAiSharedRunStoreScriptCache _scripts;
        private readonly IAiControlPlaneIdResolver _controlPlaneIdResolver;
        private readonly IExecutionContextSnapshotProvider? _executionContextSnapshotProvider;

        public RedisAiSharedRunStore(
            IConnectionMultiplexer connection,
            IOptions<RedisAiSharedRunStoreOptions> options,
            IAiControlPlaneIdResolver controlPlaneIdResolver)
            : this(
                connection,
                options,
                controlPlaneIdResolver,
                executionContextSnapshotProvider: null)
        {
        }

        public RedisAiSharedRunStore(
            IConnectionMultiplexer connection,
            IOptions<RedisAiSharedRunStoreOptions> options,
            IAiControlPlaneIdResolver controlPlaneIdResolver,
            IExecutionContextSnapshotProvider? executionContextSnapshotProvider)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(controlPlaneIdResolver);

            _database = connection.GetDatabase();
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _scripts = new RedisAiSharedRunStoreScriptCache(connection);
            _controlPlaneIdResolver = controlPlaneIdResolver;
            _executionContextSnapshotProvider = executionContextSnapshotProvider;
        }

        public async Task<AiSharedRunRecord> CreateAsync(
            AiSharedRunRecord record,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentException.ThrowIfNullOrWhiteSpace(record.SharedRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(record.ExecutionContextSnapshot.TenantId);

            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId = await ResolveControlPlaneIdAsync(
                    record.ControlPlaneId,
                    record.Metadata,
                    cancellationToken)
                .ConfigureAwait(false);

            var controlPlaneMetadata =
                await _controlPlaneIdResolver
                    .ResolveMetadataAsync(
                        new AiControlPlaneIdResolutionRequest
                        {
                            RequestedControlPlaneId = controlPlaneId,
                            Metadata = record.Metadata,
                            Source = "redis-shared-run-store-create-metadata",
                            AllowGeneratedFallback = false
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            var effectiveRecord = EnsureControlPlaneId(
                record,
                controlPlaneId,
                controlPlaneMetadata);

            var runKey = BuildRunKey(
                controlPlaneId,
                effectiveRecord.SharedRunId);

            var indexKey = BuildIndexKey(
                controlPlaneId);

            var tenantIndexKey = BuildTenantIndexKey(
                controlPlaneId,
                effectiveRecord.ExecutionContextSnapshot.TenantId);

            var submittedAtScore = effectiveRecord.SubmittedAtUtc.ToUnixTimeMilliseconds();
            var expireSeconds = GetExpireSeconds();

            var result = await _scripts
                .ExecuteCreateAsync(
                    _database,
                    new RedisKey[]
                    {
                        runKey,
                        indexKey
                    },
                    BuildCreateValues(
                        effectiveRecord,
                        submittedAtScore,
                        expireSeconds))
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

            await AddToTenantIndexAsync(
                    tenantIndexKey,
                    effectiveRecord.SharedRunId,
                    submittedAtScore,
                    expireSeconds)
                .ConfigureAwait(false);

            return effectiveRecord;
        }

        public async Task<AiSharedRunRecord?> GetAsync(
            string sharedRunId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId = await ResolveControlPlaneIdAsync(
                    requestedControlPlaneId: null,
                    metadata: null,
                    cancellationToken)
                .ConfigureAwait(false);

            return await GetAsync(
                    controlPlaneId,
                    sharedRunId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<AiSharedRunRecord>> ListAsync(
            bool includeCancelled = false,
            bool includeCompleted = false,
            bool includeFailed = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId = await ResolveControlPlaneIdAsync(
                    requestedControlPlaneId: null,
                    metadata: null,
                    cancellationToken)
                .ConfigureAwait(false);

            var tenantId = ResolveCurrentTenantIdOrNull();

            var indexKey = string.IsNullOrWhiteSpace(tenantId)
                ? BuildIndexKey(controlPlaneId)
                : BuildTenantIndexKey(controlPlaneId, tenantId);

            var ids = await _database
                .SortedSetRangeByScoreAsync(
                    indexKey,
                    order: Order.Ascending,
                    take: _options.ListScanLimit)
                .ConfigureAwait(false);

            // Backward-compatible self-healing path for records created before the tenant index existed.
            if (ids.Length == 0 && !string.IsNullOrWhiteSpace(tenantId))
            {
                ids = await _database
                    .SortedSetRangeByScoreAsync(
                        BuildIndexKey(controlPlaneId),
                        order: Order.Ascending,
                        take: _options.ListScanLimit)
                    .ConfigureAwait(false);
            }

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

                var record = EnsureControlPlaneId(
                    rawRecord,
                    controlPlaneId);

                if (!string.IsNullOrWhiteSpace(tenantId) &&
                    !BelongsToTenant(record.ExecutionContextSnapshot.TenantId, tenantId))
                {
                    continue;
                }

                if (!includeCancelled && record.Status == AiSharedRunStatus.Cancelled)
                {
                    continue;
                }

                if (!includeCompleted && record.Status == AiSharedRunStatus.Completed)
                {
                    continue;
                }

                if (!includeFailed && record.Status == AiSharedRunStatus.Failed)
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

        public async Task<AiSharedRunRecord?> CancelAsync(
            string sharedRunId,
            string? reason = null,
            string? requestedBy = null,
            string? source = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId = await ResolveControlPlaneIdAsync(
                    requestedControlPlaneId: null,
                    metadata: null,
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

            var runKey = BuildRunKey(
                controlPlaneId,
                sharedRunId);

            var updatedAtUtc = DateTimeOffset.UtcNow.ToString(
                "O",
                CultureInfo.InvariantCulture);

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

            var controlPlaneId = await ResolveControlPlaneIdAsync(
                    requestedControlPlaneId: null,
                    metadata: null,
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

            var runKey = BuildRunKey(
                controlPlaneId,
                sharedRunId);

            var updatedAtUtc = DateTimeOffset.UtcNow.ToString(
                "O",
                CultureInfo.InvariantCulture);

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

        /// <inheritdoc />
        public async Task<AiSharedRunRecord?> MarkDispatchFailedAsync(
            string sharedRunId,
            string runtimeInstanceId,
            string? failureReason,
            string? message,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(
                        requestedControlPlaneId: null,
                        metadata: null,
                        cancellationToken)
                    .ConfigureAwait(false);

            var existing =
                await GetAsync(
                        controlPlaneId,
                        sharedRunId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (existing is null)
            {
                return null;
            }

            if (IsTerminal(existing.Status))
            {
                return existing;
            }

            var runKey =
                BuildRunKey(
                    controlPlaneId,
                    sharedRunId);

            var updatedAtUtc =
                DateTimeOffset.UtcNow.ToString(
                    "O",
                    CultureInfo.InvariantCulture);

            await _database
                .HashSetAsync(
                    runKey,
                    new HashEntry[]
                    {
                new("assignedRuntimeInstanceId", string.Empty),
                new("localRunId", string.Empty),
                new("reason", message ?? existing.Reason ?? string.Empty),
                new("failureReason", failureReason ?? string.Empty),
                new("updatedAtUtc", updatedAtUtc)
                    })
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            return await GetAsync(
                    controlPlaneId,
                    sharedRunId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task<AiSharedRunRecord?> MarkRequeuedAfterScaleOutAsync(
            string sharedRunId,
            string? reason = null,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            return MarkRequeuedAfterScaleOutIfCurrentAsync(
                sharedRunId,
                expectedAssignedRuntimeInstanceId: null,
                expectedLocalRunId: null,
                reason,
                metadata,
                cancellationToken);
        }

        /// <inheritdoc />
        public async Task<AiSharedRunRecord?> MarkRequeuedAfterScaleOutIfCurrentAsync(
            string sharedRunId,
            string? expectedAssignedRuntimeInstanceId,
            string? expectedLocalRunId,
            string? reason = null,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            cancellationToken.ThrowIfCancellationRequested();

            ValidateExpectedOwnership(
                expectedAssignedRuntimeInstanceId,
                expectedLocalRunId);

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(
                        requestedControlPlaneId: null,
                        metadata,
                        cancellationToken)
                    .ConfigureAwait(false);

            var existing =
                await GetAsync(
                        controlPlaneId,
                        sharedRunId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (existing is null)
            {
                return null;
            }

            if (!CanAttemptScaleOutRequeue(
                    existing,
                    expectedAssignedRuntimeInstanceId,
                    expectedLocalRunId))
            {
                return existing;
            }

            var mergedMetadata =
                new Dictionary<string, string>(
                    existing.Metadata,
                    StringComparer.OrdinalIgnoreCase);

            if (metadata is not null)
            {
                foreach (var item in metadata)
                {
                    mergedMetadata[item.Key] =
                        item.Value;
                }
            }

            var controlPlaneMetadata =
                await _controlPlaneIdResolver
                    .ResolveMetadataAsync(
                        new AiControlPlaneIdResolutionRequest
                        {
                            RequestedControlPlaneId = controlPlaneId,
                            Metadata = mergedMetadata,
                            Source = "redis-shared-run-store-requeued-after-scaleout-metadata",
                            AllowGeneratedFallback = false
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            foreach (var item in controlPlaneMetadata)
            {
                mergedMetadata[item.Key] =
                    item.Value;
            }

            var nowUtc =
                DateTimeOffset.UtcNow;

            mergedMetadata["scaleOutRequeued"] =
                "true";

            mergedMetadata["scaleOutRequeuedAtUtc"] =
                nowUtc.ToString(
                    "O",
                    CultureInfo.InvariantCulture);

            var runKey =
                BuildRunKey(
                    controlPlaneId,
                    sharedRunId);

            var updatedAtUtc =
                nowUtc.ToString(
                    "O",
                    CultureInfo.InvariantCulture);

            var requeueReason =
                string.IsNullOrWhiteSpace(reason)
                    ? "Scale-out fulfilled; shared run requeued for dispatch."
                    : reason;

            var hasExpectedOwnership =
                HasExpectedOwnership(
                    expectedAssignedRuntimeInstanceId,
                    expectedLocalRunId);

            var result =
                await _scripts
                    .ExecuteMarkRequeuedAfterScaleOutAsync(
                        _database,
                        new RedisKey[]
                        {
                            runKey
                        },
                        new RedisValue[]
                        {
                            requeueReason,
                            updatedAtUtc,
                            Serialize(mergedMetadata),
                            hasExpectedOwnership ? "1" : "0",
                            expectedAssignedRuntimeInstanceId ?? string.Empty,
                            expectedLocalRunId ?? string.Empty
                        })
                    .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            var status =
                result.ToString();

            if (string.Equals(
                    status,
                    "missing",
                    StringComparison.Ordinal))
            {
                return null;
            }

            if (string.Equals(
                    status,
                    "requeued",
                    StringComparison.Ordinal) ||
                string.Equals(
                    status,
                    "terminal",
                    StringComparison.Ordinal) ||
                string.Equals(
                    status,
                    "stale-ownership",
                    StringComparison.Ordinal) ||
                string.Equals(
                    status,
                    "not-waiting-for-scaleout",
                    StringComparison.Ordinal))
            {
                return await GetAsync(
                        controlPlaneId,
                        sharedRunId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                $"Unexpected Redis mark-requeued-after-scaleout result for shared run '{sharedRunId}': '{status}'.");
        }

        /// <summary>
        /// Determines whether a scale-out requeue can still be attempted from the
        /// currently observed record.
        /// </summary>
        /// <param name="existing">The current shared run record.</param>
        /// <param name="expectedAssignedRuntimeInstanceId">The expected failed runtime id.</param>
        /// <param name="expectedLocalRunId">The expected failed local run id.</param>
        /// <returns><c>true</c> when the atomic transition may still succeed.</returns>
        private static bool CanAttemptScaleOutRequeue(
            AiSharedRunRecord existing,
            string? expectedAssignedRuntimeInstanceId,
            string? expectedLocalRunId)
        {
            if (IsTerminal(existing.Status))
            {
                return false;
            }

            if (HasExpectedOwnership(
                    expectedAssignedRuntimeInstanceId,
                    expectedLocalRunId))
            {
                return string.Equals(
                        existing.AssignedRuntimeInstanceId,
                        expectedAssignedRuntimeInstanceId,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        existing.LocalRunId,
                        expectedLocalRunId,
                        StringComparison.Ordinal);
            }

            return existing.Status == AiSharedRunStatus.ScaleOutRequested &&
                string.IsNullOrWhiteSpace(existing.AssignedRuntimeInstanceId) &&
                string.IsNullOrWhiteSpace(existing.LocalRunId);
        }

        /// <summary>
        /// Validates that expected failed ownership is supplied as a complete pair.
        /// </summary>
        /// <param name="expectedAssignedRuntimeInstanceId">The expected failed runtime id.</param>
        /// <param name="expectedLocalRunId">The expected failed local run id.</param>
        private static void ValidateExpectedOwnership(
            string? expectedAssignedRuntimeInstanceId,
            string? expectedLocalRunId)
        {
            var hasExpectedRuntime =
                !string.IsNullOrWhiteSpace(
                    expectedAssignedRuntimeInstanceId);

            var hasExpectedLocalRun =
                !string.IsNullOrWhiteSpace(
                    expectedLocalRunId);

            if (hasExpectedRuntime != hasExpectedLocalRun)
            {
                throw new ArgumentException(
                    "Expected failed runtime instance id and local run id must be supplied together.");
            }
        }

        /// <summary>
        /// Determines whether complete expected failed ownership is available.
        /// </summary>
        /// <param name="expectedAssignedRuntimeInstanceId">The expected failed runtime id.</param>
        /// <param name="expectedLocalRunId">The expected failed local run id.</param>
        /// <returns><c>true</c> when both ownership identifiers are present.</returns>
        private static bool HasExpectedOwnership(
            string? expectedAssignedRuntimeInstanceId,
            string? expectedLocalRunId)
        {
            return !string.IsNullOrWhiteSpace(
                    expectedAssignedRuntimeInstanceId) &&
                !string.IsNullOrWhiteSpace(
                    expectedLocalRunId);
        }

        /// <summary>
        /// Determines whether a shared run status is terminal.
        /// </summary>
        /// <param name="status">The shared run status.</param>
        /// <returns><c>true</c> when the status is terminal; otherwise, <c>false</c>.</returns>
        private static bool IsTerminal(
            AiSharedRunStatus status)
        {
            return status is
                AiSharedRunStatus.Completed or
                AiSharedRunStatus.Failed or
                AiSharedRunStatus.Cancelled;
        }

        private async Task<AiSharedRunRecord?> GetAsync(
            string controlPlaneId,
            string sharedRunId,
            CancellationToken cancellationToken)
        {
            var record = await GetRawAsync(
                    controlPlaneId,
                    sharedRunId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!BelongsToControlPlane(record?.ControlPlaneId, controlPlaneId))
            {
                return null;
            }

            if (record is null)
            {
                return null;
            }

            var tenantId = ResolveCurrentTenantIdOrNull();

            if (!string.IsNullOrWhiteSpace(tenantId) &&
                !BelongsToTenant(record.ExecutionContextSnapshot.TenantId, tenantId))
            {
                return null;
            }

            return EnsureControlPlaneId(record, controlPlaneId);
        }

        private async Task<AiSharedRunRecord?> GetRawAsync(
            string controlPlaneId,
            string sharedRunId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entries = await _database
                .HashGetAllAsync(BuildRunKey(controlPlaneId, sharedRunId))
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            return entries.Length == 0
                ? null
                : MapRecord(entries);
        }

        private Task RemoveFromIndexAsync(
            RedisKey indexKey,
            string sharedRunId)
        {
            return _database.SortedSetRemoveAsync(
                indexKey,
                sharedRunId);
        }

        private async Task AddToTenantIndexAsync(
            RedisKey tenantIndexKey,
            string sharedRunId,
            long submittedAtScore,
            long expireSeconds)
        {
            await _database
                .SortedSetAddAsync(
                    tenantIndexKey,
                    sharedRunId,
                    submittedAtScore)
                .ConfigureAwait(false);

            if (expireSeconds > 0)
            {
                await _database
                    .KeyExpireAsync(
                        tenantIndexKey,
                        TimeSpan.FromSeconds(expireSeconds))
                    .ConfigureAwait(false);
            }
        }

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
            AddField(values, "executionContextSnapshotJson", Serialize(record.ExecutionContextSnapshot));
            AddField(values, "localRunId", record.LocalRunId);
            AddField(values, "executionId", record.ExecutionId);
            AddField(values, "assignedRuntimeInstanceId", record.AssignedRuntimeInstanceId);
            AddField(values, "admissionDecisionJson", Serialize(record.AdmissionDecision));
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

        private static void AddField(
            ICollection<RedisValue> values,
            string name,
            string? value)
        {
            values.Add(name);
            values.Add(value ?? string.Empty);
        }

        private static AiSharedRunRecord MapRecord(
            IReadOnlyCollection<HashEntry> entries)
        {
            var fields = entries.ToDictionary(
                entry => entry.Name.ToString(),
                entry => entry.Value.ToString(),
                StringComparer.Ordinal);

            var runRequest = DeserializeRequired<AiRuntimePipelineRunRequest>(
                GetRequired(fields, "runRequestJson"),
                "runRequestJson");

            var executionContextSnapshot = DeserializeRequired<ExecutionContextSnapshot>(
                GetRequired(fields, "executionContextSnapshotJson"),
                "executionContextSnapshotJson");

            var metadata = DeserializeOptional<IReadOnlyDictionary<string, string>>(
                    GetOptional(fields, "metadataJson"))
                ?? new Dictionary<string, string>();

            return new AiSharedRunRecord
            {
                SharedRunId = GetRequired(fields, "sharedRunId"),
                ControlPlaneId = GetOptional(fields, "controlPlaneId"),
                Status = ParseStatus(GetRequired(fields, "status")),
                RunRequest = runRequest,
                ExecutionContextSnapshot = executionContextSnapshot,
                LocalRunId = GetOptional(fields, "localRunId"),
                ExecutionId = GetOptional(fields, "executionId"),
                AssignedRuntimeInstanceId = GetOptional(fields, "assignedRuntimeInstanceId"),
                AdmissionDecision = DeserializeOptional<AiRunAdmissionDecision>(
                    GetOptional(fields, "admissionDecisionJson")),
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

        private string? ResolveCurrentTenantIdOrNull()
        {
            if (_executionContextSnapshotProvider is null)
            {
                return null;
            }

            try
            {
                var snapshot = _executionContextSnapshotProvider.MapToSnapshot();

                return string.IsNullOrWhiteSpace(snapshot.TenantId)
                    ? null
                    : snapshot.TenantId;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private async Task<string> ResolveControlPlaneIdAsync(
            string? requestedControlPlaneId,
            IReadOnlyDictionary<string, string>? metadata,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var resolvedControlPlaneId =
                await _controlPlaneIdResolver
                    .ResolveAsync(
                        new AiControlPlaneIdResolutionRequest
                        {
                            RequestedControlPlaneId = requestedControlPlaneId,
                            Metadata = metadata,
                            Source = "redis-shared-run-store",
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

        private static AiSharedRunRecord EnsureControlPlaneId(
            AiSharedRunRecord record,
            string controlPlaneId)
        {
            return EnsureControlPlaneId(
                record,
                controlPlaneId,
                controlPlaneMetadata: null);
        }

        private static AiSharedRunRecord EnsureControlPlaneId(
            AiSharedRunRecord record,
            string controlPlaneId,
            IReadOnlyDictionary<string, string>? controlPlaneMetadata)
        {
            var metadata = new Dictionary<string, string>(
                record.Metadata,
                StringComparer.OrdinalIgnoreCase);

            if (controlPlaneMetadata is not null)
            {
                foreach (var pair in controlPlaneMetadata)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key))
                    {
                        metadata[pair.Key] = pair.Value;
                    }
                }
            }

            return new AiSharedRunRecord
            {
                SharedRunId = record.SharedRunId,
                ControlPlaneId = controlPlaneId,
                Status = record.Status,
                RunRequest = record.RunRequest,
                ExecutionContextSnapshot = record.ExecutionContextSnapshot,
                LocalRunId = record.LocalRunId,
                ExecutionId = record.ExecutionId,
                AssignedRuntimeInstanceId = record.AssignedRuntimeInstanceId,
                AdmissionDecision = record.AdmissionDecision,
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

        private static bool BelongsToTenant(
            string? recordTenantId,
            string expectedTenantId)
        {
            if (string.IsNullOrWhiteSpace(recordTenantId))
            {
                return false;
            }

            return string.Equals(
                NormalizeKeySegment(recordTenantId),
                NormalizeKeySegment(expectedTenantId),
                StringComparison.Ordinal);
        }

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

        private RedisKey BuildTenantIndexKey(
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
                SharedRunIndexSegment);
        }

        private static string NormalizeBaseKeyPrefix(string keyPrefix)
        {
            if (string.IsNullOrWhiteSpace(keyPrefix))
            {
                return DefaultKeyPrefix;
            }

            var normalized = keyPrefix.Trim().TrimEnd(':');
            const string sharedRunsSuffix = ":shared-runs";

            if (normalized.EndsWith(sharedRunsSuffix, StringComparison.Ordinal))
            {
                normalized = normalized[..^sharedRunsSuffix.Length];
            }

            return string.IsNullOrWhiteSpace(normalized)
                ? DefaultKeyPrefix
                : normalized;
        }

        private static string NormalizeKeySegment(string value)
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
            return fields.TryGetValue(name, out var value) &&
                   !string.IsNullOrWhiteSpace(value)
                ? value
                : null;
        }

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

        private static AiSharedRunStatus ParseStatus(string value)
        {
            return Enum.TryParse<AiSharedRunStatus>(
                value,
                ignoreCase: true,
                out var status)
                ? status
                : AiSharedRunStatus.Unknown;
        }

        private static DateTimeOffset ParseDateTimeOffset(string value)
        {
            return DateTimeOffset.Parse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
        }

        private static string Serialize<T>(T? value)
        {
            return value is null
                ? string.Empty
                : JsonSerializer.Serialize(value, JsonOptions);
        }

        private static T? DeserializeOptional<T>(string? json)
        {
            return string.IsNullOrWhiteSpace(json)
                ? default
                : JsonSerializer.Deserialize<T>(json, JsonOptions);
        }

        private static T DeserializeRequired<T>(
            string json,
            string fieldName)
        {
            var value = JsonSerializer.Deserialize<T>(json, JsonOptions);

            if (value is null)
            {
                throw new InvalidOperationException(
                    $"Redis shared run record field '{fieldName}' could not be deserialized.");
            }

            return value;
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
