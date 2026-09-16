using System.Collections.ObjectModel;
using System.Globalization;

namespace Multiplexed.AI.Runtime.Invocation.Workers.Isolation
{
    /// <summary>
    /// Pure server-owned OCI launch plan. The image reference always contains the immutable
    /// publication digest; no tag, pull fallback, host mount or tenant-selected engine option exists.
    /// </summary>
    public sealed record AiContainerWorkerLaunchPlan(
        string ContainerName,
        string ImageReference,
        IReadOnlyList<string> Arguments)
    {
        public static AiContainerWorkerLaunchPlan Create(AiContainerWorkerProfile profile, string containerName)
        {
            ArgumentNullException.ThrowIfNull(profile);
            ValidateContainerName(containerName);
            profile.ResourceLimits.Validate();

            var image = profile.ImageReference;
            if (!image.Contains("@sha256:", StringComparison.Ordinal) ||
                !image.EndsWith(profile.ExecutionDescriptor.Artifact.Digest, StringComparison.Ordinal))
                throw new InvalidOperationException("Container launch requires the exact immutable OCI manifest digest.");

            var cpu = (profile.ResourceLimits.CpuMilliCores / 1000m).ToString("0.###", CultureInfo.InvariantCulture);
            var arguments = new[]
            {
                "run",
                "--rm",
                "--interactive",
                "--pull=never",
                "--name", containerName,
                "--user", profile.ContainerUser,
                "--network=none",
                "--read-only",
                "--cap-drop=ALL",
                "--security-opt=no-new-privileges",
                "--pids-limit=" + profile.ResourceLimits.PidsLimit.ToString(CultureInfo.InvariantCulture),
                "--memory=" + profile.ResourceLimits.MemoryBytes.ToString(CultureInfo.InvariantCulture),
                "--memory-swap=" + profile.ResourceLimits.MemoryBytes.ToString(CultureInfo.InvariantCulture),
                "--cpus=" + cpu,
                "--tmpfs=/tmp:rw,noexec,nosuid,nodev,size=" + profile.ResourceLimits.WritableWorkspaceBytes.ToString(CultureInfo.InvariantCulture),
                "--env=TMPDIR=/tmp",
                "--log-driver=none",
                image
            };
            return new(containerName, image, Array.AsReadOnly(arguments));
        }

        private static void ValidateContainerName(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 63 ||
                !char.IsAsciiLetterOrDigit(value[0]) ||
                value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
                throw new ArgumentException("A bounded server-owned container name is required.", nameof(value));
        }
    }
}
