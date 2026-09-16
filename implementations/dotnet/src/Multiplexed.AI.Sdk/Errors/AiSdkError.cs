using System.Text.Json;

namespace Multiplexed.AI.Sdk.Errors
{
    /// <summary>Normalized client-side error independent from engine and infrastructure exception types.</summary>
    public sealed record AiSdkError
    {
        public AiSdkErrorKind Kind { get; init; } = AiSdkErrorKind.RemoteFailure;
        public string Code { get; init; } = "remote_failure";
        public string Message { get; init; } = string.Empty;
        public bool IsRetryable { get; init; }
        public IReadOnlyDictionary<string, JsonElement> Details { get; init; }
            = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    }
}
