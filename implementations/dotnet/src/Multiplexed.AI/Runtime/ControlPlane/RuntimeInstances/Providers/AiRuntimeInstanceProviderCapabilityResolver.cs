using Microsoft.Extensions.Logging;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers
{
    /// <summary>
    /// Default runtime instance provider capability resolver.
    /// </summary>
    public sealed class AiRuntimeInstanceProviderCapabilityResolver :
        IAiRuntimeInstanceProviderCapabilityResolver
    {
        private readonly IAiRuntimeInstanceCapacityStore capacityStore;
        private readonly IAiRuntimeInstanceProviderRouter providerRouter;
        private readonly ILogger<AiRuntimeInstanceProviderCapabilityResolver> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeInstanceProviderCapabilityResolver"/> class.
        /// </summary>
        /// <param name="capacityStore">The runtime instance capacity store.</param>
        /// <param name="providerRouter">The runtime instance provider router.</param>
        /// <param name="logger">The logger.</param>
        public AiRuntimeInstanceProviderCapabilityResolver(
            IAiRuntimeInstanceCapacityStore capacityStore,
            IAiRuntimeInstanceProviderRouter providerRouter,
            ILogger<AiRuntimeInstanceProviderCapabilityResolver> logger)
        {
            this.capacityStore =
                capacityStore
                ?? throw new ArgumentNullException(nameof(capacityStore));

            this.providerRouter =
                providerRouter
                ?? throw new ArgumentNullException(nameof(providerRouter));

            this.logger =
                logger
                ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public async Task<AiRuntimeInstanceProviderCapabilityResolution<TProvider>> ResolveAsync<TProvider>(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
            where TProvider : IAiRuntimeInstanceProvider
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            this.logger.LogInformation(
                "RUNTIME PROVIDER CAPABILITY RESOLUTION BEGIN RuntimeInstanceId={RuntimeInstanceId} ProviderCapability={ProviderCapability} CapacityStoreType={CapacityStoreType} ProviderRouterType={ProviderRouterType}",
                runtimeInstanceId,
                typeof(TProvider).FullName,
                this.capacityStore.GetType().FullName,
                this.providerRouter.GetType().FullName);

            var descriptor =
                await this.capacityStore
                    .GetAsync(
                        runtimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (descriptor is null)
            {
                await this.LogKnownCapacityDescriptorsAsync(
                        runtimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

                return AiRuntimeInstanceProviderCapabilityResolution<TProvider>.Failed(
                    runtimeInstanceId,
                    $"Runtime instance capacity descriptor '{runtimeInstanceId}' was not found.");
            }

            this.LogDescriptorFound<TProvider>(
                descriptor);

            if (!this.providerRouter.TryGetProvider<TProvider>(
                    descriptor,
                    out var provider))
            {
                this.logger.LogWarning(
                    "RUNTIME PROVIDER CAPABILITY NOT FOUND RuntimeInstanceId={RuntimeInstanceId} ProviderCapability={ProviderCapability} DescriptorProviderName={ProviderName} DescriptorTransportName={TransportName} Metadata={Metadata}",
                    runtimeInstanceId,
                    typeof(TProvider).FullName,
                    ResolveMetadataValue(descriptor.Metadata, AiRuntimeInstanceProviderMetadataKeys.ProviderName, AiRuntimeInstanceProviderMetadataKeys.LegacyProviderName),
                    ResolveMetadataValue(descriptor.Metadata, AiRuntimeInstanceCommandTransportMetadataKeys.TransportName, "transport"),
                    FormatMetadata(descriptor.Metadata));

                return AiRuntimeInstanceProviderCapabilityResolution<TProvider>.Failed(
                    runtimeInstanceId,
                    $"No provider capability '{typeof(TProvider).FullName}' was found for runtime instance '{runtimeInstanceId}'.");
            }

            this.logger.LogInformation(
                "RUNTIME PROVIDER CAPABILITY RESOLUTION SUCCEEDED RuntimeInstanceId={RuntimeInstanceId} ProviderCapability={ProviderCapability} ProviderType={ProviderType}",
                runtimeInstanceId,
                typeof(TProvider).FullName,
                provider.GetType().FullName);

            return AiRuntimeInstanceProviderCapabilityResolution<TProvider>.Succeeded(
                runtimeInstanceId,
                descriptor,
                provider);
        }

        /// <summary>
        /// Logs a found descriptor.
        /// </summary>
        /// <typeparam name="TProvider">The expected provider capability type.</typeparam>
        /// <param name="descriptor">The runtime capacity descriptor.</param>
        private void LogDescriptorFound<TProvider>(
            AiRuntimeInstanceCapacityDescriptor descriptor)
            where TProvider : IAiRuntimeInstanceProvider
        {
            this.logger.LogInformation(
                "RUNTIME PROVIDER CAPABILITY DESCRIPTOR FOUND RuntimeInstanceId={RuntimeInstanceId} ControlPlaneId={ControlPlaneId} ControlPlaneHostId={ControlPlaneHostId} ProviderName={ProviderName} TransportName={TransportName} Role={Role} Status={Status} CanAcceptRun={CanAcceptRun} AvailableRunSlots={AvailableRunSlots} EffectiveAvailableRunSlots={EffectiveAvailableRunSlots} ReservedRunSlots={ReservedRunSlots} TenantId={TenantId} TenantGroupId={TenantGroupId} WorkerCount={WorkerCount} ActiveWorkerCount={ActiveWorkerCount} AvailableWorkerCount={AvailableWorkerCount} QueuedRunCount={QueuedRunCount} RunningRunCount={RunningRunCount} ActiveRunCount={ActiveRunCount} LastHeartbeatAtUtc={LastHeartbeatAtUtc} ProviderCapability={ProviderCapability} Metadata={Metadata}",
                descriptor.RuntimeInstanceId,
                descriptor.ControlPlaneId,
                descriptor.ControlPlaneHostId,
                ResolveMetadataValue(descriptor.Metadata, AiRuntimeInstanceProviderMetadataKeys.ProviderName, AiRuntimeInstanceProviderMetadataKeys.LegacyProviderName),
                ResolveMetadataValue(descriptor.Metadata, AiRuntimeInstanceCommandTransportMetadataKeys.TransportName, "transport"),
                descriptor.Role,
                descriptor.Status,
                descriptor.CanAcceptRun,
                descriptor.AvailableRunSlots,
                descriptor.EffectiveAvailableRunSlots,
                descriptor.ReservedRunSlots,
                descriptor.TenantId,
                descriptor.TenantGroupId,
                descriptor.WorkerCount,
                descriptor.ActiveWorkerCount,
                descriptor.AvailableWorkerCount,
                descriptor.QueuedRunCount,
                descriptor.RunningRunCount,
                descriptor.ActiveRunCount,
                descriptor.LastHeartbeatAtUtc,
                typeof(TProvider).FullName,
                FormatMetadata(descriptor.Metadata));
        }

        /// <summary>
        /// Logs known runtime capacity descriptors when an expected runtime descriptor cannot be found.
        /// </summary>
        /// <param name="requestedRuntimeInstanceId">The requested runtime instance id.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The asynchronous operation.</returns>
        private async Task LogKnownCapacityDescriptorsAsync(
            string requestedRuntimeInstanceId,
            CancellationToken cancellationToken)
        {
            try
            {
                var descriptors =
                    await this.capacityStore
                        .ListAsync(cancellationToken)
                        .ConfigureAwait(false);

                var descriptorList =
                    descriptors
                        .OrderBy(item => item.RuntimeInstanceId, StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                this.logger.LogWarning(
                    "RUNTIME PROVIDER CAPABILITY DESCRIPTOR MISSING RequestedRuntimeInstanceId={RequestedRuntimeInstanceId} CapacityStoreType={CapacityStoreType} KnownDescriptorCount={KnownDescriptorCount} KnownRuntimeInstanceIds={KnownRuntimeInstanceIds}",
                    requestedRuntimeInstanceId,
                    this.capacityStore.GetType().FullName,
                    descriptorList.Length,
                    string.Join(",", descriptorList.Select(item => item.RuntimeInstanceId)));

                foreach (var descriptor in descriptorList)
                {
                    this.logger.LogWarning(
                        "RUNTIME PROVIDER CAPABILITY KNOWN DESCRIPTOR RequestedRuntimeInstanceId={RequestedRuntimeInstanceId} RuntimeInstanceId={RuntimeInstanceId} ControlPlaneId={ControlPlaneId} ControlPlaneHostId={ControlPlaneHostId} ProviderName={ProviderName} TransportName={TransportName} Role={Role} Status={Status} CanAcceptRun={CanAcceptRun} AvailableRunSlots={AvailableRunSlots} EffectiveAvailableRunSlots={EffectiveAvailableRunSlots} ReservedRunSlots={ReservedRunSlots} TenantId={TenantId} TenantGroupId={TenantGroupId} WorkerCount={WorkerCount} ActiveWorkerCount={ActiveWorkerCount} AvailableWorkerCount={AvailableWorkerCount} QueuedRunCount={QueuedRunCount} RunningRunCount={RunningRunCount} ActiveRunCount={ActiveRunCount} LastHeartbeatAtUtc={LastHeartbeatAtUtc} Metadata={Metadata}",
                        requestedRuntimeInstanceId,
                        descriptor.RuntimeInstanceId,
                        descriptor.ControlPlaneId,
                        descriptor.ControlPlaneHostId,
                        ResolveMetadataValue(descriptor.Metadata, AiRuntimeInstanceProviderMetadataKeys.ProviderName, AiRuntimeInstanceProviderMetadataKeys.LegacyProviderName),
                        ResolveMetadataValue(descriptor.Metadata, AiRuntimeInstanceCommandTransportMetadataKeys.TransportName, "transport"),
                        descriptor.Role,
                        descriptor.Status,
                        descriptor.CanAcceptRun,
                        descriptor.AvailableRunSlots,
                        descriptor.EffectiveAvailableRunSlots,
                        descriptor.ReservedRunSlots,
                        descriptor.TenantId,
                        descriptor.TenantGroupId,
                        descriptor.WorkerCount,
                        descriptor.ActiveWorkerCount,
                        descriptor.AvailableWorkerCount,
                        descriptor.QueuedRunCount,
                        descriptor.RunningRunCount,
                        descriptor.ActiveRunCount,
                        descriptor.LastHeartbeatAtUtc,
                        FormatMetadata(descriptor.Metadata));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                this.logger.LogWarning(
                    exception,
                    "RUNTIME PROVIDER CAPABILITY DESCRIPTOR DEBUG LIST FAILED RequestedRuntimeInstanceId={RequestedRuntimeInstanceId} CapacityStoreType={CapacityStoreType} Reason={Reason}",
                    requestedRuntimeInstanceId,
                    this.capacityStore.GetType().FullName,
                    exception.Message);
            }
        }

        /// <summary>
        /// Resolves a metadata value using case-insensitive keys.
        /// </summary>
        /// <param name="metadata">The metadata.</param>
        /// <param name="keys">The candidate keys.</param>
        /// <returns>The resolved metadata value.</returns>
        private static string ResolveMetadataValue(
            IReadOnlyDictionary<string, string>? metadata,
            params string[] keys)
        {
            if (metadata is null || metadata.Count == 0)
            {
                return string.Empty;
            }

            foreach (var key in keys)
            {
                if (metadata.TryGetValue(key, out var value) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }

                foreach (var item in metadata)
                {
                    if (string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(item.Value))
                    {
                        return item.Value;
                    }
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Formats metadata for diagnostic logging.
        /// </summary>
        /// <param name="metadata">The metadata.</param>
        /// <returns>The formatted metadata.</returns>
        private static string FormatMetadata(
            IReadOnlyDictionary<string, string>? metadata)
        {
            if (metadata is null || metadata.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(
                " | ",
                metadata
                    .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(item => $"{item.Key}={item.Value}"));
        }
    }
}