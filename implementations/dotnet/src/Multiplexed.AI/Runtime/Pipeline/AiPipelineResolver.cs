using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.AI.Runtime.Invocation;

namespace Multiplexed.AI.Runtime.Pipeline
{
    /// <summary>
    /// Resolves provider-neutral AI pipeline definitions into executable runtime plans.
    ///
    /// Architecture:
    /// Definition (JSON / DB / Memory)
    ///     → Resolver
    ///     → Resolved Pipeline
    ///     → Execution Engine
    ///     → Step Executor
    ///
    /// Responsibilities:
    /// - Validate pipeline structure (names, dependencies, cycles)
    /// - Resolve runtime step instances from StepKey
    /// - Apply runtime defaults for optional step behavior
    /// - Produce a deterministic, executable pipeline representation
    ///
    /// IMPORTANT:
    /// This layer is responsible for structural correctness.
    /// No invalid pipeline should reach the execution engine.
    /// </summary>
    public sealed class AiPipelineResolver : IAiPipelineResolver
    {
        /// <summary>
        /// Default retry count used when a step definition does not provide
        /// an explicit execution retry policy.
        /// </summary>
        private const int DefaultRetryMaxCount = 1;

        /// <summary>
        /// Default retry delay in milliseconds used when a step definition does not provide
        /// an explicit execution retry policy.
        /// </summary>
        private const int DefaultRetryDelayMs = 500;

        private readonly IAiStepRegistry _stepRegistry;
        private static readonly AiInvocationBindingResolver InvocationResolver = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="AiPipelineResolver"/> class.
        /// </summary>
        /// <param name="stepRegistry">
        /// Registry used to resolve runtime step implementations.
        /// </param>
        public AiPipelineResolver(IAiStepRegistry stepRegistry)
        {
            ArgumentNullException.ThrowIfNull(stepRegistry);
            _stepRegistry = stepRegistry;
        }

        /// <summary>
        /// Resolves a declarative pipeline definition into an executable pipeline.
        /// </summary>
        public Task<ResolvedAiPipeline> ResolveAsync(
            AiPipelineDefinition definition,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(definition);

            if (string.IsNullOrWhiteSpace(definition.Name))
            {
                throw new ArgumentException(
                    "Pipeline name cannot be null or empty.",
                    nameof(definition));
            }

            if (definition.Steps.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Pipeline '{definition.Name}' does not define any steps.");
            }

            // --- VALIDATION PHASE ---
            ValidateStepDefinitions(definition);
            ValidateAcyclicGraph(definition);
            InvocationResolver.ValidatePipelineLanguage(definition);

            // Preflight all declarations before touching the native registry. ML1-A
            // intentionally has no custom/MCP adapter installed. Never resolve their
            // StepKey as a native implementation, even when that key happens to exist.
            foreach (var stepDefinition in definition.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stepDefinition.Invocation is null && stepDefinition.ExecutionLanguage is null)
                {
                    continue;
                }
                var binding = InvocationResolver.ResolveStep(definition, stepDefinition);
                if (binding.Kind != AiInvocationKind.Native)
                {
                    throw new NotSupportedException(
                        $"Step '{stepDefinition.Name}' requires a '{binding.Kind}' invocation adapter. " +
                        "No such adapter is installed by ML1-A; native fallback is forbidden.");
                }
            }

            // --- RESOLUTION PHASE ---
            var resolvedSteps = new List<ResolvedAiPipelineStep>();

            foreach (var stepDefinition in definition.Steps.OrderBy(x => x.Order))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var step = _stepRegistry.Resolve(stepDefinition.StepKey);

                // Apply runtime defaults here, not in the definition model.
                // Execution policy is now defined through the dedicated Execution section.
                var maxRetries = stepDefinition.Execution?.MaxRetries ?? DefaultRetryMaxCount;
                var retryDelayMs = stepDefinition.Execution?.RetryDelayMs ?? DefaultRetryDelayMs;

                resolvedSteps.Add(new ResolvedAiPipelineStep
                {
                    Name = stepDefinition.Name,
                    StepKey = stepDefinition.StepKey,
                    ExecutionLanguage = stepDefinition.ExecutionLanguage,
                    Invocation = stepDefinition.Invocation,
                    InvocationBinding = stepDefinition.Invocation is not null
                        ? AiInvocationBinding.Native
                        : null,
                    Step = step,
                    Order = stepDefinition.Order,
                    DependsOn = stepDefinition.DependsOn,
                    Input = stepDefinition.Input,
                    Config = stepDefinition.Config,
                    MaxRetries = maxRetries,
                    RetryDelayMs = retryDelayMs
                });
            }

            return Task.FromResult(new ResolvedAiPipeline
            {
                Name = definition.Name,
                Version = definition.Version,
                ExecutionLanguage = definition.ExecutionLanguage,
                ExecutionMode = definition.ExecutionMode,
                Config = definition.Config,
                Steps = resolvedSteps.OrderBy(x => x.Order).ToArray()
            });
        }

        /// <summary>
        /// Validates basic step constraints:
        /// - Name must exist
        /// - StepKey must exist
        /// - Names must be unique
        /// - Dependencies must exist
        /// - No self-dependency
        /// - Retry settings must not be negative
        /// </summary>
        private static void ValidateStepDefinitions(AiPipelineDefinition definition)
        {
            var stepsByName = new Dictionary<string, AiPipelineStepDefinition>(StringComparer.Ordinal);

            foreach (var step in definition.Steps)
            {
                if (string.IsNullOrWhiteSpace(step.Name))
                {
                    throw new InvalidOperationException("Pipeline step name cannot be null or empty.");
                }

                if (string.IsNullOrWhiteSpace(step.StepKey))
                {
                    throw new InvalidOperationException(
                        $"Step '{step.Name}' does not define a valid StepKey.");
                }

                if (step.Execution?.MaxRetries < 0)
                {
                    throw new InvalidOperationException(
                        $"Step '{step.Name}' defines an invalid RetryMaxCount '{step.Execution.MaxRetries}'.");
                }

                if (step.Execution?.RetryDelayMs < 0)
                {
                    throw new InvalidOperationException(
                        $"Step '{step.Name}' defines an invalid RetryDelayMs '{step.Execution.RetryDelayMs}'.");
                }

                if (!stepsByName.TryAdd(step.Name, step))
                {
                    throw new InvalidOperationException(
                        $"Duplicate step name '{step.Name}' detected.");
                }
            }

            foreach (var step in definition.Steps)
            {
                foreach (var dep in step.DependsOn)
                {
                    if (string.IsNullOrWhiteSpace(dep))
                    {
                        throw new InvalidOperationException(
                            $"Step '{step.Name}' contains an empty dependency.");
                    }

                    if (dep == step.Name)
                    {
                        throw new InvalidOperationException(
                            $"Step '{step.Name}' cannot depend on itself.");
                    }

                    if (!stepsByName.ContainsKey(dep))
                    {
                        throw new InvalidOperationException(
                            $"Step '{step.Name}' depends on unknown step '{dep}'.");
                    }
                }
            }
        }

        /// <summary>
        /// Validates that the dependency graph is acyclic (DAG constraint).
        ///
        /// Uses depth-first search (DFS):
        /// - visited: nodes already validated
        /// - visiting: current recursion stack
        ///
        /// If a node is revisited while still in "visiting", a cycle exists.
        /// </summary>
        private static void ValidateAcyclicGraph(AiPipelineDefinition definition)
        {
            var stepsByName = definition.Steps.ToDictionary(x => x.Name, StringComparer.Ordinal);

            var visited = new HashSet<string>(StringComparer.Ordinal);
            var visiting = new HashSet<string>(StringComparer.Ordinal);

            foreach (var step in definition.Steps)
            {
                if (!visited.Contains(step.Name))
                {
                    Visit(step.Name, stepsByName, visited, visiting);
                }
            }
        }

        /// <summary>
        /// DFS traversal used for cycle detection.
        /// </summary>
        private static void Visit(
            string stepName,
            IReadOnlyDictionary<string, AiPipelineStepDefinition> stepsByName,
            ISet<string> visited,
            ISet<string> visiting)
        {
            if (visited.Contains(stepName))
            {
                return;
            }

            if (visiting.Contains(stepName))
            {
                throw new InvalidOperationException(
                    $"Circular dependency detected involving step '{stepName}'.");
            }

            visiting.Add(stepName);

            var step = stepsByName[stepName];

            foreach (var dep in step.DependsOn)
            {
                Visit(dep, stepsByName, visited, visiting);
            }

            visiting.Remove(stepName);
            visited.Add(stepName);
        }
    }
}