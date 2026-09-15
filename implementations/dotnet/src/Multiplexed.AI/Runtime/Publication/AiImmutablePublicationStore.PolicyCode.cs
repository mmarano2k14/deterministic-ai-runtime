using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.Abstractions.AI.Publication;

namespace Multiplexed.AI.Runtime.Publication
{
    public sealed partial class AiImmutablePublicationStore
    {
        internal Task<AiWorkerCodeBundle> ReadPolicyWorkerCodeAsync(AiConcurrencyPolicyRequest request,AiPublicationIdentity.Guard guard,CancellationToken token)
            => ReadPolicyWorkerCodeCoreAsync(request.Context.ExecutionId,request.PolicyName,request.Scope,request.OwnerStepName,request.ExecutionLanguage,request.ImplementationRef,AiPublicationFunctionKind.ConcurrencyPolicy,guard,token);
        internal Task<AiWorkerCodeBundle> ReadRetryPolicyWorkerCodeAsync(AiRetryPolicyRequest request,AiPublicationIdentity.Guard guard,CancellationToken token)
            => ReadPolicyWorkerCodeCoreAsync(request.Context.ExecutionId,request.PolicyName,request.Scope,request.OwnerStepName,request.ExecutionLanguage,request.ImplementationRef,AiPublicationFunctionKind.RetryPolicy,guard,token);

        private async Task<AiWorkerCodeBundle> ReadPolicyWorkerCodeCoreAsync(string executionId,string policyName,string scope,string? ownerStepName,string language,string implementationRef,AiPublicationFunctionKind kind,AiPublicationIdentity.Guard guard,CancellationToken token)
        {
            var binding=await ReadExecutionBindingAsync(executionId,guard,token).ConfigureAwait(false) ?? throw new InvalidOperationException("The original execution publication binding is unavailable.");
            var frozen=await ReadVerifiedAsync(binding.PublicationRef,guard,token).ConfigureAwait(false); var publication=frozen.Publication;
            if(publication.PublicationSha256!=binding.PublicationSha256) throw new InvalidOperationException("The custom policy no longer matches its immutable execution binding.");
            var boundDefinition=binding.DefinitionPath is null?frozen.Definition:AiPublicationDefinitionPath.Resolve(frozen.Definition,binding.DefinitionPath);
            if(AiPublicationJson.Hash(AiPublicationJson.Serialize(boundDefinition))!=binding.DefinitionSha256) throw new InvalidOperationException("Custom policy definition does not match its immutable execution binding.");
            var expectedOwner=scope=="Pipeline"?null:ownerStepName;
            var matches=publication.Manifest.Functions.Where(f=>f.Site.Kind==kind && string.Equals(f.Site.DefinitionPath,binding.DefinitionPath,StringComparison.Ordinal) && f.Site.StepName==expectedOwner && f.LogicalName==policyName && f.ExecutionLanguage==language && f.ImplementationRef==implementationRef).ToArray();
            if(matches.Length==0) throw new InvalidOperationException("The pinned custom policy call site is missing or does not match its resolved binding.");
            var function=matches[0]; if(matches.Any(c=>c.Implementation!=function.Implementation||c.Environment!=function.Environment)) throw new InvalidOperationException("Equivalent policy bindings reference inconsistent immutable code metadata.");
            var target=new AiDurableInvocationTarget(boundDefinition.Name,boundDefinition.Version!,binding.DefinitionSha256,publication.PublicationRef,publication.PublicationSha256,function.ImplementationRef,function.Implementation.Sha256,function.ExecutionLanguage,"env-"+function.Environment.Sha256,function.Environment.Sha256);
            var implementation=AiPublicationJson.Read<AiPublicationImplementation>(await DocumentAsync(function.Implementation,"implementation",guard,token).ConfigureAwait(false));
            var environment=AiPublicationJson.Read<AiPublicationEnvironmentSnapshot>(await DocumentAsync(function.Environment,"environment",guard,token).ConfigureAwait(false));
            if(implementation.SchemaVersion!=1||implementation.ExecutionLanguage!=language||implementation.Environment!=function.Environment||environment.Runtime.ExecutionLanguage!=language) throw new InvalidOperationException("Pinned custom policy implementation metadata is inconsistent.");
            AiPublicationExecutionDescriptors.RequirePinned(environment,language,_catalog);
            async Task<IReadOnlyList<AiWorkerFile>> Files(IReadOnlyList<AiPublicationFile> files)
            { var values=new List<AiWorkerFile>(files.Count); foreach(var file in files){var json=await DocumentAsync(file.Payload,"file",guard,token).ConfigureAwait(false);var envelope=AiPublicationJson.Read<AiPublicationCompiler.FileEnvelope>(json);var bytes=AiPublicationJson.DecodeBytes(envelope.Base64Url);if(envelope.SchemaVersion!=1||envelope.SizeBytes!=file.SizeBytes||envelope.ContentSha256!=file.ContentSha256||bytes.LongLength!=file.SizeBytes||AiPublicationJson.HashBytes(bytes)!=file.ContentSha256)throw new InvalidOperationException("Selected custom policy file failed content verification.");values.Add(new AiWorkerFile(file.Path,file.ContentSha256,file.SizeBytes,envelope.Base64Url));}return values.AsReadOnly(); }
            var sources=await Files(implementation.Sources).ConfigureAwait(false); var dependencies=new List<AiWorkerDependency>(environment.Dependencies.Count); foreach(var dependency in environment.Dependencies) dependencies.Add(new AiWorkerDependency(dependency.Name,dependency.Version,await Files(dependency.Files).ConfigureAwait(false)));
            guard.RequireCurrent(); token.ThrowIfCancellationRequested(); return new AiWorkerCodeBundle(target,environment.Runtime,implementation.EntryPointPath,implementation.EntryPointSymbol,sources,dependencies.AsReadOnly()){ExecutionDescriptor=environment.ExecutionDescriptor};
        }
    }
}
