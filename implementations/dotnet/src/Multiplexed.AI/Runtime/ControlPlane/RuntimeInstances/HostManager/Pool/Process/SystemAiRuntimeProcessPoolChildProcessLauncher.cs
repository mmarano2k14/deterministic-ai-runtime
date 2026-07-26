using System;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Starts real operating-system children from dynamically created process options.
    /// </summary>
    public sealed class SystemAiRuntimeProcessPoolChildProcessLauncher :
        IAiRuntimeProcessPoolChildProcessLauncher
    {
        /// <inheritdoc />
        public Task<IAiRuntimeProcessPoolChild> StartAsync(
            AiRuntimeProcessPoolChildStartRequest request,
            AiRuntimeProcessPoolChildProcessOptions options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(options);

            var factory =
                new SystemAiRuntimeProcessPoolChildFactory(options);

            return factory.StartAsync(request, cancellationToken);
        }
    }
}
