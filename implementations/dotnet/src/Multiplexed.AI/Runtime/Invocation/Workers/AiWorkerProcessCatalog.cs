using System.Collections.ObjectModel;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Publication;

namespace Multiplexed.AI.Runtime.Invocation.Workers
{
    /// <summary>
    /// Installed host executable, never tenant source. Arguments/environment are copied from
    /// trusted configuration only. Host files must reside on a deployment-controlled read-only path.
    /// </summary>
    public sealed class AiWorkerProcessProfile
    {
        public AiWorkerProcessProfile(AiPublicationEnvironment runtime, string executablePath,
            string executableSha256, IEnumerable<string> arguments, string workingDirectory,
            IReadOnlyDictionary<string, string>? environment = null,
            IReadOnlyDictionary<string, string>? verifiedHostFiles = null,
            AiPublicationExecutionDescriptor? executionDescriptor = null,
            IEnumerable<string>? approvedLaunchRoots = null)
        {
            AiPublicationJson.ValidateEnvironment(runtime);
            AiPublicationJson.ValidateHash(executableSha256);
            if (!Path.IsPathFullyQualified(executablePath) || !Path.IsPathFullyQualified(workingDirectory))
                throw new ArgumentException("Worker executable and working directory must be absolute host-owned paths.");
            ArgumentNullException.ThrowIfNull(arguments);
            var args = arguments.ToArray();
            if (args.Length > 64 || args.Any(a => a is null || a.Length > 8192 || a.Contains('\0')))
                throw new ArgumentException("Invalid trusted worker arguments.");
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in environment ?? new Dictionary<string, string>())
            {
                if (string.IsNullOrWhiteSpace(item.Key) || item.Key.Length > 128 || item.Key.Contains('=') ||
                    item.Key.Contains('\0') || item.Value is null || item.Value.Length > 32768 || item.Value.Contains('\0') || !env.TryAdd(item.Key, item.Value))
                    throw new ArgumentException("Invalid explicit worker environment.");
            }
            if (env.Count > 64) throw new ArgumentException("Too many explicit worker environment variables.");
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in verifiedHostFiles ?? new Dictionary<string, string>())
            {
                if (!Path.IsPathFullyQualified(item.Key)) throw new ArgumentException("Verified host files need absolute paths.");
                AiPublicationJson.ValidateHash(item.Value); files.Add(item.Key, item.Value);
            }
            if (files.Count > 64) throw new ArgumentException("Too many verified host files.");
            if (executionDescriptor is not null)
            {
                AiPublicationExecutionDescriptors.Validate(executionDescriptor, runtime);
                if (executionDescriptor.Artifact.Kind == AiPublicationEnvironmentArtifactKind.HostRuntime &&
                    runtime.RuntimeSha256 != executableSha256)
                    throw new InvalidOperationException("The versioned host artifact must identify the configured executable bytes.");
            }
            var roots = (approvedLaunchRoots ?? Array.Empty<string>()).ToArray();
            if (roots.Length > 16 || roots.Any(r => string.IsNullOrWhiteSpace(r) || !Path.IsPathFullyQualified(r)) ||
                roots.Distinct(StringComparer.OrdinalIgnoreCase).Count() != roots.Length)
                throw new ArgumentException("Approved launch roots must be unique bounded absolute host paths.");
            ExecutionDescriptor = executionDescriptor;
            ApprovedLaunchRoots = Array.AsReadOnly(roots);
            Runtime = runtime; ExecutablePath = executablePath; ExecutableSha256 = executableSha256;
            Arguments = Array.AsReadOnly(args); WorkingDirectory = workingDirectory;
            Environment = new ReadOnlyDictionary<string, string>(env);
            VerifiedHostFiles = new ReadOnlyDictionary<string, string>(files);
        }
        public AiPublicationEnvironment Runtime { get; }
        public AiPublicationExecutionDescriptor? ExecutionDescriptor { get; }
        public IReadOnlyList<string> ApprovedLaunchRoots { get; }
        public string ExecutablePath { get; }
        public string ExecutableSha256 { get; }
        public IReadOnlyList<string> Arguments { get; }
        public string WorkingDirectory { get; }
        public IReadOnlyDictionary<string, string> Environment { get; }
        public IReadOnlyDictionary<string, string> VerifiedHostFiles { get; }
    }

    public interface IAiWorkerProcessCatalog
    {
        AiWorkerProcessProfile Resolve(AiPublicationEnvironment runtime);
    }

    /// <summary>Exact immutable host runtime matching; no latest lookup, shell fallback or mutable tenant registration.</summary>
    public sealed class AiConfiguredWorkerProcessCatalog : IAiWorkerProcessCatalog
    {
        private readonly IReadOnlyDictionary<string, AiWorkerProcessProfile> _profiles;
        public AiConfiguredWorkerProcessCatalog(IEnumerable<AiWorkerProcessProfile> profiles)
        {
            ArgumentNullException.ThrowIfNull(profiles);
            var values = new Dictionary<string, AiWorkerProcessProfile>(StringComparer.Ordinal);
            foreach (var profile in profiles)
            {
                ArgumentNullException.ThrowIfNull(profile);
                if (!values.TryAdd(profile.Runtime.Reference, profile)) throw new ArgumentException("Duplicate worker runtime profile.");
            }
            _profiles = values;
        }
        public AiWorkerProcessProfile Resolve(AiPublicationEnvironment runtime)
        {
            AiPublicationJson.ValidateEnvironment(runtime);
            if (!_profiles.TryGetValue(runtime.Reference, out var profile) || profile.Runtime != runtime)
                throw new NotSupportedException("The exact pinned worker runtime is not installed.");
            return profile;
        }
    }

    /// <summary>Bounds for the private pipe protocol, independently of business results or retry decisions.</summary>
    public sealed class AiWorkerProcessTransportOptions
    {
        public AiWorkerProcessTransportOptions(TimeSpan? startupTimeout = null, TimeSpan? heartbeatTimeout = null,
            TimeSpan? shutdownTimeout = null, int maxRequestBytes = 50331648, int maxFrameBytes = 1048576,
            int maxStderrBytes = 65536, int maxFrames = 10000)
        {
            StartupTimeout = startupTimeout ?? TimeSpan.FromSeconds(10);
            HeartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromSeconds(10);
            ShutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(3);
            MaxRequestBytes = maxRequestBytes; MaxFrameBytes = maxFrameBytes;
            MaxStderrBytes = maxStderrBytes; MaxFrames = maxFrames;
            if (StartupTimeout < TimeSpan.FromMilliseconds(100) || StartupTimeout > TimeSpan.FromMinutes(2) ||
                HeartbeatTimeout < TimeSpan.FromMilliseconds(100) || HeartbeatTimeout > TimeSpan.FromMinutes(1) ||
                ShutdownTimeout < TimeSpan.FromMilliseconds(100) || ShutdownTimeout > TimeSpan.FromSeconds(30) ||
                maxRequestBytes is < 1024 or > 67108864 || maxFrameBytes is < 256 or > 1048576 ||
                maxStderrBytes is < 1 or > 1048576 || maxFrames is < 2 or > 100000)
                throw new ArgumentException("Invalid process transport bounds.");
        }
        public TimeSpan StartupTimeout { get; }
        public TimeSpan HeartbeatTimeout { get; }
        public TimeSpan ShutdownTimeout { get; }
        public int MaxRequestBytes { get; }
        public int MaxFrameBytes { get; }
        public int MaxStderrBytes { get; }
        public int MaxFrames { get; }
    }
}
