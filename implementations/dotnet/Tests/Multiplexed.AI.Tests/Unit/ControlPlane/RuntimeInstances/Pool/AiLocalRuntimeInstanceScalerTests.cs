using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Identity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Pool;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.ControlPlane.RuntimeInstances.Pool;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.SharedInstance;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Pool
{
    /// <summary>
    /// Provides unit tests for <see cref="AiLocalRuntimeInstanceScaler" />.
    /// </summary>
    public sealed class AiLocalRuntimeInstanceScalerTests
    {
        /// <summary>
        /// Verifies that the scaler creates, registers, and starts local runtime instances up to the requested target.
        /// </summary>
        [Fact]
        public async Task EnsureCapacityAsync_Should_Create_Register_And_Start_Local_Runtime_Instances()
        {
            var factory =
                new TestLocalRuntimeInstanceHostFactory();

            var registry =
                new TestSharedRuntimeInstanceRegistry();

            await using var scaler =
                CreateScaler(
                    factory,
                    registry);

            var result =
                await scaler
                    .EnsureCapacityAsync(
                        CreateRequest(targetInstanceCount: 2))
                    .ConfigureAwait(false);

            Assert.True(result.Success);
            Assert.False(result.Rejected);
            Assert.Equal(2, scaler.ActiveInstanceCount);
            Assert.Equal(2, factory.CreatedHosts.Count);
            Assert.Equal(2, registry.RegisteredInstances.Count);

            Assert.Equal("host-test:runtime-instance-1", factory.CreatedHosts[0].RuntimeInstanceId);
            Assert.Equal("host-test:runtime-instance-2", factory.CreatedHosts[1].RuntimeInstanceId);

            Assert.All(
                factory.CreatedHosts,
                host => Assert.True(host.Started));

            Assert.Equal("2", result.Metadata["activeInstanceCount"]);
            Assert.Equal("2", result.Metadata["targetInstanceCount"]);
            Assert.Equal("2", result.Metadata["createdInstanceCount"]);
        }

        /// <summary>
        /// Verifies that the scaler does not create more instances when the target is already reached.
        /// </summary>
        [Fact]
        public async Task EnsureCapacityAsync_Should_Not_Create_When_Target_Is_Already_Reached()
        {
            var factory =
                new TestLocalRuntimeInstanceHostFactory();

            var registry =
                new TestSharedRuntimeInstanceRegistry();

            await using var scaler =
                CreateScaler(
                    factory,
                    registry);

            var first =
                await scaler
                    .EnsureCapacityAsync(
                        CreateRequest(targetInstanceCount: 1))
                    .ConfigureAwait(false);

            var second =
                await scaler
                    .EnsureCapacityAsync(
                        CreateRequest(
                            requestId: "request-2",
                            targetInstanceCount: 1))
                    .ConfigureAwait(false);

            Assert.True(first.Success);
            Assert.True(second.Success);
            Assert.Equal(1, scaler.ActiveInstanceCount);
            Assert.Single(factory.CreatedHosts);
            Assert.Single(registry.RegisteredInstances);
            Assert.Equal("0", second.Metadata["createdInstanceCount"]);
        }

        /// <summary>
        /// Verifies that stopping the scaler stops and unregisters all local runtime instances.
        /// </summary>
        [Fact]
        public async Task StopAsync_Should_Stop_And_Unregister_Local_Runtime_Instances()
        {
            var factory =
                new TestLocalRuntimeInstanceHostFactory();

            var registry =
                new TestSharedRuntimeInstanceRegistry();

            await using var scaler =
                CreateScaler(
                    factory,
                    registry);

            await scaler
                .EnsureCapacityAsync(
                    CreateRequest(targetInstanceCount: 2))
                .ConfigureAwait(false);

            await scaler
                .StopAsync()
                .ConfigureAwait(false);

            Assert.All(
                factory.CreatedHosts,
                host => Assert.True(host.Stopped));

            Assert.Equal(
                new[]
                {
                    "host-test:runtime-instance-2",
                    "host-test:runtime-instance-1"
                },
                registry.UnregisteredRuntimeInstanceIds);
        }

        /// <summary>
        /// Verifies that disposing the scaler disposes all local runtime instances.
        /// </summary>
        [Fact]
        public async Task DisposeAsync_Should_Dispose_Local_Runtime_Instances()
        {
            var factory =
                new TestLocalRuntimeInstanceHostFactory();

            var registry =
                new TestSharedRuntimeInstanceRegistry();

            var scaler =
                CreateScaler(
                    factory,
                    registry);

            await scaler
                .EnsureCapacityAsync(
                    CreateRequest(targetInstanceCount: 2))
                .ConfigureAwait(false);

            await scaler
                .DisposeAsync()
                .ConfigureAwait(false);

            Assert.All(
                factory.CreatedHosts,
                host => Assert.True(host.Disposed));
        }

        /// <summary>
        /// Verifies that pool metadata is forwarded to the host factory.
        /// </summary>
        [Fact]
        public async Task EnsureCapacityAsync_Should_Forward_Pool_Metadata_To_Host_Factory()
        {
            var hostFactory =
                new TestLocalRuntimeInstanceHostFactory();

            var sharedRuntimeInstanceRegistry =
                new InMemoryAiSharedRuntimeInstanceRegistry();

            var runtimeHostIdentity =
                new TestRuntimeHostIdentity(
                    "test-host");

            var options =
                Options.Create(
                    new AiLocalRuntimeInstancePoolOptions
                    {
                        Enabled = true,
                        InstanceCount = 1,
                        WorkerCountPerInstance = 10,
                        MaxConcurrentRunsPerInstance = 5,
                        RuntimeInstanceIdPrefix = "test-runtime",
                        Metadata =
                        {
                            [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = "tenant-a",
                            [AiRuntimeInstanceIsolationMetadataKeys.IsolationMode] = "Dedicated",
                            [AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback] = "false",
                            [AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity] = "true"
                        }
                    });

            var configuration =
                new ConfigurationBuilder()
                    .AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AiMcpHost:Mode"] = "ControlPlaneWithLocalRuntimeInstances",
                            ["AiMcpHost:EnableSharedQueuePump"] = "false"
                        })
                    .Build();

            var logger =
                NullLogger<AiLocalRuntimeInstanceScaler>.Instance;

            var scaler =
                new AiLocalRuntimeInstanceScaler(
                    hostFactory,
                    sharedRuntimeInstanceRegistry,
                    runtimeHostIdentity,
                    options,
                    configuration,
                    logger);

            var result =
                await scaler
                    .EnsureCapacityAsync(
                        new AiRuntimeScaleOutProviderRequest
                        {
                            RequestId = $"test-request-{Guid.NewGuid():N}",
                            SharedRunId = $"test-shared-run-{Guid.NewGuid():N}",
                            ControlPlaneId = "test-control-plane",
                            ExecutionContextSnapshot = CreateExecutionContextSnapshot(
                                tenantId: "tenant-a",
                                tenantGroupId: "tenant-a"),
                            CurrentInstanceCount = 0,
                            RequestedTargetInstanceCount = 1
                        })
                    .ConfigureAwait(false);

            Assert.True(
                result.Success,
                result.FailureReason ?? result.Message);

            Assert.False(
                result.Rejected);

            Assert.Single(
                hostFactory.CreatedHosts);

            var metadata =
                Assert.Single(
                    hostFactory.ReceivedMetadata);

            Assert.NotNull(
                metadata);

            Assert.Equal(
                "tenant-a",
                metadata![AiRuntimeInstanceIsolationMetadataKeys.TenantId]);

            Assert.Equal(
                "Dedicated",
                metadata[AiRuntimeInstanceIsolationMetadataKeys.IsolationMode]);

            Assert.Equal(
                "false",
                metadata[AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback]);

            Assert.Equal(
                "true",
                metadata[AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity]);
        }

        /// <summary>
        /// Creates a local runtime instance scaler.
        /// </summary>
        /// <param name="factory">The host factory.</param>
        /// <param name="registry">The shared runtime instance registry.</param>
        /// <returns>The created scaler.</returns>
        private static AiLocalRuntimeInstanceScaler CreateScaler(
            TestLocalRuntimeInstanceHostFactory factory,
            TestSharedRuntimeInstanceRegistry registry)
        {
            var configuration =
                new ConfigurationBuilder()
                    .AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AiMcpHost:Mode"] = "ControlPlaneWithLocalRuntimeInstances",
                            ["AiMcpHost:EnableSharedQueuePump"] = "true"
                        })
                    .Build();

            return new AiLocalRuntimeInstanceScaler(
                factory,
                registry,
                new TestRuntimeHostIdentity("host-test"),
                Options.Create(new AiLocalRuntimeInstancePoolOptions
                {
                    Enabled = true,
                    InstanceCount = 1,
                    WorkerCountPerInstance = 10,
                    MaxConcurrentRunsPerInstance = 3,
                    LocalQueueCapacity = 100,
                    RuntimeInstanceIdPrefix = "runtime-instance"
                }),
                configuration,
                NullLogger<AiLocalRuntimeInstanceScaler>.Instance);
        }

        /// <summary>
        /// Creates a scale-out provider request.
        /// </summary>
        /// <param name="requestId">The request identifier.</param>
        /// <param name="targetInstanceCount">The requested target instance count.</param>
        /// <returns>The created request.</returns>
        private static AiRuntimeScaleOutProviderRequest CreateRequest(
            string requestId = "request-1",
            int targetInstanceCount = 1)
        {
            return new AiRuntimeScaleOutProviderRequest
            {
                RequestId = requestId,
                ControlPlaneId = "cp-test",
                SharedRunId = "shared-run-1",
                ExecutionContextSnapshot = CreateExecutionContextSnapshot(),
                TenantId = "tenant-test",
                PipelineKey = "pipeline-test",
                VisibleInstanceCount = 0,
                AvailableInstanceCount = 0,
                CurrentInstanceCount = 0,
                MaxInstanceCount = 5,
                RequestedTargetInstanceCount = targetInstanceCount,
                ProviderHint = "local",
                RequestedBy = "unit-test",
                Source = "unit-test",
                CorrelationId = "correlation-test",
                Reason = "No runtime capacity was available for admission.",
                Metadata = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["test"] = "true"
                }
            };
        }

        /// <summary>
        /// Creates the execution context snapshot used by local runtime instance scaler tests.
        /// </summary>
        /// <param name="tenantId">The tenant identifier.</param>
        /// <param name="tenantGroupId">The tenant group identifier.</param>
        /// <returns>The execution context snapshot.</returns>
        private static ExecutionContextSnapshot CreateExecutionContextSnapshot(
            string tenantId = "tenant-test",
            string tenantGroupId = "tenant-group-test")
        {
            return new ExecutionContextSnapshot
            {
                ContextKey = $"unit-test:{tenantId}:context",
                Project = "unit-test",
                UserId = "unit-test",
                TenantId = tenantId,
                TenantGroupId = tenantGroupId,
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
        /// Provides a test runtime host identity.
        /// </summary>
        private sealed class TestRuntimeHostIdentity : IAiRuntimeHostIdentity
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="TestRuntimeHostIdentity" /> class.
            /// </summary>
            /// <param name="hostId">The host identifier.</param>
            public TestRuntimeHostIdentity(
                string hostId)
            {
                this.HostId = hostId;
            }

            /// <inheritdoc />
            public string HostId { get; }
        }

        /// <summary>
        /// Provides a test local runtime instance host factory.
        /// </summary>
        private sealed class TestLocalRuntimeInstanceHostFactory : IAiLocalRuntimeInstanceHostFactory
        {
            /// <summary>
            /// Gets the created hosts.
            /// </summary>
            public List<TestLocalRuntimeInstanceHost> CreatedHosts { get; } = new();

            /// <summary>
            /// Gets the metadata received by each create call.
            /// </summary>
            public List<IReadOnlyDictionary<string, string>?> ReceivedMetadata { get; } = new();

            /// <inheritdoc />
            public Task<IAiLocalRuntimeInstanceHost> CreateAsync(
                string runtimeInstanceId,
                int workerCount,
                int maxConcurrentRuns,
                int? localQueueCapacity,
                IReadOnlyDictionary<string, string>? metadata = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                this.ReceivedMetadata.Add(metadata);

                var host =
                    new TestLocalRuntimeInstanceHost(
                        runtimeInstanceId,
                        workerCount);

                this.CreatedHosts.Add(host);

                return Task.FromResult<IAiLocalRuntimeInstanceHost>(
                    host);
            }
        }

        /// <summary>
        /// Provides a test local runtime instance host.
        /// </summary>
        private sealed class TestLocalRuntimeInstanceHost : IAiLocalRuntimeInstanceHost
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="TestLocalRuntimeInstanceHost" /> class.
            /// </summary>
            /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
            /// <param name="workerCount">The worker count.</param>
            public TestLocalRuntimeInstanceHost(
                string runtimeInstanceId,
                int workerCount)
            {
                this.RuntimeInstanceId = runtimeInstanceId;
                this.WorkerCount = workerCount;
                this.SharedRuntimeInstance =
                    new TestSharedRuntimeInstance(
                        runtimeInstanceId);
            }

            /// <inheritdoc />
            public string RuntimeInstanceId { get; }

            /// <inheritdoc />
            public int WorkerCount { get; }

            /// <inheritdoc />
            public IAiRuntimePipelineBackgroundController Controller { get; } = default!;

            /// <inheritdoc />
            public IAiRuntimeQueueControlPlane QueueControlPlane { get; } = default!;

            /// <inheritdoc />
            public IAiSharedRuntimeInstance SharedRuntimeInstance { get; }

            /// <summary>
            /// Gets a value indicating whether the host was started.
            /// </summary>
            public bool Started { get; private set; }

            /// <summary>
            /// Gets a value indicating whether the host was stopped.
            /// </summary>
            public bool Stopped { get; private set; }

            /// <summary>
            /// Gets a value indicating whether the host was disposed.
            /// </summary>
            public bool Disposed { get; private set; }

            /// <inheritdoc />
            public Task StartAsync(
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                this.Started = true;

                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task StopAsync(
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                this.Stopped = true;

                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public ValueTask DisposeAsync()
            {
                this.Disposed = true;

                return ValueTask.CompletedTask;
            }
        }

        /// <summary>
        /// Provides a test shared runtime instance.
        /// </summary>
        private sealed class TestSharedRuntimeInstance : IAiSharedRuntimeInstance
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="TestSharedRuntimeInstance" /> class.
            /// </summary>
            /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
            public TestSharedRuntimeInstance(
                string runtimeInstanceId)
            {
                this.RuntimeInstanceId = runtimeInstanceId;
            }

            /// <inheritdoc />
            public string RuntimeInstanceId { get; }

            /// <inheritdoc />
            public IAiRuntimeQueueControlPlane QueueControlPlane { get; } = default!;

            /// <inheritdoc />
            public Task<AiSharedRuntimeInstanceDispatchResult> DispatchAsync(
                AiSharedRuntimeInstanceDispatchRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(
                    new AiSharedRuntimeInstanceDispatchResult
                    {
                        Success = true,
                        RuntimeInstanceId = this.RuntimeInstanceId,
                        SharedRunId = request.SharedRun.SharedRunId,
                        ClaimToken = request.ClaimToken,
                        Message = "Test dispatch succeeded.",
                        StartedAtUtc = DateTimeOffset.UtcNow,
                        CompletedAtUtc = DateTimeOffset.UtcNow
                    });
            }
        }

        /// <summary>
        /// Provides a test shared runtime instance registry.
        /// </summary>
        private sealed class TestSharedRuntimeInstanceRegistry : IAiSharedRuntimeInstanceRegistry
        {
            private readonly Dictionary<string, IAiSharedRuntimeInstance> instances =
                new(StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// Gets registered runtime instances.
            /// </summary>
            public IReadOnlyCollection<IAiSharedRuntimeInstance> RegisteredInstances =>
                this.instances.Values.ToArray();

            /// <summary>
            /// Gets unregistered runtime instance identifiers.
            /// </summary>
            public List<string> UnregisteredRuntimeInstanceIds { get; } = new();

            /// <inheritdoc />
            public Task RegisterAsync(
                IAiSharedRuntimeInstance instance,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(instance);

                cancellationToken.ThrowIfCancellationRequested();

                this.instances[instance.RuntimeInstanceId] = instance;

                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task<IAiSharedRuntimeInstance?> GetAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                this.instances.TryGetValue(
                    runtimeInstanceId,
                    out var instance);

                return Task.FromResult(instance);
            }

            /// <inheritdoc />
            public Task<IReadOnlyCollection<IAiSharedRuntimeInstance>> ListAsync(
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult<IReadOnlyCollection<IAiSharedRuntimeInstance>>(
                    this.instances.Values.ToArray());
            }

            /// <inheritdoc />
            public Task<bool> UnregisterAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                this.UnregisteredRuntimeInstanceIds.Add(runtimeInstanceId);

                return Task.FromResult(
                    this.instances.Remove(runtimeInstanceId));
            }
        }
    }
}