using System;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Identity;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Identity
{
    /// <summary>
    /// Provides a unique runtime host identity for the current process.
    /// </summary>
    public sealed class AiRuntimeHostIdentity : IAiRuntimeHostIdentity
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeHostIdentity"/> class.
        /// </summary>
        public AiRuntimeHostIdentity()
        {
            HostId =
                ResolveConfiguredHostId()
                ?? $"host-{Guid.NewGuid():N}";
        }

        /// <inheritdoc />
        public string HostId { get; }

        private static string? ResolveConfiguredHostId()
        {
            var configuredHostId =
                System.Environment.GetEnvironmentVariable("AiRuntimeHostIdentity__HostId")
                ?? System.Environment.GetEnvironmentVariable("AiRuntimeHostIdentity__RuntimeHostId")
                ?? System.Environment.GetEnvironmentVariable("AiControlPlaneHostIdentity__ControlPlaneHostId")
                ?? System.Environment.GetEnvironmentVariable("AiMcpHost__ControlPlaneHostId")
                ?? System.Environment.GetEnvironmentVariable("AiLocalRuntimeInstancePool__ControlPlaneHostId")
                ?? System.Environment.GetEnvironmentVariable("AiRuntimeInstanceRegistration__ControlPlaneHostId");

            return string.IsNullOrWhiteSpace(configuredHostId)
                ? null
                : configuredHostId.Trim();
        }
    }
}