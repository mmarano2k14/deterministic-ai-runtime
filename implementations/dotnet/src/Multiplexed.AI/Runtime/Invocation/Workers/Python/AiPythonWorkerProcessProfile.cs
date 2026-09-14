using System.Globalization;
using Multiplexed.Abstractions.AI.Publication;

namespace Multiplexed.AI.Runtime.Invocation.Workers.Python
{
    /// <summary>
    /// Host-owned configuration for the published-source Python loader. No filesystem I/O,
    /// process startup, dependency installation, language fallback or automatic registration.
    /// Interpreter and loader digests are checked by the existing process transport at launch.
    /// </summary>
    public static class AiPythonWorkerProcessProfile
    {
        public static AiWorkerProcessProfile Create(AiPublicationEnvironment runtime,
            string executablePath, string executableSha256, string workerScriptPath,
            string workerScriptSha256, string workingDirectory, int heartbeatMilliseconds = 1000,
            IReadOnlyDictionary<string, string>? environment = null,
            IReadOnlyDictionary<string, string>? verifiedRuntimeFiles = null,
            AiPublicationExecutionDescriptor? executionDescriptor = null,
            IEnumerable<string>? approvedLaunchRoots = null)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            if (runtime.ExecutionLanguage != "python")
                throw new NotSupportedException("The Python source loader cannot execute another language.");
            if (!Version.TryParse(runtime.RuntimeVersion, out var version) || version.Major != 3 ||
                version.Minor is not (12 or 13) || version.Build < 0 || version.Revision != -1 ||
                version.ToString(3) != runtime.RuntimeVersion)
                throw new ArgumentException("An exact stable CPython 3.12.x or 3.13.x runtime version is required.", nameof(runtime));
            if (string.IsNullOrEmpty(workerScriptPath) || !Path.IsPathFullyQualified(workerScriptPath) ||
                !workerScriptPath.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The installed Python loader needs an absolute .py path.", nameof(workerScriptPath));
            if (heartbeatMilliseconds is < 50 or > 5000)
                throw new ArgumentOutOfRangeException(nameof(heartbeatMilliseconds));

            var verified = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in verifiedRuntimeFiles ?? new Dictionary<string, string>())
                verified.Add(item.Key, item.Value);
            if (verified.TryGetValue(workerScriptPath, out var previous) && previous != workerScriptSha256)
                throw new ArgumentException("Conflicting approved loader digests.", nameof(verifiedRuntimeFiles));
            verified[workerScriptPath] = workerScriptSha256;

            return new AiWorkerProcessProfile(runtime, executablePath, executableSha256,
                new[] { "-I", "-S", "-B", "-u", "-X", "utf8", workerScriptPath,
                    "--runtime-reference=" + runtime.Reference,
                    "--runtime-version=" + runtime.RuntimeVersion,
                    "--runtime-sha256=" + runtime.RuntimeSha256,
                    "--heartbeat-ms=" + heartbeatMilliseconds.ToString(CultureInfo.InvariantCulture) },
                workingDirectory, environment, verified, executionDescriptor, approvedLaunchRoots);
        }
    }
}
