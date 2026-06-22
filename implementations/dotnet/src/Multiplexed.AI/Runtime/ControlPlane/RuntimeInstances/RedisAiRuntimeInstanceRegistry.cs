using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Isolation;
using StackExchange.Redis;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances
{
    /// <summary>
    /// Redis-backed implementation of the runtime instance registry.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Stores runtime instance visibility, heartbeat, capacity, and status in Redis.
    /// - Allows multiple control-plane and runtime-instance processes to share the same registry.
    ///
    /// IMPORTANT:
    /// - This implementation replaces the process-local in-memory registry for distributed deployments.
    /// - Local runtime queues remain local to each runtime instance.
    /// - Runtime instance visibility is scoped by logical control-plane identifier.
    /// - Reads are defensively filtered by logical control-plane identifier to avoid returning
    ///   stale, migrated, corrupted, or foreign entries.
    /// - Registry listing self-heals the scoped index by removing missing or foreign entries.
    /// - Tenant-aware visibility is applied on read operations through the active execution context.
    /// </remarks>
    public sealed class RedisAiRuntimeInstanceRegistry : IAiRuntimeInstanceRegistry
    {
        private const string KeyPrefix = "ai:control-plane";
        private const string InstanceSetSegment = "runtime-instances";
        private const string InstanceKeySegment = "runtime-instance";

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly IDatabase database;
        private readonly AiRuntimeInstanceRegistrationOptions registrationOptions;
        private readonly IAiControlPlaneIdResolver controlPlaneIdResolver;
        private readonly IAiRuntimeInstanceVisibilityEvaluator visibilityEvaluator;
        private readonly IExecutionContextSnapshotProvider? executionContextSnapshotProvider;
        private readonly ConcurrentDictionary<string, string> controlPlaneIdsByRuntimeInstanceId =
            new(StringComparer.Ordinal);

        /// <summary>
        /// Initializes a new instance of the <see cref="RedisAiRuntimeInstanceRegistry"/> class.
        /// </summary>
        /// <param name="redis">The Redis connection multiplexer.</param>
        /// <param name="registrationOptions">The runtime instance registration options.</param>
        /// <param name="controlPlaneIdResolver">The control-plane identifier resolver.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="redis"/>, <paramref name="registrationOptions"/>,
        /// or <paramref name="controlPlaneIdResolver"/> is null.
        /// </exception>
        public RedisAiRuntimeInstanceRegistry(
            IConnectionMultiplexer redis,
            IOptions<AiRuntimeInstanceRegistrationOptions> registrationOptions,
            IAiControlPlaneIdResolver controlPlaneIdResolver)
            : this(
                redis,
                registrationOptions,
                controlPlaneIdResolver,
                new AiRuntimeInstanceVisibilityEvaluator(new HardcodedAiTenantRuntimeSettingsProvider()),
                executionContextSnapshotProvider: null)
        {
        }

        /// <summary>
        /// Initializes a tenant-aware instance of the <see cref="RedisAiRuntimeInstanceRegistry"/> class.
        /// </summary>
        /// <param name="redis">The Redis connection multiplexer.</param>
        /// <param name="registrationOptions">The runtime instance registration options.</param>
        /// <param name="controlPlaneIdResolver">The control-plane identifier resolver.</param>
        /// <param name="visibilityEvaluator">The runtime instance visibility evaluator.</param>
        /// <param name="executionContextSnapshotProvider">The execution context snapshot provider.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="redis"/>, <paramref name="registrationOptions"/>,
        /// <paramref name="controlPlaneIdResolver"/>, or <paramref name="visibilityEvaluator"/> is null.
        /// </exception>
        public RedisAiRuntimeInstanceRegistry(
            IConnectionMultiplexer redis,
            IOptions<AiRuntimeInstanceRegistrationOptions> registrationOptions,
            IAiControlPlaneIdResolver controlPlaneIdResolver,
            IAiRuntimeInstanceVisibilityEvaluator visibilityEvaluator,
            IExecutionContextSnapshotProvider? executionContextSnapshotProvider)
        {
            ArgumentNullException.ThrowIfNull(redis);
            ArgumentNullException.ThrowIfNull(registrationOptions);
            ArgumentNullException.ThrowIfNull(controlPlaneIdResolver);
            ArgumentNullException.ThrowIfNull(visibilityEvaluator);

            database = redis.GetDatabase();
            this.registrationOptions = registrationOptions.Value;
            this.controlPlaneIdResolver = controlPlaneIdResolver;
            this.visibilityEvaluator = visibilityEvaluator;
            this.executionContextSnapshotProvider = executionContextSnapshotProvider;
        }

        /// <inheritdoc />
        public async Task<AiRuntimeInstanceSnapshot> RegisterAsync(
            AiRuntimeInstanceRegistration registration,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(registration);
            ArgumentException.ThrowIfNullOrWhiteSpace(registration.RuntimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(
                        registration.ControlPlaneId,
                        cancellationToken)
                    .ConfigureAwait(false);

            controlPlaneIdsByRuntimeInstanceId[registration.RuntimeInstanceId] =
                controlPlaneId;

            var now = DateTimeOffset.UtcNow;
            var effectiveRegistration =
                EnsureRegistrationControlPlaneId(
                    registration,
                    controlPlaneId);

            var key = GetInstanceKey(
                controlPlaneId,
                effectiveRegistration.RuntimeInstanceId);

            var existing = await GetEntryAsync(
                    controlPlaneId,
                    effectiveRegistration.RuntimeInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);

            var entry = existing is null
                ? RuntimeInstanceEntry.Create(effectiveRegistration, now)
                : existing.UpdateRegistration(effectiveRegistration, now);

            await SaveEntryAsync(
                    controlPlaneId,
                    key,
                    effectiveRegistration.RuntimeInstanceId,
                    entry,
                    cancellationToken)
                .ConfigureAwait(false);

            return entry.ToSnapshot(now);
        }

        /// <inheritdoc />
        public async Task<AiRuntimeInstanceSnapshot?> HeartbeatAsync(
            string runtimeInstanceId,
            int queuedRunCount,
            int runningRunCount,
            int activeRunCount,
            int? availableRunSlots,
            int? activeWorkerCount,
            int? availableWorkerCount,
            int? maxLocalWorkersPerExecution,
            bool isQueuePaused,
            bool canAcceptRun,
            AiRuntimeInstanceStatus status,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await ResolveControlPlaneIdForRuntimeInstanceAsync(
                        runtimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            var existing = await GetEntryAsync(
                    controlPlaneId,
                    runtimeInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;

            var effectiveCanAcceptRun =
                existing.Role == AiRuntimeInstanceRole.Runtime &&
                canAcceptRun;

            var effectiveAvailableRunSlots =
                existing.Role == AiRuntimeInstanceRole.Runtime
                    ? availableRunSlots
                    : 0;

            var effectiveActiveWorkerCount =
                existing.Role == AiRuntimeInstanceRole.Runtime
                    ? activeWorkerCount
                    : 0;

            var effectiveAvailableWorkerCount =
                existing.Role == AiRuntimeInstanceRole.Runtime
                    ? availableWorkerCount
                    : 0;

            var effectiveMaxLocalWorkersPerExecution =
                existing.Role == AiRuntimeInstanceRole.Runtime
                    ? maxLocalWorkersPerExecution
                    : null;

            var updated = existing.UpdateHeartbeat(
                queuedRunCount,
                runningRunCount,
                activeRunCount,
                effectiveAvailableRunSlots,
                effectiveActiveWorkerCount,
                effectiveAvailableWorkerCount,
                effectiveMaxLocalWorkersPerExecution,
                isQueuePaused,
                effectiveCanAcceptRun,
                status,
                now);

            await SaveEntryAsync(
                    controlPlaneId,
                    GetInstanceKey(controlPlaneId, runtimeInstanceId),
                    runtimeInstanceId,
                    updated,
                    cancellationToken)
                .ConfigureAwait(false);

            return updated.ToSnapshot(now);
        }

        /// <inheritdoc />
        public async Task<AiRuntimeInstanceSnapshot?> GetAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await ResolveControlPlaneIdForRuntimeInstanceAsync(
                        runtimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            var entry = await GetEntryAsync(
                    controlPlaneId,
                    runtimeInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (entry is null)
            {
                return null;
            }

            var snapshot = entry.ToSnapshot(DateTimeOffset.UtcNow);

            if (!IsVisibleToCurrentTenant(snapshot))
            {
                return null;
            }

            return snapshot;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AiRuntimeInstanceSnapshot>> ListAsync(
            bool includeStopped = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(
                        requestedControlPlaneId: null,
                        cancellationToken)
                    .ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            var instanceSetKey = GetInstanceSetKey(controlPlaneId);

            var members = await database
                .SetMembersAsync(instanceSetKey)
                .ConfigureAwait(false);

            var snapshots = new List<AiRuntimeInstanceSnapshot>();

            foreach (var member in members)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!member.HasValue)
                {
                    continue;
                }

                var runtimeInstanceId = member.ToString();

                if (string.IsNullOrWhiteSpace(runtimeInstanceId))
                {
                    continue;
                }

                var entry = await GetRawEntryAsync(
                        controlPlaneId,
                        runtimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (entry is null)
                {
                    await RemoveFromIndexAsync(
                            instanceSetKey,
                            runtimeInstanceId)
                        .ConfigureAwait(false);

                    continue;
                }

                if (!BelongsToControlPlane(
                        entry.ControlPlaneId,
                        controlPlaneId))
                {
                    await RemoveFromIndexAsync(
                            instanceSetKey,
                            runtimeInstanceId)
                        .ConfigureAwait(false);

                    continue;
                }

                if (!includeStopped &&
                    entry.Status == AiRuntimeInstanceStatus.Stopped)
                {
                    continue;
                }

                var snapshot = entry.ToSnapshot(now);

                if (!IsVisibleToCurrentTenant(snapshot))
                {
                    continue;
                }

                snapshots.Add(snapshot);
            }

            return snapshots
                .OrderBy(snapshot => snapshot.RuntimeInstanceId, StringComparer.Ordinal)
                .ToArray();
        }

        /// <inheritdoc />
        public async Task<AiRuntimeInstanceSnapshot?> MarkDrainingAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await ResolveControlPlaneIdForRuntimeInstanceAsync(
                        runtimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            var existing = await GetEntryAsync(
                    controlPlaneId,
                    runtimeInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            var updated = existing.WithStatus(AiRuntimeInstanceStatus.Draining, now);

            await SaveEntryAsync(
                    controlPlaneId,
                    GetInstanceKey(controlPlaneId, runtimeInstanceId),
                    runtimeInstanceId,
                    updated,
                    cancellationToken)
                .ConfigureAwait(false);

            return updated.ToSnapshot(now);
        }

        /// <inheritdoc />
        public async Task<AiRuntimeInstanceSnapshot?> UnregisterAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await ResolveControlPlaneIdForRuntimeInstanceAsync(
                        runtimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            var instanceSetKey = GetInstanceSetKey(controlPlaneId);

            var existing = await GetEntryAsync(
                    controlPlaneId,
                    runtimeInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                await RemoveFromIndexAsync(
                        instanceSetKey,
                        runtimeInstanceId)
                    .ConfigureAwait(false);

                controlPlaneIdsByRuntimeInstanceId.TryRemove(
                    runtimeInstanceId,
                    out _);

                return null;
            }

            var now = DateTimeOffset.UtcNow;
            var stopped = existing.WithStatus(AiRuntimeInstanceStatus.Stopped, now);
            var snapshot = stopped.ToSnapshot(now);

            var key = GetInstanceKey(
                controlPlaneId,
                runtimeInstanceId);

            var batch = database.CreateBatch();

            var removeFromIndexTask = batch.SetRemoveAsync(
                instanceSetKey,
                runtimeInstanceId);

            var deleteEntryTask = batch.KeyDeleteAsync(key);

            batch.Execute();

            await removeFromIndexTask.ConfigureAwait(false);
            await deleteEntryTask.ConfigureAwait(false);

            controlPlaneIdsByRuntimeInstanceId.TryRemove(
                runtimeInstanceId,
                out _);

            return snapshot;
        }

        /// <summary>
        /// Determines whether a runtime instance snapshot is visible to the current tenant context.
        /// </summary>
        /// <param name="snapshot">The runtime instance snapshot.</param>
        /// <returns>
        /// <c>true</c> when the runtime instance snapshot is visible to the current tenant context;
        /// otherwise, <c>false</c>.
        /// </returns>
        private bool IsVisibleToCurrentTenant(
            AiRuntimeInstanceSnapshot snapshot)
        {
            var currentSnapshot = TryResolveSnapshot();

            if (currentSnapshot is null)
            {
                return true;
            }

            var descriptor = visibilityEvaluator.CreateDescriptor(
                snapshot.RuntimeInstanceId,
                snapshot.Metadata);

            return visibilityEvaluator.IsVisible(
                currentSnapshot.TenantId,
                currentSnapshot.TenantGroupId,
                descriptor);
        }

        /// <summary>
        /// Resolves the current execution context snapshot when a provider is available.
        /// </summary>
        /// <returns>
        /// The current execution context snapshot, or <c>null</c> when no execution context
        /// provider is available or no active execution context can be resolved.
        /// </returns>
        private ExecutionContextSnapshot? TryResolveSnapshot()
        {
            if (executionContextSnapshotProvider is null)
            {
                return null;
            }

            try
            {
                return executionContextSnapshotProvider.MapToSnapshot();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>
        /// Gets a runtime instance entry from Redis and validates that it belongs to the
        /// expected logical control-plane.
        /// </summary>
        /// <param name="controlPlaneId">The expected logical control-plane identifier.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>
        /// The runtime instance entry when found and scoped to the expected control-plane;
        /// otherwise, <c>null</c>.
        /// </returns>
        private async Task<RuntimeInstanceEntry?> GetEntryAsync(
            string controlPlaneId,
            string runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            var entry = await GetRawEntryAsync(
                    controlPlaneId,
                    runtimeInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!BelongsToControlPlane(
                    entry?.ControlPlaneId,
                    controlPlaneId))
            {
                return null;
            }

            return entry;
        }

        /// <summary>
        /// Gets a runtime instance entry from the scoped Redis key without applying
        /// control-plane validation.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier used to build the Redis key.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The raw runtime instance entry when found; otherwise, <c>null</c>.</returns>
        private async Task<RuntimeInstanceEntry?> GetRawEntryAsync(
            string controlPlaneId,
            string runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var value = await database
                .StringGetAsync(GetInstanceKey(controlPlaneId, runtimeInstanceId))
                .ConfigureAwait(false);

            if (!value.HasValue)
            {
                return null;
            }

            return JsonSerializer.Deserialize<RuntimeInstanceEntry>(
                value.ToString(),
                JsonOptions);
        }

        /// <summary>
        /// Saves a runtime instance entry and indexes it inside the scoped control-plane instance set.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <param name="key">The Redis key of the runtime instance entry.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="entry">The runtime instance entry to save.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task SaveEntryAsync(
            string controlPlaneId,
            string key,
            string runtimeInstanceId,
            RuntimeInstanceEntry entry,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var json = JsonSerializer.Serialize(entry, JsonOptions);
            var instanceSetKey = GetInstanceSetKey(controlPlaneId);

            var batch = database.CreateBatch();

            var setTask = batch.StringSetAsync(
                key,
                json,
                registrationOptions.RegistryTtl);

            var addTask = batch.SetAddAsync(
                instanceSetKey,
                runtimeInstanceId);

            batch.Execute();

            await setTask.ConfigureAwait(false);
            await addTask.ConfigureAwait(false);
        }

        /// <summary>
        /// Removes a runtime instance identifier from a scoped registry index.
        /// </summary>
        /// <param name="instanceSetKey">The scoped Redis instance set key.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        private Task RemoveFromIndexAsync(
            string instanceSetKey,
            string runtimeInstanceId)
        {
            return database.SetRemoveAsync(
                instanceSetKey,
                runtimeInstanceId);
        }

        /// <summary>
        /// Resolves the logical control-plane identifier used to scope Redis registry keys.
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
                await controlPlaneIdResolver
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
        /// Ensures that a runtime instance registration carries the resolved logical control-plane identifier.
        /// </summary>
        /// <param name="registration">The runtime instance registration.</param>
        /// <param name="controlPlaneId">The resolved logical control-plane identifier.</param>
        /// <returns>The registration with a logical control-plane identifier.</returns>
        private static AiRuntimeInstanceRegistration EnsureRegistrationControlPlaneId(
            AiRuntimeInstanceRegistration registration,
            string controlPlaneId)
        {
            if (string.Equals(
                    registration.ControlPlaneId,
                    controlPlaneId,
                    StringComparison.Ordinal))
            {
                return registration;
            }

            var metadata =
                new Dictionary<string, string>(
                    registration.Metadata,
                    StringComparer.Ordinal)
                {
                    ["controlPlaneId"] = controlPlaneId
                };

            return new AiRuntimeInstanceRegistration
            {
                RuntimeInstanceId = registration.RuntimeInstanceId,
                ControlPlaneId = controlPlaneId,
                ControlPlaneHostId = registration.ControlPlaneHostId,
                HostId = registration.HostId,
                RuntimeId = registration.RuntimeId,
                Role = registration.Role,
                WorkerCount = registration.WorkerCount,
                QueueCapacity = registration.QueueCapacity,
                MaxConcurrentRuns = registration.MaxConcurrentRuns,
                RegisteredAtUtc = registration.RegisteredAtUtc,
                Metadata = metadata
            };
        }

        /// <summary>
        /// Determines whether a stored runtime instance entry belongs to the expected logical control-plane.
        /// </summary>
        /// <param name="entryControlPlaneId">The control-plane identifier stored on the entry.</param>
        /// <param name="expectedControlPlaneId">The expected logical control-plane identifier.</param>
        /// <returns>
        /// <c>true</c> when the entry belongs to the expected control-plane, or when the
        /// entry has no control-plane identifier for backward compatibility; otherwise, <c>false</c>.
        /// </returns>
        private static bool BelongsToControlPlane(
            string? entryControlPlaneId,
            string expectedControlPlaneId)
        {
            if (string.IsNullOrWhiteSpace(entryControlPlaneId))
            {
                return true;
            }

            return string.Equals(
                NormalizeKeySegment(entryControlPlaneId),
                NormalizeKeySegment(expectedControlPlaneId),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Builds the Redis set key that indexes runtime instances for one logical control-plane.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <returns>The Redis runtime instance set key.</returns>
        private static string GetInstanceSetKey(
            string controlPlaneId)
        {
            return $"{KeyPrefix}:{NormalizeKeySegment(controlPlaneId)}:{InstanceSetSegment}";
        }

        /// <summary>
        /// Builds the Redis entry key for a runtime instance inside one logical control-plane.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <returns>The Redis runtime instance entry key.</returns>
        private static string GetInstanceKey(
            string controlPlaneId,
            string runtimeInstanceId)
        {
            return $"{KeyPrefix}:{NormalizeKeySegment(controlPlaneId)}:{InstanceKeySegment}:{NormalizeKeySegment(runtimeInstanceId)}";
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
        /// Resolves the logical control-plane identifier for an already known runtime instance.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The logical control-plane identifier.</returns>
        private async Task<string> ResolveControlPlaneIdForRuntimeInstanceAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (controlPlaneIdsByRuntimeInstanceId.TryGetValue(
                    runtimeInstanceId,
                    out var knownControlPlaneId) &&
                !string.IsNullOrWhiteSpace(knownControlPlaneId))
            {
                return knownControlPlaneId;
            }

            return await ResolveControlPlaneIdAsync(
                    requestedControlPlaneId: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}