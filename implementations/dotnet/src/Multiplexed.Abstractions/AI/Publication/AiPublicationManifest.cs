using System.Text.Json.Serialization;
using Multiplexed.Abstractions.AI.Invocation.Durable;

namespace Multiplexed.Abstractions.AI.Publication
{
    /// <summary>Storage partition derived from the current trusted RBAC and control-plane context.</summary>
    public sealed record AiPublicationPartition(AiDurableInvocationScope Scope, string Project, string Namespace);

    /// <summary>Verified immutable JSON document stored through the existing payload-store capability.</summary>
    public sealed record AiPublicationDocument(string Key, string Sha256, long SizeBytes);

    /// <summary>Byte hash is distinct from the hash of the JSON envelope carrying those bytes.</summary>
    public sealed record AiPublicationFile(
        string Path, string ContentSha256, long SizeBytes, AiPublicationDocument Payload);

    public sealed record AiPublicationDependency(string Name, string Version, IReadOnlyList<AiPublicationFile> Files)
    {
        /// <summary>
        /// Optional immutable package descriptor. Omitted for historical explicit-file dependencies.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AiPublicationDependencyPackage? Package { get; init; }
    }

    /// <summary>Exact host-approved runtime identity. A catalog entry is not proof of an available worker.</summary>
    public sealed record AiPublicationEnvironment(
        string Reference, string ExecutionLanguage, string RuntimeVersion, string RuntimeSha256);

    /// <summary>Effective environment includes the immutable dependency closure supplied at publication.</summary>
    public sealed record AiPublicationEnvironmentSnapshot(
        int SchemaVersion, AiPublicationEnvironment Runtime, IReadOnlyList<AiPublicationDependency> Dependencies)
    {
        // Omitted for historical schema 1: its canonical bytes and hashes must not change.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AiPublicationExecutionDescriptor? ExecutionDescriptor { get; init; }
    }

    public sealed record AiPublicationImplementation(
        int SchemaVersion, string ExecutionLanguage, string EntryPointPath, string EntryPointSymbol,
        IReadOnlyList<AiPublicationFile> Sources, AiPublicationDocument Environment);

    public sealed record AiPublicationFunction(
        AiPublicationCallSite Site, string LogicalName, string ExecutionLanguage,
        string ImplementationRef, AiPublicationDocument Implementation, AiPublicationDocument Environment);

    /// <summary>Written last, after every referenced document. No mutable latest-version pointer exists.</summary>
    public sealed record AiPipelinePublicationManifest(
        int SchemaVersion, AiPublicationPartition Partition, string PipelineName, string PipelineVersion,
        AiPublicationDocument Definition, IReadOnlyList<AiPublicationFunction> Functions);

    public sealed record AiPipelinePublication(
        string PublicationRef, string PublicationSha256, AiPipelinePublicationManifest Manifest);
}
