using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Policies;

namespace Multiplexed.AI.Runtime.Invocation
{
    /// <summary>
    /// Resolves declaration metadata without services, registry lookups, worker startup,
    /// mutation, or tenant caches. Recognizing a language does NOT install its handler.
    /// Publication authorization and executable adapter binding are separate operations.
    /// </summary>
    public sealed class AiInvocationBindingResolver
    {
        /// <summary>
        /// Validates the optional pipeline default. Null is absent; empty/unknown is invalid.
        /// The initial contract uses the exact tokens dotnet, python, and typescript.
        /// </summary>
        public void ValidatePipelineLanguage(AiPipelineDefinition pipeline)
        {
            ArgumentNullException.ThrowIfNull(pipeline);
            ValidateLanguage(pipeline.ExecutionLanguage, $"Pipeline '{pipeline.Name}'");
        }

        /// <summary>Resolves custom language as local override, otherwise pipeline default.</summary>
        public AiInvocationBinding ResolveStep(
            AiPipelineDefinition pipeline,
            AiPipelineStepDefinition step)
        {
            ArgumentNullException.ThrowIfNull(pipeline);
            ArgumentNullException.ThrowIfNull(step);
            ValidatePipelineLanguage(pipeline);

            var owner = $"Step '{step.Name}'";
            var kind = ValidateDescriptor(step.Invocation, owner, allowMcp: true);

            if (kind != AiInvocationKind.Custom)
            {
                RejectLocalLanguage(step.ExecutionLanguage, kind, owner);
                return kind == AiInvocationKind.Native
                    ? AiInvocationBinding.Native
                    : new AiInvocationBinding(
                        kind, null, AiExecutionLanguageSource.None,
                        ConnectionRef: step.Invocation!.ConnectionRef,
                        Tool: step.Invocation.Tool);
            }

            ValidateLanguage(step.ExecutionLanguage, owner);
            var language = step.ExecutionLanguage ?? pipeline.ExecutionLanguage;
            RequireLanguage(language, owner);

            return new AiInvocationBinding(
                kind,
                language,
                step.ExecutionLanguage is not null
                    ? AiExecutionLanguageSource.Step
                    : AiExecutionLanguageSource.Pipeline,
                ImplementationRef: step.Invocation!.ImplementationRef);
        }

        /// <summary>
        /// Resolves a policy using its original declaration scope. The caller must supply
        /// the owner, not infer scope from a merged list or the currently executing step.
        /// This method does not evaluate the policy or interpret its family's result.
        /// </summary>
        public AiPolicyInvocationBinding ResolvePolicy(
            AiPipelineDefinition pipeline,
            AiConfiguredPolicyDefinition policy,
            AiPolicyBindingScope scope,
            AiPipelineStepDefinition? ownerStep = null)
        {
            ArgumentNullException.ThrowIfNull(pipeline);
            ArgumentNullException.ThrowIfNull(policy);
            ArgumentException.ThrowIfNullOrWhiteSpace(policy.Name);
            ValidatePipelineLanguage(pipeline);

            if (!Enum.IsDefined(scope))
            {
                throw new InvalidOperationException($"Unknown policy scope '{scope}'.");
            }

            if ((scope == AiPolicyBindingScope.Step) != (ownerStep is not null))
            {
                throw new InvalidOperationException(
                    "A step policy requires its declaring step; a pipeline policy has no owner step.");
            }

            if (ownerStep is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(ownerStep.Name);
            }

            var owner = $"Policy '{policy.Name}'";
            var kind = ValidateDescriptor(policy.Invocation, owner, allowMcp: false);
            AiInvocationBinding invocation;

            if (kind == AiInvocationKind.Native)
            {
                RejectLocalLanguage(policy.ExecutionLanguage, kind, owner);
                invocation = AiInvocationBinding.Native;
            }
            else
            {
                // Resolve the declaring step, never a neighbouring/evaluation step.
                var stepBinding = ownerStep is null ? null : ResolveStep(pipeline, ownerStep);
                ValidateLanguage(policy.ExecutionLanguage, owner);
                var inheritedStepLanguage = stepBinding?.Kind == AiInvocationKind.Custom
                    ? stepBinding.ExecutionLanguage
                    : null;
                var language = policy.ExecutionLanguage
                    ?? inheritedStepLanguage
                    ?? pipeline.ExecutionLanguage;
                RequireLanguage(language, owner);

                var source = policy.ExecutionLanguage is not null
                    ? AiExecutionLanguageSource.Policy
                    : inheritedStepLanguage is not null
                        ? stepBinding!.LanguageSource
                        : AiExecutionLanguageSource.Pipeline;

                invocation = new AiInvocationBinding(
                    kind, language, source,
                    ImplementationRef: policy.Invocation!.ImplementationRef);
            }

            return new AiPolicyInvocationBinding(policy.Name, scope, ownerStep?.Name, invocation);
        }

        /// <summary>
        /// Protects name-only native policy engines until their contextual adapters exist.
        /// An unsupported custom declaration must never collapse into a native policy key.
        /// </summary>
        public static void EnsureNativePolicy(AiConfiguredPolicyDefinition policy)
        {
            ArgumentNullException.ThrowIfNull(policy);
            var owner = $"Policy '{policy.Name}'";
            var kind = ValidateDescriptor(policy.Invocation, owner, allowMcp: false);
            if (kind != AiInvocationKind.Native)
            {
                throw new NotSupportedException(
                    $"{owner} requires a contextual custom policy adapter. " +
                    "This native policy checkpoint cannot evaluate it; native fallback is forbidden.");
            }

            RejectLocalLanguage(policy.ExecutionLanguage, kind, owner);
        }

        private static AiInvocationKind ValidateDescriptor(
            AiInvocationDefinition? descriptor,
            string owner,
            bool allowMcp)
        {
            if (descriptor is null)
            {
                return AiInvocationKind.Native;
            }

            if (descriptor.Kind is null || !Enum.IsDefined(descriptor.Kind.Value))
            {
                throw new InvalidOperationException($"{owner} requires a known invocation kind.");
            }

            switch (descriptor.Kind.Value)
            {
                case AiInvocationKind.Native:
                    if (descriptor.ImplementationRef is not null ||
                        descriptor.ConnectionRef is not null || descriptor.Tool is not null)
                    {
                        throw new InvalidOperationException(
                            $"{owner}: a native invocation cannot declare custom or MCP references.");
                    }
                    break;

                case AiInvocationKind.Custom:
                    if (string.IsNullOrWhiteSpace(descriptor.ImplementationRef) ||
                        descriptor.ConnectionRef is not null || descriptor.Tool is not null)
                    {
                        throw new InvalidOperationException(
                            $"{owner}: custom invocation requires an implementationRef and no MCP fields.");
                    }
                    break;

                case AiInvocationKind.Mcp:
                    if (!allowMcp)
                    {
                        throw new InvalidOperationException($"{owner}: MCP is not a policy invocation kind.");
                    }
                    if (string.IsNullOrWhiteSpace(descriptor.ConnectionRef) ||
                        string.IsNullOrWhiteSpace(descriptor.Tool) || descriptor.ImplementationRef is not null)
                    {
                        throw new InvalidOperationException(
                            $"{owner}: MCP invocation requires connectionRef/tool and no custom implementationRef.");
                    }
                    break;
            }

            return descriptor.Kind.Value;
        }

        private static void ValidateLanguage(string? language, string owner)
        {
            if (language is not null && language is not (AiExecutionLanguages.DotNet or AiExecutionLanguages.Python or AiExecutionLanguages.TypeScript))
            {
                throw new InvalidOperationException(
                    $"{owner} declares unsupported execution language '{language}'. " +
                    "Use dotnet, python, or typescript; null means no declaration.");
            }
        }

        private static void RequireLanguage(string? language, string owner)
        {
            if (language is null)
            {
                throw new InvalidOperationException($"{owner}: custom invocation has no effective execution language.");
            }
        }

        private static void RejectLocalLanguage(string? language, AiInvocationKind kind, string owner)
        {
            if (language is not null)
            {
                throw new InvalidOperationException(
                    $"{owner}: {kind} invocation cannot declare a local execution language.");
            }
        }
    }
}
