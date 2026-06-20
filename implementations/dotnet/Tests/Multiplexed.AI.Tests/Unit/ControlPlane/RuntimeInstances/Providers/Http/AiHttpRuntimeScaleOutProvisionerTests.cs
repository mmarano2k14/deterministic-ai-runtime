using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Readiness;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http.ScaleOut;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Providers.Http
{
    /// <summary>
    /// Unit tests for <see cref="AiHttpRuntimeScaleOutProvisioner"/>.
    /// </summary>
    public sealed class AiHttpRuntimeScaleOutProvisionerTests
    {
        /// <summary>
        /// Verifies that HTTP scale-out registers a runtime instance and publishes capacity.
        /// </summary>
        [Fact]
        public async Task ProvisionAsync_Should_Register_Runtime_And_Publish_Capacity()
        {
            var registry =
                new TestRuntimeInstanceRegistry();

            var capacityStore =
                new TestRuntimeInstanceCapacityStore();

            var provisioner =
                new AiHttpRuntimeScaleOutProvisioner(
                    registry,
                    capacityStore,
                    new NoopAiRuntimeHostManager(),
                    new TestRuntimeInstanceReadinessWaiter(),
                    Options.Create(
                        new AiHttpRuntimeScaleOutOptions
                        {
                            Enabled = true,
                            Mode = AiHttpRuntimeScaleOutModes.MetadataOnly,
                            DefaultRuntimeInstanceIdPrefix = "http-runtime",
                            EndpointTemplate = "http://{runtimeInstanceId}:8080"
                        }),
                    NullLogger<AiHttpRuntimeScaleOutProvisioner>.Instance);

            var request =
                new AiRuntimeScaleOutProviderRequest
                {
                    RequestId = "scaleout-1",
                    SharedRunId = "shared-run-1",
                    ControlPlaneId = "control-plane-1",
                    ExecutionContextSnapshot = CreateExecutionContextSnapshot(),
                    TenantId = "tenant-a",
                    TenantGroupId = "group-a",
                    IsolationMode = AiRuntimeInstanceIsolationMode.Dedicated,
                    PreferDedicatedCapacity = true,
                    AllowSharedFallback = false,
                    RuntimeInstanceIdPrefix = "tenant-a-http",
                    CurrentInstanceCount = 0,
                    RequestedTargetInstanceCount = 1,
                    WorkerCountPerInstance = 7,
                    MaxConcurrentRunsPerInstance = 3,
                    LocalQueueCapacity = 42,
                    MaxRuntimeInstances = 5,
                    Metadata = new Dictionary<string, string>
                    {
                        ["source"] = "unit-test"
                    }
                };

            var result =
                await provisioner
                    .ProvisionAsync(request)
                    .ConfigureAwait(false);

            Assert.True(
                result.Success);

            Assert.False(
                result.Rejected);

            Assert.Equal(
                "control-plane-1:tenant-a-http-1",
                result.RuntimeInstanceId);

            Assert.Equal(
                "http-scaleout-scaleout-1",
                result.ProviderOperationId);

            var registration =
                await registry
                    .GetAsync(result.RuntimeInstanceId!)
                    .ConfigureAwait(false);

            Assert.NotNull(
                registration);

            Assert.Equal(
                result.RuntimeInstanceId,
                registration!.RuntimeInstanceId);

            Assert.Equal(
                "control-plane-1",
                registration.ControlPlaneId);

            Assert.Equal(
                7,
                registration.WorkerCount);

            Assert.Equal(
                3,
                registration.MaxConcurrentRuns);

            Assert.Equal(
                42,
                registration.QueueCapacity);

            Assert.Equal(
                AiRuntimeInstanceStatus.Ready,
                registration.Status);

            Assert.Equal(
                "http",
                registration.Metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName]);

            Assert.Equal(
                "http",
                registration.Metadata["provider.name"]);

            Assert.Equal(
                AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                registration.Metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName]);

            Assert.Equal(
                "http://control-plane-1:tenant-a-http-1:8080",
                registration.Metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint]);

            Assert.Equal(
                "tenant-a",
                registration.Metadata["tenant.id"]);

            Assert.Equal(
                AiRuntimeInstanceIsolationMode.Dedicated.ToString(),
                registration.Metadata["runtime.isolationMode"]);

            Assert.Equal(
                "True",
                registration.Metadata["runtime.preferDedicatedCapacity"]);

            Assert.Equal(
                "False",
                registration.Metadata["runtime.allowSharedFallback"]);

            var capacity =
                await capacityStore
                    .GetAsync(result.RuntimeInstanceId!)
                    .ConfigureAwait(false);

            Assert.NotNull(
                capacity);

            Assert.Equal(
                result.RuntimeInstanceId,
                capacity!.RuntimeInstanceId);

            Assert.Equal(
                AiRuntimeInstanceStatus.Ready,
                capacity.Status);

            Assert.True(
                capacity.CanAcceptRun);

            Assert.Equal(
                7,
                capacity.WorkerCount);

            Assert.Equal(
                7,
                capacity.AvailableWorkerCount);

            Assert.Equal(
                3,
                capacity.MaxConcurrentRuns);

            Assert.Equal(
                3,
                capacity.AvailableRunSlots);

            Assert.Equal(
                "42",
                capacity.Metadata["runtime.localQueueCapacity"]);
        }

        /// <summary>
        /// Creates the execution context snapshot used by HTTP scale-out provisioner tests.
        /// </summary>
        /// <returns>The execution context snapshot.</returns>
        private static ExecutionContextSnapshot CreateExecutionContextSnapshot()
        {
            return new ExecutionContextSnapshot
            {
                ContextKey = "unit-test:tenant-a:context",
                Project = "unit-test",
                UserId = "unit-test",
                TenantId = "tenant-a",
                TenantGroupId = "group-a",
                CurrentNamespace = "unit-test",
                Namespaces = new List<NamespaceEntry>
                {
                    new NamespaceEntry
                    {
                        Name = "unit-test",
                        Trns = new HashSet<string>()
                    }
                },
                InFlightCount = 0,
                TtlSeconds = 0,
                CreatedAtUtc = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Test runtime instance registry.
        /// </summary>
        private sealed class TestRuntimeInstanceRegistry : IAiRuntimeInstanceRegistry
        {
            private readonly Dictionary<string, AiRuntimeInstanceSnapshot> registrations =
                new(StringComparer.Ordinal);

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot> RegisterAsync(
                AiRuntimeInstanceRegistration registration,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(registration);

                var snapshot =
                    new AiRuntimeInstanceSnapshot
                    {
                        RuntimeInstanceId = registration.RuntimeInstanceId,
                        ControlPlaneId = registration.ControlPlaneId,
                        ControlPlaneHostId = registration.ControlPlaneHostId,
                        HostId = registration.HostId,
                        RuntimeId = registration.RuntimeId,
                        Role = registration.Role,
                        Status = AiRuntimeInstanceStatus.Ready,
                        WorkerCount = registration.WorkerCount,
                        QueueCapacity = registration.QueueCapacity,
                        MaxConcurrentRuns = registration.MaxConcurrentRuns,
                        RegisteredAtUtc = registration.RegisteredAtUtc,
                        LastHeartbeatAtUtc = DateTimeOffset.UtcNow,
                        QueuedRunCount = 0,
                        RunningRunCount = 0,
                        ActiveRunCount = 0,
                        AvailableRunSlots = registration.MaxConcurrentRuns,
                        ActiveWorkerCount = 0,
                        AvailableWorkerCount = registration.WorkerCount,
                        MaxLocalWorkersPerExecution = registration.WorkerCount,
                        IsQueuePaused = false,
                        CanAcceptRun = true,
                        Metadata = registration.Metadata
                    };

                registrations[registration.RuntimeInstanceId] =
                    snapshot;

                return Task.FromResult(
                    snapshot);
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> HeartbeatAsync(
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
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(
                    null);
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> GetAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                registrations.TryGetValue(
                    runtimeInstanceId,
                    out var snapshot);

                return Task.FromResult<AiRuntimeInstanceSnapshot?>(
                    snapshot);
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<AiRuntimeInstanceSnapshot>> ListAsync(
                bool includeStopped = false,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<AiRuntimeInstanceSnapshot>>(
                    registrations.Values.ToArray());
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> MarkDrainingAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(
                    null);
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> UnregisterAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(
                    null);
            }
        }

        /// <summary>
        /// Test runtime instance capacity store.
        /// </summary>
        private sealed class TestRuntimeInstanceCapacityStore : IAiRuntimeInstanceCapacityStore
        {
            private readonly Dictionary<string, AiRuntimeInstanceCapacityDescriptor> descriptors =
                new(StringComparer.Ordinal);

            /// <inheritdoc />
            public Task PublishAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(descriptor);

                descriptors[descriptor.RuntimeInstanceId] =
                    descriptor;

                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceCapacityDescriptor?> GetAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                descriptors.TryGetValue(
                    runtimeInstanceId,
                    out var descriptor);

                return Task.FromResult<AiRuntimeInstanceCapacityDescriptor?>(
                    descriptor);
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<AiRuntimeInstanceCapacityDescriptor>> ListAsync(
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<AiRuntimeInstanceCapacityDescriptor>>(
                    descriptors.Values.ToArray());
            }

            /// <inheritdoc />
            public Task<bool> RemoveAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    descriptors.Remove(runtimeInstanceId));
            }
        }

        /// <summary>
        /// Test runtime instance readiness waiter.
        /// </summary>
        private sealed class TestRuntimeInstanceReadinessWaiter : IAiRuntimeInstanceReadinessWaiter
        {
            /// <inheritdoc />
            public Task<AiRuntimeInstanceReadinessResult> WaitUntilReadyAsync(
                AiRuntimeInstanceReadinessRequest request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);

                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(
                    new AiRuntimeInstanceReadinessResult
                    {
                        Success = true,
                        ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                        RuntimeInstanceId = request.RuntimeInstanceId,
                        ProviderName = request.ProviderName,
                        TransportName = request.TransportName
                    });
            }
        }
    }
}