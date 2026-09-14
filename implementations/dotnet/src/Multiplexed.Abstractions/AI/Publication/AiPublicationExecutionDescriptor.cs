using System.Text.Json.Serialization;

namespace Multiplexed.Abstractions.AI.Publication
{
    [JsonConverter(typeof(JsonStringEnumConverter<AiWorkerIsolationTier>))]
    public enum AiWorkerIsolationTier { TrustedProcess, RestrictedProcess, SandboxedContainer }

    [JsonConverter(typeof(JsonStringEnumConverter<AiWorkerNetworkEgress>))]
    public enum AiWorkerNetworkEgress { DenyAll, PinnedPolicy, HostNetwork }

    [JsonConverter(typeof(JsonStringEnumConverter<AiWorkerPathProtection>))]
    public enum AiWorkerPathProtection { DeploymentControlled, ValidatedPaths, SealedClosure }

    [JsonConverter(typeof(JsonStringEnumConverter<AiPublicationEnvironmentArtifactKind>))]
    public enum AiPublicationEnvironmentArtifactKind { HostRuntime, OciImage }

    /// <summary>
    /// Requirements, not an assertion of enforcement. New descriptors default to a sandbox,
    /// denied network egress and a sealed code/dependency closure. Unsupported requirements fail closed.
    /// </summary>
    public sealed record AiPublicationExecutionRequirements
    {
        public AiWorkerIsolationTier IsolationTier { get; init; } = AiWorkerIsolationTier.SandboxedContainer;
        public AiWorkerNetworkEgress NetworkEgress { get; init; } = AiWorkerNetworkEgress.DenyAll;
        public AiWorkerPathProtection PathProtection { get; init; } = AiWorkerPathProtection.SealedClosure;

        // An allow-list/policy is identified by immutable content, never a mutable policy name.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? EgressPolicyDigest { get; init; }
    }

    /// <summary>
    /// Digest of an artifact, distinct from the digest of the enclosing environment document.
    /// No registry URL or mutable image tag is accepted by this contract.
    /// </summary>
    public sealed record AiPublicationEnvironmentArtifact(
        AiPublicationEnvironmentArtifactKind Kind, string Digest, string MediaType);

    /// <summary>
    /// Server-owned execution descriptor inside the immutable environment snapshot. This is
    /// deliberately not added to AiPublicationEnvironment: its existing worker JSON stays unchanged.
    /// </summary>
    public sealed record AiPublicationExecutionDescriptor
    {
        public int SchemaVersion { get; init; } = 1;
        public string OperatingSystem { get; init; } = string.Empty;
        public string Architecture { get; init; } = string.Empty;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PlatformVariant { get; init; }
        public AiPublicationEnvironmentArtifact Artifact { get; init; } = null!;
        public AiPublicationExecutionRequirements Requirements { get; init; } = new();
    }

    /// <summary>
    /// Optional extension of the existing trusted host catalog. Null identifies an explicitly
    /// legacy entry, not a sandboxed default. A pinned descriptor may never be changed in place.
    /// </summary>
    public interface IAiPublicationExecutionEnvironmentCatalog : IAiPublicationEnvironmentCatalog
    {
        AiPublicationExecutionDescriptor? FindExecutionDescriptor(string reference);
    }
}
