using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Publication;

namespace Multiplexed.AI.Runtime.Invocation.Workers.Isolation
{
    /// <summary>
    /// Capability contract for the first isolated worker provider target.
    /// This delivery freezes Linux/amd64 OCI admission only; container launch and enforcement are
    /// implemented by the subsequent transport delivery rather than inferred from this declaration.
    /// </summary>
    public static class AiContainerWorkerExecutionAdmission
    {
        public const string InitialOperatingSystem = "linux";
        public const string InitialArchitecture = "amd64";

        public static AiWorkerExecutionCapabilities Capabilities { get; } = new(
            AiPublicationEnvironmentArtifactKind.OciImage,
            AiWorkerIsolationTier.SandboxedContainer,
            AiWorkerNetworkEgress.DenyAll,
            AiWorkerPathProtection.SealedClosure);

        public static void Require(
            AiWorkerCodeBundle code,
            AiContainerWorkerProfile profile,
            AiWorkerExecutionAdmissionPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(code);
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(policy);

            AiPublicationExecutionDescriptors.ValidateRequirements(policy.MinimumRequirements);
            if (code.Runtime != profile.Runtime)
                throw new InvalidOperationException("Isolated execution requires the exact pinned runtime.");
            if (code.ExecutionDescriptor is null)
                throw new NotSupportedException("Isolated execution requires a versioned OCI execution descriptor.");
            if (code.ExecutionDescriptor != profile.ExecutionDescriptor)
                throw new InvalidOperationException("Execution descriptor does not match the installed isolated worker profile.");

            var descriptor = code.ExecutionDescriptor;
            AiPublicationExecutionDescriptors.Validate(descriptor, code.Runtime);
            if (descriptor.OperatingSystem != InitialOperatingSystem ||
                descriptor.Architecture != InitialArchitecture ||
                descriptor.PlatformVariant is not null)
                throw new NotSupportedException("The initial isolated worker provider supports Linux amd64 without a platform variant.");
            if (descriptor.Artifact.Kind != Capabilities.ArtifactKind)
                throw new NotSupportedException("The isolated worker provider requires an exact OCI image manifest.");

            RequireProviderSupport(descriptor.Requirements);
            RequireProviderSupport(policy.MinimumRequirements);
            profile.ResourceLimits.Validate();
            AiWorkerLaunchPaths.ValidateCodePaths(code);
        }

        private static void RequireProviderSupport(AiPublicationExecutionRequirements requirements)
        {
            AiPublicationExecutionDescriptors.ValidateRequirements(requirements);
            if (requirements.IsolationTier != Capabilities.IsolationTier ||
                requirements.NetworkEgress != Capabilities.NetworkEgress ||
                requirements.PathProtection != Capabilities.PathProtection ||
                requirements.EgressPolicyDigest is not null)
                throw new NotSupportedException(
                    "The initial isolated worker provider supports only sandboxed containers, denied egress and a sealed closure.");
        }
    }
}
