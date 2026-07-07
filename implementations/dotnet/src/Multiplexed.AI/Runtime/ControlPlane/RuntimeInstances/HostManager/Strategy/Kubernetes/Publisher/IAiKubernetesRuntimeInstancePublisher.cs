using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Publisher
{
    /// <summary>
    /// Publishes a Kubernetes-backed runtime host as a routable runtime instance.
    /// </summary>
    public interface IAiKubernetesRuntimeInstancePublisher
    {
        /// <summary>
        /// Publishes the runtime instance registration and capacity for a Kubernetes-backed runtime host.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <param name="result">The successful runtime host start result.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the asynchronous publication operation.</returns>
        Task PublishAsync(
            AiRuntimeHostStartRequest request,
            AiRuntimeHostStartResult result,
            CancellationToken cancellationToken = default);
    }
}