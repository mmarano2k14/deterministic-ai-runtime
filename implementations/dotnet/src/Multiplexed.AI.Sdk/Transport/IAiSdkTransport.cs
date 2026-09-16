namespace Multiplexed.AI.Sdk.Transport
{
    /// <summary>
    /// Physical transport seam for the external SDK. Implementations may automatically retry only operations
    /// marked SafeRead by AiSdkProtocol. Cancelling this call does not cancel a durable execution.
    /// </summary>
    public interface IAiSdkTransport
    {
        ValueTask<AiSdkTransportResponse> InvokeAsync(
            AiSdkTransportRequest request,
            CancellationToken cancellationToken = default);
    }
}
