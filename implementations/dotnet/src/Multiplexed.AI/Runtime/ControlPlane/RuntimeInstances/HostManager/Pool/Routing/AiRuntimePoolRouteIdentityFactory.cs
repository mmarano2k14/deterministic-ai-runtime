using System;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing
{
    /// <summary>
    /// Creates immutable identities for local runtime route incarnations.
    /// </summary>
    public static class AiRuntimePoolRouteIdentityFactory
    {
        /// <summary>
        /// Creates a fresh route-incarnation identifier.
        /// </summary>
        /// <returns>The generated route identifier.</returns>
        public static string CreateRouteId()
        {
            return string.Concat(
                "route-",
                Guid.NewGuid().ToString("N"));
        }
    }
}
