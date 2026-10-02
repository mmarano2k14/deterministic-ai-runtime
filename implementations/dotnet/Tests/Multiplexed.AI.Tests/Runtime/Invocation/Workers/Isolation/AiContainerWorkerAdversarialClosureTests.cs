using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers.Isolation;
using Multiplexed.AI.Tests.Runtime.Publication;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.Isolation
{
    /// <summary>Adversarial lifecycle and compatibility closure around the isolated provider boundary.</summary>
    [Trait("Category", "ContainerProcess")]
    public sealed class AiContainerWorkerAdversarialClosureTests
    {
        [Fact]
        public async Task Startup_Reconciliation_Removes_Only_Server_Owned_Stale_Containers_Before_Launch()
        {
            var marker = Path.Combine(Path.GetTempPath(), "multiplexed-container-reconcile-" + Guid.NewGuid().ToString("N") + ".txt");
            const string stale = "multiplexed-ai-stale-owned";
            try
            {
                var profile = ContainerWorkerTestSupport.Profile(new Dictionary<string, string>
                {
                    ["CONTAINER_ENGINE_PROBE_PS_NAMES"] = stale,
                    ["CONTAINER_ENGINE_PROBE_CLEANUP_MARKER"] = marker,
                    ["CONTAINER_ENGINE_PROBE_REQUIRE_CLEANUP_BEFORE_RUN"] = "1"
                }, containerOwnerScope: "test-host-reconcile");

                var result = await ContainerWorkerTestSupport.Transport(profile).InvokeAsync(
                    ContainerWorkerTestSupport.Request(profile), _ => Task.CompletedTask);

                Assert.True(result.Success);
                Assert.True(File.Exists(marker));
                var cleanup = (await File.ReadAllTextAsync(marker)).Replace("\r\n", "\n");
                Assert.Equal("rm\n--force\n" + stale, cleanup);
            }
            finally { try { File.Delete(marker); } catch { } }
        }

        [Fact]
        public async Task Startup_Reconciliation_Fails_Closed_When_Stale_Cleanup_Is_Unconfirmed()
        {
            var requestMarker = Path.Combine(Path.GetTempPath(), "multiplexed-container-reconcile-request-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                var profile = ContainerWorkerTestSupport.Profile(new Dictionary<string, string>
                {
                    ["CONTAINER_ENGINE_PROBE_PS_NAMES"] = "multiplexed-ai-stale-owned",
                    ["CONTAINER_ENGINE_PROBE_CLEANUP_FAIL"] = "1",
                    ["CONTAINER_ENGINE_PROBE_REQUEST_MARKER"] = requestMarker
                }, containerOwnerScope: "test-host-reconcile-fail");

                await Assert.ThrowsAsync<AiWorkerProcessCleanupException>(() =>
                    ContainerWorkerTestSupport.Transport(profile).InvokeAsync(
                        ContainerWorkerTestSupport.Request(profile), _ => Task.CompletedTask));
                Assert.False(File.Exists(requestMarker));
            }
            finally { try { File.Delete(requestMarker); } catch { } }
        }

        [Fact]
        public async Task Startup_Reconciliation_Rejects_Unexpected_Container_Identity()
        {
            var profile = ContainerWorkerTestSupport.Profile(new Dictionary<string, string>
            {
                ["CONTAINER_ENGINE_PROBE_PS_NAMES"] = "unowned-container"
            }, containerOwnerScope: "test-host-reconcile-identity");

            await Assert.ThrowsAsync<AiWorkerProcessCleanupException>(() =>
                ContainerWorkerTestSupport.Transport(profile).InvokeAsync(
                    ContainerWorkerTestSupport.Request(profile), _ => Task.CompletedTask));
        }

        [Fact]
        public async Task Cancellation_During_Isolation_Attestation_Cleans_The_Container_And_Control_Process()
        {
            var runMarker = Path.Combine(Path.GetTempPath(), "multiplexed-container-run-" + Guid.NewGuid().ToString("N") + ".txt");
            var cleanupMarker = Path.Combine(Path.GetTempPath(), "multiplexed-container-cleanup-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                var profile = ContainerWorkerTestSupport.Profile(new Dictionary<string, string>
                {
                    ["CONTAINER_ENGINE_PROBE_INSPECT_DELAY_MS"] = "5000",
                    ["CONTAINER_ENGINE_PROBE_RUN_MARKER"] = runMarker,
                    ["CONTAINER_ENGINE_PROBE_CLEANUP_MARKER"] = cleanupMarker
                }, containerOwnerScope: "test-host-startup-cancel");
                using var cancellation = new CancellationTokenSource();
                var operation = ContainerWorkerTestSupport.Transport(profile).InvokeAsync(
                    ContainerWorkerTestSupport.Request(profile), _ => Task.CompletedTask, cancellation.Token);

                await WaitForFileAsync(runMarker);
                cancellation.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(15)));
                Assert.True(File.Exists(cleanupMarker));
            }
            finally
            {
                try { File.Delete(runMarker); } catch { }
                try { File.Delete(cleanupMarker); } catch { }
            }
        }

        [Fact]
        public async Task Attached_Engine_Client_Loss_After_Attestation_Still_Forces_Direct_Container_Cleanup()
        {
            var cleanupMarker = Path.Combine(Path.GetTempPath(), "multiplexed-container-engine-loss-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                var profile = ContainerWorkerTestSupport.Profile(new Dictionary<string, string>
                {
                    ["CONTAINER_ENGINE_PROBE_MODE"] = "exit-after-inspect",
                    ["CONTAINER_ENGINE_PROBE_CLEANUP_MARKER"] = cleanupMarker
                }, containerOwnerScope: "test-host-engine-loss");

                await Assert.ThrowsAnyAsync<Exception>(() =>
                    ContainerWorkerTestSupport.Transport(profile).InvokeAsync(
                        ContainerWorkerTestSupport.Request(profile), _ => Task.CompletedTask));

                Assert.True(File.Exists(cleanupMarker));
                var cleanup = (await File.ReadAllTextAsync(cleanupMarker)).Replace("\r\n", "\n");
                Assert.StartsWith("rm\n--force\nmultiplexed-ai-", cleanup, StringComparison.Ordinal);
            }
            finally { try { File.Delete(cleanupMarker); } catch { } }
        }

        [Theory]
        [InlineData("python", AiPublicationDependencyPackageKind.PythonWheelBundle)]
        [InlineData("typescript", AiPublicationDependencyPackageKind.NodeLockedBundle)]
        [InlineData("dotnet", AiPublicationDependencyPackageKind.DotNetAssemblyClosure)]
        public async Task Packaged_Dependency_Metadata_Crosses_The_Isolated_Provider_Unchanged(
            string language,
            AiPublicationDependencyPackageKind packageKind)
        {
            var runtime = PublicationTestSupport.Environment(language);
            var profile = ContainerWorkerTestSupport.Profile(runtime: runtime, containerOwnerScope: "test-host-package-" + language);
            var request = PackageRequest(profile, packageKind);

            var result = await ContainerWorkerTestSupport.Transport(profile).InvokeAsync(
                request, _ => Task.CompletedTask);

            Assert.True(result.Success);
            using var payload = JsonDocument.Parse(result.PayloadJson);
            var packages = payload.RootElement.GetProperty("dependencyPackages")
                .EnumerateArray().Select(value => value.GetString()).ToArray();
            Assert.Equal(new[] { packageKind.ToString() }, packages);
        }

        private static AiWorkerInvocationRequest PackageRequest(
            AiContainerWorkerProfile profile,
            AiPublicationDependencyPackageKind packageKind)
        {
            var request = WorkerTestSupport.Request();
            var language = profile.Runtime.ExecutionLanguage;
            var extension = language switch
            {
                "python" => "py",
                "typescript" => "ts",
                "dotnet" => "dll",
                _ => "bin"
            };
            var entryPath = "main." + extension;
            var source = WorkerFile(entryPath, "source-" + language);
            var dependency = new AiWorkerDependency(
                "dependency",
                "1.0.0",
                new[]
                {
                    WorkerFile("bundle.manifest.json", "manifest-" + packageKind),
                    WorkerFile("payload.bin", "payload-" + packageKind)
                })
            {
                Package = new AiPublicationDependencyPackage(1, packageKind, "bundle.manifest.json")
            };
            var target = request.Code.Target with { ExecutionLanguage = language };
            var code = new AiWorkerCodeBundle(
                target,
                profile.Runtime,
                entryPath,
                "run",
                new[] { source },
                new[] { dependency })
            {
                ExecutionDescriptor = profile.ExecutionDescriptor
            };
            return request with { Code = code };
        }

        private static AiWorkerFile WorkerFile(string path, string content)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            return new(
                path,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes.LongLength,
                Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        }

        private static async Task WaitForFileAsync(string path)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!File.Exists(path)) await Task.Delay(20, timeout.Token);
        }
    }
}
