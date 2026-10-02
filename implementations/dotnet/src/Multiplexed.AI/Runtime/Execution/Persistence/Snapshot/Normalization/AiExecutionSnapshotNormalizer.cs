using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Execution.Persistence.Snapshot;
using Multiplexed.Abstractions.AI.Steps;
using System.Text.Json;

namespace Multiplexed.AI.Runtime.Execution.Persistence.Snapshot.Normalization
{
    /// <summary>
    /// Normalizes execution snapshot data into a persistence-safe form.
    ///
    /// PURPOSE:
    /// - Removes runtime-specific values that are not safe to persist directly.
    /// - Converts JsonElement values into plain CLR objects.
    /// - Converts custom CLR objects into dictionary/list/primitive graphs so BSON persistence can succeed.
    /// - Preserves payload references while normalizing inline payload values.
    ///
    /// IMPORTANT:
    /// - The snapshot is normalized in place.
    /// - This method is intended for persistence only.
    /// - Runtime-friendly reconstruction is handled separately by the remapper.
    /// - Artifact-backed payload metadata must be preserved exactly for replay/recovery.
    /// </summary>
    public static class AiExecutionSnapshotNormalizer
    {
        /// <summary>
        /// Normalizes the provided snapshot in place.
        /// </summary>
        public static AiExecutionSnapshotDocument<TContext> Normalize<TContext>(AiExecutionSnapshotDocument<TContext>? snapshot)
        {
            var normalizedSnapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

            NormalizeRecord(normalizedSnapshot.Record);


            NormalizeState(normalizedSnapshot.State);

            return normalizedSnapshot;
        }

        /// <summary>
        /// Normalizes persistence-sensitive values stored on the immutable execution record.
        /// </summary>
        /// <remarks>
        /// Published executions can carry their immutable pipeline definition through
        /// <see cref="AiExecutionRecord.PipelineDefinitionSnapshot"/>. After JSON transport or
        /// deserialization an inline payload may contain a <see cref="JsonElement"/>, which the
        /// MongoDB driver's default object serializer does not accept. Normalize that payload by
        /// the same rules already used for mutable execution state before persistence.
        /// </remarks>
        private static void NormalizeRecord(AiExecutionRecord? record)
        {
            if (record is null)
            {
                return;
            }

            NormalizePayload(record.PipelineDefinitionSnapshot);
        }

        private static void NormalizeState(AiExecutionState? state)
        {
            if (state is null)
            {
                return;
            }

            state.Data = NormalizeDictionary(state.Data);
            state.Metadata = NormalizeDictionary(state.Metadata);

            NormalizePayloadDictionary(state.DataPayloads);
            NormalizePayloadDictionary(state.MetadataPayloads);

            if (state.Steps is null || state.Steps.Count == 0)
            {
                return;
            }

            foreach (var step in state.Steps)
            {
                NormalizeStep(step.Value);
            }
        }

        private static void NormalizeStep(AiStepState? step)
        {
            if (step is null)
            {
                return;
            }

            step.Inputs = NormalizeDictionary(step.Inputs);
            step.Config = NormalizeDictionary(step.Config);

            NormalizePayloadDictionary(step.InputPayloads);
            NormalizePayloadDictionary(step.ConfigPayloads);

            NormalizeStepResult(step.Result);
        }

        private static void NormalizeStepResult(AiStepResult? result)
        {
            if (result is null)
            {
                return;
            }

            result.Value = NormalizeObject(result.Value);
            result.Data = NormalizeDictionary(result.Data);

            NormalizePayload(result.Payload);
            NormalizePayloadDictionary(result.DataPayloads);
        }

        /// <summary>
        /// Normalizes a payload dictionary while preserving artifact references.
        ///
        /// IMPORTANT:
        /// - Artifact-backed payloads keep ArtifactId, ContentHash, SizeBytes, and ContentType unchanged.
        /// - Only InlineValue is normalized, and only when the payload is inline.
        /// </summary>
        private static void NormalizePayloadDictionary(Dictionary<string, AiStoredPayload>? payloads)
        {
            if (payloads is null || payloads.Count == 0)
            {
                return;
            }

            foreach (var payload in payloads.Values)
            {
                NormalizePayload(payload);
            }
        }

        /// <summary>
        /// Normalizes inline payload content while preserving external artifact metadata.
        /// </summary>
        private static void NormalizePayload(AiStoredPayload? payload)
        {
            if (payload is null)
            {
                return;
            }

            if (!payload.IsInline)
            {
                return;
            }

            payload.InlineValue = NormalizeObject(payload.InlineValue);
        }

        /// <summary>
        /// Normalizes a dictionary recursively.
        /// Returns an empty dictionary when the input is null or empty.
        /// </summary>
        private static Dictionary<string, object?> NormalizeDictionary(Dictionary<string, object?>? value)
        {
            if (value is null || value.Count == 0)
            {
                return new Dictionary<string, object?>(StringComparer.Ordinal);
            }

            var normalized = new Dictionary<string, object?>(value.Count, StringComparer.Ordinal);

            foreach (var pair in value)
            {
                normalized[pair.Key] = NormalizeObject(pair.Value);
            }

            return normalized;
        }

        /// <summary>
        /// Recursively normalizes an arbitrary object graph into persistence-safe values.
        ///
        /// RULES:
        /// - JsonElement is converted to plain CLR values.
        /// - Dictionary values are normalized recursively.
        /// - Enumerables are normalized into lists.
        /// - Unknown CLR objects are serialized to JSON and re-normalized as plain CLR values.
        /// </summary>
        private static object? NormalizeObject(object? value)
        {
            if (value is null)
            {
                return null;
            }

            if (value is JsonElement jsonElement)
            {
                return NormalizeJsonElement(jsonElement);
            }

            if (value is Dictionary<string, object?> dictionary)
            {
                return NormalizeDictionary(dictionary);
            }

            if (value is IEnumerable<object?> enumerable && value is not string)
            {
                var list = new List<object?>();

                foreach (var item in enumerable)
                {
                    list.Add(NormalizeObject(item));
                }

                return list;
            }

            if (value is IEnumerable<object> nonNullableEnumerable && value is not string)
            {
                var list = new List<object?>();

                foreach (var item in nonNullableEnumerable)
                {
                    list.Add(NormalizeObject(item));
                }

                return list;
            }

            try
            {
                var json = JsonSerializer.Serialize(value);
                using var document = JsonDocument.Parse(json);

                return NormalizeJsonElement(document.RootElement);
            }
            catch
            {
                return value;
            }
        }

        /// <summary>
        /// Converts a JsonElement into plain CLR values.
        /// </summary>
        private static object? NormalizeJsonElement(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    {
                        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

                        foreach (var property in element.EnumerateObject())
                        {
                            result[property.Name] = NormalizeJsonElement(property.Value);
                        }

                        return result;
                    }

                case JsonValueKind.Array:
                    {
                        var result = new List<object?>();

                        foreach (var item in element.EnumerateArray())
                        {
                            result.Add(NormalizeJsonElement(item));
                        }

                        return result;
                    }

                case JsonValueKind.String:
                    return element.GetString();

                case JsonValueKind.Number:
                    if (element.TryGetInt32(out var int32))
                    {
                        return int32;
                    }

                    if (element.TryGetInt64(out var int64))
                    {
                        return int64;
                    }

                    if (element.TryGetDecimal(out var decimalValue))
                    {
                        return decimalValue;
                    }

                    if (element.TryGetDouble(out var doubleValue))
                    {
                        return doubleValue;
                    }

                    return element.GetRawText();

                case JsonValueKind.True:
                    return true;

                case JsonValueKind.False:
                    return false;

                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return null;

                default:
                    return element.GetRawText();
            }
        }
    }
}