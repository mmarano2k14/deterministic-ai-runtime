using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Publication;

namespace Multiplexed.AI.Runtime.Publication
{
    /// <summary>Authorized code publication using the existing plan resolver and immutable payload store.</summary>
    public sealed class AiPipelinePublicationService
    {
        private readonly AiPublicationIdentity _identity;
        private readonly AiPublicationOptions _options;
        private readonly IAiPublicationEnvironmentCatalog _catalog;
        private readonly IAiPipelineResolver _resolver;
        private readonly AiImmutablePublicationStore _store;
        public AiPipelinePublicationService(AiPublicationIdentity identity, AiPublicationOptions options,
            IAiPublicationEnvironmentCatalog catalog, IAiPipelineResolver resolver, AiImmutablePublicationStore store)
        { _identity = identity; _options = options; _catalog = catalog; _resolver = resolver; _store = store; }

        public async Task<AiPipelinePublication> PublishAsync(AiDurableInvocationScope scope, AiPipelinePublicationUpload upload,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var captured = AiPublicationCompiler.Capture(upload, _options);
            var guard = await _identity.AuthorizeAsync(scope, _options.Publish, cancellationToken).ConfigureAwait(false);
            var compiled = AiPublicationCompiler.Compile(captured, guard.Partition, _catalog);
            // Existing structure validation and binding only. No invocation body or policy is executed here.
            await _resolver.ResolveAsync(compiled.Definition, cancellationToken).ConfigureAwait(false);
            guard.RequireCurrent(); cancellationToken.ThrowIfCancellationRequested();
            await _store.SaveAsync(compiled, guard, cancellationToken).ConfigureAwait(false);
            return compiled.Publication;
        }

        public async Task<AiPipelinePublication> ReadAsync(AiDurableInvocationScope scope, string publicationRef,
            CancellationToken cancellationToken = default)
        {
            var guard = await _identity.AuthorizeAsync(scope, _options.Read, cancellationToken).ConfigureAwait(false);
            var result = await _store.ReadVerifiedAsync(publicationRef, guard, cancellationToken).ConfigureAwait(false);
            return result.Publication;
        }
    }
}
