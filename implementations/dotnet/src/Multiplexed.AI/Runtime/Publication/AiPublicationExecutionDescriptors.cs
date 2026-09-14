using Multiplexed.Abstractions.AI.Publication;

namespace Multiplexed.AI.Runtime.Publication
{
    /// <summary>Validates versioned server execution contracts without changing legacy hash semantics or worker wire models.</summary>
    public static class AiPublicationExecutionDescriptors
    {
        public const string HostRuntimeMediaType = "application/vnd.multiplexed.host-runtime.v1";
        public const string OciImageMediaType = "application/vnd.oci.image.manifest.v1+json";

        public static void Validate(AiPublicationExecutionDescriptor descriptor, AiPublicationEnvironment runtime)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            AiPublicationJson.ValidateEnvironment(runtime);
            if (descriptor.SchemaVersion != 1 ||
                descriptor.OperatingSystem is not ("linux" or "windows" or "darwin") ||
                descriptor.Architecture is not ("amd64" or "arm64" or "386" or "arm"))
                throw new InvalidOperationException("Unsupported execution descriptor version or platform.");
            if (descriptor.PlatformVariant is not null &&
                (descriptor.PlatformVariant.Length is < 1 or > 32 ||
                 descriptor.PlatformVariant.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))))
                throw new InvalidOperationException("Invalid immutable platform variant.");
            ArgumentNullException.ThrowIfNull(descriptor.Artifact);
            ValidateDigest(descriptor.Artifact.Digest);
            switch (descriptor.Artifact.Kind)
            {
                case AiPublicationEnvironmentArtifactKind.HostRuntime:
                    if (descriptor.Artifact.MediaType != HostRuntimeMediaType ||
                        descriptor.Artifact.Digest != "sha256:" + runtime.RuntimeSha256)
                        throw new InvalidOperationException("Host artifact digest must identify the exact approved runtime bytes.");
                    break;
                case AiPublicationEnvironmentArtifactKind.OciImage:
                    // A platform-specific manifest, not an unresolved multi-platform index or mutable tag.
                    if (descriptor.Artifact.MediaType != OciImageMediaType)
                        throw new InvalidOperationException("An OCI environment requires an exact image-manifest descriptor.");
                    break;
                default:
                    throw new InvalidOperationException("Unsupported environment artifact kind.");
            }
            ValidateRequirements(descriptor.Requirements);
            if (descriptor.Requirements.PathProtection == AiWorkerPathProtection.DeploymentControlled)
                throw new InvalidOperationException("Versioned environments must at least require validated execution paths.");
        }

        public static void ValidateRequirements(AiPublicationExecutionRequirements requirements)
        {
            ArgumentNullException.ThrowIfNull(requirements);
            if (!Enum.IsDefined(requirements.IsolationTier) || !Enum.IsDefined(requirements.NetworkEgress) ||
                !Enum.IsDefined(requirements.PathProtection))
                throw new InvalidOperationException("Unknown worker execution requirement.");
            if (requirements.NetworkEgress == AiWorkerNetworkEgress.PinnedPolicy)
                ValidateDigest(requirements.EgressPolicyDigest!);
            else if (requirements.EgressPolicyDigest is not null)
                throw new InvalidOperationException("Only pinned-policy egress accepts a policy digest.");
        }

        public static void ValidateDigest(string digest)
        {
            if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal))
                throw new InvalidOperationException("An algorithm-qualified immutable SHA-256 artifact digest is required.");
            AiPublicationJson.ValidateHash(digest[7..]);
        }

        internal static AiPublicationEnvironmentSnapshot Capture(AiPublicationEnvironment runtime,
            IReadOnlyList<AiPublicationDependency> dependencies, IAiPublicationEnvironmentCatalog catalog)
        {
            var descriptor = (catalog as IAiPublicationExecutionEnvironmentCatalog)?.FindExecutionDescriptor(runtime.Reference);
            if (descriptor is not null) Validate(descriptor, runtime);
            return new AiPublicationEnvironmentSnapshot(descriptor is null ? 1 : 2, runtime, dependencies)
                { ExecutionDescriptor = descriptor };
        }

        internal static void RequirePinned(AiPublicationEnvironmentSnapshot environment,
            string executionLanguage, IAiPublicationEnvironmentCatalog catalog)
        {
            ArgumentNullException.ThrowIfNull(environment);
            AiPublicationJson.ValidateEnvironment(environment.Runtime);
            if (environment.Runtime.ExecutionLanguage != executionLanguage ||
                catalog.Find(environment.Runtime.Reference) != environment.Runtime)
                throw new InvalidOperationException("The exact pinned host runtime is unavailable or incompatible.");
            if (environment.SchemaVersion == 1)
            {
                if (environment.ExecutionDescriptor is not null)
                    throw new InvalidOperationException("Legacy environment documents cannot contain versioned execution requirements.");
            }
            else if (environment.SchemaVersion == 2)
            {
                if (environment.ExecutionDescriptor is null)
                    throw new InvalidOperationException("A versioned environment is missing its execution descriptor.");
                Validate(environment.ExecutionDescriptor, environment.Runtime);
            }
            else throw new InvalidOperationException("Unsupported immutable environment schema version.");

            var installed = (catalog as IAiPublicationExecutionEnvironmentCatalog)?.FindExecutionDescriptor(environment.Runtime.Reference);
            if (installed != environment.ExecutionDescriptor)
                throw new InvalidOperationException("Pinned execution requirements changed or are no longer available.");
        }
    }
}
