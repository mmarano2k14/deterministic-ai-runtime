using Multiplexed.AI.Runtime.Execution.Retention.Models;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Multiplexed.AI.Stores.Cache.Redis.Serialization
{
    public static class JsonSerializationHelpers
    {
        /// <summary>
        /// Repairs legacy or Lua-corrupted step JSON before deserializing into <see cref="AiStepState"/>.
        /// Specifically ensures DependsOn is always a JSON array.
        /// </summary>
        public static string RepairStepJson(string json)
        {
            using var document = JsonDocument.Parse(json);
            using var stream = new MemoryStream();

            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();

                var hasDependsOn = false;

                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("DependsOn"))
                    {
                        hasDependsOn = true;
                        writer.WritePropertyName("DependsOn");

                        if (property.Value.ValueKind == JsonValueKind.Array)
                        {
                            property.Value.WriteTo(writer);
                        }
                        else
                        {
                            writer.WriteStartArray();
                            writer.WriteEndArray();
                        }
                    }
                    else
                    {
                        property.WriteTo(writer);
                    }
                }

                if (!hasDependsOn)
                {
                    writer.WritePropertyName("DependsOn");
                    writer.WriteStartArray();
                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>
        /// Repairs legacy or incompatible execution record JSON before deserializing into <see cref="AiExecutionRecord"/>.
        /// Specifically ensures CompletedSteps is always a JSON array.
        /// </summary>
        public static string RepairRecordJson(string json)
        {
            using var document = JsonDocument.Parse(json);
            using var stream = new MemoryStream();

            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();

                var hasCompletedSteps = false;

                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("CompletedSteps"))
                    {
                        hasCompletedSteps = true;
                        writer.WritePropertyName("CompletedSteps");

                        if (property.Value.ValueKind == JsonValueKind.Array)
                        {
                            property.Value.WriteTo(writer);
                        }
                        else
                        {
                            writer.WriteStartArray();
                            writer.WriteEndArray();
                        }
                    }
                    else if (string.Equals(
                        property.Name,
                        "ExecutionContextSnapshot",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        writer.WritePropertyName(property.Name);

                        if (property.Value.ValueKind == JsonValueKind.Object)
                        {
                            WriteRepairedExecutionContextSnapshotJson(writer, property.Value);
                        }
                        else
                        {
                            property.Value.WriteTo(writer);
                        }
                    }
                    else
                    {
                        property.WriteTo(writer);
                    }
                }

                if (!hasCompletedSteps)
                {
                    writer.WritePropertyName("CompletedSteps");
                    writer.WriteStartArray();
                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>
        /// Writes an execution-context snapshot while normalizing collection shapes that Redis Lua cjson
        /// can round-trip as empty JSON objects.
        /// </summary>
        private static void WriteRepairedExecutionContextSnapshotJson(
            Utf8JsonWriter writer,
            JsonElement snapshot)
        {
            writer.WriteStartObject();

            foreach (var property in snapshot.EnumerateObject())
            {
                if (string.Equals(property.Name, "Namespaces", StringComparison.OrdinalIgnoreCase))
                {
                    writer.WritePropertyName(property.Name);

                    if (property.Value.ValueKind == JsonValueKind.Array)
                    {
                        writer.WriteStartArray();
                        foreach (var namespaceEntry in property.Value.EnumerateArray())
                        {
                            if (namespaceEntry.ValueKind == JsonValueKind.Object)
                            {
                                WriteRepairedNamespaceEntryJson(writer, namespaceEntry);
                            }
                            else
                            {
                                namespaceEntry.WriteTo(writer);
                            }
                        }
                        writer.WriteEndArray();
                    }
                    else
                    {
                        writer.WriteStartArray();
                        writer.WriteEndArray();
                    }

                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        /// <summary>
        /// Writes a namespace entry while guaranteeing that TRN permissions remain a JSON array.
        /// </summary>
        private static void WriteRepairedNamespaceEntryJson(
            Utf8JsonWriter writer,
            JsonElement namespaceEntry)
        {
            writer.WriteStartObject();

            foreach (var property in namespaceEntry.EnumerateObject())
            {
                if (string.Equals(property.Name, "Trns", StringComparison.OrdinalIgnoreCase))
                {
                    writer.WritePropertyName(property.Name);

                    if (property.Value.ValueKind == JsonValueKind.Array)
                    {
                        property.Value.WriteTo(writer);
                    }
                    else
                    {
                        writer.WriteStartArray();
                        writer.WriteEndArray();
                    }

                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        /// <summary>
        /// Writes a repaired Retry JSON object where Policies is guaranteed to be a JSON array.
        /// </summary>
        public static void WriteRepairedRetryJson(
            Utf8JsonWriter writer,
            JsonElement retry)
        {
            writer.WriteStartObject();

            var hasPolicies = false;

            foreach (var property in retry.EnumerateObject())
            {
                if (property.NameEquals("Policies"))
                {
                    hasPolicies = true;
                    writer.WritePropertyName("Policies");

                    if (property.Value.ValueKind == JsonValueKind.Array)
                    {
                        property.Value.WriteTo(writer);
                    }
                    else if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        writer.WriteStartArray();
                        writer.WriteStringValue(property.Value.GetString());
                        writer.WriteEndArray();
                    }
                    else
                    {
                        writer.WriteStartArray();
                        writer.WriteEndArray();
                    }

                    continue;
                }

                property.WriteTo(writer);
            }

            if (!hasPolicies)
            {
                writer.WritePropertyName("Policies");
                writer.WriteStartArray();
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        /// <summary>
        /// Repairs legacy or incompatible step JSON before deserializing into <see cref="AiStepState"/>.
        /// Specifically ensures Retry.Policies is always a JSON array.
        /// </summary>
        public static string RepairRetryJson(string json)
        {
            using var document = JsonDocument.Parse(json);
            using var stream = new MemoryStream();

            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();

                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("Retry"))
                    {
                        writer.WritePropertyName("Retry");

                        if (property.Value.ValueKind == JsonValueKind.Object)
                        {
                            JsonSerializationHelpers.WriteRepairedRetryJson(writer, property.Value);
                        }
                        else
                        {
                            property.Value.WriteTo(writer);
                        }

                        continue;
                    }

                    property.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>
        /// Deserializes the retention patch result returned by Redis Lua.
        /// </summary>
        /// <param name="json">
        /// The JSON payload returned by the Lua script.
        /// </param>
        /// <returns>
        /// The normalized retention patch result.
        /// </returns>
        /// <remarks>
        /// Redis Lua cjson may encode empty Lua tables as JSON objects instead of arrays.
        /// This helper treats non-array values as empty collections so that empty patch result
        /// collections never fail deserialization.
        /// </remarks>
        public static AiRetentionPatchResult DeserializeRetentionPatchResult(
            string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AiRetentionPatchResult();
            }

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            return new AiRetentionPatchResult
            {
                CompactedSteps = ReadStringArray(root, nameof(AiRetentionPatchResult.CompactedSteps)),
                EvictedSteps = ReadStringArray(root, nameof(AiRetentionPatchResult.EvictedSteps)),
                SkippedSteps = ReadStringArray(root, nameof(AiRetentionPatchResult.SkippedSteps))
            };
        }

        /// <summary>
        /// Reads a string array from a JSON property.
        /// </summary>
        /// <param name="root">
        /// The root JSON element.
        /// </param>
        /// <param name="propertyName">
        /// The property name to read.
        /// </param>
        /// <returns>
        /// The string values contained in the array, or an empty array when the property is missing
        /// or is not represented as a JSON array.
        /// </returns>
        public static IReadOnlyCollection<string> ReadStringArray(
            JsonElement root,
            string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var property))
            {
                return Array.Empty<string>();
            }

            if (property.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            return property
                .EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                .ToArray();
        }
    }
}
