using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Isolation;
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
    /// - Reads are defensively filtered by logical control-plane identifier to avoid returning
    ///   stale, migrated, corrupted, or foreign capacity descriptors.
    /// - Capacity listing self-heals the scoped index by removing missing or foreign descriptors.
    /// - Tenant-aware visibility is applied on read operations through the active execution context.
    /// - Tenant visibility supports both metadata-based isolation and first-class tenant fields.
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

        private const int MaximumPublishCompareExchangeAttempts =
            8;

        private static readonly JsonSerializerOptions JsonOptions =
            new(JsonSerializerDefaults.Web);

        private readonly IDatabase database;
        private readonly AiRuntimeInstanceRegistrationOptions registrationOptions;
        private readonly IAiControlPlaneIdResolver controlPlaneIdResolver;
        private readonly IAiRuntimeInstanceVisibilityEvaluator visibilityEvaluator;
        private readonly IExecutionContextSnapshotProvider? executionContextSnapshotProvider;
        private readonly ConcurrentDictionary<string, string> controlPlaneIdsByRuntimeInstanceId =
            new(StringComparer.Ordinal);

        /// <summary>
        /// Initializes a new instance of the <see cref="RedisAiRuntimeInstanceCapacityStore"/> class.
        /// </summary>
        /// <param name="redis">The Redis connection multiplexer.</param>
        /// <param name="registrationOptions">The runtime instance registration options.</param>
        /// <param name="controlPlaneIdResolver">The control-plane identifier resolver.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="redis"/>, <paramref name="registrationOptions"/>,
        /// or <paramref name="controlPlaneIdResolver"/> is null.
        /// </exception>
        public RedisAiRuntimeInstanceCapacityStore(
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
        /// Initializes a tenant-aware instance of the <see cref="RedisAiRuntimeInstanceCapacityStore"/> class.
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
        public RedisAiRuntimeInstanceCapacityStore(
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

            controlPlaneIdsByRuntimeInstanceId[descriptor.RuntimeInstanceId] =
                controlPlaneId;

            var effectiveDescriptor =
                EnsureDescriptorControlPlaneId(
                    descriptor,
                    controlPlaneId);

            var capacitySetKey =
                GetCapacitySetKey(controlPlaneId);

            var capacityKey =
                GetCapacityKey(
                    controlPlaneId,
                    effectiveDescriptor.RuntimeInstanceId);

            if (!IsKubernetesRemoteRuntime(effectiveDescriptor.Metadata) ||
                HasUsableTransportEndpoint(effectiveDescriptor.Metadata))
            {
                await PublishDescriptorUnconditionallyAsync(
                        capacitySetKey,
                        capacityKey,
                        effectiveDescriptor)
                    .ConfigureAwait(false);

                return;
            }

            for (var attempt = 1;
                 attempt <= MaximumPublishCompareExchangeAttempts;
                 attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var existingValue =
                    await database
                        .StringGetAsync(capacityKey)
                        .ConfigureAwait(false);

                var descriptorToPublish =
                    PreserveExternallyPublishedKubernetesMetadata(
                        effectiveDescriptor,
                        existingValue);

                var json =
                    JsonSerializer.Serialize(
                        descriptorToPublish,
                        JsonOptions);

                var transaction =
                    database.CreateTransaction();

                transaction.AddCondition(
                    existingValue.HasValue
                        ? Condition.StringEqual(
                            capacityKey,
                            existingValue)
                        : Condition.KeyNotExists(capacityKey));

                var setTask =
                    transaction.StringSetAsync(
                        capacityKey,
                        json,
                        registrationOptions.CapacityTtl);

                var addTask =
                    transaction.SetAddAsync(
                        capacitySetKey,
                        descriptorToPublish.RuntimeInstanceId);

                var committed =
                    await transaction
                        .ExecuteAsync()
                        .ConfigureAwait(false);

                if (!committed)
                {
                    continue;
                }

                await setTask.ConfigureAwait(false);
                await addTask.ConfigureAwait(false);

                return;
            }

            throw new InvalidOperationException(
                $"Runtime capacity descriptor publication for '{effectiveDescriptor.RuntimeInstanceId}' could not converge after '{MaximumPublishCompareExchangeAttempts}' compare-exchange attempts.");
        }

        /// <inheritdoc />
        public async Task<AiRuntimeInstanceCapacityDescriptor?> GetAsync(
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

            var descriptor =
                await GetDescriptorAsync(
                        controlPlaneId,
                        runtimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (descriptor is null)
            {
                return null;
            }

            if (!IsVisibleToCurrentTenant(descriptor))
            {
                return null;
            }

            return descriptor;
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
                    await GetRawDescriptorAsync(
                            controlPlaneId,
                            runtimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (descriptor is null)
                {
                    await RemoveFromIndexAsync(
                            capacitySetKey,
                            runtimeInstanceId)
                        .ConfigureAwait(false);

                    continue;
                }

                if (!BelongsToControlPlane(
                        descriptor.ControlPlaneId,
                        controlPlaneId))
                {
                    await RemoveFromIndexAsync(
                            capacitySetKey,
                            runtimeInstanceId)
                        .ConfigureAwait(false);

                    continue;
                }

                if (!IsVisibleToCurrentTenant(descriptor))
                {
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
                await ResolveControlPlaneIdForRuntimeInstanceAsync(
                        runtimeInstanceId,
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

            var removedFromIndex =
                await removeTask.ConfigureAwait(false);

            controlPlaneIdsByRuntimeInstanceId.TryRemove(
                runtimeInstanceId,
                out _);

            return removedFromIndex;
        }

        /// <summary>
        /// Publishes a descriptor through the existing fast path when no Kubernetes
        /// endpoint-preservation compare-exchange is required.
        /// </summary>
        /// <param name="capacitySetKey">The scoped capacity index key.</param>
        /// <param name="capacityKey">The scoped capacity descriptor key.</param>
        /// <param name="descriptor">The descriptor to publish.</param>
        private async Task PublishDescriptorUnconditionallyAsync(
            string capacitySetKey,
            string capacityKey,
            AiRuntimeInstanceCapacityDescriptor descriptor)
        {
            var json =
                JsonSerializer.Serialize(
                    descriptor,
                    JsonOptions);

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

        /// <summary>
        /// Preserves Kubernetes transport and ownership metadata when a stale runtime heartbeat
        /// races with the external Kubernetes host publisher.
        /// </summary>
        /// <param name="incomingDescriptor">The descriptor prepared by the current publisher.</param>
        /// <param name="existingValue">The descriptor value observed before the compare-exchange write.</param>
        /// <returns>The descriptor that can be written without losing externally published routing metadata.</returns>
        private static AiRuntimeInstanceCapacityDescriptor PreserveExternallyPublishedKubernetesMetadata(
            AiRuntimeInstanceCapacityDescriptor incomingDescriptor,
            RedisValue existingValue)
        {
            if (!existingValue.HasValue ||
                HasUsableTransportEndpoint(incomingDescriptor.Metadata))
            {
                return incomingDescriptor;
            }

            AiRuntimeInstanceCapacityDescriptor? existingDescriptor;

            try
            {
                existingDescriptor =
                    JsonSerializer.Deserialize<AiRuntimeInstanceCapacityDescriptor>(
                        existingValue.ToString(),
                        JsonOptions);
            }
            catch (JsonException)
            {
                return incomingDescriptor;
            }

            if (existingDescriptor is null ||
                (!IsKubernetesRemoteRuntime(incomingDescriptor.Metadata) &&
                 !IsKubernetesRemoteRuntime(existingDescriptor.Metadata)) ||
                !TryGetUsableTransportEndpoint(
                    existingDescriptor.Metadata,
                    out var existingTransportEndpoint))
            {
                return incomingDescriptor;
            }

            var metadata =
                new Dictionary<string, string>(
                    existingDescriptor.Metadata,
                    StringComparer.OrdinalIgnoreCase);

            foreach (var item in incomingDescriptor.Metadata)
            {
                metadata[item.Key] =
                    item.Value;
            }

            AddTransportEndpointAliases(
                metadata,
                existingTransportEndpoint);

            metadata["transport.endpoint.source"] =
                "preserved-existing-capacity-descriptor-compare-exchange";

            return new AiRuntimeInstanceCapacityDescriptor
            {
                RuntimeInstanceId = incomingDescriptor.RuntimeInstanceId,
                TenantId =
                    FirstNonEmpty(
                        incomingDescriptor.TenantId,
                        existingDescriptor.TenantId,
                        GetMetadataValue(
                            metadata,
                            AiRuntimeInstanceIsolationMetadataKeys.TenantId)),
                TenantGroupId =
                    FirstNonEmpty(
                        incomingDescriptor.TenantGroupId,
                        existingDescriptor.TenantGroupId,
                        GetMetadataValue(
                            metadata,
                            AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId)),
                ControlPlaneId = incomingDescriptor.ControlPlaneId,
                ControlPlaneHostId =
                    FirstNonEmpty(
                        incomingDescriptor.ControlPlaneHostId,
                        existingDescriptor.ControlPlaneHostId),
                Role = incomingDescriptor.Role,
                Status = incomingDescriptor.Status,
                WorkerCount = incomingDescriptor.WorkerCount,
                ActiveWorkerCount = incomingDescriptor.ActiveWorkerCount,
                AvailableWorkerCount = incomingDescriptor.AvailableWorkerCount,
                MaxWorkersPerRun = incomingDescriptor.MaxWorkersPerRun,
                MinWorkersRequiredPerRun = incomingDescriptor.MinWorkersRequiredPerRun,
                QueuedRunCount = incomingDescriptor.QueuedRunCount,
                RunningRunCount = incomingDescriptor.RunningRunCount,
                ActiveRunCount = incomingDescriptor.ActiveRunCount,
                MaxConcurrentRuns = incomingDescriptor.MaxConcurrentRuns,
                MaxRunSlots = incomingDescriptor.MaxRunSlots,
                AvailableRunSlots = incomingDescriptor.AvailableRunSlots,
                ReservedRunSlots = incomingDescriptor.ReservedRunSlots,
                EffectiveAvailableRunSlots = incomingDescriptor.EffectiveAvailableRunSlots,
                IsQueuePaused = incomingDescriptor.IsQueuePaused,
                CanAcceptRun = incomingDescriptor.CanAcceptRun,
                LastHeartbeatAtUtc = incomingDescriptor.LastHeartbeatAtUtc,
                Metadata = metadata
            };
        }

        /// <summary>
        /// Determines whether metadata identifies a Kubernetes-backed HTTP or gRPC runtime.
        /// </summary>
        /// <param name="metadata">The runtime metadata.</param>
        /// <returns><see langword="true"/> for a Kubernetes-backed remote runtime.</returns>
        private static bool IsKubernetesRemoteRuntime(
            IReadOnlyDictionary<string, string>? metadata)
        {
            var provider =
                GetMetadataValue(
                    metadata,
                    AiRuntimeInstanceProviderMetadataKeys.ProviderName) ??
                GetMetadataValue(
                    metadata,
                    "provider");

            var transport =
                GetMetadataValue(
                    metadata,
                    "transport.name") ??
                GetMetadataValue(
                    metadata,
                    "transportName") ??
                provider;

            var isRemoteTransport =
                string.Equals(provider, "http", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(provider, "grpc", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(transport, "grpc", StringComparison.OrdinalIgnoreCase);

            var hostProvider =
                GetMetadataValue(
                    metadata,
                    "host.provider");

            var hostType =
                GetMetadataValue(
                    metadata,
                    "hostType");

            var deployment =
                GetMetadataValue(
                    metadata,
                    "deployment");

            var isKubernetes =
                string.Equals(hostProvider, "kubernetes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(hostType, "runtime-instance-kubernetes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(deployment, "kubernetes-host", StringComparison.OrdinalIgnoreCase);

            return isRemoteTransport &&
                   isKubernetes;
        }

        /// <summary>
        /// Determines whether metadata contains a safe transport endpoint.
        /// </summary>
        /// <param name="metadata">The runtime metadata.</param>
        /// <returns><see langword="true"/> when a safe transport endpoint exists.</returns>
        private static bool HasUsableTransportEndpoint(
            IReadOnlyDictionary<string, string>? metadata)
        {
            return TryGetUsableTransportEndpoint(
                metadata,
                out _);
        }

        /// <summary>
        /// Resolves a safe transport endpoint from all supported metadata aliases.
        /// </summary>
        /// <param name="metadata">The runtime metadata.</param>
        /// <param name="transportEndpoint">The resolved endpoint.</param>
        /// <returns><see langword="true"/> when a safe endpoint exists.</returns>
        private static bool TryGetUsableTransportEndpoint(
            IReadOnlyDictionary<string, string>? metadata,
            out string transportEndpoint)
        {
            transportEndpoint =
                GetMetadataValue(metadata, "transport.endpoint") ??
                GetMetadataValue(metadata, "transportEndpoint") ??
                GetMetadataValue(metadata, "runtime.command.endpoint") ??
                GetMetadataValue(metadata, "grpc.endpoint") ??
                string.Empty;

            return !string.IsNullOrWhiteSpace(transportEndpoint) &&
                   !IsUnsafeKubernetesLocalhostEndpoint(
                       transportEndpoint);
        }

        /// <summary>
        /// Adds all supported transport endpoint aliases.
        /// </summary>
        /// <param name="metadata">The metadata dictionary.</param>
        /// <param name="transportEndpoint">The transport endpoint.</param>
        private static void AddTransportEndpointAliases(
            IDictionary<string, string> metadata,
            string transportEndpoint)
        {
            metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] =
                transportEndpoint;
            metadata["transport.endpoint"] =
                transportEndpoint;
            metadata["transportEndpoint"] =
                transportEndpoint;
            metadata["grpc.endpoint"] =
                transportEndpoint;
        }

        /// <summary>
        /// Determines whether a Kubernetes endpoint is an unsafe pod-local default.
        /// </summary>
        /// <param name="transportEndpoint">The transport endpoint.</param>
        /// <returns><see langword="true"/> when the endpoint is unsafe for multi-pod dispatch.</returns>
        private static bool IsUnsafeKubernetesLocalhostEndpoint(
            string? transportEndpoint)
        {
            if (string.IsNullOrWhiteSpace(transportEndpoint))
            {
                return false;
            }

            return transportEndpoint.Contains(
                       "127.0.0.1:8080",
                       StringComparison.OrdinalIgnoreCase) ||
                   transportEndpoint.Contains(
                       "localhost:8080",
                       StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets a metadata value using case-insensitive matching.
        /// </summary>
        /// <param name="metadata">The metadata dictionary.</param>
        /// <param name="key">The metadata key.</param>
        /// <returns>The metadata value, or <see langword="null"/> when absent.</returns>
        private static string? GetMetadataValue(
            IReadOnlyDictionary<string, string>? metadata,
            string key)
        {
            if (metadata is null)
            {
                return null;
            }

            if (metadata.TryGetValue(
                    key,
                    out var value))
            {
                return value;
            }

            foreach (var item in metadata)
            {
                if (string.Equals(
                        item.Key,
                        key,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return item.Value;
                }
            }

            return null;
        }

        /// <summary>
        /// Returns the first non-empty value.
        /// </summary>
        private static string? FirstNonEmpty(
            params string?[] values)
        {
            return values.FirstOrDefault(
                value => !string.IsNullOrWhiteSpace(value));
        }

        /// <summary>
        /// Determines whether a runtime instance capacity descriptor is visible to the current tenant context.
        /// </summary>
        /// <param name="descriptor">The runtime instance capacity descriptor.</param>
        /// <returns>
        /// <c>true</c> when the runtime instance capacity descriptor is visible to the current tenant context;
        /// otherwise, <c>false</c>.
        /// </returns>
        private bool IsVisibleToCurrentTenant(
            AiRuntimeInstanceCapacityDescriptor descriptor)
        {
            var currentSnapshot = TryResolveSnapshot();

            if (currentSnapshot is null)
            {
                return true;
            }

            var visibilityDescriptor =
                visibilityEvaluator.CreateDescriptor(
                    descriptor.RuntimeInstanceId,
                    CreateEffectiveIsolationMetadata(descriptor));

            return visibilityEvaluator.IsVisible(
                currentSnapshot.TenantId,
                currentSnapshot.TenantGroupId,
                visibilityDescriptor);
        }

        /// <summary>
        /// Creates effective isolation metadata by combining descriptor metadata with
        /// first-class tenant ownership fields.
        /// </summary>
        /// <param name="descriptor">The runtime instance capacity descriptor.</param>
        /// <returns>The effective isolation metadata used for tenant-aware visibility checks.</returns>
        /// <remarks>
        /// Metadata-based tenant ownership is kept for backward compatibility.
        /// First-class tenant fields are authoritative when present.
        /// </remarks>
        private static IReadOnlyDictionary<string, string> CreateEffectiveIsolationMetadata(
            AiRuntimeInstanceCapacityDescriptor descriptor)
        {
            var metadata =
                new Dictionary<string, string>(
                    descriptor.Metadata,
                    StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(descriptor.TenantId))
            {
                metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantId] =
                    descriptor.TenantId;
            }

            if (!string.IsNullOrWhiteSpace(descriptor.TenantGroupId))
            {
                metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] =
                    descriptor.TenantGroupId;
            }

            return metadata;
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
        /// Gets a runtime instance capacity descriptor from Redis and validates that it belongs
        /// to the expected logical control-plane.
        /// </summary>
        /// <param name="controlPlaneId">The expected logical control-plane identifier.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>
        /// The capacity descriptor when found and scoped to the expected control-plane;
        /// otherwise, <c>null</c>.
        /// </returns>
        private async Task<AiRuntimeInstanceCapacityDescriptor?> GetDescriptorAsync(
            string controlPlaneId,
            string runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            var descriptor =
                await GetRawDescriptorAsync(
                        controlPlaneId,
                        runtimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!BelongsToControlPlane(
                    descriptor?.ControlPlaneId,
                    controlPlaneId))
            {
                return null;
            }

            return descriptor;
        }

        /// <summary>
        /// Gets a runtime instance capacity descriptor from the scoped Redis key without applying
        /// control-plane validation.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier used to build the Redis key.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The raw capacity descriptor when found; otherwise, <c>null</c>.</returns>
        private async Task<AiRuntimeInstanceCapacityDescriptor?> GetRawDescriptorAsync(
            string controlPlaneId,
            string runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

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

        /// <summary>
        /// Removes a runtime instance identifier from a scoped capacity index.
        /// </summary>
        /// <param name="capacitySetKey">The scoped Redis capacity set key.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        private Task RemoveFromIndexAsync(
            string capacitySetKey,
            string runtimeInstanceId)
        {
            return database.SetRemoveAsync(
                capacitySetKey,
                runtimeInstanceId);
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

            var resolvedControlPlaneId =
                await this.controlPlaneIdResolver
                    .ResolveAsync(
                        new AiControlPlaneIdResolutionRequest
                        {
                            RequestedControlPlaneId = requestedControlPlaneId,
                            Source = "redis-runtime-instance-capacity-store",
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
        /// Ensures that a runtime instance capacity descriptor carries the resolved logical control-plane identifier.
        /// </summary>
        /// <param name="descriptor">The runtime instance capacity descriptor.</param>
        /// <param name="controlPlaneId">The resolved logical control-plane identifier.</param>
        /// <returns>The descriptor with a logical control-plane identifier.</returns>
        private static AiRuntimeInstanceCapacityDescriptor EnsureDescriptorControlPlaneId(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            string controlPlaneId)
        {
            if (string.Equals(
                    descriptor.ControlPlaneId,
                    controlPlaneId,
                    StringComparison.Ordinal))
            {
                return descriptor;
            }

            var metadata =
                new Dictionary<string, string>(
                    descriptor.Metadata,
                    StringComparer.Ordinal)
                {
                    ["controlPlaneId"] = controlPlaneId
                };

            return new AiRuntimeInstanceCapacityDescriptor
            {
                RuntimeInstanceId = descriptor.RuntimeInstanceId,
                TenantId = descriptor.TenantId,
                TenantGroupId = descriptor.TenantGroupId,
                ControlPlaneId = controlPlaneId,
                ControlPlaneHostId = descriptor.ControlPlaneHostId,
                Role = descriptor.Role,
                Status = descriptor.Status,
                WorkerCount = descriptor.WorkerCount,
                ActiveWorkerCount = descriptor.ActiveWorkerCount,
                AvailableWorkerCount = descriptor.AvailableWorkerCount,
                MaxWorkersPerRun = descriptor.MaxWorkersPerRun,
                MinWorkersRequiredPerRun = descriptor.MinWorkersRequiredPerRun,
                QueuedRunCount = descriptor.QueuedRunCount,
                RunningRunCount = descriptor.RunningRunCount,
                ActiveRunCount = descriptor.ActiveRunCount,
                MaxConcurrentRuns = descriptor.MaxConcurrentRuns,
                MaxRunSlots = descriptor.MaxRunSlots,
                AvailableRunSlots = descriptor.AvailableRunSlots,
                ReservedRunSlots = descriptor.ReservedRunSlots,
                EffectiveAvailableRunSlots = descriptor.EffectiveAvailableRunSlots,
                IsQueuePaused = descriptor.IsQueuePaused,
                CanAcceptRun = descriptor.CanAcceptRun,
                LastHeartbeatAtUtc = descriptor.LastHeartbeatAtUtc,
                Metadata = metadata
            };
        }

        /// <summary>
        /// Determines whether a stored runtime instance capacity descriptor belongs to the expected logical control-plane.
        /// </summary>
        /// <param name="descriptorControlPlaneId">The control-plane identifier stored on the descriptor.</param>
        /// <param name="expectedControlPlaneId">The expected logical control-plane identifier.</param>
        /// <returns>
        /// <c>true</c> when the descriptor belongs to the expected control-plane, or when the
        /// descriptor has no control-plane identifier for backward compatibility; otherwise, <c>false</c>.
        /// </returns>
        private static bool BelongsToControlPlane(
            string? descriptorControlPlaneId,
            string expectedControlPlaneId)
        {
            if (string.IsNullOrWhiteSpace(descriptorControlPlaneId))
            {
                return true;
            }

            return string.Equals(
                NormalizeKeySegment(descriptorControlPlaneId),
                NormalizeKeySegment(expectedControlPlaneId),
                StringComparison.Ordinal);
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