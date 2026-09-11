using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers.TypeScript;
using Multiplexed.AI.Tests.Runtime.Publication;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.TypeScript
{
    /// <summary>Missing Node.js opt-in is a visible skip; invalid configured installations fail tests.</summary>
    public sealed class TypeScriptWorkerFactAttribute : FactAttribute
    {
        public TypeScriptWorkerFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MULTIPLEXED_NODE_EXECUTABLE")))
                Skip = "Set MULTIPLEXED_NODE_EXECUTABLE to an absolute supported Node.js executable path to run real TypeScript tests.";
        }
    }

    public sealed class TypeScriptWorkerTheoryAttribute : TheoryAttribute
    {
        public TypeScriptWorkerTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MULTIPLEXED_NODE_EXECUTABLE")))
                Skip = "Set MULTIPLEXED_NODE_EXECUTABLE to run real TypeScript tests.";
        }
    }

    /// <summary>Only tests discover installed version/digests; production profiles require approved identities.</summary>
    internal static class TypeScriptWorkerTestSupport
    {
        internal const string Simple = "export function run(inputs: { amount: number }, context: unknown) { return { success: true, payload: { value: inputs.amount * 2 } }; }\n";
        private static readonly Lazy<Task<AiWorkerProcessProfile>> Installation = new(LoadInstallationAsync);

        internal static Task<AiWorkerProcessProfile> ProfileAsync() => Installation.Value;

        internal static async Task<AiWorkerProcessTransport> TransportAsync(AiWorkerProcessTransportOptions? options = null) =>
            new(new AiConfiguredWorkerProcessCatalog(new[] { await ProfileAsync() }), options ?? new());

        internal static AiWorkerFile Source(string path, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            return new(path, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.LongLength,
                Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        }

        internal static async Task<AiWorkerInvocationRequest> RequestAsync(string source = Simple)
        {
            var request = WorkerTestSupport.Request();
            var profile = await ProfileAsync();
            return request with
            {
                Inputs = JsonSerializer.SerializeToElement(new { amount = 21 }),
                Code = new(request.Code.Target with { ExecutionLanguage = "typescript" }, profile.Runtime,
                    "main.ts", "run", new[] { Source("main.ts", source) }, Array.Empty<AiWorkerDependency>())
            };
        }

        internal static async Task<AiDurableInvocationResult> ExecuteAsync(string source = Simple) =>
            await (await TransportAsync()).InvokeAsync(await RequestAsync(source), _ => Task.CompletedTask);

        internal static string FindWorkerScript()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var path = Path.Combine(directory.FullName, "implementations", "node", "workers", "hosted_invocation", "worker.mjs");
                if (File.Exists(path)) return path;
            }
            throw new FileNotFoundException("Apply implementations/node from the package in the same repository as the .NET tests.");
        }

        private static async Task<AiWorkerProcessProfile> LoadInstallationAsync()
        {
            var executable = Environment.GetEnvironmentVariable("MULTIPLEXED_NODE_EXECUTABLE");
            if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) || !File.Exists(executable))
                throw new FileNotFoundException("MULTIPLEXED_NODE_EXECUTABLE must identify the installed Node.js executable.", executable);
            var script = FindWorkerScript();
            var environment = new Dictionary<string, string>();
            if (OperatingSystem.IsWindows()) environment["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot")
                ?? throw new InvalidOperationException("Windows Node.js tests require SystemRoot.");
            var start = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.Environment.Clear();
            foreach (var value in environment) start.Environment.Add(value.Key, value.Value);
            start.ArgumentList.Add("--version");
            using var process = new Process { StartInfo = start };
            if (!process.Start()) throw new IOException("The configured Node.js runtime did not start.");
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                var version = (await stdout).Trim();
                _ = await stderr;
                if (process.ExitCode != 0 || !version.StartsWith('v'))
                    throw new IOException("The installed Node.js runtime failed version inspection.");
                version = version[1..];
                var executableHash = WorkerTestSupport.FileHash(executable);
                var runtime = new AiPublicationEnvironment("typescript-node-fixed", "typescript", version, executableHash);
                return AiTypeScriptWorkerProcessProfile.Create(runtime, executable, executableHash, script,
                    WorkerTestSupport.FileHash(script), Path.GetDirectoryName(script)!, heartbeatMilliseconds: 100, environment: environment);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
        }

        internal static AiPipelinePublicationUpload Upload(AiPublicationEnvironment runtime, string revision = "1",
            string? source = null)
        {
            source ??= "export function run(inputs: { amount: number }, context: unknown) { return { success: true, payload: { revision: " +
                revision + ", amount: inputs.amount } }; }\n";
            AiPublicationFunctionUpload Function(string name) => new(new(AiPublicationFunctionKind.Step, name),
                runtime.Reference, "main.ts", "run",
                new[] { new AiPublicationFileUpload("main.ts", Encoding.UTF8.GetBytes(source)) },
                Array.Empty<AiPublicationDependencyUpload>());
            return new(PublicationTestSupport.Definition(revision, "typescript", secondLanguage: null),
                new[] { Function("first"), Function("second") });
        }

        internal static AiWorkerInvocationSupervisor Supervisor(WorkerTestSupport.PublishedFixture fixture,
            AiWorkerProcessTransport transport, AiWorkerProcessCapacity capacity, AiWorkerSupervisionOptions options) =>
            new(fixture.Publication.Journal, fixture.Preparer, transport, fixture.Publication.ControlPlane,
                capacity, options, NullLogger<AiWorkerInvocationSupervisor>.Instance, fixture.Publication.Clock);
    }
}
