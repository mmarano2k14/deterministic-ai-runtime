using System.Text;
using System.Text.Json;

namespace Multiplexed.AI.Runtime.Invocation.Durable
{
    /// <summary>
    /// Bounded JSON-only normalization for invocation inputs/results. Object keys are
    /// ordinally sorted; arrays and number spellings are preserved. This is not RFC 8785
    /// or a replacement for the runtime's existing definition/payload canonicalization.
    /// </summary>
    internal static class AiDurableInvocationJson
    {
        internal const int MaxBytes = 262144;
        private static readonly UTF8Encoding Utf8 = new(false, true);

        internal static string Normalize(string json, bool requireObject)
        {
            ArgumentNullException.ThrowIfNull(json);
            if (Utf8.GetByteCount(json) > MaxBytes)
                throw new InvalidOperationException("Invocation JSON exceeds the 262144-byte inline limit.");
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            if (requireObject && document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Resolved invocation inputs must be a JSON object.");
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                Write(writer, document.RootElement);
                writer.Flush();
            }
            if (stream.Length > MaxBytes)
                throw new InvalidOperationException("Normalized invocation JSON exceeds the inline limit.");
            return Utf8.GetString(stream.ToArray());
        }

        private static void Write(Utf8JsonWriter writer, JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var properties = element.EnumerateObject().ToArray();
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in properties)
                        if (!names.Add(property.Name)) throw new InvalidOperationException("Duplicate JSON property names are ambiguous.");
                    writer.WriteStartObject();
                    foreach (var property in properties.OrderBy(item => item.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(property.Name);
                        Write(writer, property.Value);
                    }
                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray()) Write(writer, item);
                    writer.WriteEndArray();
                    break;
                default:
                    element.WriteTo(writer);
                    break;
            }
        }
    }
}
