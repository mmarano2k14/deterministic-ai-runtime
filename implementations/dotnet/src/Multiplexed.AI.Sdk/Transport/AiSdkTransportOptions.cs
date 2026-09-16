using Multiplexed.AI.Sdk.Authentication;

namespace Multiplexed.AI.Sdk.Transport
{
    /// <summary>Transport-neutral client options shared by physical SDK transport implementations.</summary>
    public sealed record AiSdkTransportOptions
    {
        public IAiSdkCredentialProvider? CredentialProvider { get; init; }
    }
}
