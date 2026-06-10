using System.Text.Json;
using Microsoft.Extensions.Options;
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
    /// </remarks>
    public sealed class RedisAiRuntimeInstanceRegistry : IAiRuntimeInstanceRegistry
    {
        private const string InstanceSetKey = "ai:runtime-instances";
        private const string InstanceKeyPrefix = "ai:runtime-instance:";

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly IDatabase database;
        private readonly AiRuntimeInstanceRegistrationOptions registrationOptions;

        /// <summary>
        /// Initializes a new instance of the <see cref="RedisAiRuntimeInstanceRegistry"/> class.
        /// </summary>
        public RedisAiRuntimeInstanceRegistry(
            IConnectionMultiplexer redis,
            IOptions<AiRuntimeInstanceRegistrationOptions> registrationOptions)
        {
            ArgumentNullException.ThrowIfNull(redis);
            ArgumentNullException.ThrowIfNull(registrationOptions);

            database = redis.GetDatabase();
            this.registrationOptions = registrationOptions.Value;
        }

        /// <inheritdoc />
        public async Task<AiRuntimeInstanceSnapshot> RegisterAsync(
            AiRuntimeInstanceRegistration registration,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(registration);
            ArgumentException.ThrowIfNullOrWhiteSpace(registration.RuntimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            var now = DateTimeOffset.UtcNow;
            var key = GetInstanceKey(registration.RuntimeInstanceId);

            var existing = await GetEntryAsync(
                    registration.RuntimeInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);

            var entry = existing is null
                ? RuntimeInstanceEntry.Create(registration, now)
                : existing.UpdateRegistration(registration, now);

            await SaveEntryAsync(
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

            var existing = await GetEntryAsync(
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
                    GetInstanceKey(runtimeInstanceId),
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

            var now = DateTimeOffset.UtcNow;

            var entry = await GetEntryAsync(
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

            var now = DateTimeOffset.UtcNow;

            var members = await database
                .SetMembersAsync(InstanceSetKey)
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
                        runtimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (entry is null)
                {
                    await database
                        .SetRemoveAsync(InstanceSetKey, runtimeInstanceId)
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

            var existing = await GetEntryAsync(
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
                    GetInstanceKey(runtimeInstanceId),
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

            var existing = await GetEntryAsync(
                    runtimeInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                await database
                    .SetRemoveAsync(InstanceSetKey, runtimeInstanceId)
                    .ConfigureAwait(false);

                return null;
            }

            var now = DateTimeOffset.UtcNow;
            var stopped = existing.WithStatus(AiRuntimeInstanceStatus.Stopped, now);
            var snapshot = stopped.ToSnapshot(now);

            var key = GetInstanceKey(runtimeInstanceId);

            var batch = database.CreateBatch();

            var removeFromIndexTask = batch.SetRemoveAsync(InstanceSetKey, runtimeInstanceId);
            var deleteEntryTask = batch.KeyDeleteAsync(key);

            batch.Execute();

            await removeFromIndexTask.ConfigureAwait(false);
            await deleteEntryTask.ConfigureAwait(false);

            return snapshot;
        }

        private async Task<RuntimeInstanceEntry?> GetEntryAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var value = await database
                .StringGetAsync(GetInstanceKey(runtimeInstanceId))
                .ConfigureAwait(false);

            if (!value.HasValue)
            {
                return null;
            }

            return JsonSerializer.Deserialize<RuntimeInstanceEntry>(
                value.ToString(),
                JsonOptions);
        }

        private async Task SaveEntryAsync(
            string key,
            string runtimeInstanceId,
            RuntimeInstanceEntry entry,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var json = JsonSerializer.Serialize(entry, JsonOptions);

            var batch = database.CreateBatch();

            var setTask = batch.StringSetAsync(
                key,
                json,
                registrationOptions.RegistryTtl);

            var addTask = batch.SetAddAsync(InstanceSetKey, runtimeInstanceId);

            batch.Execute();

            await setTask.ConfigureAwait(false);
            await addTask.ConfigureAwait(false);
        }

        private static string GetInstanceKey(
            string runtimeInstanceId)
        {
            return $"{InstanceKeyPrefix}{runtimeInstanceId}";
        }
    }
}