using System.Text.Json;
using Multiplexed.Abstractions.AI.Concurrency;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Policies;

namespace Multiplexed.AI.Runtime.AI.Concurrency
{
    /// <summary>
    /// Default implementation of <see cref="IAiConcurrencyDefinitionResolver"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolves <c>config.concurrency</c> from pipeline-level and step-level configuration.
    /// </para>
    ///
    /// <para>
    /// The resolver uses an internal nullable representation to preserve whether a value was
    /// explicitly configured. This prevents runtime defaults from accidentally overriding
    /// pipeline-level values during step-level merges.
    /// </para>
    ///
    /// <para>
    /// Configured policy entries can provide default concurrency values through
    /// <c>policy.config</c>. These values only fill missing values from the same concurrency
    /// definition. Direct <c>config.concurrency</c> values remain authoritative.
    /// </para>
    ///
    /// <para>
    /// Generic throttle rules are also resolved from policies named
    /// <c>concurrency.throttle</c>. These rules support scoped throttling with optional targets:
    /// provider, model, operation, step, step-type, and pipeline.
    /// </para>
    /// </remarks>
    public sealed class DefaultAiConcurrencyDefinitionResolver : IAiConcurrencyDefinitionResolver
    {
        private static readonly AiConcurrencyDefinition DisabledDefinition = new()
        {
            Enabled = false,
            Policies = new List<AiConfiguredPolicyDefinition>(),
            ThrottleRules = new List<AiConcurrencyThrottleRule>(),
            DefaultRetryAfterMs = 250,
            LeaseSeconds = 300,
            MaxJitterMs = 100
        };

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        /// <inheritdoc />
        public AiConcurrencyDefinition Resolve(AiStepState stepState)
        {
            ArgumentNullException.ThrowIfNull(stepState);

            var definition = TryReadDefinition(stepState.Config);

            return definition is null
                ? DisabledDefinition
                : ToRuntimeDefinition(definition);
        }

        /// <inheritdoc />
        public AiConcurrencyDefinition Resolve(
            AiPipelineDefinition pipeline,
            AiPipelineStepDefinition step)
        {
            ArgumentNullException.ThrowIfNull(pipeline);
            ArgumentNullException.ThrowIfNull(step);

            var pipelineDefinition = TryReadDefinition(pipeline.Config);
            var stepDefinition = TryReadDefinition(step.Config);

            if (pipelineDefinition is null && stepDefinition is null)
            {
                return DisabledDefinition;
            }

            if (pipelineDefinition is null)
            {
                return ToRuntimeDefinition(stepDefinition!);
            }

            if (stepDefinition is null)
            {
                return ToRuntimeDefinition(pipelineDefinition);
            }

            return ToRuntimeDefinition(Merge(pipelineDefinition, stepDefinition));
        }

        /// <summary>
        /// Reads policy declarations without applying policy defaults or creating throttle
        /// rules. Used when compiling invocation scopes, not when deciding admission.
        /// </summary>
        public IReadOnlyList<AiConfiguredPolicyDefinition> ReadPolicyDeclarations(
            IReadOnlyDictionary<string, object?>? config)
        {
            var policies = TryReadDefinition(config, applyPolicyDefaults: false)?.Policies;
            return policies is null
                ? Array.Empty<AiConfiguredPolicyDefinition>()
                : Array.AsReadOnly(policies.ToArray());
        }

        /// <summary>
        /// Reads a raw nullable concurrency definition from a configuration dictionary.
        /// </summary>
        /// <param name="config">
        /// The configuration dictionary that may contain a <c>concurrency</c> section.
        /// </param>
        /// <returns>
        /// A raw nullable concurrency definition, or <c>null</c> when no concurrency section exists.
        /// </returns>
        private static RawAiConcurrencyDefinition? TryReadDefinition(
            IReadOnlyDictionary<string, object?>? config,
            bool applyPolicyDefaults = true)
        {
            if (config is null ||
                !config.TryGetValue("concurrency", out var value) ||
                value is null)
            {
                return null;
            }

            RawAiConcurrencyDefinition? definition;

            if (value is RawAiConcurrencyDefinition rawDefinition)
            {
                definition = rawDefinition;
            }
            else if (value is AiConcurrencyDefinition runtimeDefinition)
            {
                definition = RawAiConcurrencyDefinition.FromRuntimeDefinition(runtimeDefinition);
            }
            else
            {
                var json = JsonSerializer.Serialize(value, JsonOptions);

                definition = JsonSerializer.Deserialize<RawAiConcurrencyDefinition>(
                    json,
                    JsonOptions);
            }

            return definition is null || !applyPolicyDefaults
                ? definition
                : ApplyConfiguredPolicyDefaults(definition);
        }

        /// <summary>
        /// Applies configured policy values as defaults to a raw concurrency definition.
        /// </summary>
        /// <param name="definition">
        /// The raw definition to enrich.
        /// </param>
        /// <returns>
        /// The enriched raw definition.
        /// </returns>
        private static RawAiConcurrencyDefinition ApplyConfiguredPolicyDefaults(
            RawAiConcurrencyDefinition definition)
        {
            if (definition.Policies is null || definition.Policies.Count == 0)
            {
                return definition;
            }

            foreach (var policy in definition.Policies)
            {
                if (policy.Config.Count == 0)
                {
                    continue;
                }

                AddThrottleRuleIfConfigured(
                    definition,
                    policy);

                definition.Enabled ??= TryReadBool(policy.Config, "enabled");

                definition.MaxDegreeOfParallelism ??= TryReadInt(policy.Config, "maxDegreeOfParallelism");

                definition.MaxGlobalConcurrency ??= TryReadInt(policy.Config, "maxGlobalConcurrency");
                definition.MaxPipelineConcurrency ??= TryReadInt(policy.Config, "maxPipelineConcurrency");
                definition.MaxStepConcurrency ??= TryReadInt(policy.Config, "maxStepConcurrency");
                definition.MaxExecutionConcurrency ??= TryReadInt(policy.Config, "maxExecutionConcurrency");
                definition.MaxInstanceConcurrency ??= TryReadInt(policy.Config, "maxInstanceConcurrency");

                definition.MaxProviderConcurrency ??= TryReadInt(policy.Config, "maxProviderConcurrency");
                definition.MaxModelConcurrency ??= TryReadInt(policy.Config, "maxModelConcurrency");
                definition.MaxOperationConcurrency ??= TryReadInt(policy.Config, "maxOperationConcurrency");

                definition.DefaultRetryAfterMs ??= TryReadInt(policy.Config, "defaultRetryAfterMs");
                definition.LeaseSeconds ??= TryReadInt(policy.Config, "leaseSeconds");

                definition.Jitter ??= TryReadBool(policy.Config, "jitter");
                definition.MaxJitterMs ??= TryReadInt(policy.Config, "maxJitterMs");
            }

            return definition;
        }

        /// <summary>
        /// Adds a generic throttle rule when a configured policy declares scope and limit.
        /// </summary>
        /// <param name="definition">
        /// The raw definition being enriched.
        /// </param>
        /// <param name="policy">
        /// The configured policy definition.
        /// </param>
        /// <remarks>
        /// <para>
        /// This supports:
        /// </para>
        ///
        /// <code>
        /// {
        ///   "name": "concurrency.throttle",
        ///   "config": {
        ///     "scope": "provider",
        ///     "target": "openai",
        ///     "limit": 10
        ///   }
        /// }
        /// </code>
        /// </remarks>
        private static void AddThrottleRuleIfConfigured(
            RawAiConcurrencyDefinition definition,
            AiConfiguredPolicyDefinition policy)
        {
            if (!string.Equals(
                    policy.Name,
                    "concurrency.throttle",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var scope = TryReadString(policy.Config, "scope");
            var limit = TryReadInt(policy.Config, "limit");

            if (string.IsNullOrWhiteSpace(scope) || limit is null or <= 0)
            {
                return;
            }

            definition.ThrottleRules ??= new List<AiConcurrencyThrottleRule>();

            definition.ThrottleRules.Add(
                new AiConcurrencyThrottleRule
                {
                    Scope = scope,
                    Target = TryReadString(policy.Config, "target"),
                    Limit = limit.Value,
                    LeaseSeconds = TryReadInt(policy.Config, "leaseSeconds"),
                    DefaultRetryAfterMs = TryReadInt(policy.Config, "defaultRetryAfterMs")
                });
        }

        /// <summary>
        /// Merges pipeline-level and step-level raw concurrency definitions.
        /// </summary>
        /// <param name="pipeline">
        /// The pipeline-level raw concurrency definition.
        /// </param>
        /// <param name="step">
        /// The step-level raw concurrency definition.
        /// </param>
        /// <returns>
        /// The merged raw concurrency definition.
        /// </returns>
        private static RawAiConcurrencyDefinition Merge(
            RawAiConcurrencyDefinition pipeline,
            RawAiConcurrencyDefinition step)
        {
            return new RawAiConcurrencyDefinition
            {
                Enabled = step.Enabled ?? pipeline.Enabled,

                Policies = step.Policies is { Count: > 0 }
                    ? step.Policies
                    : pipeline.Policies,

                ThrottleRules = MergeThrottleRules(
                    pipeline.ThrottleRules,
                    step.ThrottleRules),

                MaxDegreeOfParallelism = step.MaxDegreeOfParallelism
                    ?? pipeline.MaxDegreeOfParallelism,

                MaxGlobalConcurrency = step.MaxGlobalConcurrency
                    ?? pipeline.MaxGlobalConcurrency,

                MaxPipelineConcurrency = step.MaxPipelineConcurrency
                    ?? pipeline.MaxPipelineConcurrency,

                MaxStepConcurrency = step.MaxStepConcurrency
                    ?? pipeline.MaxStepConcurrency,

                MaxExecutionConcurrency = step.MaxExecutionConcurrency
                    ?? pipeline.MaxExecutionConcurrency,

                MaxInstanceConcurrency = step.MaxInstanceConcurrency
                    ?? pipeline.MaxInstanceConcurrency,

                MaxProviderConcurrency = step.MaxProviderConcurrency
                    ?? pipeline.MaxProviderConcurrency,

                MaxModelConcurrency = step.MaxModelConcurrency
                    ?? pipeline.MaxModelConcurrency,

                MaxOperationConcurrency = step.MaxOperationConcurrency
                    ?? pipeline.MaxOperationConcurrency,

                DefaultRetryAfterMs = step.DefaultRetryAfterMs
                    ?? pipeline.DefaultRetryAfterMs,

                LeaseSeconds = step.LeaseSeconds
                    ?? pipeline.LeaseSeconds,

                Jitter = step.Jitter
                    ?? pipeline.Jitter,

                MaxJitterMs = step.MaxJitterMs
                    ?? pipeline.MaxJitterMs
            };
        }

        /// <summary>
        /// Merges pipeline-level and step-level throttle rules.
        /// </summary>
        /// <param name="pipelineRules">
        /// The pipeline-level rules.
        /// </param>
        /// <param name="stepRules">
        /// The step-level rules.
        /// </param>
        /// <returns>
        /// The combined throttle rules.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Unlike the policy list, throttle rules are additive. Pipeline rules and step rules
        /// can both apply, and direct configured concurrency values still remain authoritative
        /// during effective definition application.
        /// </para>
        /// </remarks>
        private static List<AiConcurrencyThrottleRule> MergeThrottleRules(
            List<AiConcurrencyThrottleRule>? pipelineRules,
            List<AiConcurrencyThrottleRule>? stepRules)
        {
            var result = new List<AiConcurrencyThrottleRule>();

            if (pipelineRules is { Count: > 0 })
            {
                result.AddRange(pipelineRules);
            }

            if (stepRules is { Count: > 0 })
            {
                result.AddRange(stepRules);
            }

            return result;
        }

        /// <summary>
        /// Converts a raw nullable concurrency definition into the runtime definition.
        /// </summary>
        /// <param name="definition">
        /// The raw nullable definition.
        /// </param>
        /// <returns>
        /// A normalized <see cref="AiConcurrencyDefinition"/>.
        /// </returns>
        private static AiConcurrencyDefinition ToRuntimeDefinition(
            RawAiConcurrencyDefinition definition)
        {
            return new AiConcurrencyDefinition
            {
                Enabled = definition.Enabled ?? false,

                Policies = definition.Policies ?? new List<AiConfiguredPolicyDefinition>(),
                ThrottleRules = definition.ThrottleRules ?? new List<AiConcurrencyThrottleRule>(),

                MaxDegreeOfParallelism = definition.MaxDegreeOfParallelism,

                MaxGlobalConcurrency = definition.MaxGlobalConcurrency,
                MaxPipelineConcurrency = definition.MaxPipelineConcurrency,
                MaxStepConcurrency = definition.MaxStepConcurrency,
                MaxExecutionConcurrency = definition.MaxExecutionConcurrency,
                MaxInstanceConcurrency = definition.MaxInstanceConcurrency,

                MaxProviderConcurrency = definition.MaxProviderConcurrency,
                MaxModelConcurrency = definition.MaxModelConcurrency,
                MaxOperationConcurrency = definition.MaxOperationConcurrency,

                DefaultRetryAfterMs = definition.DefaultRetryAfterMs is > 0
                    ? definition.DefaultRetryAfterMs.Value
                    : 250,

                LeaseSeconds = definition.LeaseSeconds is > 0
                    ? definition.LeaseSeconds.Value
                    : 300,

                Jitter = definition.Jitter ?? false,

                MaxJitterMs = definition.MaxJitterMs is > 0
                    ? definition.MaxJitterMs.Value
                    : 100
            };
        }

        /// <summary>
        /// Attempts to read an integer value from policy configuration.
        /// </summary>
        private static int? TryReadInt(
            IReadOnlyDictionary<string, object?> config,
            string key)
        {
            if (!TryGetValue(config, key, out var value) || value is null)
            {
                return null;
            }

            return value switch
            {
                int number => number,
                long number when number is <= int.MaxValue and >= int.MinValue => (int)number,
                double number when number % 1 == 0 => (int)number,
                decimal number when number % 1 == 0 => (int)number,
                string text when int.TryParse(text, out var number) => number,
                JsonElement element when element.ValueKind == JsonValueKind.Number &&
                                         element.TryGetInt32(out var number) => number,
                JsonElement element when element.ValueKind == JsonValueKind.String &&
                                         int.TryParse(element.GetString(), out var number) => number,
                _ => null
            };
        }

        /// <summary>
        /// Attempts to read a boolean value from policy configuration.
        /// </summary>
        private static bool? TryReadBool(
            IReadOnlyDictionary<string, object?> config,
            string key)
        {
            if (!TryGetValue(config, key, out var value) || value is null)
            {
                return null;
            }

            return value switch
            {
                bool boolean => boolean,
                string text when bool.TryParse(text, out var boolean) => boolean,
                JsonElement element when element.ValueKind == JsonValueKind.True => true,
                JsonElement element when element.ValueKind == JsonValueKind.False => false,
                JsonElement element when element.ValueKind == JsonValueKind.String &&
                                         bool.TryParse(element.GetString(), out var boolean) => boolean,
                _ => null
            };
        }

        /// <summary>
        /// Attempts to read a string value from policy configuration.
        /// </summary>
        private static string? TryReadString(
            IReadOnlyDictionary<string, object?> config,
            string key)
        {
            if (!TryGetValue(config, key, out var value) || value is null)
            {
                return null;
            }

            return value switch
            {
                string text when !string.IsNullOrWhiteSpace(text) => text,
                JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
                _ => value.ToString()
            };
        }

        /// <summary>
        /// Attempts to read a value from a dictionary using case-insensitive key matching.
        /// </summary>
        private static bool TryGetValue(
            IReadOnlyDictionary<string, object?> config,
            string key,
            out object? value)
        {
            if (config.TryGetValue(key, out value))
            {
                return true;
            }

            foreach (var pair in config)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }

            value = null;
            return false;
        }

        /// <summary>
        /// Internal nullable representation of concurrency configuration.
        /// </summary>
        private sealed class RawAiConcurrencyDefinition
        {
            /// <summary>
            /// Gets or sets whether distributed concurrency admission is enabled.
            /// </summary>
            public bool? Enabled { get; set; }

            /// <summary>
            /// Gets or sets configured concurrency policies.
            /// </summary>
            public List<AiConfiguredPolicyDefinition>? Policies { get; set; }

            /// <summary>
            /// Gets or sets generic throttle rules resolved from configured policies.
            /// </summary>
            public List<AiConcurrencyThrottleRule>? ThrottleRules { get; set; }

            /// <summary>
            /// Gets or sets the local maximum degree of parallelism.
            /// </summary>
            public int? MaxDegreeOfParallelism { get; set; }

            /// <summary>
            /// Gets or sets the global runtime concurrency limit.
            /// </summary>
            public int? MaxGlobalConcurrency { get; set; }

            /// <summary>
            /// Gets or sets the pipeline-level concurrency limit.
            /// </summary>
            public int? MaxPipelineConcurrency { get; set; }

            /// <summary>
            /// Gets or sets the pipeline-step concurrency limit.
            /// </summary>
            public int? MaxStepConcurrency { get; set; }

            /// <summary>
            /// Gets or sets the execution-level concurrency limit.
            /// </summary>
            public int? MaxExecutionConcurrency { get; set; }

            /// <summary>
            /// Gets or sets the runtime-instance concurrency limit.
            /// </summary>
            public int? MaxInstanceConcurrency { get; set; }

            /// <summary>
            /// Gets or sets the provider-level concurrency limit.
            /// </summary>
            public int? MaxProviderConcurrency { get; set; }

            /// <summary>
            /// Gets or sets the model-level concurrency limit.
            /// </summary>
            public int? MaxModelConcurrency { get; set; }

            /// <summary>
            /// Gets or sets the operation-level concurrency limit.
            /// </summary>
            public int? MaxOperationConcurrency { get; set; }

            /// <summary>
            /// Gets or sets the retry-after delay in milliseconds when admission is denied.
            /// </summary>
            public int? DefaultRetryAfterMs { get; set; }

            /// <summary>
            /// Gets or sets the Redis lease duration in seconds.
            /// </summary>
            public int? LeaseSeconds { get; set; }

            /// <summary>
            /// Gets or sets whether retry-after jitter is enabled.
            /// </summary>
            public bool? Jitter { get; set; }

            /// <summary>
            /// Gets or sets the maximum jitter delay in milliseconds.
            /// </summary>
            public int? MaxJitterMs { get; set; }

            /// <summary>
            /// Converts an already constructed runtime definition into a raw definition.
            /// </summary>
            /// <param name="definition">
            /// The runtime definition.
            /// </param>
            /// <returns>
            /// A raw definition containing the runtime values.
            /// </returns>
            public static RawAiConcurrencyDefinition FromRuntimeDefinition(
                AiConcurrencyDefinition definition)
            {
                ArgumentNullException.ThrowIfNull(definition);

                return new RawAiConcurrencyDefinition
                {
                    Enabled = definition.Enabled,
                    Policies = definition.Policies,
                    ThrottleRules = definition.ThrottleRules,
                    MaxDegreeOfParallelism = definition.MaxDegreeOfParallelism,
                    MaxGlobalConcurrency = definition.MaxGlobalConcurrency,
                    MaxPipelineConcurrency = definition.MaxPipelineConcurrency,
                    MaxStepConcurrency = definition.MaxStepConcurrency,
                    MaxExecutionConcurrency = definition.MaxExecutionConcurrency,
                    MaxInstanceConcurrency = definition.MaxInstanceConcurrency,
                    MaxProviderConcurrency = definition.MaxProviderConcurrency,
                    MaxModelConcurrency = definition.MaxModelConcurrency,
                    MaxOperationConcurrency = definition.MaxOperationConcurrency,
                    DefaultRetryAfterMs = definition.DefaultRetryAfterMs,
                    LeaseSeconds = definition.LeaseSeconds,
                    Jitter = definition.Jitter,
                    MaxJitterMs = definition.MaxJitterMs
                };
            }
        }
    }
}