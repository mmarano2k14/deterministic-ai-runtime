using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Publication;

namespace Multiplexed.AI.Runtime.Publication
{
    /// <summary>Resolves a verified call site only through its previously pinned, authorized publication.</summary>
    public sealed class AiPublicationInvocationTargetResolver : IAiDurableInvocationTargetResolver
    {
        private readonly AiPublicationIdentity _identity;
        private readonly AiPublicationOptions _options;
        private readonly AiImmutablePublicationStore _store;
        public AiPublicationInvocationTargetResolver(AiPublicationIdentity identity, AiPublicationOptions options, AiImmutablePublicationStore store)
        { _identity = identity; _options = options; _store = store; }

        public async Task<AiDurableInvocationTarget?> ResolveAsync(AiDurableInvocationTargetRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            var guard = await _identity.AuthorizeAsync(request.Scope, _options.Execute, cancellationToken).ConfigureAwait(false);
            var binding = await _store.ReadExecutionBindingAsync(request.ExecutionId, guard, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("No immutable publication binding was pinned before execution.");
            if (!string.Equals(binding.DefinitionSha256, request.DefinitionSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The invocation definition differs from its publication binding.");

            var frozen = await _store.ReadVerifiedAsync(binding.PublicationRef, guard, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(frozen.Publication.PublicationSha256, binding.PublicationSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("The invocation does not belong to the exact admitted publication.");

            var definition = binding.DefinitionPath is null
                ? frozen.Definition
                : AiPublicationDefinitionPath.Resolve(frozen.Definition, binding.DefinitionPath);
            var definitionSha256 = AiPublicationJson.Hash(AiPublicationJson.Serialize(definition));
            if (!string.Equals(definitionSha256, binding.DefinitionSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(definition.Name, request.PipelineName, StringComparison.Ordinal) ||
                !string.Equals(definition.Version, request.PipelineVersion, StringComparison.Ordinal))
                throw new InvalidOperationException("The invocation does not belong to the exact publication-bound definition.");

            var function = frozen.Publication.Manifest.Functions.SingleOrDefault(f =>
                f.Site.Kind == AiPublicationFunctionKind.Step &&
                string.Equals(f.Site.DefinitionPath, binding.DefinitionPath, StringComparison.Ordinal) &&
                f.Site.StepName == request.StepName)
                ?? throw new InvalidOperationException("The published custom call site is missing.");
            if (function.LogicalName != request.StepKey || function.ExecutionLanguage != request.ExecutionLanguage ||
                function.ImplementationRef != request.ImplementationRef)
                throw new InvalidOperationException("The invocation binding differs from its immutable code declaration.");

            return new AiDurableInvocationTarget(
                definition.Name,
                definition.Version!,
                binding.DefinitionSha256,
                binding.PublicationRef,
                binding.PublicationSha256,
                function.ImplementationRef,
                function.Implementation.Sha256,
                function.ExecutionLanguage,
                "env-" + function.Environment.Sha256,
                function.Environment.Sha256);
        }
    }
}
