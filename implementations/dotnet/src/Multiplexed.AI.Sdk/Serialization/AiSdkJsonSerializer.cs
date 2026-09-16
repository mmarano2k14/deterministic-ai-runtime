using System.Text.Json;
using System.Text.Json.Serialization;

namespace Multiplexed.AI.Sdk.Serialization
{
    /// <summary>Single JSON policy used internally by the .NET external SDK.</summary>
    internal static class AiSdkJsonSerializer
    {
        internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            PropertyNameCaseInsensitive = false,
            WriteIndented = false
        };

        internal static JsonElement ToElement<T>(T value) =>
            JsonSerializer.SerializeToElement(value, Options);

        internal static T FromElement<T>(JsonElement value) =>
            value.Deserialize<T>(Options)
            ?? throw new JsonException($"SDK response could not be deserialized as '{typeof(T).Name}'.");
    }
}
