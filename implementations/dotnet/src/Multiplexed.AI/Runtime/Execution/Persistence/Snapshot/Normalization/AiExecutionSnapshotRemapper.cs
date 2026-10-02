using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Execution.Persistence.Snapshot;
using Multiplexed.Abstractions.AI.Steps;
using System.Text.Json;

namespace Multiplexed.AI.Runtime.Execution.Persistence.Snapshot.Normalization
{
    /// <summary>
    /// Rebuilds snapshot values after persistence by remapping CLR-safe values
    /// into runtime-friendly representations.
    ///
    /// PURPOSE:
    /// - Restores complex persisted dictionaries/lists into detached <see cref="JsonElement"/> values
    ///   when needed by the runtime.
    /// - Preserves primitive CLR values as-is.
    /// - Preserves payload references while remapping inline payload values.
    ///
    /// IMPORTANT:
    /// - The snapshot is remapped in place.
    /// - This is the reverse operation of <see cref="AiExecutionSnapshotNormalizer"/>,
    ///   but it intentionally does not try to reconstruct original CLR domain types.
    /// - Runtime consumers are expected to handle JsonElement/dictionary-based values safely.
    /// - Artifact-backed payload metadata must remain unchanged for replay/recovery.
    /// </summary>
    public static class AiExecutionSnapshotRemapper
    {
        /// <summary>
        /// Remaps the provided snapshot in place.
        /// </summary>
        public static void Remap<TContext>(AiExecutionSnapshotDocument<TContext>? snapshot)
        {
            if (snapshot is null)
            {
                return;
            }

            RemapRecord(snapshot.Record);


            RemapState(snapshot.State);
        }

        /// <summary>
        /// Remaps persistence-safe values stored on the immutable execution record.
        /// </summary>
        private static void RemapRecord(AiExecutionRecord? record)
        {
            if (record is null)
            {
                return;
            }

            RemapPayload(record.PipelineDefinitionSnapshot);
        }

        private static void RemapState(AiExecutionState? state)
        {
            if (state is null)
            {
                return;
            }

            state.Data = RemapDictionary(state.Data);
            state.Metadata = RemapDictionary(state.Metadata);

            RemapPayloadDictionary(state.DataPayloads);
            RemapPayloadDictionary(state.MetadataPayloads);

            if (state.Steps is null || state.Steps.Count == 0)
            {
                return;
            }

            foreach (var step in state.Steps)
            {
                RemapStep(step.Value);
            }
        }

        private static void RemapStep(AiStepState? step)
        {
            if (step is null)
            {
                return;
            }

            step.Inputs = RemapDictionary(step.Inputs);
            step.Config = RemapDictionary(step.Config);

            RemapPayloadDictionary(step.InputPayloads);
            RemapPayloadDictionary(step.ConfigPayloads);

            RemapStepResult(step.Result);
        }

        private static void RemapStepResult(AiStepResult? result)
        {
            if (result is null)
            {
                return;
            }

            result.Value = RemapObject(result.Value);
            result.Data = RemapDictionary(result.Data);

            RemapPayload(result.Payload);
            RemapPayloadDictionary(result.DataPayloads);
        }

        /// <summary>
        /// Remaps a payload dictionary while preserving artifact references.
        ///
        /// IMPORTANT:
        /// - Artifact-backed payloads keep ArtifactId, ContentHash, SizeBytes, and ContentType unchanged.
        /// - Only InlineValue is remapped, and only when the payload is inline.
        /// </summary>
        private static void RemapPayloadDictionary(Dictionary<string, AiStoredPayload>? payloads)
        {
            if (payloads is null || payloads.Count == 0)
            {
                return;
            }

            foreach (var payload in payloads.Values)
            {
                RemapPayload(payload);
            }
        }

        /// <summary>
        /// Remaps inline payload content while preserving external artifact metadata.
        /// </summary>
        private static void RemapPayload(AiStoredPayload? payload)
        {
            if (payload is null)
            {
                return;
            }

            if (!payload.IsInline)
            {
                return;
            }

            payload.InlineValue = RemapObject(payload.InlineValue);
        }

        /// <summary>
        /// Remaps a dictionary recursively.
        /// Returns an empty dictionary when the input is null or empty.
        /// </summary>
        private static Dictionary<string, object?> RemapDictionary(Dictionary<string, object?>? value)
        {
            if (value is null || value.Count == 0)
            {
                return new Dictionary<string, object?>(StringComparer.Ordinal);
            }

            var remapped = new Dictionary<string, object?>(value.Count, StringComparer.Ordinal);

            foreach (var pair in value)
            {
                remapped[pair.Key] = RemapObject(pair.Value);
            }

            return remapped;
        }

        /// <summary>
        /// Recursively remaps persistence-safe values into runtime-friendly values.
        ///
        /// RULES:
        /// - JsonElement values are preserved.
        /// - Dictionaries are converted to detached JsonElement objects.
        /// - Enumerables are converted to detached JsonElement arrays.
        /// - Primitive CLR values are preserved as-is.
        /// </summary>
        private static object? RemapObject(object? value)
        {
            if (value is null)
            {
                return null;
            }

            if (value is JsonElement)
            {
                return value;
            }

            if (value is Dictionary<string, object?> dictionary)
            {
                return ToJsonElement(RemappedDictionaryValue(dictionary));
            }

            if (value is IEnumerable<object?> enumerable && value is not string)
            {
                var list = new List<object?>();

                foreach (var item in enumerable)
                {
                    list.Add(RemappedCollectionItem(item));
                }

                return ToJsonElement(list);
            }

            if (value is IEnumerable<object> nonNullableEnumerable && value is not string)
            {
                var list = new List<object?>();

                foreach (var item in nonNullableEnumerable)
                {
                    list.Add(RemappedCollectionItem(item));
                }

                return ToJsonElement(list);
            }

            return value;
        }

        /// <summary>
        /// Recursively rebuilds a dictionary before it is converted to JsonElement.
        /// </summary>
        private static Dictionary<string, object?> RemappedDictionaryValue(
            Dictionary<string, object?> dictionary)
        {
            var rebuilt = new Dictionary<string, object?>(dictionary.Count, StringComparer.Ordinal);

            foreach (var pair in dictionary)
            {
                rebuilt[pair.Key] = RemappedCollectionItem(pair.Value);
            }

            return rebuilt;
        }

        /// <summary>
        /// Recursively prepares nested values before JsonElement reconstruction.
        /// </summary>
        private static object? RemappedCollectionItem(object? value)
        {
            if (value is null)
            {
                return null;
            }

            if (value is JsonElement)
            {
                return value;
            }

            if (value is Dictionary<string, object?> dictionary)
            {
                return RemappedDictionaryValue(dictionary);
            }

            if (value is IEnumerable<object?> enumerable && value is not string)
            {
                var rebuilt = new List<object?>();

                foreach (var item in enumerable)
                {
                    rebuilt.Add(RemappedCollectionItem(item));
                }

                return rebuilt;
            }

            if (value is IEnumerable<object> nonNullableEnumerable && value is not string)
            {
                var rebuilt = new List<object?>();

                foreach (var item in nonNullableEnumerable)
                {
                    rebuilt.Add(RemappedCollectionItem(item));
                }

                return rebuilt;
            }

            return value;
        }

        /// <summary>
        /// Converts any CLR value into a detached JsonElement.
        /// </summary>
        private static JsonElement ToJsonElement(object? value)
        {
            var json = JsonSerializer.Serialize(value);
            using var document = JsonDocument.Parse(json);

            return document.RootElement.Clone();
        }
    }
}