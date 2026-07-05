using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes;
using Multiplexed.AI.Tests.Fixtures;
using System.Collections.Generic;
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
            var strategy = new KubernetesAiRuntimeHostCreationStrategy();

            Assert.Equal(AiRuntimeHostCreationMode.Kubernetes, strategy.Mode);
        }

        /// <summary>
        /// Verifies that the Kubernetes host creation strategy rejects while the real Kubernetes implementation is not available.
        /// </summary>
        [Fact]
        public async Task StartAsync_Should_Reject_When_Kubernetes_Implementation_Is_Not_Available()
        {
            var strategy = new KubernetesAiRuntimeHostCreationStrategy();

            var request =
                new AiRuntimeHostStartRequest
                {
                    ExecutionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create(),
                    RuntimeInstanceId = "tenant-a-runtime-001",
                    ProviderName = "grpc",
                    TransportName = "grpc",
                    TransportEndpoint = "http://127.0.0.1:5001",
                    HostCreationMode = AiRuntimeHostCreationMode.Kubernetes
                };

            var result = await strategy.StartAsync(request);

            Assert.False(result.Success);
            Assert.False(result.Retryable);
            Assert.Equal("tenant-a-runtime-001", result.RuntimeInstanceId);
            Assert.Equal("grpc", result.ProviderName);
            Assert.Equal("grpc", result.TransportName);
            Assert.Equal("http://127.0.0.1:5001", result.TransportEndpoint);
            Assert.Equal("kubernetes-runtime-host-creation-not-implemented", result.FailureReason);
            Assert.Equal("kubernetes", result.Metadata[AiRuntimeHostMetadataKeys.HostProvider]);
            Assert.Equal("Kubernetes", result.Metadata[AiRuntimeHostMetadataKeys.HostCreationMode]);
            Assert.Equal(nameof(KubernetesAiRuntimeHostCreationStrategy), result.Metadata[AiRuntimeHostMetadataKeys.HostCreationStrategy]);
            Assert.NotEqual("kubernetes", result.ProviderName);
        }

        
        
    }
}