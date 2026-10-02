namespace Multiplexed.AI.Sdk.Authentication
{
    /// <summary>Returns one immutable authorization credential for transports that use static credentials.</summary>
    public sealed class AiSdkStaticCredentialProvider : IAiSdkCredentialProvider
    {
        private readonly AiSdkCredential _credential;

        public AiSdkStaticCredentialProvider(AiSdkCredential credential)
        {
            _credential = credential ?? throw new ArgumentNullException(nameof(credential));
        }

        public ValueTask<AiSdkCredential?> GetCredentialAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<AiSdkCredential?>(_credential);
        }
    }
}
