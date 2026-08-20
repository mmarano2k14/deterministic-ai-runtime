using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Context;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Execution.Payloads.Resolvers;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Metrics.Resolvers;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Multiplexed.AI.Runtime.Execution.Context
{
    /// <summary>
    /// Default implementation of the global AI context value resolver.
    ///
    /// PURPOSE:
    /// - Resolve raw values or runtime path expressions from an AI step execution context.
    /// - Support paths such as current.*, config.*, state.*, metadata.*, steps.*, and execution.*.
    /// - Resolve externalized payload references transparently through IAiExecutionPayloadResolver.
    /// - Convert JsonElement values into usable CLR values before traversal or conversion.
    ///
    /// OBSERVABILITY:
    /// - Records resolver start, success, miss, and failure metrics when IAiRuntimeMetrics
    ///   is available from the execution service provider.
    /// - Metrics are observational only and must never influence resolution behavior.
    ///
    /// IMPORTANT:
    /// - This resolver does not mutate execution state.
    /// - If a string does not look like a runtime path, it is returned as a literal value.
    /// - If a path cannot be resolved, the original raw value is returned to preserve
    ///   backward-compatible binding behavior.
    /// </summary>
    public sealed class DefaultAiContextValueResolver : IAiContextValueResolver
    {
        /// <summary>
        /// Resolves a raw value or path expression from the provided step execution context.
        /// </summary>
        public async Task<object?> ResolveAsync(
            AiStepExecutionContext context,
            object? valueOrPath,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (valueOrPath is null)
            {
                return null;
            }

            var rawValue = ConvertJsonElementValue(valueOrPath);

            if (rawValue is AiStoredPayload storedPayload)
            {
                return await ResolvePayloadAsync(context, storedPayload, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (rawValue is not string text || string.IsNullOrWhiteSpace(text))
            {
                return rawValue;
            }

            if (!LooksLikeResolvablePath(text))
            {
                return rawValue;
            }

            var metrics = GetMetrics(context);

            metrics?.RecordResolveStarted(
                context.ExecutionId,
                context.StepName,
                text);

            try
            {
                if (await TryResolvePathAsync(context, text, cancellationToken).ConfigureAwait(false) is { Found: true } result)
                {
                    metrics?.RecordResolveSuccess(
                        context.ExecutionId,
                        context.StepName,
                        text);

                    return result.Value;
                }

                metrics?.RecordResolveMiss(
                    context.ExecutionId,
                    context.StepName,
                    text);

                return rawValue;
            }
            catch (Exception ex)
            {
                metrics?.RecordResolveFailed(
                    context.ExecutionId,
                    context.StepName,
                    text,
                    ex);

                throw;
            }
        }

        /// <summary>
        /// Resolves a raw value or path expression and converts the final value to the requested type.
        /// </summary>
        public async Task<T?> ResolveAsync<T>(
            AiStepExecutionContext context,
            object? valueOrPath,
            CancellationToken cancellationToken = default)
        {
            var value = await ResolveAsync(context, valueOrPath, cancellationToken)
                .ConfigureAwait(false);

            return ConvertValue<T>(value);
        }

        /// <summary>
        /// Resolves a required raw value or path expression and converts the final value to the requested type.
        /// </summary>
        public async Task<T> ResolveRequiredAsync<T>(
            AiStepExecutionContext context,
            object? valueOrPath,
            CancellationToken cancellationToken = default)
        {
            var value = await ResolveAsync<T>(context, valueOrPath, cancellationToken)
                .ConfigureAwait(false);

            if (value is null)
            {
                throw new InvalidOperationException(
                    $"Required runtime value '{valueOrPath}' could not be resolved.");
            }

            return value;
        }

        /// <summary>
        /// Gets resolver metrics from the execution service provider when available.
        ///
        /// IMPORTANT:
        /// - Metrics are optional.
        /// - Missing metrics must never break resolution.
        /// </summary>
        private static IAiResolverMetrics? GetMetrics(AiStepExecutionContext context)
        {
            return context.Services.GetService<IAiRuntimeMetrics>()?.Resolver;
        }

        /// <summary>
        /// Resolves a supported runtime path.
        /// </summary>
        private static async Task<(bool Found, object? Value)> TryResolvePathAsync(
            AiStepExecutionContext context,
            string path,
            CancellationToken cancellationToken)
        {
            var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
            {
                return (false, null);
            }

            return parts[0] switch
            {
                "input" or "inputs" or "current" =>
                    await TryResolveCurrentPathAsync(context, parts, cancellationToken).ConfigureAwait(false),

                "config" =>
                    await TryResolveConfigPathAsync(context, parts, startIndex: 1, cancellationToken).ConfigureAwait(false),

                "inputPayload" or "inputPayloads" =>
                    await TryResolveInputPayloadPathAsync(context, parts, startIndex: 1, cancellationToken).ConfigureAwait(false),

                "configPayload" or "configPayloads" =>
                    await TryResolveConfigPayloadPathAsync(context, parts, startIndex: 1, cancellationToken).ConfigureAwait(false),

                "state" or "data" =>
                    await TryResolveStateDataPathAsync(context, parts, startIndex: 1, cancellationToken).ConfigureAwait(false),

                "statePayload" or "statePayloads" or "dataPayload" or "dataPayloads" =>
                    await TryResolveStateDataPayloadPathAsync(context, parts, startIndex: 1, cancellationToken).ConfigureAwait(false),

                "metadata" =>
                    await TryResolveMetadataPathAsync(context, parts, startIndex: 1, cancellationToken).ConfigureAwait(false),

                "metadataPayload" or "metadataPayloads" =>
                    await TryResolveMetadataPayloadPathAsync(context, parts, startIndex: 1, cancellationToken).ConfigureAwait(false),

                "steps" =>
                    await TryResolveStepResultPathAsync(context, parts, cancellationToken).ConfigureAwait(false),

                "execution" =>
                    TryResolveExecutionPath(context, parts),

                _ => (false, null)
            };
        }

        /// <summary>
        /// Resolves current.* paths.
        /// </summary>
        private static async Task<(bool Found, object? Value)> TryResolveCurrentPathAsync(
            AiStepExecutionContext context,
            string[] parts,
            CancellationToken cancellationToken)
        {
            if (parts.Length >= 2 && parts[0] == "current")
            {
                return parts[1] switch
                {
                    "input" or "inputs" =>
                        await TryResolveInputPathAsync(context, parts, startIndex: 2, cancellationToken).ConfigureAwait(false),

                    "inputPayload" or "inputPayloads" =>
                        await TryResolveInputPayloadPathAsync(context, parts, startIndex: 2, cancellationToken).ConfigureAwait(false),

                    "config" =>
                        await TryResolveConfigPathAsync(context, parts, startIndex: 2, cancellationToken).ConfigureAwait(false),

                    "configPayload" or "configPayloads" =>
                        await TryResolveConfigPayloadPathAsync(context, parts, startIndex: 2, cancellationToken).ConfigureAwait(false),

                    _ => (false, null)
                };
            }

            return await TryResolveInputPathAsync(context, parts, startIndex: 1, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Resolves current step input values with payload precedence.
        /// </summary>
        private static async Task<(bool Found, object? Value)> TryResolveInputPathAsync(
            AiStepExecutionContext context,
            string[] parts,
            int startIndex,
            CancellationToken cancellationToken)
        {
            if (parts.Length <= startIndex)
            {
                return (false, null);
            }

            var key = parts[startIndex];

            if (context.StepState.InputPayloads != null &&
                context.StepState.InputPayloads.TryGetValue(key, out var payload))
            {
                var resolvedPayload = await ResolvePayloadAsync(context, payload, cancellationToken).ConfigureAwait(false);
                return TryTraverse(resolvedPayload, parts, startIndex + 1);
            }

            if (!context.StepState.Inputs.TryGetValue(key, out var value))
            {
                return (false, null);
            }

            return TryTraverse(ConvertJsonElementValue(value), parts, startIndex + 1);
        }

        /// <summary>
        /// Resolves current step input payload values directly.
        /// </summary>
        private static async Task<(bool Found, object? Value)> TryResolveInputPayloadPathAsync(
            AiStepExecutionContext context,
            string[] parts,
            int startIndex,
            CancellationToken cancellationToken)
        {
            if (parts.Length <= startIndex ||
                context.StepState.InputPayloads == null ||
                !context.StepState.InputPayloads.TryGetValue(parts[startIndex], out var payload))
            {
                return (false, null);
            }

            var resolvedPayload = await ResolvePayloadAsync(context, payload, cancellationToken).ConfigureAwait(false);
            return TryTraverse(resolvedPayload, parts, startIndex + 1);
        }

        /// <summary>
        /// Resolves current step config values with payload precedence.
        /// </summary>
        private static async Task<(bool Found, object? Value)> TryResolveConfigPathAsync(
            AiStepExecutionContext context,
            string[] parts,
            int startIndex,
            CancellationToken cancellationToken)
        {
            if (parts.Length <= startIndex)
            {
                return (false, null);
            }

            var key = parts[startIndex];

            if (context.StepState.ConfigPayloads != null &&
                context.StepState.ConfigPayloads.TryGetValue(key, out var payload))
            {
                var resolvedPayload = await ResolvePayloadAsync(context, payload, cancellationToken).ConfigureAwait(false);
                return TryTraverse(resolvedPayload, parts, startIndex + 1);
            }

            if (!context.StepState.Config.TryGetValue(key, out var value))
            {
                return (false, null);
            }

            return TryTraverse(ConvertJsonElementValue(value), parts, startIndex + 1);
        }

        /// <summary>
        /// Resolves current step config payload values directly.
        /// </summary>
        private static async Task<(bool Found, object? Value)> TryResolveConfigPayloadPathAsync(
            AiStepExecutionContext context,
            string[] parts,
            int startIndex,
            CancellationToken cancellationToken)
        {
            if (parts.Length <= startIndex ||
                context.StepState.ConfigPayloads == null ||
                !context.StepState.ConfigPayloads.TryGetValue(parts[startIndex], out var payload))
            {
                return (false, null);
            }

            var resolvedPayload = await ResolvePayloadAsync(context, payload, cancellationToken).ConfigureAwait(false);
            return TryTraverse(resolvedPayload, parts, startIndex + 1);
        }

        /// <summary>
        /// Resolves execution state data values with payload precedence.
        /// </summary>
        private static async Task<(bool Found, object? Value)> TryResolveStateDataPathAsync(
            AiStepExecutionContext context,
            string[] parts,
            int startIndex,
            CancellationToken cancellationToken)
        {
            if (parts.Length <= startIndex)
            {
                return (false, null);
            }

            var key = parts[startIndex];

            if (context.State.DataPayloads != null &&
                context.State.DataPayloads.TryGetValue(key, out var payload))
            {
                var resolvedPayload = await ResolvePayloadAsync(context, payload, cancellationToken).ConfigureAwait(false);
                return TryTraverse(resolvedPayload, parts, startIndex + 1);
            }

            if (!context.State.Data.TryGetValue(key, out var value))
            {
                return (false, null);
            }

            return TryTraverse(ConvertJsonElementValue(value), parts, startIndex + 1);
        }

        /// <summary>
        /// Resolves execution state data payload values directly.
        /// </summary>
        private static async Task<(bool Found, object? Value)> TryResolveStateDataPayloadPathAsync(
            AiStepExecutionContext context,
            string[] parts,
            int startIndex,
            CancellationToken cancellationToken)
        {
            if (parts.Length <= startIndex ||
                context.State.DataPayloads == null ||
                !context.State.DataPayloads.TryGetValue(parts[startIndex], out var payload))
            {
                return (false, null);
            }

            var resolvedPayload = await ResolvePayloadAsync(context, payload, cancellationToken).ConfigureAwait(false);
            return TryTraverse(resolvedPayload, parts, startIndex + 1);
        }

        /// <summary>
        /// Resolves execution metadata values with payload precedence.
        /// </summary>
        private static async Task<(bool Found, object? Value)> TryResolveMetadataPathAsync(
            AiStepExecutionContext context,
            string[] parts,
            int startIndex,
            CancellationToken cancellationToken)
        {
            if (parts.Length <= startIndex)
            {
                return (false, null);
            }

            var key = parts[startIndex];

            if (context.State.MetadataPayloads != null &&
                context.State.MetadataPayloads.TryGetValue(key, out var payload))
            {
                var resolvedPayload = await ResolvePayloadAsync(context, payload, cancellationToken).ConfigureAwait(false);
                return TryTraverse(resolvedPayload, parts, startIndex + 1);
            }

            if (!context.State.Metadata.TryGetValue(key, out var value))
            {
                return (false, null);
            }

            return TryTraverse(ConvertJsonElementValue(value), parts, startIndex + 1);
        }

        /// <summary>
        /// Resolves execution metadata payload values directly.
        /// </summary>
        private static async Task<(bool Found, object? Value)> TryResolveMetadataPayloadPathAsync(
            AiStepExecutionContext context,
            string[] parts,
            int startIndex,
            CancellationToken cancellationToken)
        {
            if (parts.Length <= startIndex ||
                context.State.MetadataPayloads == null ||
                !context.State.MetadataPayloads.TryGetValue(parts[startIndex], out var payload))
            {
                return (false, null);
            }

            var resolvedPayload = await ResolvePayloadAsync(context, payload, cancellationToken).ConfigureAwait(false);
            return TryTraverse(resolvedPayload, parts, startIndex + 1);
        }

        /// <summary>
        /// Resolves previous step result paths.
        /// </summary>
        private static async Task<(bool Found, object? Value)> TryResolveStepResultPathAsync(
            AiStepExecutionContext context,
            string[] parts,
            CancellationToken cancellationToken)
        {
            if (parts.Length < 3)
            {
                return (false, null);
            }

            var stepName = parts[1];

            if (!context.State.Steps.TryGetValue(stepName, out var stepState) ||
                stepState.Result is null)
            {
                return (false, null);
            }

            if (!string.Equals(parts[2], "result", StringComparison.Ordinal))
            {
                return TryTraverse(stepState, parts, 2);
            }

            if (parts.Length == 3)
            {
                return (true, stepState.Result);
            }

            if (parts.Length >= 5 &&
                string.Equals(parts[3], "data", StringComparison.Ordinal))
            {
                var key = parts[4];

                if (TryGetResultDataPayload(stepState.Result, key, out var payload))
                {
                    var resolvedPayload = await ResolvePayloadAsync(context, payload, cancellationToken).ConfigureAwait(false);
                    return TryTraverse(resolvedPayload, parts, 5);
                }

                if (TryGetResultData(stepState.Result, out var data) &&
                    TryGetDictionaryValue(data, key, out var value))
                {
                    return TryTraverse(ConvertJsonElementValue(value), parts, 5);
                }

                return (false, null);
            }

            if (parts.Length >= 5 &&
                string.Equals(parts[3], "dataPayloads", StringComparison.Ordinal))
            {
                var key = parts[4];

                if (!TryGetResultDataPayload(stepState.Result, key, out var payload))
                {
                    return (false, null);
                }

                var resolvedPayload = await ResolvePayloadAsync(context, payload, cancellationToken).ConfigureAwait(false);
                return TryTraverse(resolvedPayload, parts, 5);
            }

            if (string.Equals(parts[3], "value", StringComparison.Ordinal))
            {
                if (!TryGetPublicPropertyValue(stepState.Result, "Value", out var value))
                {
                    return (false, null);
                }

                return TryTraverse(ConvertJsonElementValue(value), parts, 4);
            }

            return TryTraverse(stepState.Result, parts, 3);
        }

        /// <summary>
        /// Resolves execution.* values.
        /// </summary>
        private static (bool Found, object? Value) TryResolveExecutionPath(
            AiStepExecutionContext context,
            string[] parts)
        {
            if (parts.Length < 2)
            {
                return (false, null);
            }

            return parts[1] switch
            {
                "id" or AiExecutionMetadataKeys.CamelCaseExecutionId => (true, context.ExecutionId),
                AiStepMetadataKeys.CamelCaseStepName or "currentStep" => (true, context.StepName),
                AiStepMetadataKeys.CamelCaseStepKey or "currentStepKey" => (true, context.StepKey),
                AiPipelineMetadataKeys.CamelCasePipelineName => (true, context.State.PipelineName),
                _ => (false, null)
            };
        }

        /// <summary>
        /// Resolves a stored payload through the registered payload resolver.
        /// </summary>
        private static async Task<object?> ResolvePayloadAsync(
            AiStepExecutionContext context,
            AiStoredPayload payload,
            CancellationToken cancellationToken)
        {
            var resolver = context.Services.GetRequiredService<IAiExecutionPayloadResolver>();
            var resolved = await resolver.ResolveAsync(payload, cancellationToken).ConfigureAwait(false);
            return ConvertJsonElementValue(resolved);
        }

        /// <summary>
        /// Traverses nested dictionaries, read-only dictionaries, JsonElement values, and public properties.
        /// </summary>
        private static (bool Found, object? Value) TryTraverse(
            object? source,
            string[] parts,
            int startIndex)
        {
            object? current = ConvertJsonElementValue(source);

            for (var i = startIndex; i < parts.Length; i++)
            {
                if (current is null)
                {
                    return (false, null);
                }

                var part = parts[i];

                if (current is IDictionary<string, object?> dict)
                {
                    if (!TryGetDictionaryValue(dict, part, out current))
                    {
                        return (false, null);
                    }

                    current = ConvertJsonElementValue(current);
                    continue;
                }

                if (current is IReadOnlyDictionary<string, object?> readOnlyDict)
                {
                    if (!TryGetReadOnlyDictionaryValue(readOnlyDict, part, out current))
                    {
                        return (false, null);
                    }

                    current = ConvertJsonElementValue(current);
                    continue;
                }

                if (current is JsonElement json)
                {
                    if (json.ValueKind != JsonValueKind.Object ||
                        !json.TryGetProperty(part, out var child))
                    {
                        return (false, null);
                    }

                    current = ConvertJsonElementValue(child);
                    continue;
                }

                if (!TryGetPublicPropertyValue(current, part, out current))
                {
                    return (false, null);
                }

                current = ConvertJsonElementValue(current);
            }

            return (true, current);
        }

        /// <summary>
        /// Converts JsonElement values into usable CLR objects.
        /// </summary>
        private static object? ConvertJsonElementValue(object? value)
        {
            if (value is not JsonElement json)
            {
                return value;
            }

            return json.ValueKind switch
            {
                JsonValueKind.String => json.GetString(),
                JsonValueKind.Number when json.TryGetInt64(out var longValue) => longValue,
                JsonValueKind.Number when json.TryGetDouble(out var doubleValue) => doubleValue,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.Undefined => null,
                JsonValueKind.Array => json.EnumerateArray()
                    .Select(x => ConvertJsonElementValue(x))
                    .ToList(),
                JsonValueKind.Object => json.EnumerateObject()
                    .ToDictionary(
                        x => x.Name,
                        x => ConvertJsonElementValue(x.Value),
                        StringComparer.Ordinal),
                _ => json.GetRawText()
            };
        }

        /// <summary>
        /// Converts a resolved value to the requested type.
        /// </summary>
        private static T? ConvertValue<T>(object? value)
        {
            value = ConvertJsonElementValue(value);

            if (value is null)
            {
                return default;
            }

            if (value is T typed)
            {
                return typed;
            }

            var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

            if (targetType.IsEnum)
            {
                if (value is string enumText &&
                    Enum.TryParse(targetType, enumText, ignoreCase: true, out var enumValue))
                {
                    return (T?)enumValue;
                }

                return (T?)Enum.ToObject(targetType, value);
            }

            if (targetType == typeof(string))
            {
                return (T?)(object?)value.ToString();
            }

            if (TryConvertStringCollection<T>(value, targetType, out var stringCollection))
            {
                return stringCollection;
            }

            if (value is JsonElement json)
            {
                return json.Deserialize<T>(ConversionJsonOptions);
            }

            if (!IsSimpleConvertibleType(targetType))
            {
                var jsonText = JsonSerializer.Serialize(value);

                return JsonSerializer.Deserialize<T>(jsonText, ConversionJsonOptions);
            }

            return (T?)Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Attempts to convert a value into a supported string collection shape.
        /// </summary>
        private static bool TryConvertStringCollection<T>(
            object value,
            Type targetType,
            out T? converted)
        {
            converted = default;

            var isStringList =
                targetType == typeof(List<string>) ||
                targetType == typeof(IReadOnlyList<string>) ||
                targetType == typeof(IEnumerable<string>) ||
                targetType == typeof(string[]);

            if (!isStringList)
            {
                return false;
            }

            if (value is string singleValue)
            {
                var single = new List<string> { singleValue };

                converted = targetType == typeof(string[])
                    ? (T?)(object)single.ToArray()
                    : (T?)(object)single;

                return true;
            }

            if (value is System.Collections.IEnumerable enumerable)
            {
                var list = new List<string>();

                foreach (var item in enumerable)
                {
                    var text = item?.ToString();

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        list.Add(text);
                    }
                }

                converted = targetType == typeof(string[])
                    ? (T?)(object)list.ToArray()
                    : (T?)(object)list;

                return true;
            }

            return false;
        }

        /// <summary>
        /// Determines whether a type can safely use Convert.ChangeType.
        /// </summary>
        private static bool IsSimpleConvertibleType(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;

            return type.IsPrimitive ||
                   type.IsEnum ||
                   type == typeof(string) ||
                   type == typeof(decimal) ||
                   type == typeof(DateTime) ||
                   type == typeof(DateTimeOffset) ||
                   type == typeof(Guid) ||
                   type == typeof(TimeSpan);
        }

        /// <summary>
        /// Determines whether a string looks like a runtime path.
        /// </summary>
        private static bool LooksLikeResolvablePath(string value)
        {
            return value.StartsWith("input.", StringComparison.Ordinal) ||
                   value.StartsWith("inputs.", StringComparison.Ordinal) ||
                   value.StartsWith("current.", StringComparison.Ordinal) ||
                   value.StartsWith("config.", StringComparison.Ordinal) ||
                   value.StartsWith("inputPayload.", StringComparison.Ordinal) ||
                   value.StartsWith("inputPayloads.", StringComparison.Ordinal) ||
                   value.StartsWith("configPayload.", StringComparison.Ordinal) ||
                   value.StartsWith("configPayloads.", StringComparison.Ordinal) ||
                   value.StartsWith("state.", StringComparison.Ordinal) ||
                   value.StartsWith("data.", StringComparison.Ordinal) ||
                   value.StartsWith("statePayload.", StringComparison.Ordinal) ||
                   value.StartsWith("statePayloads.", StringComparison.Ordinal) ||
                   value.StartsWith("dataPayload.", StringComparison.Ordinal) ||
                   value.StartsWith("dataPayloads.", StringComparison.Ordinal) ||
                   value.StartsWith("metadata.", StringComparison.Ordinal) ||
                   value.StartsWith("metadataPayload.", StringComparison.Ordinal) ||
                   value.StartsWith("metadataPayloads.", StringComparison.Ordinal) ||
                   value.StartsWith("steps.", StringComparison.Ordinal) ||
                   value.StartsWith("execution.", StringComparison.Ordinal);
        }

        /// <summary>
        /// Reads result.Data using reflection to avoid coupling the resolver to result internals.
        /// </summary>
        private static bool TryGetResultData(
            object result,
            out IDictionary<string, object?> data)
        {
            if (TryGetPublicPropertyValue(result, "Data", out var value) &&
                value is IDictionary<string, object?> dict)
            {
                data = dict;
                return true;
            }

            data = default!;
            return false;
        }

        /// <summary>
        /// Reads result.DataPayloads using reflection to avoid coupling the resolver to result internals.
        /// </summary>
        private static bool TryGetResultDataPayload(
            object result,
            string key,
            out AiStoredPayload payload)
        {
            if (TryGetPublicPropertyValue(result, "DataPayloads", out var value) &&
                value is IDictionary<string, AiStoredPayload> payloads &&
                payloads.TryGetValue(key, out var storedPayload))
            {
                payload = storedPayload;
                return true;
            }

            payload = default!;
            return false;
        }

        /// <summary>
        /// Reads a public property value using case-sensitive lookup first and case-insensitive fallback.
        /// </summary>
        private static bool TryGetPublicPropertyValue(
            object source,
            string propertyName,
            out object? value)
        {
            var type = source.GetType();

            var property = type.GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public);

            property ??= type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(x => string.Equals(x.Name, propertyName, StringComparison.OrdinalIgnoreCase));

            if (property is null)
            {
                value = null;
                return false;
            }

            value = property.GetValue(source);
            return true;
        }

        /// <summary>
        /// Reads a dictionary value using case-sensitive lookup first and case-insensitive fallback.
        /// </summary>
        private static bool TryGetDictionaryValue(
            IDictionary<string, object?> dict,
            string key,
            out object? value)
        {
            if (dict.TryGetValue(key, out value))
            {
                return true;
            }

            foreach (var entry in dict)
            {
                if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = entry.Value;
                    return true;
                }
            }

            value = null;
            return false;
        }

        /// <summary>
        /// Reads a read-only dictionary value using case-sensitive lookup first and case-insensitive fallback.
        /// </summary>
        private static bool TryGetReadOnlyDictionaryValue(
            IReadOnlyDictionary<string, object?> dict,
            string key,
            out object? value)
        {
            if (dict.TryGetValue(key, out value))
            {
                return true;
            }

            foreach (var entry in dict)
            {
                if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = entry.Value;
                    return true;
                }
            }

            value = null;
            return false;
        }

        private static readonly JsonSerializerOptions ConversionJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
                Converters =
                {
                    new JsonStringEnumConverter()
                }
        };
    }
}