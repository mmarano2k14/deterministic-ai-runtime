using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

namespace Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Snapshots
{
    /// <summary>
    /// Produces deterministic JSON for immutable child DAG composition snapshots.
    /// </summary>
    internal static class AiChildDagCanonicalJson
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        /// <summary>
        /// Serializes a value and canonicalizes all JSON object property ordering using ordinal comparison.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <returns>The canonical JSON representation.</returns>
        public static string Serialize(object? value)
        {
            var element = JsonSerializer.SerializeToElement(value, SerializerOptions);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            {
                WriteElement(writer, element);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>
        /// Deserializes canonical JSON using the same serializer contract used to freeze child DAG snapshots.
        /// </summary>
        /// <typeparam name="T">The value type to deserialize.</typeparam>
        /// <param name="json">The canonical JSON content.</param>
        /// <returns>The deserialized value.</returns>
        /// <exception cref="InvalidOperationException">Thrown when deserialization produces a null value.</exception>
        public static T Deserialize<T>(string json)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(json);

            return JsonSerializer.Deserialize<T>(json, SerializerOptions)
                ?? throw new InvalidOperationException(
                    $"Canonical child DAG snapshot could not be deserialized as '{typeof(T).FullName}'.");
        }

        /// <summary>
        /// Computes the stable SHA-256 digest for canonical JSON content.
        /// </summary>
        /// <param name="canonicalJson">The canonical JSON content.</param>
        /// <returns>The lower-case hexadecimal SHA-256 digest.</returns>
        public static string ComputeSha256(string canonicalJson)
        {
            ArgumentNullException.ThrowIfNull(canonicalJson);
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        /// <summary>
        /// Canonicalizes an already serialized JSON document.
        /// </summary>
        /// <param name="json">The JSON content.</param>
        /// <returns>The canonical JSON representation.</returns>
        public static string Canonicalize(string json)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(json);

            using var document = JsonDocument.Parse(json);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            {
                WriteElement(writer, document.RootElement);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>
        /// Writes one JSON element using deterministic object-property ordering while preserving array order.
        /// </summary>
        /// <param name="writer">The canonical JSON writer.</param>
        /// <param name="element">The JSON element to write.</param>
        private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in element
                                 .EnumerateObject()
                                 .OrderBy(item => item.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(property.Name);
                        WriteElement(writer, property.Value);
                    }

                    writer.WriteEndObject();
                    break;

                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                    {
                        WriteElement(writer, item);
                    }

                    writer.WriteEndArray();
                    break;

                default:
                    element.WriteTo(writer);
                    break;
            }
        }
    }
}
