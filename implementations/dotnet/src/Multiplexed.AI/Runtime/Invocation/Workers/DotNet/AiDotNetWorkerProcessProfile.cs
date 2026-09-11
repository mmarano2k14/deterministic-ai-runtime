using System.Globalization;
using Multiplexed.Abstractions.AI.Publication;

namespace Multiplexed.AI.Runtime.Invocation.Workers.DotNet
{
    /// <summary>
    /// Host-owned profile for the published .NET assembly loader. The runtime host, worker assembly,
    /// dependency manifest and runtime configuration are deployment-owned and digest-verified.
    /// Published assemblies remain invocation material and are never registered in the server AppDomain.
    /// </summary>
    public static class AiDotNetWorkerProcessProfile
    {
        public static AiWorkerProcessProfile Create(AiPublicationEnvironment runtime,
            string executablePath, string executableSha256, string workerAssemblyPath,
            string workerAssemblySha256, string workerDepsPath, string workerDepsSha256,
            string workerRuntimeConfigPath, string workerRuntimeConfigSha256, string workingDirectory,
            int heartbeatMilliseconds = 1000, IReadOnlyDictionary<string, string>? environment = null,
            IReadOnlyDictionary<string, string>? verifiedRuntimeFiles = null)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            if (runtime.ExecutionLanguage != "dotnet")
                throw new NotSupportedException("The .NET assembly loader cannot execute another language.");
            if (!Version.TryParse(runtime.RuntimeVersion, out var version) || version.Major != 10 ||
                version.Minor != 0 || version.Build < 0 || version.Revision != -1 ||
                version.ToString(3) != runtime.RuntimeVersion)
                throw new ArgumentException("An exact .NET 10.0.x runtime version is required.", nameof(runtime));
            foreach (var path in new[] { workerAssemblyPath, workerDepsPath, workerRuntimeConfigPath })
                if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path))
                    throw new ArgumentException("The installed .NET worker launch files require absolute paths.");
            if (!workerAssemblyPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                !workerDepsPath.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
                !workerRuntimeConfigPath.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The installed .NET worker launch file set is invalid.");
            if (heartbeatMilliseconds is < 50 or > 5000)
                throw new ArgumentOutOfRangeException(nameof(heartbeatMilliseconds));

            var verified = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in verifiedRuntimeFiles ?? new Dictionary<string, string>())
                verified.Add(item.Key, item.Value);
            Add(workerAssemblyPath, workerAssemblySha256);
            Add(workerDepsPath, workerDepsSha256);
            Add(workerRuntimeConfigPath, workerRuntimeConfigSha256);

            return new AiWorkerProcessProfile(runtime, executablePath, executableSha256,
                new[] { workerAssemblyPath,
                    "--runtime-reference=" + runtime.Reference,
                    "--runtime-version=" + runtime.RuntimeVersion,
                    "--runtime-sha256=" + runtime.RuntimeSha256,
                    "--heartbeat-ms=" + heartbeatMilliseconds.ToString(CultureInfo.InvariantCulture) },
                workingDirectory, environment, verified);

            void Add(string path, string hash)
            {
                if (verified.TryGetValue(path, out var previous) && previous != hash)
                    throw new ArgumentException("Conflicting approved .NET worker file digests.", nameof(verifiedRuntimeFiles));
                verified[path] = hash;
            }
        }
    }
}
