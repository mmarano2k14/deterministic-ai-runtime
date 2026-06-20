using System;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager
{
    /// <summary>
    /// Provides a disabled runtime host manager implementation.
    /// </summary>
    /// <remarks>
    /// This implementation is intentionally safe by default. It allows the host manager boundary to be registered
    /// without starting any runtime process until a real provider-specific implementation is configured.
    /// </remarks>
    public sealed class NoopAiRuntimeHostManager : IAiRuntimeHostManager
    {
        /// <inheritdoc />
        public Task<AiRuntimeHostStartResult> StartRuntimeAsync(
            AiRuntimeHostStartRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            cancellationToken.ThrowIfCancellationRequested();

            var result = new AiRuntimeHostStartResult
            {
                Success = false,
                ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                RuntimeInstanceId = request.RuntimeInstanceId,
                ProviderName = request.ProviderName,
                TransportName = request.TransportName,
                TransportEndpoint = request.TransportEndpoint,
                FailureReason = "runtime-host-manager-disabled",
                Retryable = false
            };

            return Task.FromResult(result);
        }
    }
}