using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Invocation.Mcp;

namespace Multiplexed.AI.Runtime.Publication
{
    /// <summary>Concrete capabilities resolved by the existing RBAC engine. No grants are installed.</summary>
    public sealed record AiPublicationCapability(string Resource, string Feature, string Action);

    /// <summary>Host-owned capability mapping and hard bounds, frozen when registered.</summary>
    public sealed class AiPublicationOptions
    {
        public AiPublicationOptions(AiPublicationCapability publish, AiPublicationCapability read,
            AiPublicationCapability execute, int maxFileBytes = 1048576, int maxTotalBytes = 8388608,
            int maxFunctions = 128, int maxFiles = 512)
        {
            Publish = Validate(publish); Read = Validate(read); Execute = Validate(execute);
            if (maxFileBytes is < 1 or > 2097152 || maxTotalBytes < maxFileBytes || maxTotalBytes > 33554432 ||
                maxFunctions is < 1 or > 256 || maxFiles is < 1 or > 2048)
                throw new ArgumentOutOfRangeException(nameof(maxFileBytes), "Invalid publication limits.");
            MaxFileBytes = maxFileBytes; MaxTotalBytes = maxTotalBytes; MaxFunctions = maxFunctions; MaxFiles = maxFiles;
        }
        public AiPublicationCapability Publish { get; }
        public AiPublicationCapability Read { get; }
        public AiPublicationCapability Execute { get; }
        public int MaxFileBytes { get; }
        public int MaxTotalBytes { get; }
        public int MaxFunctions { get; }
        public int MaxFiles { get; }
        private static AiPublicationCapability Validate(AiPublicationCapability value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!AiMcpInvocationIdentity.IsSegment(value.Resource) || !AiMcpInvocationIdentity.IsSegment(value.Feature) ||
                !AiMcpInvocationIdentity.IsSegment(value.Action))
                throw new ArgumentException("Publication capabilities must be concrete resource/feature/action segments.");
            return value;
        }
    }

    /// <summary>Fixed exact runtime catalog for hosts with explicitly provisioned language environments.</summary>
    public sealed class AiConfiguredPublicationEnvironmentCatalog : IAiPublicationEnvironmentCatalog
    {
        private readonly IReadOnlyDictionary<string, AiPublicationEnvironment> _environments;
        public AiConfiguredPublicationEnvironmentCatalog(IEnumerable<AiPublicationEnvironment> environments)
        {
            ArgumentNullException.ThrowIfNull(environments);
            var copy = new Dictionary<string, AiPublicationEnvironment>(StringComparer.Ordinal);
            foreach (var entry in environments)
            {
                AiPublicationJson.ValidateEnvironment(entry);
                if (!copy.TryAdd(entry.Reference, entry)) throw new ArgumentException("Duplicate environment reference.");
            }
            _environments = copy;
        }
        public AiPublicationEnvironment? Find(string reference) => _environments.GetValueOrDefault(reference);
    }
}
