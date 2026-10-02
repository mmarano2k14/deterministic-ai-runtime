using Multiplexed.Abstractions.AI.Execution;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.Abstractions.AI.Execution.Context
{
    /// <summary>
    /// Provides the global value resolution layer for the AI runtime.
    ///
    /// PURPOSE:
    /// - Centralizes all value resolution logic used by AI steps.
    /// - Prevents business steps from reading raw runtime state directly.
    /// - Provides one consistent place for:
    ///   - path resolution
    ///   - JsonElement conversion
    ///   - dictionary traversal
    ///   - payload-backed value resolution
    ///
    /// SUPPORTED SOURCES:
    /// - Current step inputs:
    ///   - input.xxx
    ///   - inputs.xxx
    ///   - current.input.xxx
    ///   - current.inputs.xxx
    ///
    /// - Current step input payloads:
    ///   - inputPayload.xxx
    ///   - inputPayloads.xxx
    ///   - current.inputPayload.xxx
    ///   - current.inputPayloads.xxx
    ///
    /// - Current step configuration:
    ///   - config.xxx
    ///   - current.config.xxx
    ///
    /// - Current step configuration payloads:
    ///   - configPayload.xxx
    ///   - configPayloads.xxx
    ///   - current.configPayload.xxx
    ///   - current.configPayloads.xxx
    ///
    /// - Execution state data:
    ///   - state.xxx
    ///   - data.xxx
    ///
    /// - Execution state data payloads:
    ///   - statePayload.xxx
    ///   - statePayloads.xxx
    ///   - dataPayload.xxx
    ///   - dataPayloads.xxx
    ///
    /// - Execution metadata:
    ///   - metadata.xxx
    ///
    /// - Execution metadata payloads:
    ///   - metadataPayload.xxx
    ///   - metadataPayloads.xxx
    ///
    /// - Previous step results:
    ///   - steps.stepName.result.data.xxx
    ///   - steps.stepName.result.dataPayloads.xxx
    ///   - steps.stepName.result.value
    ///
    /// - Execution information:
    ///   - execution.id
    ///   - execution.executionId
    ///   - execution.stepName
    ///   - execution.stepKey
    ///
    /// PAYLOAD RULE:
    /// - When an inline value and a payload-backed value exist for the same logical key,
    ///   the payload-backed value must take precedence.
    ///
    /// RAW FALLBACK RULE:
    /// - If the provided value can be resolved as a path, the resolved value is returned.
    /// - If the value looks like a path but cannot be resolved, the original raw value is returned.
    /// - If the value is not a path, the original raw value is returned.
    ///
    /// IMPORTANT:
    /// - This fallback behavior preserves backward compatibility for literals such as:
    ///   - provider keys
    ///   - operation keys
    ///   - static config values
    ///   - plain strings
    /// </summary>
    public interface IAiContextValueResolver
    {
        /// <summary>
        /// Resolves a raw value or path expression from the provided step execution context.
        ///
        /// BEHAVIOR:
        /// - Resolves known runtime paths when possible.
        /// - Resolves payload-backed values when available.
        /// - Converts JsonElement values into usable CLR values.
        /// - Returns the original raw value when no runtime value can be resolved.
        /// </summary>
        Task<object?> ResolveAsync(
            AiStepExecutionContext context,
            object? valueOrPath,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Resolves a raw value or path expression and converts the final value
        /// to the requested type.
        ///
        /// BEHAVIOR:
        /// - Uses the resolved value when resolution succeeds.
        /// - Uses the original raw value when resolution fails.
        /// - Returns default when the final value is null.
        /// - Throws when a non-null final value cannot be converted to the requested type.
        /// </summary>
        Task<T?> ResolveAsync<T>(
            AiStepExecutionContext context,
            object? valueOrPath,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Resolves a required runtime path and converts the resolved value to the requested type.
        ///
        /// STRICT PATH BEHAVIOR:
        /// - The supplied value must be a supported runtime path expression.
        /// - Raw/literal fallback is NOT applied.
        /// - Throws when the path cannot be resolved.
        /// - Throws when the resolved value is null.
        /// - Throws when conversion fails.
        ///
        /// USE CASE:
        /// - Control/infrastructure steps that must fail closed rather than accidentally
        ///   persist an unresolved path string as business data.
        /// </summary>
        Task<T> ResolveRequiredPathAsync<T>(
            AiStepExecutionContext context,
            string path,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Resolves a required raw value or path expression and converts the final value
        /// to the requested type.
        ///
        /// BEHAVIOR:
        /// - Uses the resolved value when resolution succeeds.
        /// - Uses the original raw value when resolution fails.
        /// - Throws when the final value is null.
        /// - Throws when conversion fails.
        /// </summary>
        Task<T> ResolveRequiredAsync<T>(
            AiStepExecutionContext context,
            object? valueOrPath,
            CancellationToken cancellationToken = default);
    }
}