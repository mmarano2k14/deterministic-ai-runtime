using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Execution.Payloads.Resolvers;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Abstractions.AI.Retry;
using System.Text.Json.Serialization;

namespace Multiplexed.Abstractions.AI.Execution
{
    /// <summary>
    /// Represents the durable runtime state of a single step within an AI pipeline execution.
    ///
    /// PURPOSE:
    /// - This object is the source of truth for DAG step lifecycle
    /// - It drives scheduling, retry eligibility, completion, failure, and recovery semantics
    /// - It is shared across local execution and distributed Redis/Lua coordination
    ///
    /// IMPORTANT:
    /// - This model stores durable execution state only
    /// - Distributed retry and recovery behavior must be derived from this object
    /// - Local in-process attempt tracking must not replace or override this state
    ///
    /// DESIGN:
    /// - Each step is uniquely identified by <see cref="StepName"/>
    /// - State transitions remain deterministic and monotonic
    /// - Computed helper properties are excluded from persistence where appropriate
    /// - Distributed ownership is modeled through explicit claim metadata and lease expiration
    ///
    /// PAYLOAD EVOLUTION:
    /// - <see cref="InputPayloads"/> and <see cref="ConfigPayloads"/> are additive only
    /// - Existing <see cref="Inputs"/> and <see cref="Config"/> remain fully supported
    /// - Payload-aware accessors prefer payload-backed values when present
    ///
    /// INVARIANT MODEL:
    /// - A step in <see cref="AiStepExecutionStatus.Running"/> must represent a claimed in-flight execution
    /// - Terminal states (<see cref="AiStepExecutionStatus.Completed"/> / <see cref="AiStepExecutionStatus.Failed"/>) must not retain claim metadata
    /// - Business retry and infrastructure recovery are intentionally separate concepts
    /// </summary>
    public sealed class AiStepState
    {
        // ---------------------------------------------------------------------
        // IDENTITY
        // ---------------------------------------------------------------------

        /// <summary>
        /// Gets or sets the unique logical name of the step within the pipeline execution.
        /// </summary>
        public string StepName { get; set; } = string.Empty;

        // ---------------------------------------------------------------------
        // STATUS
        // ---------------------------------------------------------------------

        /// <summary>
        /// Gets or sets the current lifecycle status of the step.
        ///
        /// This is the primary state used for:
        /// - DAG readiness selection
        /// - retry scheduling
        /// - completion and failure transitions
        /// - distributed claim ownership and recovery
        ///
        /// Typical lifecycle:
        /// None -> Ready -> Running -> Completed
        ///                      -> WaitingForRetry -> Ready -> Running
        ///                      -> Failed
        /// </summary>
        public AiStepExecutionStatus Status { get; set; } = AiStepExecutionStatus.None;

        // ---------------------------------------------------------------------
        // DAG
        // ---------------------------------------------------------------------

        /// <summary>
        /// Gets or sets the list of prerequisite step names that must be completed
        /// before this step becomes eligible for execution.
        /// </summary>
        public List<string> DependsOn { get; set; } = new();

        // ---------------------------------------------------------------------
        // DISTRIBUTED CLAIM
        // ---------------------------------------------------------------------

        /// <summary>
        /// Gets or sets the identifier of the worker that currently owns this step claim.
        /// Null when the step is not currently claimed.
        /// </summary>
        public string? ClaimedBy { get; set; }

        /// <summary>
        /// Gets or sets the unique claim token used to validate ownership
        /// during distributed completion and failure operations.
        ///
        /// IMPORTANT:
        /// - A new token should be generated for every claim or reclaim
        /// - Completion or failure writes should only succeed when the expected token matches
        /// - This prevents stale workers from persisting results after ownership has changed
        /// </summary>
        public string? ClaimToken { get; set; }

        /// <summary>
        /// Gets or sets the UTC timestamp at which the step was claimed.
        /// Used for ownership tracing, timeout analysis, and recovery diagnostics.
        /// </summary>
        public DateTime? ClaimedAtUtc { get; set; }

        /// <summary>
        /// Gets or sets the UTC timestamp when the current claim lease expires.
        ///
        /// IMPORTANT:
        /// - This value is computed at claim time
        /// - It is the authoritative source for reclaim eligibility
        /// - Distributed workers must use this value instead of recomputing expiration
        ///   from <see cref="ClaimedAtUtc"/> and <see cref="ClaimTimeoutSeconds"/>
        /// </summary>
        public DateTime? LeaseExpiresAtUtc { get; set; }

        // ---------------------------------------------------------------------
        // TIMING
        // ---------------------------------------------------------------------

        /// <summary>
        /// Gets or sets the UTC timestamp when execution first started for this step.
        ///
        /// IMPORTANT:
        /// - This value is preserved across retries once set
        /// - It represents the first logical start of the step lifecycle
        /// </summary>
        public DateTime? StartedAtUtc { get; set; }

        /// <summary>
        /// Gets or sets the UTC timestamp of the last state mutation.
        /// </summary>
        public DateTime? UpdatedAtUtc { get; set; }

        /// <summary>
        /// Gets or sets the UTC timestamp when the step reached a terminal state.
        /// Terminal states are typically Completed or Failed.
        /// </summary>
        public DateTime? CompletedAtUtc { get; set; }

        /// <summary>
        /// Gets or sets the final serialized execution duration once the step reaches a terminal state.
        ///
        /// SEMANTICS:
        /// - Null while the step has not yet reached a terminal state
        /// - Set when the step transitions to Completed or Failed
        /// - Represents total logical step lifetime from first <see cref="StartedAtUtc"/>
        ///   to terminal <see cref="CompletedAtUtc"/>
        /// - This value is intended for persistence, replay, audit, and snapshot serialization
        /// </summary>
        public TimeSpan? Duration { get; set; }

        // ---------------------------------------------------------------------
        // ERROR / RETRY
        // ---------------------------------------------------------------------

        /// <summary>
        /// Gets or sets the last error message associated with the step.
        ///
        /// This may represent either:
        /// - a retryable business failure
        /// - a terminal failure
        /// </summary>
        public string? Error { get; set; }


        /// <summary>
        /// Gets or sets the number of infrastructure recovery operations.
        ///
        /// This counter is incremented when a distributed running claim is recovered
        /// after timeout or worker loss.
        ///
        /// IMPORTANT:
        /// - This is intentionally separate from business retry state stored in <see cref="RetryState"/>
        /// - Infrastructure recovery must not consume business retry budget
        /// </summary>
        public int RecoveryCount { get; set; }

        /// <summary>
        /// Gets or sets the distributed claim timeout in seconds.
        ///
        /// This value is a lease configuration input.
        /// It is used when creating a claim to compute <see cref="LeaseExpiresAtUtc"/>.
        ///
        /// IMPORTANT:
        /// - Recovery should not recompute expiration from this property
        /// - The authoritative recovery boundary is <see cref="LeaseExpiresAtUtc"/>
        /// </summary>
        public int? ClaimTimeoutSeconds { get; set; }

        // ---------------------------------------------------------------------
        // INPUT / CONFIG
        // ---------------------------------------------------------------------

        /// <summary>
        /// Gets the resolved runtime input values for this step instance.
        /// These values are prepared during pipeline binding and resolution.
        /// </summary>
        public Dictionary<string, object?> Inputs { get; set; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Gets or sets the optional payload-backed representation of resolved runtime inputs.
        ///
        /// PURPOSE:
        /// - Enables large input values to be represented by compact payload references
        /// - Supports future ledger compaction without removing inline <see cref="Inputs"/>
        ///
        /// COMPATIBILITY:
        /// - Existing callers may continue using <see cref="Inputs"/>
        /// - Payload-aware callers should use <see cref="GetInputAsync"/> or <see cref="GetInput"/>
        ///
        /// PRECEDENCE:
        /// - Payload-backed input values take priority in payload-aware accessors
        /// </summary>
        public Dictionary<string, AiStoredPayload>? InputPayloads { get; set; }

        /// <summary>
        /// Gets the resolved static configuration values for this step instance.
        /// </summary>
        public Dictionary<string, object?> Config { get; set; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Gets or sets the optional payload-backed representation of resolved static configuration values.
        ///
        /// PURPOSE:
        /// - Enables large configuration values to be represented by compact payload references
        /// - Supports future ledger compaction without removing inline <see cref="Config"/>
        ///
        /// COMPATIBILITY:
        /// - Existing callers may continue using <see cref="Config"/>
        /// - Payload-aware callers should use <see cref="GetConfigAsync"/> or <see cref="GetConfig"/>
        ///
        /// PRECEDENCE:
        /// - Payload-backed configuration values take priority in payload-aware accessors
        /// </summary>
        public Dictionary<string, AiStoredPayload>? ConfigPayloads { get; set; }

        /// <summary>
        /// Gets or sets the retry policy definition resolved for the step.
        /// </summary>
        /// <remarks>
        /// This property represents the declarative retry configuration associated with the step,
        /// typically resolved from config.retry during pipeline initialization.
        /// It defines retry policies, retry limits, and delay strategies, but does not contain
        /// mutable execution state.
        /// </remarks>
        public AiRetryPolicyDefinition? Retry { get; set; }

        /// <summary>
        /// Gets or sets the runtime retry state associated with the step.
        /// </summary>
        /// <remarks>
        /// This property contains mutable retry execution data, including retry counters,
        /// scheduling timestamps, and the last retry decision applied by the retry engine.
        /// This state is separate from Retry to distinguish configuration from execution state.
        /// </remarks>
        public AiStepRetryState? RetryState { get; set; }

        // ---------------------------------------------------------------------
        // RESULT
        // ---------------------------------------------------------------------

        /// <summary>
        /// Gets or sets the final execution result of the step.
        /// This is populated when the step completes successfully.
        /// </summary>
        public AiStepResult? Result { get; set; }

        // ---------------------------------------------------------------------
        // VERSIONING
        // ---------------------------------------------------------------------

        /// <summary>
        /// Gets or sets the state version.
        /// Used for optimistic concurrency and Lua-based CAS semantics.
        /// </summary>
        public long Version { get; set; }

        /// <summary>
        /// Gets or sets the estimated inline payload size in bytes for this step.
        /// </summary>
        /// <remarks>
        /// This value is computed once when the step completes and is used by retention
        /// triggers to avoid recalculating payload size repeatedly.
        /// </remarks>
        public long? InlinePayloadSizeBytes { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the step payload has been evicted from hot state.
        /// </summary>
        /// <remarks>
        /// The step shell remains in the DAG state so that scheduling, convergence,
        /// dependencies, retry diagnostics, and replay metadata remain structurally stable.
        /// </remarks>
        public bool IsEvictedFromHotState { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the step result has been compacted out of hot state.
        /// </summary>
        /// <remarks>
        /// The step shell remains in the DAG state so that scheduling, convergence,
        /// dependencies, retry diagnostics, and replay metadata remain structurally stable.
        ///
        /// When this value is <see langword="true"/>, the heavy result payload may have been
        /// removed from <see cref="Result"/> after it was archived in the durable step payload store.
        /// Replay validation must treat a completed step with a null result as valid only when
        /// either this marker or <see cref="IsEvictedFromHotState"/> explains why the result is absent.
        /// </remarks>
        public bool IsCompacted { get; set; }

        /// <summary>
        /// Gets or sets the UTC timestamp when the step result was compacted out of hot state.
        /// </summary>
        public DateTime? CompactedAtUtc { get; set; }

        /// <summary>
        /// Gets or sets the durable archived payload artifact identifier produced during compaction.
        /// </summary>
        /// <remarks>
        /// This identifier points to the archived step payload stored by the durable payload store.
        /// It allows replay, audit, and diagnostics to distinguish intentional compaction from
        /// accidental result loss.
        /// </remarks>
        public string? ArchivePayloadId { get; set; }

        /// <summary>
        /// Gets or sets the reason why the step result was compacted.
        /// </summary>
        public string? CompactionReason { get; set; }

        // ---------------------------------------------------------------------
        // SETTERS
        // ---------------------------------------------------------------------

        /// <summary>
        /// Replaces the current input dictionary with a new immutable copy.
        /// </summary>
        public void SetInputs(IReadOnlyDictionary<string, object?>? inputs)
        {
            Inputs = inputs is null
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : new Dictionary<string, object?>(inputs, StringComparer.Ordinal);
        }

        /// <summary>
        /// Replaces the current configuration dictionary with a new immutable copy.
        /// </summary>
        public void SetConfig(IReadOnlyDictionary<string, object?>? config)
        {
            Config = config is null
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : new Dictionary<string, object?>(config, StringComparer.Ordinal);
        }

        // ---------------------------------------------------------------------
        // PAYLOAD-AWARE INPUT / CONFIG ACCESS
        // ---------------------------------------------------------------------

        /// <summary>
        /// Retrieves an input value using payload resolution when available.
        ///
        /// BEHAVIOR:
        /// - If a payload exists for the requested key, it is resolved through the provided resolver
        /// - Otherwise, the method falls back to the inline <see cref="Inputs"/> dictionary
        ///
        /// IMPORTANT:
        /// - This method does not mutate state
        /// - Payload-backed inputs take priority over inline inputs
        /// - Existing callers using <see cref="Inputs"/> remain fully supported
        /// </summary>
        public async Task<object?> GetInputAsync(
            string key,
            IAiExecutionPayloadResolver resolver,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(resolver);

            if (InputPayloads != null &&
                InputPayloads.TryGetValue(key, out var payload))
            {
                return await resolver.ResolveAsync(payload, cancellationToken);
            }

            Inputs.TryGetValue(key, out var value);
            return value;
        }

        /// <summary>
        /// Synchronous compatibility helper for retrieving an input value using payload
        /// resolution when available.
        ///
        /// Prefer <see cref="GetInputAsync"/> in async runtime paths.
        /// </summary>
        public object? GetInput(
            string key,
            IAiExecutionPayloadResolver resolver)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(resolver);

            if (InputPayloads != null &&
                InputPayloads.TryGetValue(key, out var payload))
            {
                return resolver.ResolveAsync(payload)
                    .GetAwaiter()
                    .GetResult();
            }

            Inputs.TryGetValue(key, out var value);
            return value;
        }

        /// <summary>
        /// Retrieves a configuration value using payload resolution when available.
        ///
        /// BEHAVIOR:
        /// - If a payload exists for the requested key, it is resolved through the provided resolver
        /// - Otherwise, the method falls back to the inline <see cref="Config"/> dictionary
        ///
        /// IMPORTANT:
        /// - This method does not mutate state
        /// - Payload-backed configuration values take priority over inline configuration values
        /// - Existing callers using <see cref="Config"/> remain fully supported
        /// </summary>
        public async Task<object?> GetConfigAsync(
            string key,
            IAiExecutionPayloadResolver resolver,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(resolver);

            if (ConfigPayloads != null &&
                ConfigPayloads.TryGetValue(key, out var payload))
            {
                return await resolver.ResolveAsync(payload, cancellationToken);
            }

            Config.TryGetValue(key, out var value);
            return value;
        }

        /// <summary>
        /// Synchronous compatibility helper for retrieving a configuration value using
        /// payload resolution when available.
        ///
        /// Prefer <see cref="GetConfigAsync"/> in async runtime paths.
        /// </summary>
        public object? GetConfig(
            string key,
            IAiExecutionPayloadResolver resolver)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(resolver);

            if (ConfigPayloads != null &&
                ConfigPayloads.TryGetValue(key, out var payload))
            {
                return resolver.ResolveAsync(payload)
                    .GetAwaiter()
                    .GetResult();
            }

            Config.TryGetValue(key, out var value);
            return value;
        }

        // ---------------------------------------------------------------------
        // STATE TRANSITIONS
        // ---------------------------------------------------------------------

        /// <summary>
        /// Marks the step as ready for execution.
        /// </summary>
        public void MarkReady()
        {
            Status = AiStepExecutionStatus.Ready;

            ClaimedBy = null;
            ClaimToken = null;
            ClaimedAtUtc = null;
            LeaseExpiresAtUtc = null;

            CompletedAtUtc = null;
            Duration = null;

            UpdatedAtUtc = DateTime.UtcNow;
            Version++;
        }

        /// <summary>
        /// Marks the step as running after a successful distributed claim.
        /// </summary>
        public void MarkRunning(string workerId, string claimToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
            ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);

            if (Status == AiStepExecutionStatus.Running)
                throw new InvalidOperationException($"Step '{StepName}' is already running.");

            if (Status == AiStepExecutionStatus.Completed || Status == AiStepExecutionStatus.Failed)
                throw new InvalidOperationException($"Step '{StepName}' is terminal and cannot be claimed.");

            var now = DateTime.UtcNow;

            Status = AiStepExecutionStatus.Running;
            ClaimedBy = workerId;
            ClaimToken = claimToken;
            ClaimedAtUtc = now;
            LeaseExpiresAtUtc = ClaimTimeoutSeconds.HasValue && ClaimTimeoutSeconds.Value > 0
                ? now.AddSeconds(ClaimTimeoutSeconds.Value)
                : null;
            StartedAtUtc ??= now;
            UpdatedAtUtc = now;
            Version++;
        }

        /// <summary>
        /// Marks the step as completed successfully.
        /// </summary>
        public void MarkCompleted(AiStepResult result)
        {
            ArgumentNullException.ThrowIfNull(result);

            if (Status != AiStepExecutionStatus.Running)
            {
                throw new InvalidOperationException(
                    $"Step '{StepName}' cannot complete from status '{Status}'.");
            }

            var now = DateTime.UtcNow;

            Status = AiStepExecutionStatus.Completed;
            Result = result;
            Error = null;

            IsCompacted = false;
            CompactedAtUtc = null;
            ArchivePayloadId = null;
            CompactionReason = null;

            if (RetryState is not null)
            {
                RetryState.NextRetryAtUtc = null;
            }

            CompletedAtUtc = now;
            UpdatedAtUtc = now;
            Duration = StartedAtUtc.HasValue && now >= StartedAtUtc.Value
                ? now - StartedAtUtc.Value
                : TimeSpan.Zero;

            ClaimedBy = null;
            ClaimToken = null;
            ClaimedAtUtc = null;
            LeaseExpiresAtUtc = null;

            // compute payload size once
            InlinePayloadSizeBytes = EstimateInlinePayloadSize(result);

            Version++;
        }

        /// <summary>
        /// Marks the step as terminally failed.
        /// </summary>
        public void MarkFailed(string? error)
        {
            if (Status != AiStepExecutionStatus.Running &&
                Status != AiStepExecutionStatus.WaitingForRetry)
            {
                throw new InvalidOperationException(
                    $"Step '{StepName}' cannot fail from status '{Status}'.");
            }

            var now = DateTime.UtcNow;

            Status = AiStepExecutionStatus.Failed;
            Error = error;

            if (RetryState is not null)
            {
                RetryState.NextRetryAtUtc = null;
            }

            CompletedAtUtc = now;
            UpdatedAtUtc = now;
            Duration = StartedAtUtc.HasValue && now >= StartedAtUtc.Value
                ? now - StartedAtUtc.Value
                : TimeSpan.Zero;

            ClaimedBy = null;
            ClaimToken = null;
            ClaimedAtUtc = null;
            LeaseExpiresAtUtc = null;

            Version++;
        }

        /// <summary>
        /// Marks the step as waiting for a future retry attempt.
        /// </summary>
        public void MarkWaitingForRetry(string? error, DateTime nextRetryAtUtc)
        {
            if (Status != AiStepExecutionStatus.Running)
            {
                throw new InvalidOperationException(
                    $"Step '{StepName}' cannot enter WaitingForRetry from status '{Status}'.");
            }

            RetryState ??= new AiStepRetryState();

            Status = AiStepExecutionStatus.WaitingForRetry;
            Error = error;
            RetryState.NextRetryAtUtc = nextRetryAtUtc;
            UpdatedAtUtc = DateTime.UtcNow;

            ClaimedBy = null;
            ClaimToken = null;
            ClaimedAtUtc = null;
            LeaseExpiresAtUtc = null;

            Version++;
        }

        /// <summary>
        /// Requeues a timed-out running step back to Ready.
        /// </summary>
        public void MarkRequeuedAfterTimeout()
        {
            if (Status != AiStepExecutionStatus.Running)
            {
                throw new InvalidOperationException(
                    $"Step '{StepName}' cannot be recovered from status '{Status}'.");
            }

            Status = AiStepExecutionStatus.Ready;

            ClaimedBy = null;
            ClaimToken = null;
            ClaimedAtUtc = null;
            LeaseExpiresAtUtc = null;

            UpdatedAtUtc = DateTime.UtcNow;
            RecoveryCount++;
            Version++;
        }

        /// <summary>
        /// Estimates the total inline payload size in bytes for a step result.
        /// </summary>
        /// <remarks>
        /// PURPOSE:
        /// - Compute the size of inline result data once at completion time.
        /// - Avoid repeated serialization during retention trigger evaluation.
        ///
        /// BEHAVIOR:
        /// - Iterates over all values stored in <see cref="AiStepResult.Data"/>.
        /// - Serializes each value to UTF-8 JSON and sums their byte lengths.
        /// - Ignores <c>null</c> values.
        ///
        /// IMPORTANT:
        /// - This method must remain deterministic.
        /// - It should be called only once per step completion.
        /// - The result should be cached in <c>AiStepState.InlinePayloadSizeBytes</c>.
        ///
        /// PERFORMANCE:
        /// - Serialization cost is paid once at write time (O(N)).
        /// - Retention trigger evaluation becomes O(1) per step.
        ///
        /// LIMITATIONS:
        /// - This is an approximation of actual memory usage.
        /// - JSON serialization size may differ from in-memory representation.
        /// </remarks>
        /// <param name="result">The step result containing inline data.</param>
        /// <returns>
        /// The estimated size in bytes of all inline payload values,
        /// or <c>0</c> when no data is present.
        /// </returns>
        private static long EstimateInlinePayloadSize(AiStepResult result)
        {
            if (result?.Data is null || result.Data.Count == 0)
            {
                return 0;
            }

            long total = 0;

            foreach (var value in result.Data.Values)
            {
                if (value is null)
                {
                    continue;
                }

                total += System.Text.Json.JsonSerializer
                    .SerializeToUtf8Bytes(value)
                    .LongLength;
            }

            return total;
        }

        /// <summary>
        /// Promotes a retry-waiting step back to Ready when its retry window has opened.
        /// </summary>
        public void PromoteRetryToReadyIfDue(DateTime utcNow)
        {
            if (Status != AiStepExecutionStatus.WaitingForRetry)
                return;

            if (RetryState?.NextRetryAtUtc is null)
                return;

            if (RetryState.NextRetryAtUtc.Value > utcNow)
                return;

            Status = AiStepExecutionStatus.Ready;
            RetryState.NextRetryAtUtc = null;
            UpdatedAtUtc = utcNow;
            Version++;
        }

        // ---------------------------------------------------------------------
        // COMPUTED HELPERS
        // ---------------------------------------------------------------------

        [JsonIgnore]
        public bool IsCompleted => Status == AiStepExecutionStatus.Completed;

        [JsonIgnore]
        public bool IsRunning => Status == AiStepExecutionStatus.Running;

        [JsonIgnore]
        public bool IsFailed => Status == AiStepExecutionStatus.Failed;

        [JsonIgnore]
        public bool IsWaitingForRetry => Status == AiStepExecutionStatus.WaitingForRetry;

        [JsonIgnore]
        public bool IsTerminal =>
            Status == AiStepExecutionStatus.Completed ||
            Status == AiStepExecutionStatus.Failed;

        [JsonIgnore]
        public bool IsSchedulable =>
            Status == AiStepExecutionStatus.Ready ||
            Status == AiStepExecutionStatus.None;

        [JsonIgnore]
        public TimeSpan? ElapsedDuration
        {
            get
            {
                if (Duration.HasValue)
                    return Duration;

                if (!StartedAtUtc.HasValue)
                    return null;

                var now = DateTime.UtcNow;
                return now >= StartedAtUtc.Value
                    ? now - StartedAtUtc.Value
                    : TimeSpan.Zero;
            }
        }

        public bool IsLeaseExpired(DateTime utcNow)
        {
            if (Status != AiStepExecutionStatus.Running)
                return false;

            if (!LeaseExpiresAtUtc.HasValue)
                return false;

            return LeaseExpiresAtUtc.Value <= utcNow;
        }

        public bool HasValidLease(DateTime utcNow)
        {
            if (Status != AiStepExecutionStatus.Running)
                return false;

            if (!LeaseExpiresAtUtc.HasValue)
                return false;

            return LeaseExpiresAtUtc.Value > utcNow;
        }
    }
}