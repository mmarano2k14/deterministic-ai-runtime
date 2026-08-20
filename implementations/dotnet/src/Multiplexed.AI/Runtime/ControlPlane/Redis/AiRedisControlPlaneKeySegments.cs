using Multiplexed.Abstractions.AI.ControlPlane.Redis;

namespace Multiplexed.AI.Runtime.ControlPlane.Redis
{
    /// <summary>
    /// Defines shared Redis key segments used by control-plane stores.
    /// </summary>
    internal static class AiRedisControlPlaneKeySegments
    {
        /// <summary>The default root key prefix.</summary>
        public const string DefaultKeyPrefix = AiRedisControlPlaneDefaults.DefaultKeyPrefix;
        /// <summary>The legacy combined control-plane key prefix.</summary>
        public const string LegacyControlPlanePrefix = "ai:control-plane";
        /// <summary>The control-plane key segment.</summary>
        public const string ControlPlane = "control-plane";
        /// <summary>The tenant key segment.</summary>
        public const string Tenant = "tenant";
        /// <summary>The item key segment.</summary>
        public const string Item = "item";
        /// <summary>The all-items index key segment.</summary>
        public const string All = "all";
    }
}
