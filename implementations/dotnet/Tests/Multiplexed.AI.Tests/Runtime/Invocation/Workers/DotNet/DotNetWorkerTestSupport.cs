using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers.DotNet;
using Multiplexed.AI.Tests.Runtime.Publication;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.DotNet
{
    /// <summary>Tests read build-owned fixture assemblies; production profiles require deployment-owned approved digests.</summary>
    internal static class DotNetWorkerTestSupport
    {
        internal const string TypeName = "Multiplexed.AI.HostedInvocation.TestFunctions.Functions";
        private static readonly Lazy<Task<Installation>> Installed = new(LoadInstallationAsync);

        internal static async Task<AiWorkerProcessProfile> ProfileAsync() => (await Installed.Value).Profile;
        internal static async Task<AiWorkerProcessTransport> TransportAsync(AiWorkerProcessTransportOptions? options = null) =>
            new(new AiConfiguredWorkerProcessCatalog(new[] { await ProfileAsync() }), options ?? new());

        internal static AiWorkerFile File(string path, byte[] bytes) => new(path,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.LongLength,
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'));

        internal static async Task<AiWorkerInvocationRequest> RequestAsync(string method = "Run", bool includeDependency = true)
        {
            var installation = await Installed.Value;
            var request = WorkerTestSupport.Request();
            var dependencies = includeDependency
                ? new[] { new AiWorkerDependency("testdependency", "1.0.0",
                    new[] { File("Multiplexed.AI.HostedInvocation.TestDependency.dll", installation.DependencyBytes) }) }
                : Array.Empty<AiWorkerDependency>();
            return request with
            {
                Inputs = JsonSerializer.SerializeToElement(new { amount = 21 }),
                Code = new(request.Code.Target with { ExecutionLanguage = "dotnet" }, installation.Profile.Runtime,
                    "functions.dll", TypeName + "::" + method,
                    new[] { File("functions.dll", installation.FunctionBytes) }, dependencies)
            };
        }

        internal static async Task<AiDurableInvocationResult> ExecuteAsync(string method = "Run", bool includeDependency = true)
        {
            var request = await RequestAsync(method, includeDependency);
            return await (await TransportAsync()).InvokeAsync(request, _ => Task.CompletedTask);
        }

        internal static AiPipelinePublicationUpload Upload(AiPublicationEnvironment runtime, string revision = "1")
        {
            var installation = Installed.Value.GetAwaiter().GetResult();
            var symbol = TypeName + "::" + (revision == "2" ? "Revision2" : "Revision1");
            AiPublicationFunctionUpload Function(string name) => new(new(AiPublicationFunctionKind.Step, name),
                runtime.Reference, "functions.dll", symbol,
                new[] { new AiPublicationFileUpload("functions.dll", installation.FunctionBytes) },
                new[] { new AiPublicationDependencyUpload("testdependency", "1.0.0",
                    new[] { new AiPublicationFileUpload("Multiplexed.AI.HostedInvocation.TestDependency.dll", installation.DependencyBytes) }) });
            return new(PublicationTestSupport.Definition(revision, "dotnet", secondLanguage: null),
                new[] { Function("first"), Function("second") });
        }

        internal static AiWorkerInvocationSupervisor Supervisor(WorkerTestSupport.PublishedFixture fixture,
            AiWorkerProcessTransport transport, AiWorkerProcessCapacity capacity, AiWorkerSupervisionOptions options) =>
            new(fixture.Publication.Journal, fixture.Preparer, transport, fixture.Publication.ControlPlane,
                capacity, options, NullLogger<AiWorkerInvocationSupervisor>.Instance, fixture.Publication.Clock);

        private static async Task<Installation> LoadInstallationAsync()
        {
            var executable = ResolveDotNetExecutable();
            var root = FixtureRoot();
            string Worker(string name) => Path.Combine(root, name);
            var workerAssembly = Worker("Multiplexed.AI.HostedInvocation.DotNetWorker.dll");
            var workerDeps = Worker("Multiplexed.AI.HostedInvocation.DotNetWorker.deps.json");
            var workerRuntimeConfig = Worker("Multiplexed.AI.HostedInvocation.DotNetWorker.runtimeconfig.json");
            var functions = Worker("Multiplexed.AI.HostedInvocation.TestFunctions.dll");
            var dependency = Worker("Multiplexed.AI.HostedInvocation.TestDependency.dll");
            foreach (var path in new[] { workerAssembly, workerDeps, workerRuntimeConfig, functions, dependency })
                if (!System.IO.File.Exists(path)) throw new FileNotFoundException("Hosted .NET fixture output is missing.", path);
            var environment = new Dictionary<string, string>();
            if (OperatingSystem.IsWindows()) environment["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot")
                ?? throw new InvalidOperationException("Windows hosted .NET tests require SystemRoot.");
            var version = await FindRuntimeVersionAsync(executable, environment);
            var executableHash = WorkerTestSupport.FileHash(executable);
            var runtime = new AiPublicationEnvironment("dotnet-fixed", "dotnet", version, executableHash);
            var profile = AiDotNetWorkerProcessProfile.Create(runtime, executable, executableHash,
                workerAssembly, WorkerTestSupport.FileHash(workerAssembly), workerDeps, WorkerTestSupport.FileHash(workerDeps),
                workerRuntimeConfig, WorkerTestSupport.FileHash(workerRuntimeConfig), root, 100, environment);
            return new(profile, await System.IO.File.ReadAllBytesAsync(functions), await System.IO.File.ReadAllBytesAsync(dependency));
        }


        private static string ResolveDotNetExecutable()
        {
            foreach (var candidate in new[]
            {
                Environment.GetEnvironmentVariable("MULTIPLEXED_DOTNET_EXECUTABLE"),
                Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"),
                Environment.ProcessPath is { } process && string.Equals(Path.GetFileNameWithoutExtension(process), "dotnet", StringComparison.OrdinalIgnoreCase) ? process : null
            })
                if (!string.IsNullOrWhiteSpace(candidate) && Path.IsPathFullyQualified(candidate) && System.IO.File.Exists(candidate))
                    return candidate;
            var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(directory.Trim(), name);
                if (System.IO.File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            throw new FileNotFoundException("The hosted .NET tests require an installed dotnet host. Set MULTIPLEXED_DOTNET_EXECUTABLE when PATH discovery is unavailable.");
        }

        private static async Task<string> FindRuntimeVersionAsync(string executable, IReadOnlyDictionary<string, string> environment)
        {
            var start = new ProcessStartInfo
            {
                FileName = executable, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.Environment.Clear(); foreach (var item in environment) start.Environment[item.Key] = item.Value;
            start.ArgumentList.Add("--list-runtimes");
            using var process = new Process { StartInfo = start };
            if (!process.Start()) throw new IOException("The configured dotnet host did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); _ = await stderr;
            if (process.ExitCode != 0) throw new IOException("The configured dotnet host failed runtime inspection.");
            var versions = (await stdout).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith("Microsoft.NETCore.App 10.0.", StringComparison.Ordinal))
                .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1])
                .Select(text => Version.TryParse(text, out var value) ? value : null).Where(value => value is not null)
                .Cast<Version>().OrderByDescending(value => value).ToArray();
            if (versions.Length == 0) throw new NotSupportedException("Hosted .NET tests require Microsoft.NETCore.App 10.0.x.");
            return versions[0].ToString(3);
        }

        private static string FixtureRoot()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "dotnet-worker-fixture");
            if (Directory.Exists(path)) return path;
            throw new DirectoryNotFoundException("Build the test project without --no-build so the hosted .NET fixture is produced.");
        }

        private sealed record Installation(AiWorkerProcessProfile Profile, byte[] FunctionBytes, byte[] DependencyBytes);
    }
}
