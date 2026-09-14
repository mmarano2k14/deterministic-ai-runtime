using System.Collections;
using System.Text.Json;

namespace Multiplexed.AI.Runtime.Invocation.Mcp
{
    /// <summary>
    /// Bounded, detached JSON data for the MCP boundary. No arbitrary CLR reflection,
    /// lazy IEnumerable execution or duplicate JSON properties. This does not redact
    /// secrets explicitly placed in inputs or constitute a network allocation quota.
    /// </summary>
    internal static class AiMcpToolJson
    {
        private const int MaxDepth = 32;
        private const long MaxBytes = 65536;

        public static JsonElement CopyArguments(IReadOnlyDictionary<string, object?> inputs)
        {
            ArgumentNullException.ThrowIfNull(inputs);
            return Copy(inputs);
        }

        public static JsonElement CopyResponse(JsonElement response) => Copy(response);

        private static JsonElement Copy(object value)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { MaxDepth = MaxDepth + 1 }))
            {
                WriteValue(writer, value, 0);
                writer.Flush();
            }
            using var document = JsonDocument.Parse(stream.ToArray());
            return document.RootElement.Clone();
        }

        private static void WriteValue(Utf8JsonWriter writer, object? value, int depth)
        {
            if (depth > MaxDepth) throw new InvalidOperationException("MCP JSON exceeds the depth limit.");
            switch (value)
            {
                case null: writer.WriteNullValue(); break;
                case string text: writer.WriteStringValue(text); break;
                case bool boolean: writer.WriteBooleanValue(boolean); break;
                case byte byteNumber: writer.WriteNumberValue((int)byteNumber); break;
                case sbyte sbyteNumber: writer.WriteNumberValue((int)sbyteNumber); break;
                case short shortNumber: writer.WriteNumberValue((int)shortNumber); break;
                case ushort ushortNumber: writer.WriteNumberValue((int)ushortNumber); break;
                case int intNumber: writer.WriteNumberValue(intNumber); break;
                case uint uintNumber: writer.WriteNumberValue(uintNumber); break;
                case long longNumber: writer.WriteNumberValue(longNumber); break;
                case ulong ulongNumber: writer.WriteNumberValue(ulongNumber); break;
                case decimal decimalNumber: writer.WriteNumberValue(decimalNumber); break;
                case float floatNumber when float.IsFinite(floatNumber): writer.WriteNumberValue(floatNumber); break;
                case double doubleNumber when double.IsFinite(doubleNumber): writer.WriteNumberValue(doubleNumber); break;
                case JsonElement json: WriteJson(writer, json, depth); break;
                case IReadOnlyDictionary<string, object?> dictionary: WriteObject(writer, dictionary, depth); break;
                case IDictionary<string, object?> mutableDictionary: WriteObject(writer, mutableDictionary, depth); break;
                case IList list:
                    writer.WriteStartArray();
                    foreach (var item in list) WriteValue(writer, item, depth + 1);
                    writer.WriteEndArray();
                    break;
                default:
                    throw new NotSupportedException("MCP inputs accept JSON data only; arbitrary CLR values are not serialized.");
            }
            CheckSize(writer);
        }

        private static void WriteObject(Utf8JsonWriter writer, IEnumerable<KeyValuePair<string, object?>> values, int depth)
        {
            writer.WriteStartObject();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in values)
            {
                if (!seen.Add(pair.Key)) throw new InvalidOperationException("Duplicate MCP JSON property.");
                writer.WritePropertyName(pair.Key);
                WriteValue(writer, pair.Value, depth + 1);
            }
            writer.WriteEndObject();
        }

        private static void WriteJson(Utf8JsonWriter writer, JsonElement json, int depth)
        {
            switch (json.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in json.EnumerateObject())
                    {
                        if (!seen.Add(property.Name)) throw new InvalidOperationException("Duplicate MCP JSON property.");
                        writer.WritePropertyName(property.Name);
                        WriteValue(writer, property.Value, depth + 1);
                    }
                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in json.EnumerateArray()) WriteValue(writer, item, depth + 1);
                    writer.WriteEndArray();
                    break;
                case JsonValueKind.String:
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                case JsonValueKind.Null:
                    json.WriteTo(writer);
                    break;
                default:
                    throw new InvalidOperationException("Undefined JSON is not an MCP message.");
            }
        }

        private static void CheckSize(Utf8JsonWriter writer)
        {
            if (writer.BytesCommitted + writer.BytesPending > MaxBytes)
            {
                throw new InvalidOperationException("MCP JSON exceeds 65536 UTF-8 bytes.");
            }
        }
    }
}
