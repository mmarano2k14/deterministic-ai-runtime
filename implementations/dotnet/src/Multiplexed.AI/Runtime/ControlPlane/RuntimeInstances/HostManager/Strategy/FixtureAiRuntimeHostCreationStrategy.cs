using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy
{
    /// <summary>
    /// Fixture host creation strategy used by integration tests.
    /// </summary>
    public sealed class FixtureAiRuntimeHostCreationStrategy : IAiRuntimeHostCreationStrategy
    {
        /// <summary>
        /// The runtime instance registry used to publish the fixture runtime host registration.
        /// </summary>
        private readonly IAiRuntimeInstanceRegistry runtimeInstanceRegistry;

        /// <summary>
        /// The runtime instance capacity store used to publish the fixture runtime host capacity.
        /// </summary>
        private readonly IAiRuntimeInstanceCapacityStore runtimeInstanceCapacityStore;

        /// <summary>
        /// Initializes a new instance of the <see cref="FixtureAiRuntimeHostCreationStrategy"/> class.
        /// </summary>
        /// <param name="runtimeInstanceRegistry">The runtime instance registry.</param>
        /// <param name="runtimeInstanceCapacityStore">The runtime instance capacity store.</param>
        public FixtureAiRuntimeHostCreationStrategy(
            IAiRuntimeInstanceRegistry runtimeInstanceRegistry,
            IAiRuntimeInstanceCapacityStore runtimeInstanceCapacityStore)
        {
            this.runtimeInstanceRegistry = runtimeInstanceRegistry ?? throw new ArgumentNullException(nameof(runtimeInstanceRegistry));
            this.runtimeInstanceCapacityStore = runtimeInstanceCapacityStore ?? throw new ArgumentNullException(nameof(runtimeInstanceCapacityStore));
        }

        /// <inheritdoc />
        public AiRuntimeHostCreationMode Mode => AiRuntimeHostCreationMode.Fixture;

        /// <inheritdoc />
        public async Task<AiRuntimeHostStartResult> StartAsync(
            AiRuntimeHostStartRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var metadata = CreateMetadata(request);
            var now = DateTimeOffset.UtcNow;

            await this.runtimeInstanceRegistry.RegisterAsync(
                new AiRuntimeInstanceRegistration
                {
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    ControlPlaneId = request.ControlPlaneId,
                    WorkerCount = request.WorkerCountPerInstance,
                    MaxConcurrentRuns = request.MaxConcurrentRunsPerInstance,
                    QueueCapacity = request.LocalQueueCapacity,
                    Metadata = metadata,
                    RegisteredAtUtc = now
                },
                cancellationToken).ConfigureAwait(false);

            await this.runtimeInstanceCapacityStore.PublishAsync(
                new AiRuntimeInstanceCapacityDescriptor
                {
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    Status = AiRuntimeInstanceStatus.Ready,
                    WorkerCount = request.WorkerCountPerInstance,
                    ActiveWorkerCount = 0,
                    AvailableWorkerCount = request.WorkerCountPerInstance,
                    MaxConcurrentRuns = request.MaxConcurrentRunsPerInstance,
                    MaxRunSlots = request.MaxConcurrentRunsPerInstance,
                    AvailableRunSlots = request.MaxConcurrentRunsPerInstance,
                    ReservedRunSlots = 0,
                    EffectiveAvailableRunSlots = request.MaxConcurrentRunsPerInstance,
                    QueuedRunCount = 0,
                    RunningRunCount = 0,
                    ActiveRunCount = 0,
                    IsQueuePaused = false,
                    CanAcceptRun = true,
                    LastHeartbeatAtUtc = now,
                    ControlPlaneId = request.ControlPlaneId,
                    Metadata = metadata
                },
                cancellationToken).ConfigureAwait(false);

            return AiRuntimeHostStartResult.Started(
                request.ExecutionContextSnapshot,
                request.RuntimeInstanceId,
                request.ProviderName,
                request.TransportName,
                request.TransportEndpoint,
                metadata);
        }

        /// <summary>
        /// Creates metadata for the fixture runtime host registration and capacity descriptors.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <returns>The metadata dictionary.</returns>
        private static IReadOnlyDictionary<string, string> CreateMetadata(
            AiRuntimeHostStartRequest request)
        {
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = request.ProviderName,
                ["provider.name"] = request.ProviderName,
                ["runtime.status"] = AiRuntimeInstanceStatus.Ready.ToString(),
                ["hostCreation.mode"] = request.HostCreationMode.ToString(),
                ["hostCreation.strategy"] = nameof(FixtureAiRuntimeHostCreationStrategy),
                ["runtime.isolationMode"] = request.IsolationMode,
                ["runtime.preferDedicatedCapacity"] = request.PreferDedicatedCapacity.ToString(),
                ["runtime.allowSharedFallback"] = request.AllowSharedFallback.ToString(),
                ["runtime.maxRuntimeInstances"] = request.MaxRuntimeInstances?.ToString() ?? string.Empty,
                ["runtime.instanceIdPrefix"] = request.RuntimeInstanceIdPrefix,
                ["runtime.workerCountPerInstance"] = request.WorkerCountPerInstance.ToString(),
                ["runtime.maxConcurrentRunsPerInstance"] = request.MaxConcurrentRunsPerInstance.ToString(),
                ["runtime.localQueueCapacity"] = request.LocalQueueCapacity.ToString()
            };

            if (!string.IsNullOrWhiteSpace(request.TransportName))
            {
                metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName] = request.TransportName;
            }

            if (!string.IsNullOrWhiteSpace(request.TransportEndpoint))
            {
                metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] = request.TransportEndpoint;
            }

            if (!string.IsNullOrWhiteSpace(request.TenantId))
            {
                metadata["tenant.id"] = request.TenantId;
            }

            if (!string.IsNullOrWhiteSpace(request.TenantGroupId))
            {
                metadata["tenant.group.id"] = request.TenantGroupId;
            }

            return metadata;
        }
    }
}