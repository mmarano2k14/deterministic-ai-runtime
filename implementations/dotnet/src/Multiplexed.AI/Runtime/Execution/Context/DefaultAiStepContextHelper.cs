using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Context;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Execution.Payloads.Resolvers;
using Multiplexed.Abstractions.AI.Rag.Models;
using Multiplexed.Abstractions.Core.ExecutionContext;

namespace Multiplexed.AI.Runtime.Execution.Context
{
    /// <summary>
    /// Default step-scoped helper for resolving values safely from an AI step context.
    /// </summary>
    public sealed class DefaultAiStepContextHelper : IAiStepContextHelper
    {
        private readonly AiStepExecutionContext _context;
        private readonly IAiContextValueResolver _resolver;

        /// <summary>
        /// Initializes a new instance of the helper bound to one step execution context.
        /// </summary>
        public DefaultAiStepContextHelper(
            AiStepExecutionContext context,
            IAiContextValueResolver resolver)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        }

        /// <summary>
        /// Gets the current execution identifier.
        /// </summary>
        public string ExecutionId => _context.ExecutionId;

        /// <summary>
        /// Gets the current logical step name.
        /// </summary>
        public string StepName => _context.StepName;

        /// <summary>
        /// Gets the current resolved step key.
        /// </summary>
        public string StepKey => _context.StepKey;

        /// <summary>
        /// Gets the persisted RBAC execution context snapshot, if available.
        /// </summary>
        public ExecutionContextSnapshot? ExecutionContextSnapshot =>
            _context.Record.ExecutionContextSnapshot;

        /// <summary>
        /// Retrieves an optional input value with payload precedence.
        /// </summary>
        public async Task<T?> GetInputAsync<T>(
            string key,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);

            if (!HasInput(key))
            {
                return default;
            }

            var rawValue = await GetRawInputAsync(key, cancellationToken).ConfigureAwait(false);
            return await _resolver.ResolveAsync<T>(_context, rawValue, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Retrieves a required input value with payload precedence.
        /// </summary>
        public async Task<T> GetRequiredInputAsync<T>(
            string key,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);

            if (!HasInput(key))
            {
                throw new InvalidOperationException(
                    $"Required input '{key}' is missing for step '{StepName}'.");
            }

            var value = await GetInputAsync<T>(key, cancellationToken).ConfigureAwait(false);

            if (value is null)
            {
                throw new InvalidOperationException(
                    $"Required input '{key}' resolved to null for step '{StepName}'.");
            }

            return value;
        }

        /// <summary>
        /// Retrieves an optional config value with payload precedence.
        /// </summary>
        public async Task<T?> GetConfigAsync<T>(
            string key,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);

            if (!HasConfig(key))
            {
                return default;
            }

            var rawValue = await GetRawConfigAsync(key, cancellationToken).ConfigureAwait(false);
            rawValue = NormalizeRawValue(rawValue);

            return await _resolver.ResolveAsync<T>(_context, rawValue, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Retrieves a required config value with payload precedence.
        /// </summary>
        public async Task<T> GetRequiredConfigAsync<T>(
            string key,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);

            if (!HasConfig(key))
            {
                throw new InvalidOperationException(
                    $"Required config '{key}' is missing for step '{StepName}'.");
            }

            var value = await GetConfigAsync<T>(key, cancellationToken).ConfigureAwait(false);

            if (value is null)
            {
                throw new InvalidOperationException(
                    $"Required config '{key}' resolved to null for step '{StepName}'.");
            }

            return value;
        }

        /// <summary>
        /// Retrieves an optional execution state data value with payload precedence.
        /// </summary>
        public async Task<T?> GetDataAsync<T>(
            string key,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);

            if (!HasData(key))
            {
                return default;
            }

            var rawValue = await GetRawDataAsync(key, cancellationToken).ConfigureAwait(false);
            return await _resolver.ResolveAsync<T>(_context, rawValue, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Retrieves a required execution state data value with payload precedence.
        /// </summary>
        public async Task<T> GetRequiredDataAsync<T>(
            string key,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);

            if (!HasData(key))
            {
                throw new InvalidOperationException(
                    $"Required state data '{key}' is missing for execution '{ExecutionId}'.");
            }

            var value = await GetDataAsync<T>(key, cancellationToken).ConfigureAwait(false);

            if (value is null)
            {
                throw new InvalidOperationException(
                    $"Required state data '{key}' resolved to null for execution '{ExecutionId}'.");
            }

            return value;
        }

        /// <summary>
        /// Retrieves an optional execution metadata value with payload precedence.
        /// </summary>
        public async Task<T?> GetMetadataAsync<T>(
            string key,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);

            if (!HasMetadata(key))
            {
                return default;
            }

            var rawValue = await GetRawMetadataAsync(key, cancellationToken).ConfigureAwait(false);
            return await _resolver.ResolveAsync<T>(_context, rawValue, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Retrieves a required execution metadata value with payload precedence.
        /// </summary>
        public async Task<T> GetRequiredMetadataAsync<T>(
            string key,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);

            if (!HasMetadata(key))
            {
                throw new InvalidOperationException(
                    $"Required metadata '{key}' is missing for execution '{ExecutionId}'.");
            }

            var value = await GetMetadataAsync<T>(key, cancellationToken).ConfigureAwait(false);

            if (value is null)
            {
                throw new InvalidOperationException(
                    $"Required metadata '{key}' resolved to null for execution '{ExecutionId}'.");
            }

            return value;
        }

        /// <summary>
        /// Resolves an optional raw value, literal, or runtime path.
        /// </summary>
        public Task<T?> ResolveAsync<T>(
            object? valueOrPath,
            CancellationToken cancellationToken = default)
        {
            return _resolver.ResolveAsync<T>(_context, valueOrPath, cancellationToken);
        }

        /// <summary>
        /// Resolves a required raw value, literal, or runtime path.
        /// </summary>
        public Task<T> ResolveRequiredAsync<T>(
            object? valueOrPath,
            CancellationToken cancellationToken = default)
        {
            return _resolver.ResolveRequiredAsync<T>(_context, valueOrPath, cancellationToken);
        }

        /// <summary>
        /// Resolves all declared inputs for the current step.
        /// </summary>
        public async Task<IReadOnlyDictionary<string, object?>> GetResolvedInputsAsync(
            bool includeReservedVariables = false,
            CancellationToken cancellationToken = default)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var key in _context.StepState.Inputs.Keys)
            {
                keys.Add(key);
            }

            if (_context.StepState.InputPayloads != null)
            {
                foreach (var key in _context.StepState.InputPayloads.Keys)
                {
                    keys.Add(key);
                }
            }

            var result = new Dictionary<string, object?>(StringComparer.Ordinal);

            foreach (var key in keys)
            {
                result[key] = await GetInputAsync<object?>(key, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (includeReservedVariables)
            {
                result[AiExecutionMetadataKeys.CamelCaseExecutionId] = ExecutionId;
                result[AiStepMetadataKeys.CamelCaseStepName] = StepName;
                result[AiStepMetadataKeys.CamelCaseStepKey] = StepKey;
                result["currentStep"] = StepName;
                result["currentStepKey"] = StepKey;
            }

            return result;
        }

        /// <summary>
        /// Retrieves a required RAG retrieval batch produced by a previous step.
        ///
        /// PURPOSE:
        /// - Provides a safe and consistent way to access cross-step retrieval results.
        /// - Abstracts the underlying path resolution logic from business steps.
        /// - Supports both inline and payload-backed batch storage transparently.
        ///
        /// SOURCE:
        /// - steps.{stepName}.result.data.batch
        /// - steps.{stepName}.result.dataPayloads.batch (handled automatically by resolver)
        ///
        /// BEHAVIOR:
        /// - Resolves the batch using the global context value resolver.
        /// - Payload-backed values take precedence over inline values.
        /// - Throws if the step does not exist or does not expose a valid batch.
        /// - Throws if the resolved value cannot be converted to <see cref="RagRetrievalBatch"/>.
        ///
        /// DETERMINISM:
        /// - Ensures consistent retrieval of previously computed batch results.
        /// - Relies on deterministic step outputs and resolver behavior.
        ///
        /// USAGE:
        /// - Used by RAG merge and composition steps to access upstream retrieval results.
        /// </summary>
        public async Task<RagRetrievalBatch> GetRequiredBatchAsync(
            string stepName,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stepName);

            var path = $"steps.{stepName}.result.data.batch";

            try
            {
                return await ResolveRequiredAsync<RagRetrievalBatch>(
                    path,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is not InvalidOperationException &&
                ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"rag: Retrieval batch from step '{stepName}' could not be resolved or converted from path '{path}'.",
                    ex);
            }
        }


        /// <summary>
        /// Resolves a unified argument bag for the current step by combining declared inputs
        /// and selected configuration values.
        ///
        /// PURPOSE:
        /// - Provides a single, normalized argument dictionary for downstream execution.
        /// - Eliminates duplication between input resolution and configuration access.
        /// - Centralizes resolution logic including payload handling and type conversion.
        /// </summary>
        public async Task<Dictionary<string, object?>> GetResolvedArgumentsAsync(
            IReadOnlyCollection<string>? configKeys = null,
            bool includeReservedVariables = true,
            CancellationToken cancellationToken = default)
        {
            var result = new Dictionary<string, object?>(
                await GetResolvedInputsAsync(includeReservedVariables, cancellationToken)
                    .ConfigureAwait(false),
                StringComparer.Ordinal);

            if (configKeys is null || configKeys.Count == 0)
            {
                return result;
            }

            foreach (var key in configKeys)
            {
                if (string.IsNullOrWhiteSpace(key) || result.ContainsKey(key))
                {
                    continue;
                }

                var value = await GetConfigAsync<object>(
                    key,
                    cancellationToken).ConfigureAwait(false);

                if (value is not null)
                {
                    result[key] = value;
                }
            }

            return result;
        }

        /// <summary>
        /// Resolves a typed argument bag for the current step.
        ///
        /// PURPOSE:
        /// - Provides both dictionary-based and DTO-based access to resolved step arguments.
        /// - Keeps argument construction centralized in the context helper.
        /// </summary>
        public async Task<IAiStepArguments> GetArgumentsAsync(
            IReadOnlyCollection<string>? configKeys = null,
            bool includeReservedVariables = true,
            CancellationToken cancellationToken = default)
        {
            var values = await GetResolvedArgumentsAsync(
                configKeys,
                includeReservedVariables,
                cancellationToken).ConfigureAwait(false);

            return new AiStepArguments(values);
        }

        /// <summary>
        /// Converts an object into a dictionary representation suitable for step result data.
        ///
        /// PURPOSE:
        /// - Provides a consistent way to build result data payloads.
        /// - Avoids manual dictionary construction in steps.
        /// - Supports anonymous objects and structured models.
        ///
        /// BEHAVIOR:
        /// - If the value is already a dictionary, it is returned as-is.
        /// - Otherwise, public properties are mapped to dictionary entries.
        /// - Uses case-sensitive keys (StringComparer.Ordinal).
        ///
        /// USE CASE:
        /// - Building step result data without boilerplate.
        /// </summary>
        public Dictionary<string, object?>? ToDictionary(
             object value,
             bool ignoreNull = false)
        {
            ArgumentNullException.ThrowIfNull(value);

            // Fast path
            if (value is Dictionary<string, object?> dict)
            {
                return dict.Count == 0 ? null : dict;
            }

            if (value is IReadOnlyDictionary<string, object?> readOnlyDict)
            {
                if (readOnlyDict.Count == 0)
                {
                    return null;
                }

                return new Dictionary<string, object?>(readOnlyDict, StringComparer.Ordinal);
            }

            var result = new Dictionary<string, object?>(StringComparer.Ordinal);

            var properties = value.GetType().GetProperties(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public);

            foreach (var prop in properties)
            {
                if (!prop.CanRead)
                {
                    continue;
                }

                var val = prop.GetValue(value);

                if (ignoreNull && val is null)
                {
                    continue;
                }

                var key = ToCamelCase(prop.Name);

                result[key] = val;
            }

            return result.Count == 0 ? null : result;
        }

        private static string ToCamelCase(string name)
        {
            if (string.IsNullOrEmpty(name) || char.IsLower(name[0]))
            {
                return name;
            }

            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        /// <summary>
        /// Normalizes raw configuration values before typed resolution.
        /// </summary>
        /// <remarks>
        /// PURPOSE:
        /// - Prevents mixed runtime object graphs from leaking into typed config binding.
        /// - Converts <see cref="JsonElement"/> values into plain CLR values.
        /// - Preserves step override / pipeline fallback behavior without deep-merging raw objects.
        ///
        /// WHY:
        /// Pipeline configuration loaded from JSON may be stored as <see cref="JsonElement"/>,
        /// while step configuration can be stored as dictionaries, arrays, or primitive CLR values.
        /// If JsonElement values are serialized later as normal objects, nested values such as
        /// retry.policies can become impossible to deserialize into strongly typed collections.
        ///
        /// RULE:
        /// - Step config still has priority over pipeline config.
        /// - This method only normalizes the selected raw value.
        /// - It does not merge step and pipeline configuration sections.
        /// </remarks>
        /// <param name="value">The raw value to normalize.</param>
        /// <returns>A JSON-safe CLR representation of the value.</returns>
        private static object? NormalizeRawValue(object? value)
        {
            return value switch
            {
                null => null,

                JsonElement element => NormalizeJsonElement(element),

                IReadOnlyDictionary<string, object?> readOnlyDictionary => readOnlyDictionary
                    .ToDictionary(
                        pair => pair.Key,
                        pair => NormalizeRawValue(pair.Value),
                        StringComparer.Ordinal),

                IDictionary<string, object?> dictionary => dictionary
                    .ToDictionary(
                        pair => pair.Key,
                        pair => NormalizeRawValue(pair.Value),
                        StringComparer.Ordinal),

                IEnumerable<object?> enumerable when value is not string => enumerable
                    .Select(NormalizeRawValue)
                    .ToArray(),

                _ => value
            };
        }

        /// <summary>
        /// Converts a <see cref="JsonElement"/> into a plain CLR value.
        /// </summary>
        /// <param name="element">The JSON element to normalize.</param>
        /// <returns>A plain CLR representation of the JSON element.</returns>
        private static object? NormalizeJsonElement(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Object => element
                    .EnumerateObject()
                    .ToDictionary(
                        property => property.Name,
                        property => NormalizeJsonElement(property.Value),
                        StringComparer.Ordinal),

                JsonValueKind.Array => element
                    .EnumerateArray()
                    .Select(NormalizeJsonElement)
                    .ToArray(),

                JsonValueKind.String => element.GetString(),

                JsonValueKind.Number when element.TryGetInt32(out var intValue) => intValue,

                JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,

                JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,

                JsonValueKind.True => true,

                JsonValueKind.False => false,

                JsonValueKind.Null => null,

                JsonValueKind.Undefined => null,

                _ => element.GetRawText()
            };
        }


        /// <summary>
        /// Determines whether the current step has an input key either inline or payload-backed.
        /// </summary>
        private bool HasInput(string key)
        {
            return _context.StepState.Inputs.ContainsKey(key) ||
                   (_context.StepState.InputPayloads != null &&
                    _context.StepState.InputPayloads.ContainsKey(key));
        }

        /// <summary>
        /// Determines whether the current step has a config key either inline or payload-backed.
        /// </summary>
        private bool HasConfig(string key)
        {
            return _context.StepState.Config.ContainsKey(key) ||

                   (_context.StepState.ConfigPayloads != null &&
                    _context.StepState.ConfigPayloads.ContainsKey(key)) ||

                   (_context.State.PipelineConfig != null &&
                    _context.State.PipelineConfig.ContainsKey(key));
        }

        /// <summary>
        /// Determines whether execution state has a data key either inline or payload-backed.
        /// </summary>
        private bool HasData(string key)
        {
            return _context.State.Data.ContainsKey(key) ||
                   (_context.State.DataPayloads != null &&
                    _context.State.DataPayloads.ContainsKey(key));
        }

        /// <summary>
        /// Determines whether execution state has a metadata key either inline or payload-backed.
        /// </summary>
        private bool HasMetadata(string key)
        {
            return _context.State.Metadata.ContainsKey(key) ||
                   (_context.State.MetadataPayloads != null &&
                    _context.State.MetadataPayloads.ContainsKey(key));
        }

        /// <summary>
        /// Gets the raw input value, resolving payload storage first when present.
        /// </summary>
        private async Task<object?> GetRawInputAsync(
            string key,
            CancellationToken cancellationToken)
        {
            if (_context.StepState.InputPayloads != null &&
                _context.StepState.InputPayloads.TryGetValue(key, out var payload))
            {
                return await ResolvePayloadAsync(payload, cancellationToken).ConfigureAwait(false);
            }

            _context.StepState.Inputs.TryGetValue(key, out var value);
            return value;
        }

        /// <summary>
        /// Gets the raw config value, resolving payload storage first when present.
        /// </summary>
        private async Task<object?> GetRawConfigAsync(
            string key,
            CancellationToken cancellationToken)
        {
            // 1. Step payload override
            if (_context.StepState.ConfigPayloads != null &&
                _context.StepState.ConfigPayloads.TryGetValue(key, out var payload))
            {
                return await ResolvePayloadAsync(payload, cancellationToken).ConfigureAwait(false);
            }

            // 2. Step inline override
            if (_context.StepState.Config.TryGetValue(key, out var stepValue))
            {
                return stepValue;
            }

            // 3. Pipeline fallback 💥
            if (_context.State.PipelineConfig != null &&
                _context.State.PipelineConfig.TryGetValue(key, out var pipelineValue))
            {
                return pipelineValue;
            }

            return null;
        }

        /// <summary>
        /// Gets the raw execution data value, resolving payload storage first when present.
        /// </summary>
        private async Task<object?> GetRawDataAsync(
            string key,
            CancellationToken cancellationToken)
        {
            if (_context.State.DataPayloads != null &&
                _context.State.DataPayloads.TryGetValue(key, out var payload))
            {
                return await ResolvePayloadAsync(payload, cancellationToken).ConfigureAwait(false);
            }

            _context.State.Data.TryGetValue(key, out var value);
            return value;
        }

        /// <summary>
        /// Gets the raw execution metadata value, resolving payload storage first when present.
        /// </summary>
        private async Task<object?> GetRawMetadataAsync(
            string key,
            CancellationToken cancellationToken)
        {
            if (_context.State.MetadataPayloads != null &&
                _context.State.MetadataPayloads.TryGetValue(key, out var payload))
            {
                return await ResolvePayloadAsync(payload, cancellationToken).ConfigureAwait(false);
            }

            _context.State.Metadata.TryGetValue(key, out var value);
            return value;
        }

        /// <summary>
        /// Resolves a stored payload through the registered payload resolver.
        /// </summary>
        private async Task<object?> ResolvePayloadAsync(
            AiStoredPayload payload,
            CancellationToken cancellationToken)
        {
            var resolver = _context.Services.GetRequiredService<IAiExecutionPayloadResolver>();
            return await resolver.ResolveAsync(payload, cancellationToken).ConfigureAwait(false);
        }
    }
}