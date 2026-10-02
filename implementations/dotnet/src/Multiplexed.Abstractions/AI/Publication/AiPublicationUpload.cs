using System.Text.Json.Serialization;
using Multiplexed.Abstractions.AI.Pipeline;

namespace Multiplexed.Abstractions.AI.Publication
{
    /// <summary>Supported declaration sites; a policy index preserves its original ordered scope.</summary>
    public enum AiPublicationFunctionKind { Step, ConcurrencyPolicy, RetryPolicy, DelegationPolicy }

    /// <summary>
    /// Identifies one immutable custom declaration inside a published definition closure.
    /// A null <see cref="DefinitionPath"/> identifies the root pipeline. A non-null value is the
    /// canonical embedded Child DAG declaration path and is emitted only for nested declarations.
    /// </summary>
    public sealed record AiPublicationCallSite(
        AiPublicationFunctionKind Kind, string? StepName = null, int? PolicyIndex = null)
    {
        /// <summary>
        /// Canonical JSON-Pointer-style path of parent ExecuteChildDag step names. Root call sites omit it.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DefinitionPath { get; init; }
    }

    /// <summary>Raw files only. Publication never extracts archives, installs packages or executes a file.</summary>
    public sealed record AiPublicationFileUpload(string Path, byte[] Content);

    /// <summary>An exact dependency label and the bytes supplied for that dependency, not a registry query.</summary>
    public sealed record AiPublicationDependencyUpload(
        string Name, string Version, IReadOnlyList<AiPublicationFileUpload> Files)
    {
        /// <summary>
        /// Optional deterministic package descriptor. Null preserves the historical explicit-file dependency contract.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AiPublicationDependencyPackage? Package { get; init; }
    }

    /// <summary>Code attached to a declaration. The publisher generates implementation references.</summary>
    public sealed record AiPublicationFunctionUpload(
        AiPublicationCallSite Site, string EnvironmentRef, string EntryPointPath, string EntryPointSymbol,
        IReadOnlyList<AiPublicationFileUpload> Sources, IReadOnlyList<AiPublicationDependencyUpload> Dependencies);

    /// <summary>Server-side publication input. The independent SDK must not reference these CLR models.</summary>
    public sealed record AiPipelinePublicationUpload(
        AiPipelineDefinition Definition, IReadOnlyList<AiPublicationFunctionUpload> Functions);
}
