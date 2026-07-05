using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Readiness;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client;
using Multiplexed.AI.Tests.Fixtures;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager
{
    /// <summary>
    /// Provides unit tests for <see cref="KubernetesAiRuntimeHostCreationStrategy"/>.
    /// </summary>
    public sealed class KubernetesAiRuntimeHostCreationStrategyTests
    {
        /// <summary>
        /// Verifies that the Kubernetes host creation strategy exposes the Kubernetes host creation mode.
        /// </summary>
        [Fact]
        public void Mode_Should_Return_Kubernetes()
        {
            var strategy = CreateStrategy();

            Assert.Equal(AiRuntimeHostCreationMode.Kubernetes, strategy.Mode);
        }

        /// <summary>
        /// Verifies that the Kubernetes host creation strategy rejects when Kubernetes host creation is disabled.
        /// </summary>
        [Fact]
        public async Task StartAsync_Should_Reject_When_Kubernetes_Host_Creation_Is_Disabled()
        {
            var strategy =
                CreateStrategy(
                    options: new AiKubernetesRuntimeHostOptions
                    {
                        Enabled = false,
                        RuntimeImage = "multiplexed-ai-runtime:test"
                    });

            var request = CreateRequest();

            var result = await strategy.StartAsync(request);

            Assert.False(result.Success);
            Assert.False(result.Retryable);
            Assert.Equal("kubernetes-runtime-host-creation-disabled", result.FailureReason);
            Assert.Equal("tenant-a-runtime-001", result.RuntimeInstanceId);
            Assert.Equal("grpc", result.ProviderName);
            Assert.Equal("grpc", result.TransportName);
            Assert.Equal("kubernetes", result.Metadata[AiRuntimeHostMetadataKeys.HostProvider]);
            Assert.Equal("Kubernetes", result.Metadata[AiRuntimeHostMetadataKeys.HostCreationMode]);
            Assert.Equal(nameof(KubernetesAiRuntimeHostCreationStrategy), result.Metadata[AiRuntimeHostMetadataKeys.HostCreationStrategy]);
            Assert.NotEqual("kubernetes", result.ProviderName);
        }

        /// <summary>
        /// Verifies that the Kubernetes host creation strategy rejects when no Kubernetes namespace is configured.
        /// </summary>
        [Fact]
        public async Task StartAsync_Should_Reject_When_Kubernetes_Namespace_Is_Missing()
        {
            var client = new FakeAiKubernetesRuntimeHostClient();

            var strategy =
                CreateStrategy(
                    options: new AiKubernetesRuntimeHostOptions
                    {
                        Enabled = true,
                        Namespace = string.Empty,
                        RuntimeImage = "multiplexed-ai-runtime:test",
                        ContainerName = "runtime-instance",
                        ContainerPort = 8080,
                        PodNamePrefix = "runtime",
                        TransportName = "grpc",
                        DeleteResourcesOnFailure = true
                    },
                    client: client);

            var request = CreateRequest();

            var result = await strategy.StartAsync(request);

            Assert.False(result.Success);
            Assert.False(result.Retryable);
            Assert.Equal("kubernetes-runtime-namespace-missing", result.FailureReason);
            Assert.Equal(0, client.CreateCallCount);
            Assert.Equal(0, client.ReadinessCallCount);
            Assert.Equal(0, client.DeleteCallCount);
        }

        /// <summary>
        /// Verifies that the Kubernetes host creation strategy rejects when no runtime image is configured.
        /// </summary>
        [Fact]
        public async Task StartAsync_Should_Reject_When_Runtime_Image_Is_Missing()
        {
            var strategy =
                CreateStrategy(
                    options: new AiKubernetesRuntimeHostOptions
                    {
                        Enabled = true,
                        RuntimeImage = string.Empty
                    });

            var request = CreateRequest();

            var result = await strategy.StartAsync(request);

            Assert.False(result.Success);
            Assert.False(result.Retryable);
            Assert.Equal("kubernetes-runtime-image-missing", result.FailureReason);
            Assert.Equal("grpc", result.ProviderName);
            Assert.Equal("grpc", result.TransportName);
            Assert.NotEqual("kubernetes", result.ProviderName);
        }

        /// <summary>
        /// Verifies that the Kubernetes host creation strategy creates a host, waits for Kubernetes readiness, waits for runtime readiness, and returns started.
        /// </summary>
        [Fact]
        public async Task StartAsync_Should_Return_Started_When_Kubernetes_And_Runtime_Readiness_Succeed()
        {
            var client = new FakeAiKubernetesRuntimeHostClient();
            var readinessWaiter = new FakeRuntimeInstanceReadinessWaiter();
            var strategy = CreateStrategy(client: client, readinessWaiter: readinessWaiter);
            var request = CreateRequest();

            var result = await strategy.StartAsync(request);

            Assert.True(result.Success);
            Assert.False(result.Retryable);
            Assert.Equal("tenant-a-runtime-001", result.RuntimeInstanceId);
            Assert.Equal("grpc", result.ProviderName);
            Assert.Equal("grpc", result.TransportName);
            Assert.Equal("http://127.0.0.1:5001", result.TransportEndpoint);
            Assert.Equal(1, client.CreateCallCount);
            Assert.Equal(1, client.ReadinessCallCount);
            Assert.Equal(0, client.DeleteCallCount);
            Assert.Equal(1, readinessWaiter.CallCount);
            Assert.Equal("kubernetes", result.Metadata[AiRuntimeHostMetadataKeys.HostProvider]);
            Assert.Equal("Kubernetes", result.Metadata[AiRuntimeHostMetadataKeys.HostCreationMode]);
            Assert.Equal(nameof(KubernetesAiRuntimeHostCreationStrategy), result.Metadata[AiRuntimeHostMetadataKeys.HostCreationStrategy]);
            Assert.NotEqual("kubernetes", result.ProviderName);
        }

        /// <summary>
        /// Verifies that the Kubernetes host creation strategy rejects when Kubernetes resource creation fails.
        /// </summary>
        [Fact]
        public async Task StartAsync_Should_Reject_When_Kubernetes_Create_Fails()
        {
            var client =
                new FakeAiKubernetesRuntimeHostClient
                {
                    FailCreate = true
                };

            var readinessWaiter = new FakeRuntimeInstanceReadinessWaiter();
            var strategy = CreateStrategy(client: client, readinessWaiter: readinessWaiter);
            var request = CreateRequest();

            var result = await strategy.StartAsync(request);

            Assert.False(result.Success);
            Assert.True(result.Retryable);
            Assert.Equal("fake-kubernetes-create-failed", result.FailureReason);
            Assert.Equal(1, client.CreateCallCount);
            Assert.Equal(0, client.ReadinessCallCount);
            Assert.Equal(0, client.DeleteCallCount);
            Assert.Equal(0, readinessWaiter.CallCount);
            Assert.Equal("grpc", result.ProviderName);
            Assert.NotEqual("kubernetes", result.ProviderName);
        }

        /// <summary>
        /// Verifies that the Kubernetes host creation strategy deletes resources when Kubernetes readiness fails.
        /// </summary>
        [Fact]
        public async Task StartAsync_Should_Delete_And_Reject_When_Kubernetes_Readiness_Fails()
        {
            var client =
                new FakeAiKubernetesRuntimeHostClient
                {
                    FailReadiness = true,
                    ReadinessTimedOut = true
                };

            var readinessWaiter = new FakeRuntimeInstanceReadinessWaiter();
            var strategy = CreateStrategy(client: client, readinessWaiter: readinessWaiter);
            var request = CreateRequest();

            var result = await strategy.StartAsync(request);

            Assert.False(result.Success);
            Assert.True(result.Retryable);
            Assert.Equal("fake-kubernetes-readiness-failed", result.FailureReason);
            Assert.Equal(1, client.CreateCallCount);
            Assert.Equal(1, client.ReadinessCallCount);
            Assert.Equal(1, client.DeleteCallCount);
            Assert.Equal(0, readinessWaiter.CallCount);
            Assert.Equal("grpc", result.ProviderName);
            Assert.NotEqual("kubernetes", result.ProviderName);
        }

        /// <summary>
        /// Verifies that the Kubernetes host creation strategy deletes resources when runtime readiness fails.
        /// </summary>
        [Fact]
        public async Task StartAsync_Should_Delete_And_Reject_When_Runtime_Readiness_Fails()
        {
            var client = new FakeAiKubernetesRuntimeHostClient();
            var readinessWaiter =
                new FakeRuntimeInstanceReadinessWaiter
                {
                    Success = false,
                    FailureReason = "runtime-readiness-capacity-missing",
                    TimedOut = true
                };

            var strategy = CreateStrategy(client: client, readinessWaiter: readinessWaiter);
            var request = CreateRequest();

            var result = await strategy.StartAsync(request);

            Assert.False(result.Success);
            Assert.True(result.Retryable);
            Assert.Equal("runtime-readiness-capacity-missing", result.FailureReason);
            Assert.Equal(1, client.CreateCallCount);
            Assert.Equal(1, client.ReadinessCallCount);
            Assert.Equal(1, client.DeleteCallCount);
            Assert.Equal(1, readinessWaiter.CallCount);
            Assert.Equal("grpc", result.ProviderName);
            Assert.NotEqual("kubernetes", result.ProviderName);
        }

        /// <summary>
        /// Verifies that the Kubernetes host creation strategy rejects when no container name is configured.
        /// </summary>
        [Fact]
        public async Task StartAsync_Should_Reject_When_Container_Name_Is_Missing()
        {
            var client = new FakeAiKubernetesRuntimeHostClient();

            var strategy =
                CreateStrategy(
                    options: new AiKubernetesRuntimeHostOptions
                    {
                        Enabled = true,
                        Namespace = "ai-runtime",
                        RuntimeImage = "multiplexed-ai-runtime:test",
                        ContainerName = string.Empty,
                        ContainerPort = 8080,
                        PodNamePrefix = "runtime",
                        TransportName = "grpc",
                        DeleteResourcesOnFailure = true
                    },
                    client: client);

            var request = CreateRequest();

            var result = await strategy.StartAsync(request);

            Assert.False(result.Success);
            Assert.False(result.Retryable);
            Assert.Equal("kubernetes-runtime-container-name-missing", result.FailureReason);
            Assert.Equal(0, client.CreateCallCount);
            Assert.Equal(0, client.ReadinessCallCount);
            Assert.Equal(0, client.DeleteCallCount);
        }

        /// <summary>
        /// Verifies that Kubernetes host creation can skip runtime registry readiness when configured for fake lifecycle tests.
        /// </summary>
        /// <returns>A task that completes when the proof has finished.</returns>
        [Fact]
        public async Task StartAsync_Should_Succeed_Without_Runtime_Readiness_When_RequireRuntimeReadiness_Is_False()
        {
            var readinessWaiter =
                new RecordingRuntimeInstanceReadinessWaiter();

            var strategy =
               CreateStrategy(
                   options: new AiKubernetesRuntimeHostOptions
                   {
                       Enabled = true,
                       Namespace = "ai-runtime",
                       RuntimeImage = "multiplexed-ai-runtime:test",
                       ContainerName = "runtime-instance",
                       ContainerPort = 8080,
                       PodNamePrefix = "runtime",
                       TransportName = "grpc",
                       DeleteResourcesOnFailure = true,
                       ClientMode = AiKubernetesRuntimeHostClientMode.Fake,
                       RequireRuntimeReadiness = false
                   },
                   readinessWaiter: readinessWaiter);

            var request =
                CreateStartRequest();

            var result =
                await strategy
                    .StartAsync(request)
                    .ConfigureAwait(false);

            Assert.True(result.Success, result.FailureReason);
            Assert.False(readinessWaiter.WasCalled);
            Assert.Equal(request.ProviderName, result.ProviderName);
            Assert.Equal(request.TransportName, result.TransportName);
            Assert.Equal(AiRuntimeHostProviderNames.Kubernetes, result.Metadata[AiRuntimeHostMetadataKeys.HostProvider]);
        }

        /// <summary>
        /// Creates a Kubernetes host creation strategy for tests.
        /// </summary>
        /// <param name="options">The optional Kubernetes host options.</param>
        /// <param name="client">The optional Kubernetes host client.</param>
        /// <param name="readinessWaiter">The optional runtime readiness waiter.</param>
        /// <returns>The created strategy.</returns>
        private static KubernetesAiRuntimeHostCreationStrategy CreateStrategy(
            AiKubernetesRuntimeHostOptions? options = null,
            IAiKubernetesRuntimeHostClient? client = null,
            IAiRuntimeInstanceReadinessWaiter? readinessWaiter = null)
        {
            var effectiveOptions =
                options ??
                new AiKubernetesRuntimeHostOptions
                {
                    Enabled = true,
                    Namespace = "ai-runtime",
                    RuntimeImage = "multiplexed-ai-runtime:test",
                    ContainerName = "runtime-instance",
                    ContainerPort = 8080,
                    PodNamePrefix = "runtime",
                    TransportName = "grpc",
                    DeleteResourcesOnFailure = true
                };

            return new KubernetesAiRuntimeHostCreationStrategy(
                Options.Create(effectiveOptions),
                new AiKubernetesRuntimePodSpecBuilder(
                    effectiveOptions,
                    new AiKubernetesRuntimePodMetadataBuilder(effectiveOptions)),
                client ?? new FakeAiKubernetesRuntimeHostClient(),
                readinessWaiter ?? new FakeRuntimeInstanceReadinessWaiter());
        }

        private static AiRuntimeHostStartRequest CreateStartRequest()
        {
            return new AiRuntimeHostStartRequest
            {
                ControlPlaneId = "test-control-plane",
                RuntimeInstanceId = "test-runtime-instance-1",
                ProviderName = "grpc",
                TransportName = "grpc",
                TransportEndpoint = "http://127.0.0.1:50051",
                ExecutionContextSnapshot = new ExecutionContextSnapshot
                {
                    ContextKey = "test-context",
                    Project = "unit-tests",
                    UserId = "unit-test",
                    TenantId = "tenant-a",
                    TenantGroupId = "tenant-a-group",
                    CurrentNamespace = "tenant-a",
                    Namespaces = AiExecutionContextSnapshotTestFactory.Create().Namespaces,
                    InFlightCount = 0,
                    TtlSeconds = 300,
                    CreatedAtUtc = DateTime.UtcNow
                },
                Metadata = new Dictionary<string, string>
                {
                    ["provider.name"] = "grpc",
                    ["transport.name"] = "grpc",
                    ["host.provider"] = AiRuntimeHostProviderNames.Kubernetes,
                    ["host.creation.mode"] = AiRuntimeHostCreationMode.Kubernetes.ToString()
                }
            };
        }

        /// <summary>
        /// Creates a runtime host start request for Kubernetes strategy tests.
        /// </summary>
        /// <returns>The runtime host start request.</returns>
        private static AiRuntimeHostStartRequest CreateRequest()
        {
            return new AiRuntimeHostStartRequest
            {
                ControlPlaneId = "control-plane-a",
                ExecutionContextSnapshot =
                    AiExecutionContextSnapshotTestFactory.Create(
                        tenantId: "tenant-a",
                        tenantGroupId: "tenant-group-a"),
                RuntimeInstanceId = "tenant-a-runtime-001",
                ProviderName = "grpc",
                TransportName = "grpc",
                TransportEndpoint = "http://127.0.0.1:5001",
                HostCreationMode = AiRuntimeHostCreationMode.Kubernetes
            };
        }

        private sealed class RecordingRuntimeInstanceReadinessWaiter : IAiRuntimeInstanceReadinessWaiter
        {
            public bool WasCalled { get; private set; }

            public Task<AiRuntimeInstanceReadinessResult> WaitUntilReadyAsync(
                AiRuntimeInstanceReadinessRequest request,
                CancellationToken cancellationToken = default)
            {
                this.WasCalled = true;

                return Task.FromResult(
                    new AiRuntimeInstanceReadinessResult
                    {
                        Success = true,
                        ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                        RuntimeInstanceId = request.RuntimeInstanceId,
                        ProviderName = request.ProviderName,
                        TransportName = request.TransportName,
                        TransportEndpoint = request.TransportEndpoint,
                        FailureReason = null,
                        TimedOut = false
                    });
            }
        }


        /// <summary>
        /// Provides a fake runtime instance readiness waiter for Kubernetes strategy tests.
        /// </summary>
        private sealed class FakeRuntimeInstanceReadinessWaiter : IAiRuntimeInstanceReadinessWaiter
        {
            /// <summary>
            /// Gets or sets a value indicating whether readiness should succeed.
            /// </summary>
            public bool Success { get; set; } = true;

            /// <summary>
            /// Gets or sets the readiness failure reason.
            /// </summary>
            public string? FailureReason { get; set; }

            /// <summary>
            /// Gets or sets a value indicating whether readiness timed out.
            /// </summary>
            public bool TimedOut { get; set; }

            /// <summary>
            /// Gets the number of readiness calls.
            /// </summary>
            public int CallCount { get; private set; }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceReadinessResult> WaitUntilReadyAsync(
                AiRuntimeInstanceReadinessRequest request,
                CancellationToken cancellationToken = default)
            {
                this.CallCount++;

                return Task.FromResult(
                    new AiRuntimeInstanceReadinessResult
                    {
                        Success = this.Success,
                        ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                        RuntimeInstanceId = request.RuntimeInstanceId,
                        ProviderName = request.ProviderName,
                        TransportName = request.TransportName,
                        TransportEndpoint = request.TransportEndpoint,
                        FailureReason = this.FailureReason,
                        TimedOut = this.TimedOut
                    });
            }
        }
    }
}