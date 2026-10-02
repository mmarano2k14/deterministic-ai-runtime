namespace Multiplexed.AI.Sdk.Protocol
{
    /// <summary>Stable transport behavior associated with one public SDK operation.</summary>
    public sealed record AiSdkOperationDescriptor(string Name, AiSdkTransportRetryMode TransportRetryMode);
}
