using System;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure
{
    /// <summary>
    /// Creates immutable runtime-pool failure observation identifiers.
    /// </summary>
    public static class AiRuntimePoolFailureIdentityFactory
    {
        /// <summary>
        /// Creates one fresh failure observation identifier.
        /// </summary>
        /// <returns>The generated identifier.</returns>
        public static string CreateFailureId()
        {
            return string.Concat(
                "failure-",
                Guid.NewGuid().ToString("N"));
        }
    }
}
