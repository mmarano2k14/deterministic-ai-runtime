using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Publisher;
using System;
using System.Collections.Generic;
using System.Text;

namespace Multiplexed.AI.Tests.Fixtures
{
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
    }
}
