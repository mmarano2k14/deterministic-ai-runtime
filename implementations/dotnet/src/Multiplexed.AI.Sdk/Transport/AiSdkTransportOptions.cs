using Multiplexed.AI.Sdk.Authentication;

namespace Multiplexed.AI.Sdk.Transport
{
    /// <summary>Transport-neutral client options shared by physical SDK transport implementations.</summary>
    public sealed record AiSdkTransportOptions
    {
        public IAiSdkCredentialProvider? CredentialProvider { get; init; }

        /// <summary>Total attempts allowed for operations explicitly marked as safe reads.</summary>
        public int SafeReadMaxAttempts { get; init; } = 2;

        /// <summary>Delay between transport-level safe-read retry attempts.</summary>
        public TimeSpan SafeReadRetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);

        /// <summary>Maximum time allowed for one physical MCP connection setup.</summary>
        public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(30);
    }
}
