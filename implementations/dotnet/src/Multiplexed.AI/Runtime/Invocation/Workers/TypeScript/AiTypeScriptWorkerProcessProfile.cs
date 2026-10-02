using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Publication;

namespace Multiplexed.AI.Runtime.Invocation.Workers.TypeScript
{
    /// <summary>
    /// Host-owned TypeScript-to-JavaScript profile, independent of Node's native TypeScript flags.
    /// Publication pins the exact Node executable and the compiler/loader contract before execution.
    /// Profile construction performs no package-manager I/O, file reads or process startup.
    /// </summary>
    public static class AiTypeScriptWorkerProcessProfile
    {
        public const string CompilerVersion = "5.8.3";
        public const string CompilerSha256 = "dd17428736a07e1db1a138d8a14295ddb2699ba780ee15038acdd2c6da5373a0";
        public const string CompilerFileName = "typescript-5.8.3.cjs";
        public const string ToolchainContract = "multiplexed-typescript-js-v1|typescript=5.8.3|sha256=dd17428736a07e1db1a138d8a14295ddb2699ba780ee15038acdd2c6da5373a0|target=ES2020|module=ESNext|resolution=Bundler|verbatim=true|rewriteRelativeImportExtensions=true|useDefineForClassFields=true|newLine=LF|sourceMaps=false|helpers=inline";
        private const string ReferenceSuffix = "@typescript-js-v1-";

        /// <summary>
        /// Create a new publication-time environment. RuntimeSha256 remains the Node executable hash;
        /// the immutable reference additionally identifies the loader, compiler bytes and emit options.
        /// Existing pinned native-loader environments must not be relabelled or changed in place.
        /// </summary>
        public static AiPublicationEnvironment CreateRuntime(string reference, string runtimeVersion,
            string executableSha256, string workerScriptSha256)
        {
            var runtime = new AiPublicationEnvironment(reference, AiExecutionLanguages.TypeScript, runtimeVersion, executableSha256);
            ValidateVersion(runtimeVersion);
            AiPublicationJson.ValidateEnvironment(runtime);
            AiPublicationJson.ValidateHash(workerScriptSha256);
            if (reference.Contains(ReferenceSuffix, StringComparison.Ordinal))
                throw new ArgumentException("Use the unbound host reference when creating a TypeScript environment.", nameof(reference));
            var bound = runtime with { Reference = reference + Suffix(workerScriptSha256) };
            AiPublicationJson.ValidateEnvironment(bound);
            return bound;
        }

        public static AiWorkerProcessProfile Create(AiPublicationEnvironment runtime,
            string executablePath, string executableSha256, string workerScriptPath,
            string workerScriptSha256, string workingDirectory, int heartbeatMilliseconds = 1000,
            IReadOnlyDictionary<string, string>? environment = null,
            IReadOnlyDictionary<string, string>? verifiedRuntimeFiles = null,
            AiPublicationExecutionDescriptor? executionDescriptor = null,
            IEnumerable<string>? approvedLaunchRoots = null)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            if (runtime.ExecutionLanguage != AiExecutionLanguages.TypeScript)
                throw new NotSupportedException("The TypeScript source loader cannot execute another language.");
            ValidateVersion(runtime.RuntimeVersion);
            if (string.IsNullOrEmpty(workerScriptPath) || !Path.IsPathFullyQualified(workerScriptPath) ||
                !workerScriptPath.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The installed TypeScript loader needs an absolute .mjs path.", nameof(workerScriptPath));
            if (heartbeatMilliseconds is < 50 or > 5000)
                throw new ArgumentOutOfRangeException(nameof(heartbeatMilliseconds));
            AiPublicationJson.ValidateHash(workerScriptSha256);
            AiPublicationJson.ValidateHash(executableSha256);
            if (!runtime.Reference.EndsWith(Suffix(workerScriptSha256), StringComparison.Ordinal) ||
                runtime.RuntimeSha256 != executableSha256)
                throw new InvalidOperationException("The exact TypeScript toolchain is not pinned. Use CreateRuntime before publishing a new run; retain the original profile for an existing run.");

            if (environment?.Keys.Any(key => key.Equals("NODE_OPTIONS", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("NODE_PATH", StringComparison.OrdinalIgnoreCase)) == true)
                throw new ArgumentException("NODE_OPTIONS and NODE_PATH cannot override the pinned TypeScript loader.", nameof(environment));

            var verified = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in verifiedRuntimeFiles ?? new Dictionary<string, string>())
                verified.Add(item.Key, item.Value);
            AddVerified(workerScriptPath, workerScriptSha256);
            // A fixed adjacent host file, never a tenant/global-module lookup or package installation.
            AddVerified(Path.Combine(Path.GetDirectoryName(workerScriptPath)!, "vendor", CompilerFileName), CompilerSha256);

            return new AiWorkerProcessProfile(runtime, executablePath, executableSha256,
                new[] { workerScriptPath,
                    "--runtime-reference=" + runtime.Reference,
                    "--runtime-version=" + runtime.RuntimeVersion,
                    "--runtime-sha256=" + runtime.RuntimeSha256,
                    "--heartbeat-ms=" + heartbeatMilliseconds.ToString(CultureInfo.InvariantCulture) },
                workingDirectory, environment, verified, executionDescriptor, approvedLaunchRoots);

            void AddVerified(string path, string hash)
            {
                if (verified.TryGetValue(path, out var previous) && previous != hash)
                    throw new ArgumentException("Conflicting approved TypeScript toolchain digests.", nameof(verifiedRuntimeFiles));
                verified[path] = hash;
            }
        }

        private static string Suffix(string workerScriptSha256) => ReferenceSuffix +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                ToolchainContract + "|loader-sha256=" + workerScriptSha256))).ToLowerInvariant();

        private static void ValidateVersion(string value)
        {
            if (!Version.TryParse(value, out var version) || version.Major < 1 || version.Build < 0 ||
                version.Revision != -1 || version.ToString(3) != value)
                throw new ArgumentException("An exact stable Node.js major.minor.patch version is required.", nameof(value));
            // Capability/execution tests determine compatibility; no allow-list of Node major versions.
        }
    }
}
