using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Multiplexed.Abstractions.AI.Steps
{
    /// <summary>
    /// Represents the persisted result of an AI step execution.
    ///
    /// PURPOSE:
    /// - Carries the success/failure state of a step.
    /// - Stores an optional primary value or payload reference.
    /// - Stores optional human-readable output.
    /// - Stores optional structured result data.
    ///
    /// DESIGN:
    /// - This type is a lightweight result contract.
    /// - Payload-aware reading is handled by runtime extensions.
    /// - The object remains persistence-friendly for Redis, MongoDB, replay, and snapshots.
    /// </summary>
    public sealed class AiStepResult
    {
        /// <summary>
        /// Gets or sets whether the step completed successfully.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Gets or sets the explicit orchestration outcome for this step attempt.
        /// </summary>
        /// <remarks>
        /// The property is nullable for backward compatibility with persisted results created
        /// before explicit outcomes existed. <see cref="EffectiveOutcome"/> preserves the legacy
        /// mapping from <see cref="Success"/> when this value is absent.
        /// </remarks>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AiStepExecutionOutcome? Outcome { get; set; }

        /// <summary>
        /// Gets the effective orchestration outcome, including compatibility mapping for legacy results.
        /// </summary>
        [JsonIgnore]
        public AiStepExecutionOutcome EffectiveOutcome =>
            Outcome ?? (Success ? AiStepExecutionOutcome.Complete : AiStepExecutionOutcome.Fail);

        /// <summary>
        /// Gets or sets the optional primary inline value returned by the step.
        /// </summary>
        public object? Value { get; set; }

        /// <summary>
        /// Gets or sets the optional payload-backed primary value.
        /// </summary>
        public AiStoredPayload? Payload { get; set; }

        /// <summary>
        /// Gets or sets the optional human-readable step output.
        /// </summary>
        public string? Output { get; set; }

        /// <summary>
        /// Gets or sets the optional error message when the step failed.
        /// </summary>
        public string? Error { get; set; }

        /// <summary>
        /// Gets or sets structured inline data returned by the step.
        /// </summary>
        public Dictionary<string, object?> Data { get; set; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Gets or sets payload-backed structured data entries.
        ///
        /// RULE:
        /// - When a key exists in both Data and DataPayloads, payload-aware readers
        ///   must prefer DataPayloads.
        /// </summary>
        public Dictionary<string, AiStoredPayload>? DataPayloads { get; set; }

        /// <summary>
        /// Creates a successful step result.
        /// </summary>
        public static AiStepResult Ok(
            object? value = null,
            string? output = null,
            Dictionary<string, object?>? data = null)
        {
            return new AiStepResult
            {
                Success = true,
                Outcome = AiStepExecutionOutcome.Complete,
                Value = value,
                Payload = null,
                Output = output,
                Error = null,
                Data = data ?? CreateEmptyData()
            };
        }

        /// <summary>
        /// Creates a successful step result backed by a payload.
        /// </summary>
        public static AiStepResult OkPayload(
            AiStoredPayload payload,
            string? output = null,
            Dictionary<string, object?>? data = null)
        {
            ArgumentNullException.ThrowIfNull(payload);

            return new AiStepResult
            {
                Success = true,
                Outcome = AiStepExecutionOutcome.Complete,
                Value = null,
                Payload = payload,
                Output = output,
                Error = null,
                Data = data ?? CreateEmptyData()
            };
        }

        /// <summary>
        /// Creates a failed step result.
        /// </summary>
        public static AiStepResult Fail(
            string error,
            object? value = null,
            Dictionary<string, object?>? data = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(error);

            return new AiStepResult
            {
                Success = false,
                Outcome = AiStepExecutionOutcome.Fail,
                Value = value,
                Payload = null,
                Output = null,
                Error = error,
                Data = data ?? CreateEmptyData()
            };
        }

        /// <summary>
        /// Creates a failed step result backed by a payload.
        /// </summary>
        public static AiStepResult FailPayload(
            string error,
            AiStoredPayload payload,
            Dictionary<string, object?>? data = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(error);
            ArgumentNullException.ThrowIfNull(payload);

            return new AiStepResult
            {
                Success = false,
                Outcome = AiStepExecutionOutcome.Fail,
                Value = null,
                Payload = payload,
                Output = null,
                Error = error,
                Data = data ?? CreateEmptyData()
            };
        }

        /// <summary>
        /// Creates a non-terminal result that asks the DAG runtime to park the current step.
        /// </summary>
        /// <param name="output">Optional human-readable suspension output.</param>
        /// <returns>A result whose effective outcome is <see cref="AiStepExecutionOutcome.Park"/>.</returns>
        /// <remarks>
        /// <para>
        /// A parked result is intentionally not successful because it has not completed the
        /// logical step. DAG runners recognize the explicit outcome before applying legacy
        /// success/failure handling. Non-DAG executors must reject this outcome.
        /// </para>
        /// <para>
        /// Authoritative external-wait state must be committed durably before this result is
        /// returned. The Park outcome deliberately carries no authoritative business payload.
        /// </para>
        /// </remarks>
        public static AiStepResult Park(string? output = null)
        {
            return new AiStepResult
            {
                Success = false,
                Outcome = AiStepExecutionOutcome.Park,
                Value = null,
                Payload = null,
                Output = output,
                Error = null,
                Data = CreateEmptyData()
            };
        }

        /// <summary>
        /// Creates a successful step result with a single structured data entry.
        /// </summary>
        public static AiStepResult Ok(
            string key,
            object? dataValue,
            object? value = null,
            string? output = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);

            return Ok(
                value: value,
                output: output,
                data: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [key] = dataValue
                });
        }

        /// <summary>
        /// Creates an empty structured data dictionary.
        /// </summary>
        private static Dictionary<string, object?> CreateEmptyData()
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }
    }
}