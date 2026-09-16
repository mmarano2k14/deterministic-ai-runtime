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
            var definition = invocation.Definition;
            var expected = definition.Target;
            var binding = await ReadExecutionBindingAsync(definition.Identity.ExecutionId, guard, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The original execution publication binding is unavailable.");
            if (binding.PublicationRef != expected.PublicationRef || binding.PublicationSha256 != expected.PublicationSha256 ||
                !string.Equals(binding.DefinitionSha256, expected.DefinitionSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The invocation no longer matches its immutable publication binding.");

            var frozen = await ReadVerifiedAsync(binding.PublicationRef, guard, token).ConfigureAwait(false);
            var publication = frozen.Publication;
            if (publication.PublicationSha256 != binding.PublicationSha256)
                throw new InvalidOperationException("Worker materialization cannot substitute the bound publication.");

            var boundDefinition = binding.DefinitionPath is null
                ? frozen.Definition
                : AiPublicationDefinitionPath.Resolve(frozen.Definition, binding.DefinitionPath);
            var boundDefinitionSha256 = AiPublicationJson.Hash(AiPublicationJson.Serialize(boundDefinition));
            if (!string.Equals(boundDefinitionSha256, binding.DefinitionSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Worker materialization resolved a definition different from the execution binding.");

            var function = publication.Manifest.Functions.SingleOrDefault(f =>
                f.Site.Kind == AiPublicationFunctionKind.Step &&
                string.Equals(f.Site.DefinitionPath, binding.DefinitionPath, StringComparison.Ordinal) &&
                f.Site.StepName == definition.Identity.StepName)
                ?? throw new InvalidOperationException("The published worker call site is absent.");
            var actual = new AiDurableInvocationTarget(
                boundDefinition.Name,
                boundDefinition.Version!,
                binding.DefinitionSha256,
                publication.PublicationRef,
                publication.PublicationSha256,
                function.ImplementationRef,
                function.Implementation.Sha256,
                function.ExecutionLanguage,
                "env-" + function.Environment.Sha256,
                function.Environment.Sha256);
            if (actual != expected)
                throw new InvalidOperationException("Worker materialization cannot substitute code or environment versions.");

            var implementation = AiPublicationJson.Read<AiPublicationImplementation>(
                await DocumentAsync(function.Implementation, "implementation", guard, token).ConfigureAwait(false));
            var environment = AiPublicationJson.Read<AiPublicationEnvironmentSnapshot>(
                await DocumentAsync(function.Environment, "environment", guard, token).ConfigureAwait(false));
            AiPublicationExecutionDescriptors.RequirePinned(environment, actual.ExecutionLanguage, _catalog);
            AiDependencyPackagingContracts.RequireExecutionSupported(environment.Dependencies, actual.ExecutionLanguage);
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
