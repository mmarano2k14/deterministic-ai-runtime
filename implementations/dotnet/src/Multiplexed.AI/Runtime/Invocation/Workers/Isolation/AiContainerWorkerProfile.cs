using System.Collections.ObjectModel;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Publication;

namespace Multiplexed.AI.Runtime.Invocation.Workers.Isolation
{
    /// <summary>
    /// Server-owned resource bounds for one isolated container assignment.
    /// These limits are provider configuration, not tenant-selected execution authority.
    /// </summary>
    public sealed record AiContainerWorkerResourceLimits
    {
        public int CpuMilliCores { get; init; } = 1000;
        public long MemoryBytes { get; init; } = 268435456;
        public int PidsLimit { get; init; } = 64;
        public long WritableWorkspaceBytes { get; init; } = 67108864;

        internal void Validate()
        {
            if (CpuMilliCores is < 50 or > 16000)
                throw new ArgumentOutOfRangeException(nameof(CpuMilliCores), "Container CPU must be bounded between 50m and 16000m.");
            if (MemoryBytes is < 67108864 or > 68719476736)
                throw new ArgumentOutOfRangeException(nameof(MemoryBytes), "Container memory must be bounded between 64 MiB and 64 GiB.");
            if (PidsLimit is < 8 or > 4096)
                throw new ArgumentOutOfRangeException(nameof(PidsLimit), "Container process count must be bounded between 8 and 4096.");
            if (WritableWorkspaceBytes is < 8388608 or > 4294967296 || WritableWorkspaceBytes > MemoryBytes)
                throw new ArgumentOutOfRangeException(nameof(WritableWorkspaceBytes),
                    "Writable container workspace must be between 8 MiB and 4 GiB and cannot exceed the memory limit.");
        }
    }

    /// <summary>
    /// Trusted host configuration for one exact OCI-backed worker environment.
    /// The image repository and physical owner scope are server-owned; the immutable manifest digest
    /// comes only from the publication execution descriptor. No tenant execution identity, mutable tag
    /// or tenant-provided engine option is accepted.
    /// </summary>
    public sealed class AiContainerWorkerProfile
    {
        public AiContainerWorkerProfile(
            AiPublicationEnvironment runtime,
            AiPublicationExecutionDescriptor executionDescriptor,
            string engineExecutablePath,
            string engineExecutableSha256,
            string engineWorkingDirectory,
            string imageRepository,
            string containerOwnerScope,
            AiContainerWorkerResourceLimits? resourceLimits = null,
            string containerUser = "65532:65532",
            IReadOnlyDictionary<string, string>? engineEnvironment = null,
            IEnumerable<string>? approvedLaunchRoots = null,
            int heartbeatMilliseconds = 1000)
        {
            AiPublicationJson.ValidateEnvironment(runtime);
            ArgumentNullException.ThrowIfNull(executionDescriptor);
            AiPublicationExecutionDescriptors.Validate(executionDescriptor, runtime);
            if (executionDescriptor.Artifact.Kind != AiPublicationEnvironmentArtifactKind.OciImage)
                throw new ArgumentException("Container worker profiles require an OCI image execution descriptor.", nameof(executionDescriptor));

            AiPublicationJson.ValidateHash(engineExecutableSha256);
            if (!Path.IsPathFullyQualified(engineExecutablePath) || !Path.IsPathFullyQualified(engineWorkingDirectory))
                throw new ArgumentException("Container engine executable and working directory must be absolute host-owned paths.");

            var roots = (approvedLaunchRoots ?? Array.Empty<string>()).ToArray();
            if (roots.Length is < 1 or > 16 ||
                roots.Any(root => string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) ||
                roots.Distinct(StringComparer.OrdinalIgnoreCase).Count() != roots.Length)
                throw new ArgumentException("Container provider launch roots must be unique bounded absolute host paths.", nameof(approvedLaunchRoots));

            var normalizedRoots = roots.Select(Path.GetFullPath).ToArray();
            if (!normalizedRoots.Any(root => Contains(root, engineExecutablePath)) ||
                !normalizedRoots.Any(root => Contains(root, engineWorkingDirectory)))
                throw new ArgumentException("Container engine executable and working directory must be inside an approved launch root.");

            ValidateImageRepository(imageRepository);
            AiContainerWorkerOwnership.ValidateOwnerScope(containerOwnerScope);
            ValidateContainerUser(containerUser);
            if (heartbeatMilliseconds is < 50 or > 5000)
                throw new ArgumentOutOfRangeException(nameof(heartbeatMilliseconds));

            var limits = resourceLimits ?? new AiContainerWorkerResourceLimits();
            limits.Validate();

            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in engineEnvironment ?? new Dictionary<string, string>())
            {
                if (string.IsNullOrWhiteSpace(item.Key) || item.Key.Length > 128 || item.Key.Contains('=') ||
                    item.Key.Contains('\0') || item.Value is null || item.Value.Length > 32768 ||
                    item.Value.Contains('\0') || !environment.TryAdd(item.Key, item.Value))
                    throw new ArgumentException("Invalid explicit container-engine environment.", nameof(engineEnvironment));
            }
            if (environment.Count > 64)
                throw new ArgumentException("Too many explicit container-engine environment variables.", nameof(engineEnvironment));

            Runtime = runtime;
            ExecutionDescriptor = executionDescriptor;
            EngineExecutablePath = Path.GetFullPath(engineExecutablePath);
            EngineExecutableSha256 = engineExecutableSha256;
            EngineWorkingDirectory = Path.GetFullPath(engineWorkingDirectory);
            ImageRepository = imageRepository;
            ContainerOwnerScope = containerOwnerScope;
            ResourceLimits = limits;
            ContainerUser = containerUser;
            HeartbeatMilliseconds = heartbeatMilliseconds;
            EngineEnvironment = new ReadOnlyDictionary<string, string>(environment);
            ApprovedLaunchRoots = Array.AsReadOnly(normalizedRoots);
        }

        public AiPublicationEnvironment Runtime { get; }
        public AiPublicationExecutionDescriptor ExecutionDescriptor { get; }
        public string EngineExecutablePath { get; }
        public string EngineExecutableSha256 { get; }
        public string EngineWorkingDirectory { get; }
        public string ImageRepository { get; }
        public string ContainerOwnerScope { get; }
        public string ImageReference => ImageRepository + "@" + ExecutionDescriptor.Artifact.Digest;
        public AiContainerWorkerResourceLimits ResourceLimits { get; }
        public string ContainerUser { get; }
        public int HeartbeatMilliseconds { get; }
        public IReadOnlyDictionary<string, string> EngineEnvironment { get; }
        public IReadOnlyList<string> ApprovedLaunchRoots { get; }

        private static bool Contains(string root, string path)
        {
            var fullRoot = Path.GetFullPath(root);
            var fullPath = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(fullRoot, fullPath);
            return !Path.IsPathRooted(relative) &&
                relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        }

        private static void ValidateImageRepository(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 255 || value.Contains('@') ||
                value.Contains('\\') || value.Any(char.IsWhiteSpace) || value != value.ToLowerInvariant() ||
                value.StartsWith('/') || value.EndsWith('/') || value.Contains("//", StringComparison.Ordinal))
                throw new ArgumentException("A bounded lowercase server-owned OCI repository without tag or digest is required.",
                    nameof(value));

            foreach (var character in value)
                if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-' or '/' or ':'))
                    throw new ArgumentException("The OCI repository contains an unsupported character.", nameof(value));

            var lastSlash = value.LastIndexOf('/');
            var lastColon = value.LastIndexOf(':');
            if (lastColon > lastSlash)
                throw new ArgumentException("Mutable OCI image tags are not accepted; the publication descriptor supplies the digest.",
                    nameof(value));
        }

        private static void ValidateContainerUser(string value)
        {
            var parts = value?.Split(':') ?? Array.Empty<string>();
            if (parts.Length != 2 ||
                !uint.TryParse(parts[0], out var uid) || !uint.TryParse(parts[1], out var gid) ||
                uid == 0 || gid == 0)
                throw new ArgumentException("Container workers require an explicit non-root numeric uid:gid.", nameof(value));
        }
    }

    public interface IAiContainerWorkerCatalog
    {
        AiContainerWorkerProfile Resolve(AiPublicationEnvironment runtime);
    }

    /// <summary>
    /// Exact immutable environment selection for the isolated provider. There is no latest,
    /// language fallback, image-tag lookup or tenant-controlled profile registration.
    /// </summary>
    public sealed class AiConfiguredContainerWorkerCatalog : IAiContainerWorkerCatalog
    {
        private readonly IReadOnlyDictionary<string, AiContainerWorkerProfile> profiles;

        public AiConfiguredContainerWorkerCatalog(IEnumerable<AiContainerWorkerProfile> profiles)
        {
            ArgumentNullException.ThrowIfNull(profiles);
            var values = new Dictionary<string, AiContainerWorkerProfile>(StringComparer.Ordinal);
            foreach (var profile in profiles)
            {
                ArgumentNullException.ThrowIfNull(profile);
                if (!values.TryAdd(profile.Runtime.Reference, profile))
                    throw new ArgumentException("Duplicate isolated worker runtime profile.", nameof(profiles));
            }
            this.profiles = values;
        }

        public AiContainerWorkerProfile Resolve(AiPublicationEnvironment runtime)
        {
            AiPublicationJson.ValidateEnvironment(runtime);
            if (!profiles.TryGetValue(runtime.Reference, out var profile) || profile.Runtime != runtime)
                throw new NotSupportedException("The exact pinned isolated worker runtime is not installed.");
            return profile;
        }
    }
}
