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
            var pin = await _store.ReadPinAsync(request.ExecutionId, guard, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("No whole-run publication was pinned before execution.");
            if (pin.DefinitionSha256 != request.DefinitionSha256)
                throw new InvalidOperationException("The invocation definition differs from its run admission.");
            var frozen = await _store.ReadVerifiedAsync(pin.PublicationRef, guard, cancellationToken).ConfigureAwait(false);
            var manifest = frozen.Publication.Manifest;
            if (manifest.Definition.Sha256 != request.DefinitionSha256 || manifest.PipelineName != request.PipelineName ||
                manifest.PipelineVersion != request.PipelineVersion || frozen.Publication.PublicationSha256 != pin.PublicationSha256)
                throw new InvalidOperationException("The invocation does not belong to the exact admitted publication.");
            var function = manifest.Functions.SingleOrDefault(f => f.Site.Kind == AiPublicationFunctionKind.Step && f.Site.StepName == request.StepName)
                ?? throw new InvalidOperationException("The published custom call site is missing.");
            if (function.LogicalName != request.StepKey || function.ExecutionLanguage != request.ExecutionLanguage || function.ImplementationRef != request.ImplementationRef)
                throw new InvalidOperationException("The invocation binding differs from its immutable code declaration.");
            return new AiDurableInvocationTarget(request.PipelineName, request.PipelineVersion, request.DefinitionSha256,
                pin.PublicationRef, pin.PublicationSha256, function.ImplementationRef, function.Implementation.Sha256,
                function.ExecutionLanguage, "env-" + function.Environment.Sha256, function.Environment.Sha256);
        }
    }
}
