namespace Multiplexed.AI.Sdk.Errors
{
    /// <summary>Exception surfaced by the ergonomic SDK client when a normalized SDK operation fails.</summary>
    public sealed class AiSdkException : Exception
    {
        public AiSdkException(AiSdkError error)
            : base(error?.Message)
        {
            Error = error ?? throw new ArgumentNullException(nameof(error));
        }

        public AiSdkError Error { get; }
    }
}
