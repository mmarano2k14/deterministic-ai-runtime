using Multiplexed.AI.Sdk.Authentication;

namespace Multiplexed.AI.Sdk.Transport
{
    /// <summary>Transport-neutral client options shared by physical SDK transport implementations.</summary>
    public sealed record AiSdkTransportOptions
    {
        public IAiSdkCredentialProvider? CredentialProvider { get; init; }

        /// <summary>
        /// Additional transport headers required by the public server boundary, such as an access-context handle.
        /// Authorization remains owned by <see cref="CredentialProvider"/> and cannot be overridden here.
        /// </summary>
        public IReadOnlyDictionary<string, string>? AdditionalHeaders { get; init; }

        /// <summary>
        /// Response/request header carrying the rotating runtime access-context handle.
        /// Set to <see langword="null"/> to disable access-context rotation tracking.
        /// </summary>
        public string? AccessContextHeaderName { get; init; } = "X-Access-Context";

        /// <summary>Total attempts allowed for operations explicitly marked as safe reads.</summary>
        public int SafeReadMaxAttempts { get; init; } = 2;

        /// <summary>Delay between transport-level safe-read retry attempts.</summary>
        public TimeSpan SafeReadRetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);

        /// <summary>Maximum time allowed for one physical MCP connection setup.</summary>
        public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(30);
    }
}
