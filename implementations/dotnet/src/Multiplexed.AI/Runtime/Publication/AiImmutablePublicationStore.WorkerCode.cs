using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.Abstractions.AI.Publication;

namespace Multiplexed.AI.Runtime.Publication
{
    public sealed partial class AiImmutablePublicationStore
    {
        /// <summary>Reuses verified publication reads and projects bytes without exporting internal document keys.</summary>
        internal async Task<AiWorkerCodeBundle> ReadWorkerCodeAsync(AiDurableInvocationRecord invocation,
            AiPublicationIdentity.Guard guard, CancellationToken token)
        {
            var definition = invocation.Definition; var expected = definition.Target;
            var pin = await ReadPinAsync(definition.Identity.ExecutionId, guard, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The original run publication pin is unavailable.");
            if (pin.PublicationRef != expected.PublicationRef || pin.PublicationSha256 != expected.PublicationSha256 ||
                pin.DefinitionSha256 != expected.DefinitionSha256)
                throw new InvalidOperationException("The invocation no longer matches its immutable run pin.");
            var publication = (await ReadVerifiedAsync(pin.PublicationRef, guard, token).ConfigureAwait(false)).Publication;
            var function = publication.Manifest.Functions.SingleOrDefault(f =>
                f.Site.Kind == AiPublicationFunctionKind.Step && f.Site.StepName == definition.Identity.StepName)
                ?? throw new InvalidOperationException("The published worker call site is absent.");
            var actual = new AiDurableInvocationTarget(publication.Manifest.PipelineName, publication.Manifest.PipelineVersion,
                publication.Manifest.Definition.Sha256, publication.PublicationRef, publication.PublicationSha256,
                function.ImplementationRef, function.Implementation.Sha256, function.ExecutionLanguage,
                "env-" + function.Environment.Sha256, function.Environment.Sha256);
            if (actual != expected) throw new InvalidOperationException("Worker materialization cannot substitute code or environment versions.");
            var implementation = AiPublicationJson.Read<AiPublicationImplementation>(
                await DocumentAsync(function.Implementation, "implementation", guard, token).ConfigureAwait(false));
            var environment = AiPublicationJson.Read<AiPublicationEnvironmentSnapshot>(
                await DocumentAsync(function.Environment, "environment", guard, token).ConfigureAwait(false));
            AiPublicationExecutionDescriptors.RequirePinned(environment, actual.ExecutionLanguage, _catalog);
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
                        throw new InvalidOperationException("Selected worker file failed content verification.");
                    values.Add(new AiWorkerFile(file.Path, file.ContentSha256, file.SizeBytes, envelope.Base64Url));
                }
                return values.AsReadOnly();
            }
            var sources = await Files(implementation.Sources).ConfigureAwait(false);
            var dependencies = new List<AiWorkerDependency>(environment.Dependencies.Count);
            foreach (var dependency in environment.Dependencies)
                dependencies.Add(new(dependency.Name, dependency.Version, await Files(dependency.Files).ConfigureAwait(false)));
            guard.RequireCurrent(); token.ThrowIfCancellationRequested();
            return new(actual, environment.Runtime, implementation.EntryPointPath, implementation.EntryPointSymbol,
                sources, dependencies.AsReadOnly()) { ExecutionDescriptor = environment.ExecutionDescriptor };
        }
    }
}
