using System.Security.Cryptography;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers.Isolation;
using Multiplexed.AI.Runtime.Publication;
using Multiplexed.AI.Tests.Runtime.Publication;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.Isolation
{
    /// <summary>Build-owned Docker-compatible engine probe and exact OCI profile helpers.</summary>
    internal static class ContainerWorkerTestSupport
    {
        internal static string ProbeRoot => Path.Combine(AppContext.BaseDirectory, "container-engine-probe");
        internal static string ProbeExecutable => Path.Combine(ProbeRoot,
            "Multiplexed.AI.ContainerEngine.Probe" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));

        internal static AiContainerWorkerProfile Profile(
            IReadOnlyDictionary<string, string>? environment = null,
            string? executableHash = null)
        {
            if (!File.Exists(ProbeExecutable))
                throw new FileNotFoundException("Build the test project to produce the container engine probe.", ProbeExecutable);
            var runtime = PublicationTestSupport.Environment("python");
            var descriptor = new AiPublicationExecutionDescriptor
            {
                OperatingSystem = "linux",
                Architecture = "amd64",
                Artifact = new(
                    AiPublicationEnvironmentArtifactKind.OciImage,
                    "sha256:" + new string('b', 64),
                    AiPublicationExecutionDescriptors.OciImageMediaType),
                Requirements = new()
            };
            var engineEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["CONTAINER_ENGINE_PROBE_VALUE"] = "explicit-only",
                ["CONTAINER_ENGINE_PROBE_STATE_DIR"] = Path.Combine(ProbeRoot, "state-" + Guid.NewGuid().ToString("N")),
                ["CONTAINER_ENGINE_PROBE_REQUIRE_INSPECT_BEFORE_REQUEST"] = "1"
            };
            if (OperatingSystem.IsWindows())
                engineEnvironment["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot")
                    ?? throw new InvalidOperationException("Windows container-engine tests require SystemRoot.");
            foreach (var item in environment ?? new Dictionary<string, string>()) engineEnvironment[item.Key] = item.Value;
            return new(
                runtime,
                descriptor,
                ProbeExecutable,
                executableHash ?? FileHash(ProbeExecutable),
                ProbeRoot,
                "registry.example.com/multiplexed/python-worker",
                new AiContainerWorkerResourceLimits
                {
                    CpuMilliCores = 750,
                    MemoryBytes = 268435456,
                    PidsLimit = 32,
                    WritableWorkspaceBytes = 33554432
                },
                "65532:65532",
                engineEnvironment,
                new[] { ProbeRoot });
        }

        internal static AiWorkerInvocationRequest Request(AiContainerWorkerProfile profile)
        {
            var request = WorkerTestSupport.Request();
            return request with
            {
                Code = request.Code with
                {
                    Runtime = profile.Runtime,
                    ExecutionDescriptor = profile.ExecutionDescriptor
                }
            };
        }

        internal static AiContainerWorkerTransport Transport(
            AiContainerWorkerProfile profile,
            AiWorkerProcessTransportOptions? options = null) =>
            new(new AiConfiguredContainerWorkerCatalog(new[] { profile }), options ?? new());

        internal static string FileHash(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
    }
}
