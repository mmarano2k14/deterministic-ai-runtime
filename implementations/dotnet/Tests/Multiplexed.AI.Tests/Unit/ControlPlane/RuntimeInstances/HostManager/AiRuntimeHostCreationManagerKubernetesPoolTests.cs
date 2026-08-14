using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy;
using Multiplexed.AI.Tests.Fixtures;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager
{
    /// <summary>
    /// Validates explicit host-manager selection for the opt-in Kubernetes Runtime Pool mode.
    /// </summary>
    public sealed class AiRuntimeHostCreationManagerKubernetesPoolTests
    {
        /// <summary>
        /// Verifies that KubernetesPool selects only its dedicated registered strategy.
        /// </summary>
        [Fact]
        public async Task StartRuntimeAsync_Should_Select_Only_KubernetesPool_Strategy()
        {
            var kubernetesStrategy =
                new RecordingRuntimeHostCreationStrategy(
                    AiRuntimeHostCreationMode.Kubernetes);

            var kubernetesPoolStrategy =
                new RecordingRuntimeHostCreationStrategy(
                    AiRuntimeHostCreationMode.KubernetesPool);

            var manager =
                new AiRuntimeHostCreationManager(
                    new IAiRuntimeHostCreationStrategy[]
                    {
                        kubernetesStrategy,
                        kubernetesPoolStrategy
                    },
                    NullLogger<AiRuntimeHostCreationManager>.Instance);

            var request =
                CreateRequest(
                    AiRuntimeHostCreationMode.KubernetesPool);

            var result =
                await manager.StartRuntimeAsync(request);

            Assert.True(result.Success);
            Assert.Equal(0, kubernetesStrategy.CallCount);
            Assert.Equal(1, kubernetesPoolStrategy.CallCount);
            Assert.Equal(
                AiRuntimeHostCreationMode.KubernetesPool.ToString(),
                result.Metadata[AiRuntimeHostMetadataKeys.HostCreationMode]);
        }

        /// <summary>
        /// Verifies that the new mode remains unavailable until its real strategy is registered.
        /// </summary>
        [Fact]
        public async Task StartRuntimeAsync_Should_Reject_KubernetesPool_When_NotRegistered()
        {
            var kubernetesStrategy =
                new RecordingRuntimeHostCreationStrategy(
                    AiRuntimeHostCreationMode.Kubernetes);

            var manager =
                new AiRuntimeHostCreationManager(
                    new IAiRuntimeHostCreationStrategy[]
                    {
                        kubernetesStrategy
                    },
                    NullLogger<AiRuntimeHostCreationManager>.Instance);

            var result =
                await manager.StartRuntimeAsync(
                    CreateRequest(
                        AiRuntimeHostCreationMode.KubernetesPool));

            Assert.False(result.Success);
            Assert.Equal(0, kubernetesStrategy.CallCount);
            Assert.Equal(
                "runtime-host-creation-mode-not-registered:KubernetesPool",
                result.FailureReason);
        }

        /// <summary>
        /// Creates one runtime host request for host-manager strategy selection.
        /// </summary>
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
                PoolId = "pool-shared-01",
                RuntimeInstanceId = "tenant-a-runtime-001",
                ProviderName = "grpc",
                TransportName = "grpc",
                TransportEndpoint = "http://127.0.0.1:5001",
                HostCreationMode = hostCreationMode
            };
        }

        /// <summary>
        /// Records strategy selection without materializing Kubernetes resources.
        /// </summary>
        private sealed class RecordingRuntimeHostCreationStrategy :
            IAiRuntimeHostCreationStrategy
        {
            /// <summary>
            /// Initializes a new recording strategy.
            /// </summary>
            public RecordingRuntimeHostCreationStrategy(
                AiRuntimeHostCreationMode mode)
            {
                this.Mode = mode;
            }

            /// <inheritdoc />
            public AiRuntimeHostCreationMode Mode { get; }

            /// <summary>
            /// Gets the number of received calls.
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
                            [AiRuntimeHostMetadataKeys.HostProvider] =
                                this.Mode.ToString(),
                            [AiRuntimeHostMetadataKeys.HostCreationMode] =
                                this.Mode.ToString(),
                            [AiRuntimeHostMetadataKeys.HostCreationStrategy] =
                                nameof(RecordingRuntimeHostCreationStrategy)
                        }));
            }
        }
    }
}
