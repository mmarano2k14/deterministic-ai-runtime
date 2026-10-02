namespace Multiplexed.AI.Sdk.Authentication
{
    /// <summary>Transport credential kept outside public operation payloads.</summary>
    public sealed record AiSdkCredential(string Scheme, string Value);
}
