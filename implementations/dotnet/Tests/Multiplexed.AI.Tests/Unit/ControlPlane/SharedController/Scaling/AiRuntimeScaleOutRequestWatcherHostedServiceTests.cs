using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Readiness;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http.ScaleOut;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Tests.Fixtures;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Provides unit tests for <see cref="AiRuntimeScaleOutRequestWatcherHostedService" />.
    /// </summary>
    public sealed class AiRuntimeScaleOutRequestWatcherHostedServiceTests
    {
        /// <summary>
        /// Verifies that a pending scale-out request is observed and fulfilled by the watcher.
        /// </summary>
        [Fact]
        public async Task ProcessCycleAsync_Should_Fulfill_Pending_Request_When_Provider_Succeeds()
        {
            var store =
                CreateStore();

            await store
                .CreateAsync(
                    CreateRequest("request-1"))
                .ConfigureAwait(false);

            var requeueService =
                new TestScaleOutFulfilledRunRequeueService();

            var watcher =
                new AiRuntimeScaleOutRequestWatcherHostedService(
                    store,
                    new TestScaleOutProviderSelector(
                        new SimulatedAiRuntimeScaleOutProvider(
                            Options.Create(new SimulatedAiRuntimeScaleOutProviderOptions
                            {
                                Succeed = true,
                                RuntimeInstanceIdPrefix = "simulated-runtime"
                            }))),
                    requeueService,
                    new StaticAiControlPlaneIdResolver("cp-test"),
                    Options.Create(new AiRuntimeScaleOutRequestWatcherOptions
                    {
                        Enabled = true,
                        ControlPlaneId = "cp-test",
                        WatcherId = "watcher-test",
                        Interval = TimeSpan.FromSeconds(1),
                        MaxRequestsPerCycle = 10,
                        RejectOnProviderFailure = true
                    }));

            await watcher
                .ProcessCycleAsync()
                .ConfigureAwait(false);

            var loaded =
                await store
                    .GetAsync("request-1")
                    .ConfigureAwait(false);

            Assert.NotNull(loaded);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Fulfilled, loaded.Status);
            Assert.Equal("watcher-test", loaded.ObservedBy);
            Assert.Equal("watcher-test", loaded.FulfilledBy);
            Assert.False(string.IsNullOrWhiteSpace(loaded.FulfilledRuntimeInstanceId));
            Assert.StartsWith("simulated-runtime-", loaded.FulfilledRuntimeInstanceId, StringComparison.Ordinal);
            Assert.Equal(1, requeueService.CallCount);

            var pending =
                await store
                    .ListPendingAsync(
                        new AiRuntimeScaleOutRequestQuery
                        {
                            ControlPlaneId = "cp-test"
                        })
                    .ConfigureAwait(false);

            Assert.Empty(pending);
        }

        /// <summary>
        /// Verifies that a shared-queue redispatch replacement is not requeued a
        /// second time after provider fulfillment.
        /// </summary>
        [Fact]
        public async Task ProcessCycleAsync_Should_Not_Requeue_SharedQueue_Redispatch_Replacement_Twice()
        {
            var store =
                CreateStore();

            var request =
                CreateRequest(
                    requestId:
                        "request-shared-queue-replacement-1",
                    sharedRunId:
                        "shared-run-replacement-1");

            request.Metadata["scaleout.intent"] =
                "shared-queue-redispatch-replacement";

            await store
                .CreateAsync(request)
                .ConfigureAwait(false);

            var requeueService =
                new TestScaleOutFulfilledRunRequeueService();

            var watcher =
                new AiRuntimeScaleOutRequestWatcherHostedService(
                    store,
                    new TestScaleOutProviderSelector(
                        new SimulatedAiRuntimeScaleOutProvider(
                            Options.Create(
                                new SimulatedAiRuntimeScaleOutProviderOptions
                                {
                                    Succeed = true,
                                    RuntimeInstanceIdPrefix =
                                        "simulated-runtime"
                                }))),
                    requeueService,
                    new StaticAiControlPlaneIdResolver("cp-test"),
                    Options.Create(
                        new AiRuntimeScaleOutRequestWatcherOptions
                        {
                            Enabled = true,
                            ControlPlaneId = "cp-test",
                            WatcherId = "watcher-test",
                            Interval = TimeSpan.FromSeconds(1),
                            MaxRequestsPerCycle = 10,
                            RejectOnProviderFailure = true
                        }));

            await watcher
                .ProcessCycleAsync()
                .ConfigureAwait(false);

            var loaded =
                await store
                    .GetAsync(
                        "request-shared-queue-replacement-1")
                    .ConfigureAwait(false);

            Assert.NotNull(loaded);
            Assert.Equal(
                AiRuntimeScaleOutRequestStatus.Fulfilled,
                loaded!.Status);
            Assert.Equal(0, requeueService.CallCount);
            Assert.False(
                string.IsNullOrWhiteSpace(
                    loaded.FulfilledRuntimeInstanceId));
        }

        /// <summary>
        /// Verifies that a pending scale-out request is rejected when the provider rejects it.
        /// </summary>
        [Fact]
        public async Task ProcessCycleAsync_Should_Reject_Pending_Request_When_Provider_Fails()
        {
            var store =
                CreateStore();

            await store
                .CreateAsync(
                    CreateRequest("request-1"))
                .ConfigureAwait(false);

            var watcher =
                new AiRuntimeScaleOutRequestWatcherHostedService(
                    store,
                    new TestScaleOutProviderSelector(
                        new SimulatedAiRuntimeScaleOutProvider(
                            Options.Create(new SimulatedAiRuntimeScaleOutProviderOptions
                            {
                                Succeed = false,
                                FailureReason = "simulated failure"
                            }))),
                    new TestScaleOutFulfilledRunRequeueService(),
                    new StaticAiControlPlaneIdResolver("cp-test"),
                    Options.Create(new AiRuntimeScaleOutRequestWatcherOptions
                    {
                        Enabled = true,
                        ControlPlaneId = "cp-test",
                        WatcherId = "watcher-test",
                        Interval = TimeSpan.FromSeconds(1),
                        MaxRequestsPerCycle = 10,
                        RejectOnProviderFailure = true
                    }));

            await watcher
                .ProcessCycleAsync()
                .ConfigureAwait(false);

            var loaded =
                await store
                    .GetAsync("request-1")
                    .ConfigureAwait(false);

            Assert.NotNull(loaded);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Rejected, loaded.Status);
            Assert.Equal("watcher-test", loaded.ObservedBy);
            Assert.Equal("watcher-test", loaded.RejectedBy);
            Assert.Equal("simulated failure", loaded.RejectionReason);
        }

        /// <summary>
        /// Verifies that the watcher respects the maximum number of requests processed per cycle.
        /// </summary>
        [Fact]
        public async Task ProcessCycleAsync_Should_Respect_MaxRequestsPerCycle()
        {
            var store =
                CreateStore();

            await store
                .CreateAsync(
                    CreateRequest("request-1"))
                .ConfigureAwait(false);

            var second =
                CreateRequest(
                    requestId: "request-2",
                    sharedRunId: "shared-run-2",
                    pipelineKey: "pipeline-2");

            await store
                .CreateAsync(
                    second)
                .ConfigureAwait(false);

            var watcher =
                new AiRuntimeScaleOutRequestWatcherHostedService(
                    store,
                    new TestScaleOutProviderSelector(
                        new SimulatedAiRuntimeScaleOutProvider()),
                    new TestScaleOutFulfilledRunRequeueService(),
                    new StaticAiControlPlaneIdResolver("cp-test"),
                    Options.Create(new AiRuntimeScaleOutRequestWatcherOptions
                    {
                        Enabled = true,
                        ControlPlaneId = "cp-test",
                        WatcherId = "watcher-test",
                        Interval = TimeSpan.FromSeconds(1),
                        MaxRequestsPerCycle = 1,
                        RejectOnProviderFailure = true
                    }));

            await watcher
                .ProcessCycleAsync()
                .ConfigureAwait(false);

            var all =
                await store
                    .ListAsync(
                        new AiRuntimeScaleOutRequestQuery
                        {
                            ControlPlaneId = "cp-test",
                            IncludeExpired = true,
                            MaxResults = 10
                        })
                    .ConfigureAwait(false);

            Assert.Single(
                all,
                request => request.Status == AiRuntimeScaleOutRequestStatus.Fulfilled);

            Assert.Single(
                all,
                request => request.Status == AiRuntimeScaleOutRequestStatus.Pending);
        }

        /// <summary>
        /// Verifies that the watcher can resolve the control-plane id from the resolver.
        /// </summary>
        [Fact]
        public async Task ProcessCycleAsync_Should_Use_ControlPlaneId_Resolver_When_Option_Is_Missing()
        {
            var store =
                CreateStore();

            await store
                .CreateAsync(
                    CreateRequest("request-1"))
                .ConfigureAwait(false);

            var watcher =
                new AiRuntimeScaleOutRequestWatcherHostedService(
                    store,
                    new TestScaleOutProviderSelector(
                        new SimulatedAiRuntimeScaleOutProvider()),
                    new TestScaleOutFulfilledRunRequeueService(),
                    new StaticAiControlPlaneIdResolver("cp-test"),
                    Options.Create(new AiRuntimeScaleOutRequestWatcherOptions
                    {
                        Enabled = true,
                        ControlPlaneId = null,
                        WatcherId = "watcher-test",
                        Interval = TimeSpan.FromSeconds(1),
                        MaxRequestsPerCycle = 10,
                        RejectOnProviderFailure = true
                    }));

            await watcher
                .ProcessCycleAsync()
                .ConfigureAwait(false);

            var loaded =
                await store
                    .GetAsync("request-1")
                    .ConfigureAwait(false);

            Assert.NotNull(loaded);
            Assert.Equal(AiRuntimeScaleOutRequestStatus.Fulfilled, loaded.Status);
        }

        /// <summary>
        /// Verifies that a pending HTTP scale-out request is fulfilled by the watcher
        /// and materializes HTTP runtime capacity.
        /// </summary>
        [Fact]
        public async Task ProcessCycleAsync_Should_Fulfill_Http_Pending_Request_And_Publish_Capacity()
        {
            var store =
                CreateStore();

            var request =
                CreateRequest(
                    requestId: "request-http-1",
                    sharedRunId: "shared-run-1",
                    tenantId: "tenant-a",
                    tenantGroupId: "tenant-group-a",
                    pipelineKey: "pipeline-http",
                    providerHint: "http");

            request.IsolationMode = AiRuntimeInstanceIsolationMode.Dedicated;
            request.PreferDedicatedCapacity = true;
            request.AllowSharedFallback = false;
            request.MaxRuntimeInstances = 5;
            request.RuntimeInstanceIdPrefix = "tenant-a-http";
            request.WorkerCountPerInstance = 7;
            request.MaxConcurrentRunsPerInstance = 3;
            request.LocalQueueCapacity = 42;

            request.Metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantId] = "tenant-a";
            request.Metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = "tenant-group-a";
            request.Metadata[AiRuntimeInstanceIsolationMetadataKeys.IsolationMode] = "Dedicated";
            request.Metadata[AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity] = "true";
            request.Metadata[AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback] = "false";
            request.Metadata["runtime.maxRuntimeInstances"] = "5";
            request.Metadata["runtime.instanceIdPrefix"] = "tenant-a-http";
            request.Metadata["runtime.workerCountPerInstance"] = "7";
            request.Metadata["runtime.maxConcurrentRunsPerInstance"] = "3";
            request.Metadata["runtime.localQueueCapacity"] = "42";

            await store
                .CreateAsync(
                    request)
                .ConfigureAwait(false);

            var registry =
                new TestRuntimeInstanceRegistry();

            var capacityStore =
                new TestRuntimeInstanceCapacityStore();

            var tenantRuntimeSettingsProvider = new HardcodedAiTenantRuntimeSettingsProvider();

            var provisioner =
                new AiHttpRuntimeScaleOutProvisioner(
                    registry,
                    capacityStore,
                    new NoopAiRuntimeHostManager(),
                    new TestRuntimeInstanceReadinessWaiter(),
                    tenantRuntimeSettingsProvider,
                    Options.Create(
                        new AiHttpRuntimeScaleOutOptions
                        {
                            Enabled = true,
                            Mode = AiHttpRuntimeScaleOutModes.MetadataOnly,
                            DefaultRuntimeInstanceIdPrefix = "http-runtime",
                            EndpointTemplate = "http://runtime-host/{runtimeInstanceId}"
                        }),
                    NullLogger<AiHttpRuntimeScaleOutProvisioner>.Instance);

            var httpProvider =
                new HttpAiRuntimeInstanceProvider(
                    new HttpClient(),
                    NullLogger<HttpAiRuntimeInstanceProvider>.Instance,
                    Options.Create(new AiHttpRuntimeInstanceProviderOptions()),
                    provisioner);

            var watcher =
                new AiRuntimeScaleOutRequestWatcherHostedService(
                    store,
                    new TestScaleOutProviderSelector(
                        httpProvider),
                    new TestScaleOutFulfilledRunRequeueService(),
                    new StaticAiControlPlaneIdResolver("cp-test"),
                    Options.Create(new AiRuntimeScaleOutRequestWatcherOptions
                    {
                        Enabled = true,
                        ControlPlaneId = "cp-test",
                        WatcherId = "watcher-test",
                        Interval = TimeSpan.FromSeconds(1),
                        MaxRequestsPerCycle = 10,
                        RejectOnProviderFailure = true
                    }));

            await watcher
                .ProcessCycleAsync()
                .ConfigureAwait(false);

            var loaded =
                await store
                    .GetAsync("request-http-1")
                    .ConfigureAwait(false);

            Assert.NotNull(
                loaded);

            Assert.Equal(
                AiRuntimeScaleOutRequestStatus.Fulfilled,
                loaded!.Status);

            Assert.Equal(
                "watcher-test",
                loaded.ObservedBy);

            Assert.Equal(
                "watcher-test",
                loaded.FulfilledBy);

            Assert.False(
                string.IsNullOrWhiteSpace(loaded.FulfilledRuntimeInstanceId));

            var runtimeInstanceId =
                loaded.FulfilledRuntimeInstanceId!;

            var registered =
                await registry
                    .GetAsync(runtimeInstanceId)
                    .ConfigureAwait(false);

            Assert.NotNull(
                registered);

            Assert.Equal(
                runtimeInstanceId,
                registered!.RuntimeInstanceId);

            Assert.Equal(
                "http",
                registered.Metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName]);

            Assert.Equal(
                "http",
                registered.Metadata["provider.name"]);

            Assert.Equal(
                AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                registered.Metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName]);

            Assert.Equal(
                "tenant-a",
                registered.Metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantId]);

            Assert.Equal(
                "Dedicated",
                registered.Metadata[AiRuntimeInstanceIsolationMetadataKeys.IsolationMode]);

            var capacity =
                await capacityStore
                    .GetAsync(runtimeInstanceId)
                    .ConfigureAwait(false);

            Assert.NotNull(
                capacity);

            Assert.Equal(
                runtimeInstanceId,
                capacity!.RuntimeInstanceId);

            Assert.Equal(
                AiRuntimeInstanceStatus.Ready,
                capacity.Status);

            Assert.True(
                capacity.CanAcceptRun);

            Assert.Equal(
                "http",
                capacity.Metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName]);

            Assert.Equal(
                "tenant-a",
                capacity.Metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantId]);

            var pending =
                await store
                    .ListPendingAsync(
                        new AiRuntimeScaleOutRequestQuery
                        {
                            ControlPlaneId = "cp-test"
                        })
                    .ConfigureAwait(false);

            Assert.Empty(
                pending);
        }

        /// <summary>
        /// Creates an in-memory scale-out request store.
        /// </summary>
        /// <returns>The created store.</returns>
        private static InMemoryAiRuntimeScaleOutRequestStore CreateStore()
        {
            return new InMemoryAiRuntimeScaleOutRequestStore(
                Options.Create(new AiRuntimeScaleOutRequestStoreOptions
                {
                    DefaultTtl = TimeSpan.FromMinutes(5),
                    DeduplicationWindow = TimeSpan.FromSeconds(30),
                    EnableDeduplication = true,
                    MaxListResults = 100
                }));
        }

        /// <summary>
        /// Creates a valid scale-out request record for tests.
        /// </summary>
        /// <param name="requestId">The request identifier.</param>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <param name="tenantId">The tenant identifier.</param>
        /// <param name="tenantGroupId">The tenant group identifier.</param>
        /// <param name="pipelineKey">The pipeline key.</param>
        /// <param name="providerHint">The provider hint.</param>
        /// <returns>The created request record.</returns>
        private static AiRuntimeScaleOutRequestRecord CreateRequest(
            string requestId,
            string sharedRunId = "shared-run-1",
            string tenantId = "tenant-test",
            string tenantGroupId = "tenant-group-test",
            string pipelineKey = "pipeline-test",
            string providerHint = "simulated")
        {
            return new AiRuntimeScaleOutRequestRecord
            {
                RequestId = requestId,
                ControlPlaneId = "cp-test",
                SharedRunId = sharedRunId,
                ExecutionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create(
                    contextKey: $"unit-test:{tenantId}:context",
                    project: "unit-test",
                    userId: "unit-test",
                    tenantId: tenantId,
                    tenantGroupId: tenantGroupId,
                    currentNamespace: "unit-test"),
                TenantId = tenantId,
                TenantGroupId = tenantGroupId,
                PipelineKey = pipelineKey,
                Status = AiRuntimeScaleOutRequestStatus.Pending,
                Reason = "No runtime capacity was available for admission.",
                VisibleInstanceCount = 0,
                AvailableInstanceCount = 0,
                CurrentInstanceCount = 0,
                MaxInstanceCount = 3,
                RequestedTargetInstanceCount = 1,
                ProviderHint = providerHint,
                RequestedBy = "unit-test",
                Source = "unit-test",
                CorrelationId = "correlation-test",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["test"] = "true"
                }
            };
        }

        /// <summary>
        /// Test runtime instance registry used by the HTTP scale-out provisioner.
        /// </summary>
        private sealed class TestRuntimeInstanceRegistry : IAiRuntimeInstanceRegistry
        {
            private readonly Dictionary<string, AiRuntimeInstanceSnapshot> snapshots =
                new(StringComparer.Ordinal);

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot> RegisterAsync(
                AiRuntimeInstanceRegistration registration,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(registration);

                cancellationToken.ThrowIfCancellationRequested();

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

                this.snapshots[registration.RuntimeInstanceId] =
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
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult<AiRuntimeInstanceSnapshot?>(
                    null);
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> GetAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

                cancellationToken.ThrowIfCancellationRequested();

                this.snapshots.TryGetValue(
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
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<AiRuntimeInstanceSnapshot> result =
                    this.snapshots
                        .Values
                        .ToArray();

                return Task.FromResult(
                    result);
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> MarkDrainingAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult<AiRuntimeInstanceSnapshot?>(
                    null);
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> UnregisterAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult<AiRuntimeInstanceSnapshot?>(
                    null);
            }

            public Task<AiRuntimeInstanceSnapshot?> MarkUnhealthyAsync(string runtimeInstanceId, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult<AiRuntimeInstanceSnapshot?>(
                    null);
            }
        }

        /// <summary>
        /// Test runtime instance capacity store used by the HTTP scale-out provisioner.
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

                cancellationToken.ThrowIfCancellationRequested();

                this.descriptors[descriptor.RuntimeInstanceId] =
                    descriptor;

                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceCapacityDescriptor?> GetAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

                cancellationToken.ThrowIfCancellationRequested();

                this.descriptors.TryGetValue(
                    runtimeInstanceId,
                    out var descriptor);

                return Task.FromResult<AiRuntimeInstanceCapacityDescriptor?>(
                    descriptor);
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<AiRuntimeInstanceCapacityDescriptor>> ListAsync(
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<AiRuntimeInstanceCapacityDescriptor> result =
                    this.descriptors
                        .Values
                        .ToArray();

                return Task.FromResult(
                    result);
            }

            /// <inheritdoc />
            public Task<bool> RemoveAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(
                    this.descriptors.Remove(runtimeInstanceId));
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

        /// <summary>
        /// Provides a fixed scale-out provider selector for watcher tests.
        /// </summary>
        private sealed class TestScaleOutProviderSelector : IAiRuntimeScaleOutProviderSelector
        {
            /// <summary>
            /// The provider invoked by the selector.
            /// </summary>
            private readonly IAiRuntimeScaleOutProvider provider;

            /// <summary>
            /// Initializes a new instance of the <see cref="TestScaleOutProviderSelector" /> class.
            /// </summary>
            /// <param name="provider">The provider to invoke.</param>
            public TestScaleOutProviderSelector(
                IAiRuntimeScaleOutProvider provider)
            {
                this.provider =
                    provider
                    ?? throw new ArgumentNullException(nameof(provider));
            }

            /// <inheritdoc />
            public Task<AiRuntimeScaleOutProviderResult> RequestScaleOutAsync(
                AiRuntimeScaleOutProviderRequest request,
                CancellationToken cancellationToken = default)
            {
                return this.provider
                    .RequestScaleOutAsync(
                        request,
                        cancellationToken);
            }
        }
    }
}