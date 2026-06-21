using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy
{
    /// <summary>
    /// Starts or attaches a runtime host for a specific physical host creation mode.
    /// </summary>
    public interface IAiRuntimeHostCreationStrategy
    {
        /// <summary>
        /// Gets the supported host creation mode.
        /// </summary>
        AiRuntimeHostCreationMode Mode { get; }

        /// <summary>
        /// Starts or attaches the runtime host.
        /// </summary>
        Task<AiRuntimeHostStartResult> StartAsync(
            AiRuntimeHostStartRequest request,
            CancellationToken cancellationToken = default);
    }
}