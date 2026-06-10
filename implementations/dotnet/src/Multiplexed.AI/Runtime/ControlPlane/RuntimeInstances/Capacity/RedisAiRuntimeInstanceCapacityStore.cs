using System.Text.Json;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using StackExchange.Redis;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Capacity
{
    /// <summary>
    /// Redis-backed implementation of runtime instance capacity visibility.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Stores runtime instance capacity descriptors in Redis.
    /// - Allows distributed control-plane components to observe local runtime capacity.
    ///
    /// IMPORTANT:
    /// - This store contains data-only descriptors.
    /// - It does not replace local queues.
    /// - It does not replace local in-memory dispatch objects.
    /// </remarks>
    public sealed class RedisAiRuntimeInstanceCapacityStore :
        IAiRuntimeInstanceCapacityStore
    {
        private const string CapacitySetKey =
            "ai:runtime-instance-capacity";

        private const string CapacityKeyPrefix =
            "ai:runtime-instance-capacity:";

        private static readonly JsonSerializerOptions JsonOptions =
            new(JsonSerializerDefaults.Web);

        private readonly IDatabase database;
        private readonly AiRuntimeInstanceRegistrationOptions registrationOptions;

        /// <summary>
        /// Initializes a new instance of the <see cref="RedisAiRuntimeInstanceCapacityStore"/> class.
        /// </summary>
        public RedisAiRuntimeInstanceCapacityStore(
            IConnectionMultiplexer redis,
            IOptions<AiRuntimeInstanceRegistrationOptions> registrationOptions)
        {
            ArgumentNullException.ThrowIfNull(redis);
            ArgumentNullException.ThrowIfNull(registrationOptions);

            database = redis.GetDatabase();
            this.registrationOptions = registrationOptions.Value;
        }

        /// <inheritdoc />
        public async Task PublishAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.RuntimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            var json =
                JsonSerializer.Serialize(
                    descriptor,
                    JsonOptions);

            var batch =
                database.CreateBatch();

            var setTask =
                batch.StringSetAsync(
                    GetCapacityKey(descriptor.RuntimeInstanceId),
                    json,
                    registrationOptions.CapacityTtl);

            var addTask =
                batch.SetAddAsync(
                    CapacitySetKey,
                    descriptor.RuntimeInstanceId);

            batch.Execute();

            await setTask.ConfigureAwait(false);
            await addTask.ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<AiRuntimeInstanceCapacityDescriptor?> GetAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            var value =
                await database
                    .StringGetAsync(GetCapacityKey(runtimeInstanceId))
                    .ConfigureAwait(false);

            if (!value.HasValue)
            {
                return null;
            }

            return JsonSerializer.Deserialize<AiRuntimeInstanceCapacityDescriptor>(
                value.ToString(),
                JsonOptions);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AiRuntimeInstanceCapacityDescriptor>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var members =
                await database
                    .SetMembersAsync(CapacitySetKey)
                    .ConfigureAwait(false);

            var descriptors =
                new List<AiRuntimeInstanceCapacityDescriptor>();

            foreach (var member in members)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!member.HasValue)
                {
                    continue;
                }

                var runtimeInstanceId =
                    member.ToString();

                if (string.IsNullOrWhiteSpace(runtimeInstanceId))
                {
                    continue;
                }

                var descriptor =
                    await GetAsync(
                            runtimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (descriptor is null)
                {
                    await database
                        .SetRemoveAsync(
                            CapacitySetKey,
                            runtimeInstanceId)
                        .ConfigureAwait(false);

                    continue;
                }

                descriptors.Add(descriptor);
            }

            return descriptors
                .OrderBy(
                    descriptor => descriptor.RuntimeInstanceId,
                    StringComparer.Ordinal)
                .ToArray();
        }

        /// <inheritdoc />
        public async Task<bool> RemoveAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            var batch =
                database.CreateBatch();

            var deleteTask =
                batch.KeyDeleteAsync(
                    GetCapacityKey(runtimeInstanceId));

            var removeTask =
                batch.SetRemoveAsync(
                    CapacitySetKey,
                    runtimeInstanceId);

            batch.Execute();

            await deleteTask.ConfigureAwait(false);

            return await removeTask.ConfigureAwait(false);
        }

        private static string GetCapacityKey(
            string runtimeInstanceId)
        {
            return $"{CapacityKeyPrefix}{runtimeInstanceId}";
        }
    }
}