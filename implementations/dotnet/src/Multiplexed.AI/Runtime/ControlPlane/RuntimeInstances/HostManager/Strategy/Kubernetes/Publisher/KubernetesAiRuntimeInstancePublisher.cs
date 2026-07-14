using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Publisher
{
    /// <summary>
    /// Publishes Kubernetes-created runtime hosts as runtime instances visible to the control plane.
    /// </summary>
    public sealed class KubernetesAiRuntimeInstancePublisher :
        IAiKubernetesRuntimeInstancePublisher
    {
        private const string KubernetesHostProviderName = "kubernetes";
        private readonly IAiRuntimeInstanceRegistry runtimeInstanceRegistry;
        private readonly IAiRuntimeInstanceCapacityStore runtimeInstanceCapacityStore;

        /// <summary>
        /// Initializes a new instance of the <see cref="KubernetesAiRuntimeInstancePublisher"/> class.
        /// </summary>
        /// <param name="runtimeInstanceRegistry">The runtime instance registry.</param>
        /// <param name="runtimeInstanceCapacityStore">The runtime instance capacity store.</param>
        public KubernetesAiRuntimeInstancePublisher(
            IAiRuntimeInstanceRegistry runtimeInstanceRegistry,
            IAiRuntimeInstanceCapacityStore runtimeInstanceCapacityStore)
        {
            this.runtimeInstanceRegistry = runtimeInstanceRegistry ?? throw new ArgumentNullException(nameof(runtimeInstanceRegistry));
            this.runtimeInstanceCapacityStore = runtimeInstanceCapacityStore ?? throw new ArgumentNullException(nameof(runtimeInstanceCapacityStore));
        }

        /// <inheritdoc />
        public async Task PublishAsync(
            AiRuntimeHostStartRequest request,
            AiRuntimeHostStartResult result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(result);

            if (!result.Success)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var tenantId = request.TenantId ?? request.ExecutionContextSnapshot.TenantId;
            var tenantGroupId = request.TenantGroupId ?? request.ExecutionContextSnapshot.TenantGroupId;
            var workerCount = request.WorkerCountPerInstance <= 0 ? 1 : request.WorkerCountPerInstance;
            var maxConcurrentRuns = request.MaxConcurrentRunsPerInstance <= 0 ? 1 : request.MaxConcurrentRunsPerInstance;
            var queueCapacity = request.LocalQueueCapacity <= 0 ? 1 : request.LocalQueueCapacity;
            var metadata = CreateMetadata(request, result, tenantId, tenantGroupId);
            var hostId = ResolveHostId(request, result, metadata);

            var registration =
                new AiRuntimeInstanceRegistration
                {
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    RuntimeId = request.RuntimeInstanceId,
                    McpRuntimeId = request.RuntimeInstanceId,
                    HostId = hostId,
                    ControlPlaneId = request.ControlPlaneId,
                    ControlPlaneHostId = request.ControlPlaneId,
                    Role = AiRuntimeInstanceRole.Runtime,
                    TenantId = tenantId,
                    TenantGroupId = tenantGroupId,
                    HostName = GetMetadataValue(metadata, "host.name") ?? GetMetadataValue(metadata, "kubernetes.node.name"),
                    KubernetesNamespace = GetMetadataValue(metadata, "kubernetes.namespace"),
                    KubernetesPodName = GetMetadataValue(metadata, "kubernetes.pod.name"),
                    KubernetesNodeName = GetMetadataValue(metadata, "kubernetes.node.name"),
                    WorkerCount = workerCount,
                    MaxConcurrentRuns = maxConcurrentRuns,
                    QueueCapacity = queueCapacity,
                    RegisteredAtUtc = now,
                    Metadata = metadata
                };

            var capacity =
                new AiRuntimeInstanceCapacityDescriptor
                {
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    ControlPlaneId = request.ControlPlaneId,
                    ControlPlaneHostId = request.ControlPlaneId,
                    Role = AiRuntimeInstanceRole.Runtime,
                    Status = AiRuntimeInstanceStatus.Ready,
                    TenantId = tenantId,
                    TenantGroupId = tenantGroupId,
                    WorkerCount = workerCount,
                    ActiveWorkerCount = 0,
                    AvailableWorkerCount = workerCount,
                    MaxWorkersPerRun = workerCount,
                    MinWorkersRequiredPerRun = 1,
                    QueuedRunCount = 0,
                    RunningRunCount = 0,
                    ActiveRunCount = 0,
                    MaxConcurrentRuns = maxConcurrentRuns,
                    MaxRunSlots = maxConcurrentRuns,
                    AvailableRunSlots = maxConcurrentRuns,
                    ReservedRunSlots = 0,
                    EffectiveAvailableRunSlots = maxConcurrentRuns,
                    IsQueuePaused = false,
                    CanAcceptRun = true,
                    LastHeartbeatAtUtc = now,
                    Metadata = metadata
                };

            await this.runtimeInstanceRegistry
                .RegisterAsync(
                    registration,
                    cancellationToken)
                .ConfigureAwait(false);

            await LogRegistryAfterRegisterAsync(
                    this.runtimeInstanceRegistry,
                    request.RuntimeInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);

            await this.runtimeInstanceCapacityStore
                .PublishAsync(
                    capacity,
                    cancellationToken)
                .ConfigureAwait(false);

            await LogCapacityAfterPublishAsync(
                    this.runtimeInstanceCapacityStore,
                    request.RuntimeInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);

            Console.WriteLine(
                $"[KUBERNETES RUNTIME INSTANCE PUBLISHED] RuntimeInstanceId='{request.RuntimeInstanceId}', ControlPlaneId='{request.ControlPlaneId}', TenantId='{tenantId}', TenantGroupId='{tenantGroupId}', HostId='{hostId}', WorkerCount='{workerCount}', MaxConcurrentRuns='{maxConcurrentRuns}', AvailableRunSlots='{maxConcurrentRuns}', CanAcceptRun='True'.");
        }

        /// <inheritdoc />
        public async Task UnpublishAsync(
            string runtimeInstanceId,
            string reason,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            cancellationToken.ThrowIfCancellationRequested();

            Console.WriteLine(
                $"[KUBERNETES RUNTIME INSTANCE UNPUBLISH BEGIN] RuntimeInstanceId='{runtimeInstanceId}', Reason='{reason}', RegistryType='{this.runtimeInstanceRegistry.GetType().FullName}', RegistryHash='{this.runtimeInstanceRegistry.GetHashCode()}', CapacityStoreType='{this.runtimeInstanceCapacityStore.GetType().FullName}', CapacityStoreHash='{this.runtimeInstanceCapacityStore.GetHashCode()}'.");

            await this.runtimeInstanceCapacityStore
                .RemoveAsync(
                    runtimeInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);

            await this.runtimeInstanceRegistry
                .UnregisterAsync(
                    runtimeInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);

            await LogRegistryAfterUnpublishAsync(
                    this.runtimeInstanceRegistry,
                    runtimeInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);

            await LogCapacityAfterUnpublishAsync(
                    this.runtimeInstanceCapacityStore,
                    runtimeInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);

            Console.WriteLine(
                $"[KUBERNETES RUNTIME INSTANCE UNPUBLISHED] RuntimeInstanceId='{runtimeInstanceId}', Reason='{reason}'.");
        }

        /// <summary>
        /// Logs the registry content immediately after unpublishing a Kubernetes runtime instance.
        /// </summary>
        /// <param name="runtimeInstanceRegistry">The runtime instance registry.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The asynchronous operation.</returns>
        private static async Task LogRegistryAfterUnpublishAsync(
            IAiRuntimeInstanceRegistry runtimeInstanceRegistry,
            string runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            try
            {
                var snapshots =
                    await runtimeInstanceRegistry
                        .ListAsync(
                            includeStopped: true,
                            cancellationToken)
                        .ConfigureAwait(false);

                var snapshotList =
                    snapshots
                        .OrderBy(item => item.RuntimeInstanceId, StringComparer.Ordinal)
                        .ToArray();

                Console.WriteLine(
                    $"[KUBERNETES REGISTRY AFTER UNPUBLISH] RuntimeInstanceId='{runtimeInstanceId}', RegistryType='{runtimeInstanceRegistry.GetType().FullName}', RegistryHash='{runtimeInstanceRegistry.GetHashCode()}', Count='{snapshotList.Length}', Ids='{string.Join(",", snapshotList.Select(item => item.RuntimeInstanceId))}'.");

                foreach (var snapshot in snapshotList)
                {
                    Console.WriteLine(
                        $"[KUBERNETES REGISTRY SNAPSHOT AFTER UNPUBLISH] RequestedRuntimeInstanceId='{runtimeInstanceId}', RuntimeInstanceId='{snapshot.RuntimeInstanceId}', ControlPlaneId='{snapshot.ControlPlaneId}', ControlPlaneHostId='{snapshot.ControlPlaneHostId}', Role='{snapshot.Role}', Status='{snapshot.Status}', TenantId='{snapshot.TenantId}', TenantGroupId='{snapshot.TenantGroupId}', CanAcceptRun='{snapshot.CanAcceptRun}', AvailableRunSlots='{snapshot.AvailableRunSlots}', WorkerCount='{snapshot.WorkerCount}', ActiveWorkerCount='{snapshot.ActiveWorkerCount}', AvailableWorkerCount='{snapshot.AvailableWorkerCount}', QueuedRunCount='{snapshot.QueuedRunCount}', RunningRunCount='{snapshot.RunningRunCount}', ActiveRunCount='{snapshot.ActiveRunCount}'.");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Console.WriteLine(
                    $"[KUBERNETES REGISTRY AFTER UNPUBLISH FAILED] RuntimeInstanceId='{runtimeInstanceId}', RegistryType='{runtimeInstanceRegistry.GetType().FullName}', RegistryHash='{runtimeInstanceRegistry.GetHashCode()}', ExceptionType='{exception.GetType().FullName}', Reason='{exception.Message}'.");
            }
        }

        /// <summary>
        /// Logs the capacity store content immediately after unpublishing a Kubernetes runtime capacity descriptor.
        /// </summary>
        /// <param name="runtimeInstanceCapacityStore">The runtime instance capacity store.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The asynchronous operation.</returns>
        private static async Task LogCapacityAfterUnpublishAsync(
            IAiRuntimeInstanceCapacityStore runtimeInstanceCapacityStore,
            string runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            try
            {
                var descriptors =
                    await runtimeInstanceCapacityStore
                        .ListAsync(cancellationToken)
                        .ConfigureAwait(false);

                var descriptorList =
                    descriptors
                        .OrderBy(item => item.RuntimeInstanceId, StringComparer.Ordinal)
                        .ToArray();

                Console.WriteLine(
                    $"[KUBERNETES CAPACITY AFTER UNPUBLISH] RuntimeInstanceId='{runtimeInstanceId}', CapacityStoreType='{runtimeInstanceCapacityStore.GetType().FullName}', CapacityStoreHash='{runtimeInstanceCapacityStore.GetHashCode()}', Count='{descriptorList.Length}', Ids='{string.Join(",", descriptorList.Select(item => item.RuntimeInstanceId))}'.");

                foreach (var descriptor in descriptorList)
                {
                    Console.WriteLine(
                        $"[KUBERNETES CAPACITY DESCRIPTOR AFTER UNPUBLISH] RequestedRuntimeInstanceId='{runtimeInstanceId}', RuntimeInstanceId='{descriptor.RuntimeInstanceId}', ControlPlaneId='{descriptor.ControlPlaneId}', ControlPlaneHostId='{descriptor.ControlPlaneHostId}', Role='{descriptor.Role}', Status='{descriptor.Status}', TenantId='{descriptor.TenantId}', TenantGroupId='{descriptor.TenantGroupId}', CanAcceptRun='{descriptor.CanAcceptRun}', AvailableRunSlots='{descriptor.AvailableRunSlots}', EffectiveAvailableRunSlots='{descriptor.EffectiveAvailableRunSlots}', ReservedRunSlots='{descriptor.ReservedRunSlots}', WorkerCount='{descriptor.WorkerCount}', ActiveWorkerCount='{descriptor.ActiveWorkerCount}', AvailableWorkerCount='{descriptor.AvailableWorkerCount}', QueuedRunCount='{descriptor.QueuedRunCount}', RunningRunCount='{descriptor.RunningRunCount}', ActiveRunCount='{descriptor.ActiveRunCount}'.");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Console.WriteLine(
                    $"[KUBERNETES CAPACITY AFTER UNPUBLISH FAILED] RuntimeInstanceId='{runtimeInstanceId}', CapacityStoreType='{runtimeInstanceCapacityStore.GetType().FullName}', CapacityStoreHash='{runtimeInstanceCapacityStore.GetHashCode()}', ExceptionType='{exception.GetType().FullName}', Reason='{exception.Message}'.");
            }
        }

        /// <summary>
        /// Logs the registry content immediately after registering a Kubernetes runtime instance.
        /// </summary>
        /// <param name="runtimeInstanceRegistry">The runtime instance registry.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The asynchronous operation.</returns>
        private static async Task LogRegistryAfterRegisterAsync(
            IAiRuntimeInstanceRegistry runtimeInstanceRegistry,
            string runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            try
            {
                var snapshots =
                    await runtimeInstanceRegistry
                        .ListAsync(
                            includeStopped: true,
                            cancellationToken)
                        .ConfigureAwait(false);

                var snapshotList =
                    snapshots
                        .OrderBy(item => item.RuntimeInstanceId, StringComparer.Ordinal)
                        .ToArray();

                Console.WriteLine(
                    $"[KUBERNETES REGISTRY AFTER REGISTER] RuntimeInstanceId='{runtimeInstanceId}', RegistryType='{runtimeInstanceRegistry.GetType().FullName}', RegistryHash='{runtimeInstanceRegistry.GetHashCode()}', Count='{snapshotList.Length}', Ids='{string.Join(",", snapshotList.Select(item => item.RuntimeInstanceId))}'.");

                foreach (var snapshot in snapshotList)
                {
                    Console.WriteLine(
                        $"[KUBERNETES REGISTRY SNAPSHOT AFTER REGISTER] RequestedRuntimeInstanceId='{runtimeInstanceId}', RuntimeInstanceId='{snapshot.RuntimeInstanceId}', ControlPlaneId='{snapshot.ControlPlaneId}', ControlPlaneHostId='{snapshot.ControlPlaneHostId}', Role='{snapshot.Role}', Status='{snapshot.Status}', TenantId='{snapshot.TenantId}', TenantGroupId='{snapshot.TenantGroupId}', CanAcceptRun='{snapshot.CanAcceptRun}', AvailableRunSlots='{snapshot.AvailableRunSlots}', WorkerCount='{snapshot.WorkerCount}', ActiveWorkerCount='{snapshot.ActiveWorkerCount}', AvailableWorkerCount='{snapshot.AvailableWorkerCount}', QueuedRunCount='{snapshot.QueuedRunCount}', RunningRunCount='{snapshot.RunningRunCount}', ActiveRunCount='{snapshot.ActiveRunCount}'.");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Console.WriteLine(
                    $"[KUBERNETES REGISTRY AFTER REGISTER FAILED] RuntimeInstanceId='{runtimeInstanceId}', RegistryType='{runtimeInstanceRegistry.GetType().FullName}', RegistryHash='{runtimeInstanceRegistry.GetHashCode()}', ExceptionType='{exception.GetType().FullName}', Reason='{exception.Message}'.");
            }
        }

        /// <summary>
        /// Logs the capacity store content immediately after publishing a Kubernetes runtime capacity descriptor.
        /// </summary>
        /// <param name="runtimeInstanceCapacityStore">The runtime instance capacity store.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The asynchronous operation.</returns>
        private static async Task LogCapacityAfterPublishAsync(
            IAiRuntimeInstanceCapacityStore runtimeInstanceCapacityStore,
            string runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            try
            {
                var descriptors =
                    await runtimeInstanceCapacityStore
                        .ListAsync(cancellationToken)
                        .ConfigureAwait(false);

                var descriptorList =
                    descriptors
                        .OrderBy(item => item.RuntimeInstanceId, StringComparer.Ordinal)
                        .ToArray();

                Console.WriteLine(
                    $"[KUBERNETES CAPACITY AFTER PUBLISH] RuntimeInstanceId='{runtimeInstanceId}', CapacityStoreType='{runtimeInstanceCapacityStore.GetType().FullName}', CapacityStoreHash='{runtimeInstanceCapacityStore.GetHashCode()}', Count='{descriptorList.Length}', Ids='{string.Join(",", descriptorList.Select(item => item.RuntimeInstanceId))}'.");

                foreach (var descriptor in descriptorList)
                {
                    Console.WriteLine(
                        $"[KUBERNETES CAPACITY DESCRIPTOR AFTER PUBLISH] RequestedRuntimeInstanceId='{runtimeInstanceId}', RuntimeInstanceId='{descriptor.RuntimeInstanceId}', ControlPlaneId='{descriptor.ControlPlaneId}', ControlPlaneHostId='{descriptor.ControlPlaneHostId}', Role='{descriptor.Role}', Status='{descriptor.Status}', TenantId='{descriptor.TenantId}', TenantGroupId='{descriptor.TenantGroupId}', CanAcceptRun='{descriptor.CanAcceptRun}', AvailableRunSlots='{descriptor.AvailableRunSlots}', EffectiveAvailableRunSlots='{descriptor.EffectiveAvailableRunSlots}', ReservedRunSlots='{descriptor.ReservedRunSlots}', WorkerCount='{descriptor.WorkerCount}', ActiveWorkerCount='{descriptor.ActiveWorkerCount}', AvailableWorkerCount='{descriptor.AvailableWorkerCount}', QueuedRunCount='{descriptor.QueuedRunCount}', RunningRunCount='{descriptor.RunningRunCount}', ActiveRunCount='{descriptor.ActiveRunCount}'.");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Console.WriteLine(
                    $"[KUBERNETES CAPACITY AFTER PUBLISH FAILED] RuntimeInstanceId='{runtimeInstanceId}', CapacityStoreType='{runtimeInstanceCapacityStore.GetType().FullName}', CapacityStoreHash='{runtimeInstanceCapacityStore.GetHashCode()}', ExceptionType='{exception.GetType().FullName}', Reason='{exception.Message}'.");
            }
        }

        /// <summary>
        /// Creates metadata for the Kubernetes-backed runtime instance.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <param name="result">The runtime host start result.</param>
        /// <param name="tenantId">The resolved tenant id.</param>
        /// <param name="tenantGroupId">The resolved tenant group id.</param>
        /// <returns>The metadata dictionary.</returns>
        private static Dictionary<string, string> CreateMetadata(
            AiRuntimeHostStartRequest request,
            AiRuntimeHostStartResult result,
            string? tenantId,
            string? tenantGroupId)
        {
            var providerName = string.IsNullOrWhiteSpace(request.ProviderName) ? result.ProviderName : request.ProviderName;
            var transportName = string.IsNullOrWhiteSpace(request.TransportName) ? result.TransportName : request.TransportName;
            providerName = string.IsNullOrWhiteSpace(providerName) ? "grpc" : providerName;
            transportName = string.IsNullOrWhiteSpace(transportName) ? "grpc" : transportName;

            var metadata =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

            CopyMetadata(metadata, request.Metadata);
            CopyMetadata(metadata, result.Metadata);

            metadata["provider.name"] = providerName;
            metadata["provider"] = providerName;
            metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName] = providerName;
            metadata["transport.name"] = transportName;
            metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName] = transportName;
            metadata[AiRuntimeInstanceCommandTransportMetadataKeys.RuntimeInstanceId] = request.RuntimeInstanceId;
            metadata["host.provider"] = KubernetesHostProviderName;
            metadata["host.creation.mode"] = KubernetesHostProviderName;
            metadata["controlPlaneId"] = request.ControlPlaneId;
            metadata["control-plane.id"] = request.ControlPlaneId;
            metadata["controlplane.id"] = request.ControlPlaneId;
            metadata["runtime.controlPlaneId"] = request.ControlPlaneId;
            metadata["runtimeInstanceId"] = request.RuntimeInstanceId;

            var transportEndpoint =
                ResolveTransportEndpoint(
                    request,
                    result);

            ValidateTransportEndpoint(
                request,
                transportName,
                transportEndpoint);

            AddIfNotEmpty(metadata, "host.id", ResolveHostId(request, result, metadata));
            AddIfNotEmpty(metadata, "tenant.id", tenantId);
            AddIfNotEmpty(metadata, "tenantId", tenantId);
            AddIfNotEmpty(metadata, "tenant.group.id", tenantGroupId);
            AddIfNotEmpty(metadata, "tenant.groupId", tenantGroupId);
            AddIfNotEmpty(metadata, "tenantGroupId", tenantGroupId);
            AddIfNotEmpty(metadata, AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint, transportEndpoint);
            AddIfNotEmpty(metadata, "transport.endpoint", transportEndpoint);
            AddIfNotEmpty(metadata, "transportEndpoint", transportEndpoint);
            AddIfNotEmpty(metadata, "runtime.command.endpoint", transportEndpoint);
            AddIfNotEmpty(metadata, "grpc.endpoint", transportEndpoint);
            AddIfNotEmpty(metadata, "host.runtimeInstanceId", request.RuntimeInstanceId);

            return metadata;
        }

        /// <summary>
        /// Copies metadata into the target dictionary.
        /// </summary>
        /// <param name="target">The target metadata dictionary.</param>
        /// <param name="source">The source metadata dictionary.</param>
        private static void CopyMetadata(
            IDictionary<string, string> target,
            IReadOnlyDictionary<string, string>? source)
        {
            if (source is null)
            {
                return;
            }

            foreach (var item in source)
            {
                if (!string.IsNullOrWhiteSpace(item.Key))
                {
                    target[item.Key] = item.Value ?? string.Empty;
                }
            }
        }


        /// <summary>
        /// Resolves the transport endpoint that must be published for command dispatch.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <param name="result">The runtime host start result.</param>
        /// <returns>The resolved transport endpoint.</returns>
        private static string? ResolveTransportEndpoint(
            AiRuntimeHostStartRequest request,
            AiRuntimeHostStartResult result)
        {
            return !string.IsNullOrWhiteSpace(result.TransportEndpoint)
                ? result.TransportEndpoint
                : request.TransportEndpoint;
        }

        /// <summary>
        /// Validates the Kubernetes transport endpoint before publishing registry and capacity descriptors.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <param name="transportName">The transport name.</param>
        /// <param name="transportEndpoint">The transport endpoint.</param>
        private static void ValidateTransportEndpoint(
            AiRuntimeHostStartRequest request,
            string transportName,
            string? transportEndpoint)
        {
            var isGrpc =
                string.Equals(
                    request.ProviderName,
                    "grpc",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    transportName,
                    "grpc",
                    StringComparison.OrdinalIgnoreCase);

            if (!isGrpc)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(transportEndpoint))
            {
                throw new InvalidOperationException(
                    $"Kubernetes gRPC transport endpoint is missing. RuntimeInstanceId='{request.RuntimeInstanceId}'.");
            }

            if (transportEndpoint.Contains(
                    "127.0.0.1:8080",
                    StringComparison.OrdinalIgnoreCase) ||
                transportEndpoint.Contains(
                    "localhost:8080",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Kubernetes gRPC transport endpoint is not unique for multi-pod dispatch. RuntimeInstanceId='{request.RuntimeInstanceId}', Endpoint='{transportEndpoint}'.");
            }
        }

        /// <summary>
        /// Resolves the host id from metadata or request data.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <param name="result">The runtime host start result.</param>
        /// <param name="metadata">The metadata dictionary.</param>
        /// <returns>The resolved host id.</returns>
        private static string ResolveHostId(
            AiRuntimeHostStartRequest request,
            AiRuntimeHostStartResult result,
            IReadOnlyDictionary<string, string> metadata)
        {
            return GetMetadataValue(metadata, "host.id") ??
                   GetMetadataValue(metadata, "hostId") ??
                   GetMetadataValue(metadata, "kubernetes.pod.name") ??
                   GetMetadataValue(metadata, "kubernetes.service.name") ??
                   result.RuntimeInstanceId ??
                   request.RuntimeInstanceId;
        }

        /// <summary>
        /// Gets a metadata value using case-insensitive key matching.
        /// </summary>
        /// <param name="metadata">The metadata dictionary.</param>
        /// <param name="key">The metadata key.</param>
        /// <returns>The metadata value, or null.</returns>
        private static string? GetMetadataValue(
            IReadOnlyDictionary<string, string> metadata,
            string key)
        {
            if (metadata.TryGetValue(key, out var value))
            {
                return value;
            }

            foreach (var item in metadata)
            {
                if (string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return item.Value;
                }
            }

            return null;
        }

        /// <summary>
        /// Adds a metadata value when it is not empty.
        /// </summary>
        /// <param name="metadata">The metadata dictionary.</param>
        /// <param name="key">The metadata key.</param>
        /// <param name="value">The metadata value.</param>
        private static void AddIfNotEmpty(
            IDictionary<string, string> metadata,
            string key,
            string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                metadata[key] = value;
            }
        }
    }
}