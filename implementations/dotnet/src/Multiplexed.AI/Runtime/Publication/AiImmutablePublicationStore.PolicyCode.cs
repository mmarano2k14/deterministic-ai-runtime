using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.Abstractions.AI.Publication;

namespace Multiplexed.AI.Runtime.Publication
{
    public sealed partial class AiImmutablePublicationStore
    {
        /// <summary>
        /// Resolves one concurrency policy only through the publication pinned to the
        /// execution. The selected implementation and environment are verified again
        /// before portable bytes are projected to the hosted worker transport.
        /// </summary>
        internal async Task<AiWorkerCodeBundle> ReadPolicyWorkerCodeAsync(
            AiConcurrencyPolicyRequest request,
            AiPublicationIdentity.Guard guard,
            CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(request);
            var pin = await ReadPinAsync(request.Context.ExecutionId, guard, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The original run publication pin is unavailable.");
            var frozen = await ReadVerifiedAsync(pin.PublicationRef, guard, token).ConfigureAwait(false);
            var publication = frozen.Publication;
            if (publication.PublicationSha256 != pin.PublicationSha256 ||
                publication.Manifest.Definition.Sha256 != pin.DefinitionSha256)
            {
                throw new InvalidOperationException("The custom policy no longer matches its immutable run pin.");
            }

            var expectedOwner = request.Scope == "Pipeline" ? null : request.OwnerStepName;
            var matches = publication.Manifest.Functions.Where(f =>
                f.Site.Kind == AiPublicationFunctionKind.ConcurrencyPolicy &&
                f.Site.DefinitionPath is null &&
                f.Site.StepName == expectedOwner &&
                f.LogicalName == request.PolicyName &&
                f.ExecutionLanguage == request.ExecutionLanguage &&
                f.ImplementationRef == request.ImplementationRef).ToArray();
            if (matches.Length == 0)
            {
                throw new InvalidOperationException("The pinned custom policy call site is missing or does not match its resolved binding.");
            }
            var function = matches[0];
            if (matches.Any(candidate => candidate.Implementation != function.Implementation || candidate.Environment != function.Environment))
            {
                throw new InvalidOperationException("Equivalent policy bindings reference inconsistent immutable code metadata.");
            }

            var target = new AiDurableInvocationTarget(
                publication.Manifest.PipelineName,
                publication.Manifest.PipelineVersion,
                publication.Manifest.Definition.Sha256,
                publication.PublicationRef,
                publication.PublicationSha256,
                function.ImplementationRef,
                function.Implementation.Sha256,
                function.ExecutionLanguage,
                "env-" + function.Environment.Sha256,
                function.Environment.Sha256);

            var implementation = AiPublicationJson.Read<AiPublicationImplementation>(
                await DocumentAsync(function.Implementation, "implementation", guard, token).ConfigureAwait(false));
            var environment = AiPublicationJson.Read<AiPublicationEnvironmentSnapshot>(
                await DocumentAsync(function.Environment, "environment", guard, token).ConfigureAwait(false));
            if (implementation.SchemaVersion != 1 || implementation.ExecutionLanguage != request.ExecutionLanguage ||
                implementation.Environment != function.Environment ||
                environment.Runtime.ExecutionLanguage != request.ExecutionLanguage)
            {
                throw new InvalidOperationException("Pinned custom policy implementation metadata is inconsistent.");
            }

            AiPublicationExecutionDescriptors.RequirePinned(environment, request.ExecutionLanguage, _catalog);

            async Task<IReadOnlyList<AiWorkerFile>> Files(IReadOnlyList<AiPublicationFile> files)
            {
                var values = new List<AiWorkerFile>(files.Count);
                foreach (var file in files)
                {
                    var json = await DocumentAsync(file.Payload, "file", guard, token).ConfigureAwait(false);
                    var envelope = AiPublicationJson.Read<AiPublicationCompiler.FileEnvelope>(json);
                    var bytes = AiPublicationJson.DecodeBytes(envelope.Base64Url);
                    if (envelope.SchemaVersion != 1 || envelope.SizeBytes != file.SizeBytes ||
                        envelope.ContentSha256 != file.ContentSha256 || bytes.LongLength != file.SizeBytes ||
                        AiPublicationJson.HashBytes(bytes) != file.ContentSha256)
                    {
                        throw new InvalidOperationException("Selected custom policy file failed content verification.");
                    }
                    values.Add(new AiWorkerFile(file.Path, file.ContentSha256, file.SizeBytes, envelope.Base64Url));
                }
                return values.AsReadOnly();
            }

            var sources = await Files(implementation.Sources).ConfigureAwait(false);
            var dependencies = new List<AiWorkerDependency>(environment.Dependencies.Count);
            foreach (var dependency in environment.Dependencies)
            {
                dependencies.Add(new AiWorkerDependency(
                    dependency.Name,
                    dependency.Version,
                    await Files(dependency.Files).ConfigureAwait(false)));
            }

            guard.RequireCurrent();
            token.ThrowIfCancellationRequested();
            return new AiWorkerCodeBundle(
                target,
                environment.Runtime,
                implementation.EntryPointPath,
                implementation.EntryPointSymbol,
                sources,
                dependencies.AsReadOnly()) { ExecutionDescriptor = environment.ExecutionDescriptor };
        }
    }
}
