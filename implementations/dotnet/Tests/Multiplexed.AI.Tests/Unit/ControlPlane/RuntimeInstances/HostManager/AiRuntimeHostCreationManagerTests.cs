using Microsoft.Extensions.Logging.Abstractions;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy;
using Multiplexed.AI.Tests.Fixtures;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager
{
    /// <summary>
    /// Provides unit tests for <see cref="AiRuntimeHostCreationManager"/>.
    /// </summary>
    public sealed class AiRuntimeHostCreationManagerTests
    {
        /// <summary>
        /// Verifies that the host creation manager selects the Kubernetes strategy when Kubernetes host creation mode is requested.
        /// </summary>
        [Fact]
        public async Task StartRuntimeAsync_Should_Select_Kubernetes_Strategy_When_HostCreationMode_Is_Kubernetes()
        {
            var processStrategy =
                new RecordingRuntimeHostCreationStrategy(
                    AiRuntimeHostCreationMode.Process);

            var kubernetesStrategy =
                new RecordingRuntimeHostCreationStrategy(
                    AiRuntimeHostCreationMode.Kubernetes);

            var manager =
                new AiRuntimeHostCreationManager(
                    new IAiRuntimeHostCreationStrategy[]
                    {
                        processStrategy,
                        kubernetesStrategy
                    },
                    NullLogger<AiRuntimeHostCreationManager>.Instance);

            var request =
                CreateRequest(
                    hostCreationMode: AiRuntimeHostCreationMode.Kubernetes);

            var result =
                await manager.StartRuntimeAsync(request);

            Assert.True(result.Success);
            Assert.Equal(0, processStrategy.CallCount);
            Assert.Equal(1, kubernetesStrategy.CallCount);
            Assert.Equal("grpc", result.ProviderName);
            Assert.Equal("grpc", result.TransportName);
            Assert.Equal(AiRuntimeHostCreationMode.Kubernetes.ToString(), result.Metadata[AiRuntimeHostMetadataKeys.HostCreationMode]);
            Assert.Equal(nameof(RecordingRuntimeHostCreationStrategy), result.Metadata[AiRuntimeHostMetadataKeys.HostCreationStrategy]);
            Assert.NotEqual("kubernetes", result.ProviderName);
        }

        /// <summary>
        /// Verifies that the host creation manager selects the process strategy when process host creation mode is requested.
        /// </summary>
        [Fact]
        public async Task StartRuntimeAsync_Should_Select_Process_Strategy_When_HostCreationMode_Is_Process()
        {
            var processStrategy =
                new RecordingRuntimeHostCreationStrategy(
                    AiRuntimeHostCreationMode.Process);

            var kubernetesStrategy =
                new RecordingRuntimeHostCreationStrategy(
                    AiRuntimeHostCreationMode.Kubernetes);

            var manager =
                new AiRuntimeHostCreationManager(
                    new IAiRuntimeHostCreationStrategy[]
                    {
                        processStrategy,
                        kubernetesStrategy
                    },
                    NullLogger<AiRuntimeHostCreationManager>.Instance);

            var request =
                CreateRequest(
                    hostCreationMode: AiRuntimeHostCreationMode.Process);

            var result =
                await manager.StartRuntimeAsync(request);

            Assert.True(result.Success);
            Assert.Equal(1, processStrategy.CallCount);
            Assert.Equal(0, kubernetesStrategy.CallCount);
            Assert.Equal("grpc", result.ProviderName);
            Assert.Equal("grpc", result.TransportName);
            Assert.Equal(AiRuntimeHostCreationMode.Process.ToString(), result.Metadata[AiRuntimeHostMetadataKeys.HostCreationMode]);
            Assert.Equal(nameof(RecordingRuntimeHostCreationStrategy), result.Metadata[AiRuntimeHostMetadataKeys.HostCreationStrategy]);
            Assert.NotEqual("kubernetes", result.ProviderName);
        }

        /// <summary>
        /// Verifies that the host creation manager rejects when no strategy is registered for the requested host creation mode.
        /// </summary>
        [Fact]
        public async Task StartRuntimeAsync_Should_Reject_When_HostCreationMode_Is_Not_Registered()
        {
            var processStrategy =
                new RecordingRuntimeHostCreationStrategy(
                    AiRuntimeHostCreationMode.Process);

            var manager =
                new AiRuntimeHostCreationManager(
                    new IAiRuntimeHostCreationStrategy[]
                    {
                        processStrategy
                    },
                    NullLogger<AiRuntimeHostCreationManager>.Instance);

            var request =
                CreateRequest(
                    hostCreationMode: AiRuntimeHostCreationMode.Kubernetes);

            var result =
                await manager.StartRuntimeAsync(request);

            Assert.False(result.Success);
            Assert.Equal(1, processStrategy.CallCount == 0 ? 1 : 0);
            Assert.Equal("grpc", result.ProviderName);
            Assert.Equal("grpc", result.TransportName);
            Assert.Equal("runtime-host-creation-mode-not-registered:Kubernetes", result.FailureReason);
            Assert.NotEqual("kubernetes", result.ProviderName);
        }

        /// <summary>
        /// Creates a runtime host start request.
        /// </summary>
        /// <param name="hostCreationMode">The requested host creation mode.</param>
        /// <returns>The runtime host start request.</returns>
        private static AiRuntimeHostStartRequest CreateRequest(
            AiRuntimeHostCreationMode hostCreationMode)
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
                HostCreationMode = hostCreationMode
            };
        }

        /// <summary>
        /// Provides a recording runtime host creation strategy for host creation manager tests.
        /// </summary>
        private sealed class RecordingRuntimeHostCreationStrategy : IAiRuntimeHostCreationStrategy
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="RecordingRuntimeHostCreationStrategy"/> class.
            /// </summary>
            /// <param name="mode">The host creation mode handled by the strategy.</param>
            public RecordingRuntimeHostCreationStrategy(
                AiRuntimeHostCreationMode mode)
            {
                this.Mode = mode;
            }

            /// <inheritdoc />
            public AiRuntimeHostCreationMode Mode { get; }

            /// <summary>
            /// Gets the number of calls received by the strategy.
            /// </summary>
            public int CallCount { get; private set; }

            /// <inheritdoc />
            public Task<AiRuntimeHostStartResult> StartAsync(
                AiRuntimeHostStartRequest request,
                CancellationToken cancellationToken = default)
            {
                this.CallCount++;

                return Task.FromResult(
                    AiRuntimeHostStartResult.Started(
                        request.ExecutionContextSnapshot,
                        request.RuntimeInstanceId,
                        request.ProviderName,
                        request.TransportName,
                        request.TransportEndpoint,
                        new Dictionary<string, string>
                        {
                            [AiRuntimeHostMetadataKeys.HostProvider] = this.Mode.ToString(),
                            [AiRuntimeHostMetadataKeys.HostCreationMode] = this.Mode.ToString(),
                            [AiRuntimeHostMetadataKeys.HostCreationStrategy] = nameof(RecordingRuntimeHostCreationStrategy)
                        }));
            }
        }
    }
}