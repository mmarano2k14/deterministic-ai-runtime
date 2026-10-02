namespace Multiplexed.AI.Sdk.Authentication
{
    /// <summary>Result of one authenticated access-context bootstrap request.</summary>
    public sealed record AiSdkAccessContextBootstrapResult
    {
        public required string AccessContext { get; init; }
        public required string HeaderName { get; init; }
    }
}
