using System.Collections;
using System.Text.Json;

namespace Multiplexed.AI.Runtime.Invocation
{
    /// <summary>
    /// Copies JSON data without reflecting over arbitrary CLR objects. This projection is
    /// intentionally restricted to custom policy config, not a new global config resolver.
    /// Runtime-owned secrets are not projected; secrets explicitly placed in policy config
    /// are still data and must be prevented by publication validation outside this pack.
    /// </summary>
    internal static class AiPolicyJsonSnapshot
    {
        private const int MaxDepth = 32;
        private const int MaxBytes = 65536;

        public static JsonElement Create(IReadOnlyDictionary<string, object?> config)
        {
            ArgumentNullException.ThrowIfNull(config);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { MaxDepth = MaxDepth + 1 }))
            {
                WriteValue(writer, config, 0);
                writer.Flush();
            }
            using var document = JsonDocument.Parse(stream.ToArray());
            return document.RootElement.Clone();
        }

        private static void WriteValue(Utf8JsonWriter writer, object? value, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new InvalidOperationException("Custom policy configuration exceeds the JSON depth limit.");
            }

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
                case JsonElement json:
                    WriteJson(writer, json, depth);
                    break;
                case IReadOnlyDictionary<string, object?> dictionary:
                    writer.WriteStartObject();
                    foreach (var pair in dictionary)
                    {
                        writer.WritePropertyName(pair.Key);
                        WriteValue(writer, pair.Value, depth + 1);
                    }
                    writer.WriteEndObject();
                    break;
                case IDictionary<string, object?> mutableDictionary:
                    writer.WriteStartObject();
                    foreach (var pair in mutableDictionary)
                    {
                        writer.WritePropertyName(pair.Key);
                        WriteValue(writer, pair.Value, depth + 1);
                    }
                    writer.WriteEndObject();
                    break;
                case IList list:
                    writer.WriteStartArray();
                    foreach (var item in list) WriteValue(writer, item, depth + 1);
                    writer.WriteEndArray();
                    break;
                default:
                    throw new NotSupportedException(
                        "Custom policy configuration accepts JSON data only; arbitrary CLR values are not serialized.");
            }

            if (writer.BytesCommitted + writer.BytesPending > MaxBytes)
            {
                throw new InvalidOperationException("Custom policy configuration exceeds 65536 UTF-8 bytes.");
            }
        }

        private static void WriteJson(Utf8JsonWriter writer, JsonElement json, int depth)
        {
            switch (json.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in json.EnumerateObject())
                    {
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
                    throw new InvalidOperationException("Undefined JSON is not a custom policy input.");
            }
        }
    }
}
