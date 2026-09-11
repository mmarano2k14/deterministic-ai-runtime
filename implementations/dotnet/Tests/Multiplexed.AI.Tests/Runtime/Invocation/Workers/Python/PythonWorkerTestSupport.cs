using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers.Python;
using Multiplexed.AI.Tests.Runtime.Publication;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.Python
{
    /// <summary>Missing Python opt-in is a visible skip; invalid configured installations fail tests.</summary>
    public sealed class PythonWorkerFactAttribute : FactAttribute
    {
        public PythonWorkerFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MULTIPLEXED_PYTHON_EXECUTABLE")))
                Skip = "Set MULTIPLEXED_PYTHON_EXECUTABLE to an absolute CPython 3.12/3.13 executable path to run real Python tests.";
        }
    }
    public sealed class PythonWorkerTheoryAttribute : TheoryAttribute
    {
        public PythonWorkerTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MULTIPLEXED_PYTHON_EXECUTABLE")))
                Skip = "Set MULTIPLEXED_PYTHON_EXECUTABLE to run real Python tests.";
        }
    }

    /// <summary>Only tests discover installed version/digests; production profiles require approved identities.</summary>
    internal static class PythonWorkerTestSupport
    {
        internal const string Simple = "def run(inputs, context):\n    return {\"success\": True, \"payload\": {\"value\": inputs[\"amount\"] * 2}}\n";
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
            var request = WorkerTestSupport.Request(); var profile = await ProfileAsync();
            return request with
            {
                Inputs = JsonSerializer.SerializeToElement(new { amount = 21 }),
                Code = new(request.Code.Target, profile.Runtime, "main.py", "run", new[] { Source("main.py", source) },
                    Array.Empty<AiWorkerDependency>())
            };
        }
        internal static async Task<AiDurableInvocationResult> ExecuteAsync(string source = Simple) =>
            await (await TransportAsync()).InvokeAsync(await RequestAsync(source), _ => Task.CompletedTask);
        internal static string FindWorkerScript()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var path = Path.Combine(directory.FullName, "implementations", "python", "workers", "hosted_invocation", "worker.py");
                if (File.Exists(path)) return path;
            }
            throw new FileNotFoundException("Apply implementations/python from the package in the same repository as the .NET tests.");
        }
        private static async Task<AiWorkerProcessProfile> LoadInstallationAsync()
        {
            var executable = Environment.GetEnvironmentVariable("MULTIPLEXED_PYTHON_EXECUTABLE");
            if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) || !File.Exists(executable))
                throw new FileNotFoundException("MULTIPLEXED_PYTHON_EXECUTABLE must identify the installed Python executable, not py.exe or a directory.", executable);
            var script = FindWorkerScript();
            var environment = new Dictionary<string, string>();
            if (OperatingSystem.IsWindows()) environment["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot")
                ?? throw new InvalidOperationException("Windows Python tests require SystemRoot.");
            var start = new ProcessStartInfo
            {
                FileName = executable, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.Environment.Clear();
            foreach (var value in environment) start.Environment.Add(value.Key, value.Value);
            foreach (var value in new[] { "-I", "-S", "-B", "-X", "utf8", "-c",
                "import sys; print('.'.join(map(str, sys.version_info[:3])))" }) start.ArgumentList.Add(value);
            using var process = new Process { StartInfo = start };
            if (!process.Start()) throw new IOException("The configured Python interpreter did not start.");
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                var version = (await stdout).Trim(); _ = await stderr;
                if (process.ExitCode != 0) throw new IOException("The installed Python interpreter failed version inspection.");
                var executableHash = WorkerTestSupport.FileHash(executable);
                var runtime = new AiPublicationEnvironment("python-fixed", "python", version, executableHash);
                return AiPythonWorkerProcessProfile.Create(runtime, executable, executableHash, script,
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
            source ??= "def run(inputs, context):\n    return {\"success\": True, \"payload\": {\"revision\": " + revision +
                ", \"amount\": inputs[\"amount\"]}}\n";
            AiPublicationFunctionUpload Function(string name) => new(new(AiPublicationFunctionKind.Step, name),
                runtime.Reference, "main.py", "run",
                new[] { new AiPublicationFileUpload("main.py", Encoding.UTF8.GetBytes(source)) },
                Array.Empty<AiPublicationDependencyUpload>());
            return new(PublicationTestSupport.Definition(revision, "python", secondLanguage: null),
                new[] { Function("first"), Function("second") });
        }

        internal static AiWorkerInvocationSupervisor Supervisor(WorkerTestSupport.PublishedFixture fixture,
            AiWorkerProcessTransport transport, AiWorkerProcessCapacity capacity, AiWorkerSupervisionOptions options) =>
            new(fixture.Publication.Journal, fixture.Preparer, transport, fixture.Publication.ControlPlane,
                capacity, options, NullLogger<AiWorkerInvocationSupervisor>.Instance, fixture.Publication.Clock);
    }
}
