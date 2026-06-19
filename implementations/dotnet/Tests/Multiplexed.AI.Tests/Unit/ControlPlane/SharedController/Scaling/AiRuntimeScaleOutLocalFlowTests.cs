using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Identity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Pool;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.AI.ControlPlane.RuntimeInstances.Pool;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Local;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Tests.Fixtures;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Provides local scale-out flow tests using the real watcher, selector, local provider, and scaler.
    /// </summary>
    public sealed class AiRuntimeScaleOutLocalFlowTests
    {
        /// <summary>
        /// Verifies that a pending local scale-out request is fulfilled through the real local provider and scaler.
        /// </summary>
        [Fact]
        public async Task ProcessCycleAsync_Should_Fulfill_Local_ScaleOut_Request_Through_Local_Provider_And_Scaler()
        {
            var store =
                new InMemoryAiRuntimeScaleOutRequestStore();

            await store
                .CreateAsync(
                    CreateScaleOutRequestRecord(
                        requestId: "request-1",
                        targetInstanceCount: 1))
                .ConfigureAwait(false);

            var factory =
                new TestLocalRuntimeInstanceHostFactory();

            var registry =
                new TestSharedRuntimeInstanceRegistry();

            await using var scaler =
                CreateScaler(
                    factory,
                    registry);

            var localProvider =
                new LocalAiRuntimeInstanceProvider(
                    registry,
                    scaler);

            var providerRouter =
                new AiRuntimeInstanceProviderRouter(
                    new IAiRuntimeInstanceProvider[]
                    {
                        localProvider
                    });

            var selector =
                new AiRuntimeScaleOutProviderSelector(
                    providerRouter,
                    Options.Create(new AiRuntimeInstanceRegistrationOptions
                    {
                        ProviderName = "local"
                    }));

            var watcher =
                new AiRuntimeScaleOutRequestWatcherHostedService(
                    store,
                    selector,
                    new TestScaleOutFulfilledRunRequeueService(),
                    new TestControlPlaneIdResolver("cp-test"),
                    Options.Create(new AiRuntimeScaleOutRequestWatcherOptions
                    {
                        Enabled = true,
                        ControlPlaneId = "cp-test",
                        WatcherId = "local-flow-watcher",
                        Interval = TimeSpan.FromSeconds(1),
                        MaxRequestsPerCycle = 10,
                        RejectOnProviderFailure = true
                    }));

            await watcher
                .ProcessCycleAsync()
                .ConfigureAwait(false);

            var record =
                await store
                    .GetAsync("request-1")
                    .ConfigureAwait(false);

            Assert.NotNull(record);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Fulfilled, record.Status);
            Assert.Equal("local-flow-watcher", record.ObservedBy);
            Assert.Equal("local-flow-watcher", record.FulfilledBy);
            Assert.Equal("host-test:runtime-instance-1", record.FulfilledRuntimeInstanceId);

            Assert.Equal(1, scaler.ActiveInstanceCount);
            Assert.Single(factory.CreatedHosts);
            Assert.Single(registry.RegisteredInstances);

            var createdHost =
                factory.CreatedHosts[0];

            Assert.Equal("host-test:runtime-instance-1", createdHost.RuntimeInstanceId);
            Assert.True(createdHost.Started);
            Assert.False(createdHost.Stopped);
            Assert.False(createdHost.Disposed);
        }

        /// <summary>
        /// Verifies that a local scale-out request can create multiple runtime instances.
        /// </summary>
        [Fact]
        public async Task ProcessCycleAsync_Should_Create_Multiple_Local_Runtime_Instances_When_Target_Is_Greater_Than_One()
        {
            var store =
                new InMemoryAiRuntimeScaleOutRequestStore();

            await store
                .CreateAsync(
                    CreateScaleOutRequestRecord(
                        requestId: "request-1",
                        targetInstanceCount: 2))
                .ConfigureAwait(false);

            var factory =
                new TestLocalRuntimeInstanceHostFactory();

            var registry =
                new TestSharedRuntimeInstanceRegistry();

            await using var scaler =
                CreateScaler(
                    factory,
                    registry);

            var localProvider =
                new LocalAiRuntimeInstanceProvider(
                    registry,
                    scaler);

            var providerRouter =
                new AiRuntimeInstanceProviderRouter(
                    new IAiRuntimeInstanceProvider[]
                    {
                        localProvider
                    });

            var selector =
                new AiRuntimeScaleOutProviderSelector(
                    providerRouter,
                    Options.Create(new AiRuntimeInstanceRegistrationOptions
                    {
                        ProviderName = "local"
                    }));

            var watcher =
                new AiRuntimeScaleOutRequestWatcherHostedService(
                    store,
                    selector,
                    new TestScaleOutFulfilledRunRequeueService(),
                    new TestControlPlaneIdResolver("cp-test"),
                    Options.Create(new AiRuntimeScaleOutRequestWatcherOptions
                    {
                        Enabled = true,
                        ControlPlaneId = "cp-test",
                        WatcherId = "local-flow-watcher",
                        Interval = TimeSpan.FromSeconds(1),
                        MaxRequestsPerCycle = 10,
                        RejectOnProviderFailure = true
                    }));

            await watcher
                .ProcessCycleAsync()
                .ConfigureAwait(false);

            var record =
                await store
                    .GetAsync("request-1")
                    .ConfigureAwait(false);

            Assert.NotNull(record);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Fulfilled, record.Status);
            Assert.Equal("host-test:runtime-instance-2", record.FulfilledRuntimeInstanceId);

            Assert.Equal(2, scaler.ActiveInstanceCount);
            Assert.Equal(2, factory.CreatedHosts.Count);
            Assert.Equal(2, registry.RegisteredInstances.Count);

            Assert.Equal("host-test:runtime-instance-1", factory.CreatedHosts[0].RuntimeInstanceId);
            Assert.Equal("host-test:runtime-instance-2", factory.CreatedHosts[1].RuntimeInstanceId);

            Assert.All(
                factory.CreatedHosts,
                host => Assert.True(host.Started));
        }

        /// <summary>
        /// Creates a local runtime instance scaler.
        /// </summary>
        /// <param name="factory">The local runtime instance host factory.</param>
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
        /// Creates a pending scale-out request record.
        /// </summary>
        /// <param name="requestId">The scale-out request identifier.</param>
        /// <param name="targetInstanceCount">The requested target instance count.</param>
        /// <returns>The created scale-out request record.</returns>
        private static AiRuntimeScaleOutRequestRecord CreateScaleOutRequestRecord(
            string requestId,
            int targetInstanceCount)
        {
            return new AiRuntimeScaleOutRequestRecord
            {
                RequestId = requestId,
                ControlPlaneId = "cp-test",
                SharedRunId = "shared-run-1",
                TenantId = "tenant-test",
                PipelineKey = "pipeline-test",
                Status = AiRuntimeScaleOutRequestStatus.Pending,
                Reason = "No runtime capacity was available for admission.",
                VisibleInstanceCount = 0,
                AvailableInstanceCount = 0,
                CurrentInstanceCount = 0,
                MaxInstanceCount = 5,
                RequestedTargetInstanceCount = targetInstanceCount,
                ProviderHint = "local",
                RequestedBy = "unit-test",
                Source = "unit-test",
                CorrelationId = "correlation-test",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["test"] = "true"
                }
            };
        }

        /// <summary>
        /// Provides a test control-plane id resolver.
        /// </summary>
        private sealed class TestControlPlaneIdResolver : IAiControlPlaneIdResolver
        {
            /// <summary>
            /// The control-plane identifier.
            /// </summary>
            private readonly string? controlPlaneId;

            /// <summary>
            /// Initializes a new instance of the <see cref="TestControlPlaneIdResolver" /> class.
            /// </summary>
            /// <param name="controlPlaneId">The control-plane identifier.</param>
            public TestControlPlaneIdResolver(
                string? controlPlaneId)
            {
                this.controlPlaneId =
                    controlPlaneId;
            }

            /// <inheritdoc />
            public Task<string?> ResolveAsync(
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(
                    this.controlPlaneId);
            }
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
                this.RuntimeInstanceId =
                    runtimeInstanceId;
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