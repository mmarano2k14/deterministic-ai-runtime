using System.Text.Json;
using Multiplexed.AI.Sdk.Errors;

namespace Multiplexed.AI.Sdk.Transport
{
    /// <summary>Normalized transport outcome. A response is either a result or one SDK error.</summary>
    public sealed class AiSdkTransportResponse
    {
        private AiSdkTransportResponse(JsonElement? result, AiSdkError? error)
        {
            Result = result;
            Error = error;
        }

        public JsonElement? Result { get; }
        public AiSdkError? Error { get; }
        public bool IsSuccess => Error is null;

        public static AiSdkTransportResponse Success(JsonElement result) => new(result, null);

        public static AiSdkTransportResponse Failure(AiSdkError error) =>
            new(null, error ?? throw new ArgumentNullException(nameof(error)));
    }
}
