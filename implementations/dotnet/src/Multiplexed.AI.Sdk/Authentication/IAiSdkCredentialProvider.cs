namespace Multiplexed.AI.Sdk.Authentication
{
    /// <summary>Supplies credentials to a physical transport without changing SDK operation payloads.</summary>
    public interface IAiSdkCredentialProvider
    {
        ValueTask<AiSdkCredential?> GetCredentialAsync(CancellationToken cancellationToken = default);
    }
}
