using System.Runtime.InteropServices;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Publication;

namespace Multiplexed.AI.Runtime.Invocation.Workers
{
    /// <summary>
    /// Server deployment minimum. New policy instances are strict. The historical process
    /// transport constructor retains a named legacy compatibility path; it never implies a sandbox.
    /// </summary>
    public sealed record AiWorkerExecutionAdmissionPolicy
    {
        public bool AllowLegacyTrustedProcess { get; init; }
        public AiPublicationExecutionRequirements MinimumRequirements { get; init; } = new();

        public static AiWorkerExecutionAdmissionPolicy LegacyCompatible { get; } = new()
        {
            AllowLegacyTrustedProcess = true,
            MinimumRequirements = new()
            {
                IsolationTier = AiWorkerIsolationTier.TrustedProcess,
                NetworkEgress = AiWorkerNetworkEgress.HostNetwork,
                PathProtection = AiWorkerPathProtection.DeploymentControlled
            }
        };
    }

    /// <summary>Provider facts are separate from requested requirements. Configuration cannot grant enforcement to the process provider.</summary>
    public sealed record AiWorkerExecutionCapabilities(
        AiPublicationEnvironmentArtifactKind ArtifactKind,
        AiWorkerIsolationTier IsolationTier,
        AiWorkerNetworkEgress NetworkEgress,
        AiWorkerPathProtection PathProtection);

    /// <summary>
    /// Common pre-launch guard for custom steps and hosted policies. This provider implements
    /// trusted processes and validated launch paths only; no container, network deny or sealed workspace is claimed.
    /// </summary>
    public static class AiWorkerExecutionAdmission
    {
        public static AiWorkerExecutionCapabilities ProcessCapabilities { get; } = new(
            AiPublicationEnvironmentArtifactKind.HostRuntime, AiWorkerIsolationTier.TrustedProcess,
            AiWorkerNetworkEgress.HostNetwork, AiWorkerPathProtection.ValidatedPaths);

        public static string CurrentOperatingSystem => OperatingSystem.IsWindows() ? "windows" :
            OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "darwin" : "unsupported";
        public static string CurrentArchitecture => RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "amd64", Architecture.Arm64 => "arm64", Architecture.X86 => "386",
            Architecture.Arm => "arm", _ => "unsupported"
        };

        public static void Require(AiWorkerCodeBundle code, AiWorkerProcessProfile profile,
            AiWorkerExecutionAdmissionPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(code); ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(policy);
            AiPublicationExecutionDescriptors.ValidateRequirements(policy.MinimumRequirements);
            if (code.Runtime != profile.Runtime)
                throw new InvalidOperationException("Execution admission requires the exact pinned runtime.");
            if (code.ExecutionDescriptor != profile.ExecutionDescriptor)
                throw new InvalidOperationException("Execution descriptor does not match the installed process profile.");
            if (code.ExecutionDescriptor is null)
            {
                if (!policy.AllowLegacyTrustedProcess || policy.MinimumRequirements != AiWorkerExecutionAdmissionPolicy.LegacyCompatible.MinimumRequirements)
                    throw new NotSupportedException("Legacy execution has no sandbox, egress or sealed-path guarantees.");
                return;
            }

            var descriptor = code.ExecutionDescriptor;
            AiPublicationExecutionDescriptors.Validate(descriptor, code.Runtime);
            if (descriptor.OperatingSystem != CurrentOperatingSystem || descriptor.Architecture != CurrentArchitecture ||
                descriptor.PlatformVariant is not null)
                throw new NotSupportedException("The pinned execution platform is not available from this process provider.");
            if (descriptor.Artifact.Kind != ProcessCapabilities.ArtifactKind)
                throw new NotSupportedException("This process provider cannot execute an OCI image artifact.");
            RequireProcessSupport(descriptor.Requirements);
            RequireProcessSupport(policy.MinimumRequirements);
            if (profile.ApprovedLaunchRoots.Count == 0)
                throw new InvalidOperationException("Versioned process profiles require explicit approved launch roots.");
            AiWorkerLaunchPaths.ValidateCodePaths(code);
        }

        private static void RequireProcessSupport(AiPublicationExecutionRequirements required)
        {
            if (required.IsolationTier != ProcessCapabilities.IsolationTier ||
                required.NetworkEgress != ProcessCapabilities.NetworkEgress ||
                required.PathProtection == AiWorkerPathProtection.SealedClosure)
                throw new NotSupportedException("The process provider cannot enforce the required isolation, network or closure protection.");
        }
    }
}
