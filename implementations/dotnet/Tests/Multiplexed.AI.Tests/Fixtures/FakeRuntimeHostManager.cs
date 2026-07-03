using System.Globalization;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;

namespace Multiplexed.AI.Tests.Fixtures
{
    /// <summary>
    /// Fake runtime host manager that records runtime start requests and optionally registers runtime capacity.
    /// </summary>
    public sealed class FakeRuntimeHostManager : IAiRuntimeHostManager
    {
        private readonly IAiRuntimeInstanceRegistry? registry;
        private readonly IAiRuntimeInstanceCapacityStore? capacityStore;

        /// <summary>
        /// Initializes a new instance of the <see cref="FakeRuntimeHostManager"/> class.
        /// </summary>
        public FakeRuntimeHostManager()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="FakeRuntimeHostManager"/> class.
        /// </summary>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="capacityStore">The runtime instance capacity store.</param>
        public FakeRuntimeHostManager(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeInstanceCapacityStore capacityStore)
        {
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.capacityStore = capacityStore ?? throw new ArgumentNullException(nameof(capacityStore));
        }

        /// <summary>
        /// Gets the runtime start requests observed by the fake host manager.
        /// </summary>
        public List<AiRuntimeHostStartRequest> StartRequests { get; } = [];

        /// <summary>
        /// Gets or sets the explicit result returned by the fake host manager.
        /// </summary>
        public AiRuntimeHostStartResult? Result { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether host startup should succeed.
        /// </summary>
        public bool Succeed { get; set; } = true;

        /// <summary>
        /// Gets or sets the failure reason returned when startup fails.
        /// </summary>
        public string FailureReason { get; set; } = "fake-runtime-host-start-failed";

        /// <inheritdoc />
        public async Task<AiRuntimeHostStartResult> StartRuntimeAsync(
            AiRuntimeHostStartRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            StartRequests.Add(request);

            var metadata = CreateRuntimeMetadata(request);

            if (Result is not null)
            {
                return new AiRuntimeHostStartResult
                {
                    Success = Result.Success,
                    FailureReason = Result.FailureReason,
                    RuntimeInstanceId = Result.RuntimeInstanceId ?? request.RuntimeInstanceId,
                    ProviderName = Result.ProviderName ?? request.ProviderName,
                    TransportName = Result.TransportName ?? request.TransportName,
                    TransportEndpoint = Result.TransportEndpoint ?? request.TransportEndpoint,
                    ExecutionContextSnapshot = Result.ExecutionContextSnapshot ?? request.ExecutionContextSnapshot,
                    Metadata = Result.Metadata ?? metadata
                };
            }

            if (!Succeed)
            {
                return new AiRuntimeHostStartResult
                {
                    Success = false,
                    FailureReason = FailureReason,
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    ProviderName = request.ProviderName,
                    TransportName = request.TransportName,
                    TransportEndpoint = request.TransportEndpoint,
                    ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                    Metadata = metadata
                };
            }

            if (registry is not null)
            {
                await registry
                    .RegisterAsync(
                        new AiRuntimeInstanceRegistration
                        {
                            RuntimeInstanceId = request.RuntimeInstanceId,
                            ControlPlaneId = request.ControlPlaneId,
                            ControlPlaneHostId = $"control-plane-host-{request.ControlPlaneId}",
                            HostId = request.RuntimeInstanceId,
                            RuntimeId = request.RuntimeInstanceId,
                            TenantId = request.TenantId,
                            TenantGroupId = request.TenantGroupId,
                            Role = AiRuntimeInstanceRole.Runtime,
                            WorkerCount = request.WorkerCountPerInstance,
                            QueueCapacity = request.LocalQueueCapacity,
                            MaxConcurrentRuns = request.MaxConcurrentRunsPerInstance,
                            RegisteredAtUtc = DateTimeOffset.UtcNow,
                            Metadata = metadata
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (capacityStore is not null)
            {
                await capacityStore
                    .PublishAsync(
                        new AiRuntimeInstanceCapacityDescriptor
                        {
                            RuntimeInstanceId = request.RuntimeInstanceId,
                            ControlPlaneId = request.ControlPlaneId,
                            ControlPlaneHostId = $"control-plane-host-{request.ControlPlaneId}",
                            TenantId = request.TenantId,
                            TenantGroupId = request.TenantGroupId,
                            Role = AiRuntimeInstanceRole.Runtime,
                            Status = AiRuntimeInstanceStatus.Ready,
                            WorkerCount = request.WorkerCountPerInstance,
                            ActiveWorkerCount = 0,
                            AvailableWorkerCount = request.WorkerCountPerInstance,
                            MaxWorkersPerRun = request.WorkerCountPerInstance,
                            MinWorkersRequiredPerRun = 1,
                            QueuedRunCount = 0,
                            RunningRunCount = 0,
                            ActiveRunCount = 0,
                            MaxConcurrentRuns = request.MaxConcurrentRunsPerInstance,
                            MaxRunSlots = request.MaxConcurrentRunsPerInstance,
                            AvailableRunSlots = request.MaxConcurrentRunsPerInstance,
                            ReservedRunSlots = 0,
                            EffectiveAvailableRunSlots = request.MaxConcurrentRunsPerInstance,
                            IsQueuePaused = false,
                            CanAcceptRun = true,
                            LastHeartbeatAtUtc = DateTimeOffset.UtcNow,
                            Metadata = metadata
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return new AiRuntimeHostStartResult
            {
                Success = true,
                RuntimeInstanceId = request.RuntimeInstanceId,
                ProviderName = request.ProviderName,
                TransportName = request.TransportName,
                TransportEndpoint = request.TransportEndpoint,
                ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                Metadata = metadata
            };
        }

        /// <summary>
        /// Creates metadata for the registered fake runtime host.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <returns>The metadata dictionary.</returns>
        private static Dictionary<string, string> CreateRuntimeMetadata(
            AiRuntimeHostStartRequest request)
        {
            var metadata =
                new Dictionary<string, string>(
                    request.Metadata,
                    StringComparer.OrdinalIgnoreCase)
                {
                    [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = request.ProviderName,
                    ["provider.name"] = request.ProviderName,
                    [AiRuntimeInstanceCommandTransportMetadataKeys.TransportName] = request.TransportName,
                    [AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] = request.TransportEndpoint ?? string.Empty,
                    [AiRuntimeInstanceCommandTransportMetadataKeys.RuntimeInstanceId] = request.RuntimeInstanceId,
                    ["runtime.instance.id"] = request.RuntimeInstanceId,
                    ["runtime.localQueueCapacity"] = request.LocalQueueCapacity.ToString(CultureInfo.InvariantCulture),
                    ["queueCapacity"] = request.LocalQueueCapacity.ToString(CultureInfo.InvariantCulture),
                    ["controlPlaneId"] = request.ControlPlaneId
                };

            if (!string.IsNullOrWhiteSpace(request.TenantId))
            {
                metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantId] = request.TenantId;
            }

            if (!string.IsNullOrWhiteSpace(request.TenantGroupId))
            {
                metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = request.TenantGroupId;
            }

            if (!string.IsNullOrWhiteSpace(request.IsolationMode))
            {
                metadata[AiRuntimeInstanceIsolationMetadataKeys.IsolationMode] = request.IsolationMode;
            }

            metadata[AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity] =
                request.PreferDedicatedCapacity.ToString(CultureInfo.InvariantCulture);

            metadata[AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback] =
                request.AllowSharedFallback.ToString(CultureInfo.InvariantCulture);

            return metadata;
        }
    }
}