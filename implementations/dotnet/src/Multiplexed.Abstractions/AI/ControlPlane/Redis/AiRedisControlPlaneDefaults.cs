namespace Multiplexed.Abstractions.AI.ControlPlane.Redis
{
    /// <summary>
    /// Defines canonical Redis defaults shared by control-plane persistence components.
    /// </summary>
    public static class AiRedisControlPlaneDefaults
    {
        /// <summary>
        /// Gets the default Redis root key prefix.
        /// </summary>
        public const string DefaultKeyPrefix = "ai";
    }
}
