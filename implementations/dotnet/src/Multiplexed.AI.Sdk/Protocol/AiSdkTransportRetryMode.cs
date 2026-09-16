namespace Multiplexed.AI.Sdk.Protocol
{
    /// <summary>Automatic transport retry policy. This never grants business-operation retry authority.</summary>
    public enum AiSdkTransportRetryMode
    {
        Never,
        SafeRead
    }
}
