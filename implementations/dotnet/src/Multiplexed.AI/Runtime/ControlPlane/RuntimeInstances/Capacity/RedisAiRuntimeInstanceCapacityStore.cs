using System.Text.Json;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
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
    /// - Capacity visibility is scoped by logical control-plane identifier.
    /// </remarks>
    public sealed class RedisAiRuntimeInstanceCapacityStore :
        IAiRuntimeInstanceCapacityStore
    {
        private const string KeyPrefix =
            "ai:control-plane";

        private const string CapacitySetSegment =
            "runtime-instance-capacity";

        private const string CapacityKeySegment =
            "runtime-instance-capacity";

        private static readonly JsonSerializerOptions JsonOptions =
            new(JsonSerializerDefaults.Web);

        private readonly IDatabase database;
        private readonly AiRuntimeInstanceRegistrationOptions registrationOptions;
        private readonly IAiControlPlaneIdResolver controlPlaneIdResolver;

        /// <summary>
        /// Initializes a new instance of the <see cref="RedisAiRuntimeInstanceCapacityStore"/> class.
        /// </summary>
        /// <param name="redis">The Redis connection multiplexer.</param>
        /// <param name="registrationOptions">The runtime instance registration options.</param>
        /// <param name="controlPlaneIdResolver">The control-plane identifier resolver.</param>
        public RedisAiRuntimeInstanceCapacityStore(
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
        public async Task PublishAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.RuntimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(
                        descriptor.ControlPlaneId,
                        cancellationToken)
                    .ConfigureAwait(false);

            var json =
                JsonSerializer.Serialize(
                    descriptor,
                    JsonOptions);

            var capacitySetKey =
                GetCapacitySetKey(controlPlaneId);

            var capacityKey =
                GetCapacityKey(
                    controlPlaneId,
                    descriptor.RuntimeInstanceId);

            var batch =
                database.CreateBatch();

            var setTask =
                batch.StringSetAsync(
                    capacityKey,
                    json,
                    registrationOptions.CapacityTtl);

            var addTask =
                batch.SetAddAsync(
                    capacitySetKey,
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

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(
                        requestedControlPlaneId: null,
                        cancellationToken)
                    .ConfigureAwait(false);

            var value =
                await database
                    .StringGetAsync(
                        GetCapacityKey(
                            controlPlaneId,
                            runtimeInstanceId))
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

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(
                        requestedControlPlaneId: null,
                        cancellationToken)
                    .ConfigureAwait(false);

            var capacitySetKey =
                GetCapacitySetKey(controlPlaneId);

            var members =
                await database
                    .SetMembersAsync(capacitySetKey)
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
                            capacitySetKey,
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

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(
                        requestedControlPlaneId: null,
                        cancellationToken)
                    .ConfigureAwait(false);

            var capacitySetKey =
                GetCapacitySetKey(controlPlaneId);

            var capacityKey =
                GetCapacityKey(
                    controlPlaneId,
                    runtimeInstanceId);

            var batch =
                database.CreateBatch();

            var deleteTask =
                batch.KeyDeleteAsync(capacityKey);

            var removeTask =
                batch.SetRemoveAsync(
                    capacitySetKey,
                    runtimeInstanceId);

            batch.Execute();

            await deleteTask.ConfigureAwait(false);

            return await removeTask.ConfigureAwait(false);
        }

        /// <summary>
        /// Resolves the logical control-plane identifier used to scope Redis capacity keys.
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
        /// Builds the Redis set key that indexes runtime capacity descriptors for one logical control-plane.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <returns>The Redis runtime capacity set key.</returns>
        private static string GetCapacitySetKey(
            string controlPlaneId)
        {
            return $"{KeyPrefix}:{NormalizeKeySegment(controlPlaneId)}:{CapacitySetSegment}";
        }

        /// <summary>
        /// Builds the Redis entry key for a runtime capacity descriptor inside one logical control-plane.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <returns>The Redis runtime capacity entry key.</returns>
        private static string GetCapacityKey(
            string controlPlaneId,
            string runtimeInstanceId)
        {
            return $"{KeyPrefix}:{NormalizeKeySegment(controlPlaneId)}:{CapacityKeySegment}:{NormalizeKeySegment(runtimeInstanceId)}";
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