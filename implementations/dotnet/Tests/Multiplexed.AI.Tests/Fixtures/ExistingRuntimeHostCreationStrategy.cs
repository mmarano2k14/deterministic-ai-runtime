using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy;
using System;
using System.Collections.Generic;
using System.Text;

namespace Multiplexed.AI.Tests.Fixtures
{
    /// <summary>
    /// Provides an existing host creation strategy used to validate additive Kubernetes registration.
    /// </summary>
    public sealed class ExistingRuntimeHostCreationStrategy : IAiRuntimeHostCreationStrategy
    {
        /// <inheritdoc />
        public AiRuntimeHostCreationMode Mode => AiRuntimeHostCreationMode.Fixture;

        /// <inheritdoc />
        public Task<AiRuntimeHostStartResult> StartAsync(
           AiRuntimeHostStartRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                AiRuntimeHostStartResult.Rejected(
                    request.ExecutionContextSnapshot,
                    request.RuntimeInstanceId,
                    request.ProviderName,
                    request.TransportName,
                    request.TransportEndpoint,
                    "existing-strategy-not-used"));
        }
    }
}
