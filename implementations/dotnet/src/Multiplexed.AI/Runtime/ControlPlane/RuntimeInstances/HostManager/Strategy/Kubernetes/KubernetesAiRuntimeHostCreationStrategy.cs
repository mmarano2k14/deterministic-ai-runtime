using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes
{
    /// <summary>
    /// Provides the Kubernetes runtime host creation strategy skeleton.
    /// </summary>
    /// <remarks>
    /// This strategy represents Kubernetes as a runtime host lifecycle provider.
    /// It does not dispatch work and does not execute DAG steps directly.
    /// Runtime command dispatch must continue to use the configured transport provider, such as HTTP or gRPC.
    /// </remarks>
    public sealed class KubernetesAiRuntimeHostCreationStrategy : IAiRuntimeHostCreationStrategy
    {
        /// <inheritdoc />
        public AiRuntimeHostCreationMode Mode => AiRuntimeHostCreationMode.Kubernetes;

        /// <inheritdoc />
        public Task<AiRuntimeHostStartResult> StartAsync(
            AiRuntimeHostStartRequest request,
            CancellationToken cancellationToken = default)
        {
            var metadata =
                new Dictionary<string, string>
                {
                    [AiRuntimeHostMetadataKeys.HostProvider] = AiRuntimeHostProviderNames.Kubernetes,
                    [AiRuntimeHostMetadataKeys.HostCreationMode] = AiRuntimeHostCreationMode.Kubernetes.ToString(),
                    [AiRuntimeHostMetadataKeys.HostCreationStrategy] = nameof(KubernetesAiRuntimeHostCreationStrategy)
                };

            var result =
                AiRuntimeHostStartResult.Rejected(
                    request.ExecutionContextSnapshot,
                    request.RuntimeInstanceId,
                    request.ProviderName,
                    request.TransportName,
                    request.TransportEndpoint,
                    "kubernetes-runtime-host-creation-not-implemented",
                    retryable: false,
                    metadata: metadata);

            return Task.FromResult(result);
        }
    }
}