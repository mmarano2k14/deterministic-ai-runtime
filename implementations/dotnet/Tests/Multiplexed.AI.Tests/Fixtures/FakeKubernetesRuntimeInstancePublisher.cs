using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Publisher;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Tests.Fixtures
{
    /// <summary>
    /// Fake Kubernetes runtime instance publisher used by tests that do not need registry or capacity publication.
    /// </summary>
    public sealed class FakeKubernetesRuntimeInstancePublisher : IAiKubernetesRuntimeInstancePublisher
    {
        /// <inheritdoc />
        public Task PublishAsync(
            AiRuntimeHostStartRequest request,
            AiRuntimeHostStartResult result,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task UnpublishAsync(
            string runtimeInstanceId,
            string reason,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            cancellationToken.ThrowIfCancellationRequested();

            return Task.CompletedTask;
        }
    }
}