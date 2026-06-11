using System.Text.Json;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
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
    /// - Admission reservation should be added separately after this registry is stable.
    /// - Runtime instance visibility is scoped by logical control-plane identifier.
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

        /// <summary>
        /// Initializes a new instance of the <see cref="RedisAiRuntimeInstanceRegistry"/> class.
        /// </summary>
        /// <param name="redis">The Redis connection multiplexer.</param>
        /// <param name="registrationOptions">The runtime instance registration options.</param>
        /// <param name="controlPlaneIdResolver">The control-plane identifier resolver.</param>
        public RedisAiRuntimeInstanceRegistry(
            IConnectionMultiplexer redis,
            IOptions<AiRuntimeInstanceRegistrationOptions> registrationOptions,
            IAiControlPlaneIdResolver controlPlaneIdResolver)
        {
            ArgumentNullException.ThrowIfNull(redis);
            ArgumentNullException.ThrowIfNull(registrationOptions);
            ArgumentNullException.ThrowIfNull(controlPlaneIdResolver);

            database = redis.GetDatabase();
            this.registrationOptions = registrationOptions.Value;
            this.controlPlaneIdResolver = controlPlaneIdResolver;
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

            var now = DateTimeOffset.UtcNow;
            var key = GetInstanceKey(
                controlPlaneId,
                registration.RuntimeInstanceId);

            var existing = await GetEntryAsync(
                    controlPlaneId,
                    registration.RuntimeInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);

            var entry = existing is null
                ? RuntimeInstanceEntry.Create(registration, now)
                : existing.UpdateRegistration(registration, now);

            await SaveEntryAsync(
                    controlPlaneId,
                    key,
                    registration.RuntimeInstanceId,
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
                await ResolveControlPlaneIdAsync(
                        requestedControlPlaneId: null,
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
                await ResolveControlPlaneIdAsync(
                        requestedControlPlaneId: null,
                        cancellationToken)
                    .ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;

            var entry = await GetEntryAsync(
                    controlPlaneId,
                    runtimeInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);

            return entry?.ToSnapshot(now);
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

                var entry = await GetEntryAsync(
                        controlPlaneId,
                        runtimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (entry is null)
                {
                    await database
                        .SetRemoveAsync(instanceSetKey, runtimeInstanceId)
                        .ConfigureAwait(false);

                    continue;
                }

                if (!includeStopped && entry.Status == AiRuntimeInstanceStatus.Stopped)
                {
                    continue;
                }

                snapshots.Add(entry.ToSnapshot(now));
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
                await ResolveControlPlaneIdAsync(
                        requestedControlPlaneId: null,
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
                await ResolveControlPlaneIdAsync(
                        requestedControlPlaneId: null,
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
                await database
                    .SetRemoveAsync(instanceSetKey, runtimeInstanceId)
                    .ConfigureAwait(false);

                return null;
            }

            var now = DateTimeOffset.UtcNow;
            var stopped = existing.WithStatus(AiRuntimeInstanceStatus.Stopped, now);
            var snapshot = stopped.ToSnapshot(now);

            var key = GetInstanceKey(
                controlPlaneId,
                runtimeInstanceId);

            var batch = database.CreateBatch();

            var removeFromIndexTask = batch.SetRemoveAsync(instanceSetKey, runtimeInstanceId);
            var deleteEntryTask = batch.KeyDeleteAsync(key);

            batch.Execute();

            await removeFromIndexTask.ConfigureAwait(false);
            await deleteEntryTask.ConfigureAwait(false);

            return snapshot;
        }

        /// <summary>
        /// Gets a runtime instance entry from Redis.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The runtime instance entry when found; otherwise, <c>null</c>.</returns>
        private async Task<RuntimeInstanceEntry?> GetEntryAsync(
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

            var addTask = batch.SetAddAsync(instanceSetKey, runtimeInstanceId);

            batch.Execute();

            await setTask.ConfigureAwait(false);
            await addTask.ConfigureAwait(false);
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
    }
}