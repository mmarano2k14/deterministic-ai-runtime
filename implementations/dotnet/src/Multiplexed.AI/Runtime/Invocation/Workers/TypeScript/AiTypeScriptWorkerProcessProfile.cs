using System.Globalization;
using Multiplexed.Abstractions.AI.Publication;

namespace Multiplexed.AI.Runtime.Invocation.Workers.TypeScript
{
    /// <summary>
    /// Host-owned configuration for the published-source TypeScript loader. No package-manager I/O,
    /// process startup, language fallback or automatic registration occurs during profile creation.
    /// Node.js and loader digests are verified by the existing process transport at launch.
    /// </summary>
    public static class AiTypeScriptWorkerProcessProfile
    {
        public static AiWorkerProcessProfile Create(AiPublicationEnvironment runtime,
            string executablePath, string executableSha256, string workerScriptPath,
            string workerScriptSha256, string workingDirectory, int heartbeatMilliseconds = 1000,
            IReadOnlyDictionary<string, string>? environment = null,
            IReadOnlyDictionary<string, string>? verifiedRuntimeFiles = null)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            if (runtime.ExecutionLanguage != "typescript")
                throw new NotSupportedException("The TypeScript source loader cannot execute another language.");
            if (!Version.TryParse(runtime.RuntimeVersion, out var version) || version.Build < 0 || version.Revision != -1 ||
                version.ToString(3) != runtime.RuntimeVersion ||
                !((version.Major == 22 && version.Minor >= 13) || version.Major == 24))
                throw new ArgumentException("An exact stable Node.js 22.13+ or 24.x runtime version is required.", nameof(runtime));
            if (string.IsNullOrEmpty(workerScriptPath) || !Path.IsPathFullyQualified(workerScriptPath) ||
                !workerScriptPath.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The installed TypeScript loader needs an absolute .mjs path.", nameof(workerScriptPath));
            if (heartbeatMilliseconds is < 50 or > 5000)
                throw new ArgumentOutOfRangeException(nameof(heartbeatMilliseconds));

            var verified = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in verifiedRuntimeFiles ?? new Dictionary<string, string>())
                verified.Add(item.Key, item.Value);
            if (verified.TryGetValue(workerScriptPath, out var previous) && previous != workerScriptSha256)
                throw new ArgumentException("Conflicting approved loader digests.", nameof(verifiedRuntimeFiles));
            verified[workerScriptPath] = workerScriptSha256;

            return new AiWorkerProcessProfile(runtime, executablePath, executableSha256,
                new[] { "--no-warnings", "--experimental-strip-types", "--experimental-transform-types", workerScriptPath,
                    "--runtime-reference=" + runtime.Reference,
                    "--runtime-version=" + runtime.RuntimeVersion,
                    "--runtime-sha256=" + runtime.RuntimeSha256,
                    "--heartbeat-ms=" + heartbeatMilliseconds.ToString(CultureInfo.InvariantCulture) },
                workingDirectory, environment, verified);
        }
    }
}
