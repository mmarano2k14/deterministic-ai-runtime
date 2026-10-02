using System.Text.Json;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers.Isolation;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.Isolation
{
    /// <summary>Exact OCI launch and private protocol tests using a build-owned Docker-compatible engine probe.</summary>
    [Trait("Category", "ContainerProcess")]
    public sealed class AiContainerWorkerTransportTests
    {
        [Fact]
        public void Launch_Plan_Uses_Exact_Manifest_And_Never_Pulls()
        {
            var profile = ContainerWorkerTestSupport.Profile();
            var plan = AiContainerWorkerLaunchPlan.Create(profile, "multiplexed-ai-test");
            Assert.Equal(profile.ImageReference, plan.ImageReference);
            Assert.Equal(profile.ImageReference, plan.Arguments[^5]);
            Assert.Equal("--runtime-reference=" + profile.Runtime.Reference, plan.Arguments[^4]);
            Assert.Equal("--runtime-version=" + profile.Runtime.RuntimeVersion, plan.Arguments[^3]);
            Assert.Equal("--runtime-sha256=" + profile.Runtime.RuntimeSha256, plan.Arguments[^2]);
            Assert.Equal("--heartbeat-ms=" + profile.HeartbeatMilliseconds, plan.Arguments[^1]);
            Assert.Contains("--pull=never", plan.Arguments);
            Assert.DoesNotContain("pull", plan.Arguments);
            Assert.DoesNotContain("--volume", plan.Arguments);
            Assert.DoesNotContain("--mount", plan.Arguments);
            Assert.DoesNotContain("--privileged", plan.Arguments);
        }

        [Fact]
        public void Launch_Plan_Carries_The_Selected_Isolation_Controls()
        {
            var profile = ContainerWorkerTestSupport.Profile();
            var plan = AiContainerWorkerLaunchPlan.Create(profile, "multiplexed-ai-test");
            Assert.Contains("--user", plan.Arguments);
            Assert.Contains(profile.ContainerUser, plan.Arguments);
            Assert.Contains("--label=multiplexed.ai.hosted-worker=1", plan.Arguments);
            Assert.Contains("--label=multiplexed.ai.owner-scope=" + profile.ContainerOwnerScope, plan.Arguments);
            Assert.Contains("--network=none", plan.Arguments);
            Assert.Contains("--read-only", plan.Arguments);
            Assert.Contains("--cap-drop=ALL", plan.Arguments);
            Assert.Contains("--security-opt=no-new-privileges", plan.Arguments);
            Assert.Contains("--pids-limit=32", plan.Arguments);
            Assert.Contains("--memory=268435456", plan.Arguments);
            Assert.Contains("--cpus=0.75", plan.Arguments);
            Assert.Contains("--tmpfs=/tmp:rw,noexec,nosuid,nodev,size=33554432", plan.Arguments);
        }

        [Fact]
        public void Start_Info_Uses_Literal_Arguments_And_Only_Explicit_Engine_Environment()
        {
            var profile = ContainerWorkerTestSupport.Profile();
            var plan = AiContainerWorkerLaunchPlan.Create(profile, "multiplexed-ai-test");
            var start = AiContainerWorkerTransport.CreateStartInfo(profile, plan);
            Assert.False(start.UseShellExecute);
            Assert.Equal(profile.EngineExecutablePath, start.FileName);
            Assert.Equal(profile.EngineWorkingDirectory, start.WorkingDirectory);
            Assert.Equal(plan.Arguments, start.ArgumentList.ToArray());
            Assert.Equal("explicit-only", start.Environment["CONTAINER_ENGINE_PROBE_VALUE"]);
            Assert.False(start.Environment.ContainsKey("PATH"));
        }

        [Fact]
        public async Task Real_Engine_Probe_Receives_Exact_Image_And_Existing_Worker_Protocol()
        {
            var profile = ContainerWorkerTestSupport.Profile();
            var request = ContainerWorkerTestSupport.Request(profile);
            var heartbeats = 0;
            var result = await ContainerWorkerTestSupport.Transport(profile).InvokeAsync(
                request, _ => { heartbeats++; return Task.CompletedTask; });
            Assert.True(result.Success);
            Assert.Equal(1, heartbeats);
            using var payload = JsonDocument.Parse(result.PayloadJson);
            var root = payload.RootElement;
            Assert.Equal(profile.ImageReference, root.GetProperty("image").GetString());
            Assert.Equal("explicit-only", root.GetProperty("explicitValue").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("inheritedPath").ValueKind);
            var arguments = root.GetProperty("arguments").EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert.Contains("--pull=never", arguments);
            Assert.Equal(profile.ImageReference, arguments[^5]);
            var workerArguments = root.GetProperty("workerArguments").EnumerateArray().Select(item => item.GetString()!).ToArray();
            Assert.Equal(new[]
            {
                "--runtime-reference=" + profile.Runtime.Reference,
                "--runtime-version=" + profile.Runtime.RuntimeVersion,
                "--runtime-sha256=" + profile.Runtime.RuntimeSha256,
                "--heartbeat-ms=" + profile.HeartbeatMilliseconds
            }, workerArguments);
            var nameIndex = Array.IndexOf(arguments, "--name");
            Assert.True(nameIndex >= 0);
            Assert.StartsWith("multiplexed-ai-", arguments[nameIndex + 1], StringComparison.Ordinal);
            Assert.DoesNotContain(request.TenantId, arguments[nameIndex + 1], StringComparison.Ordinal);
            Assert.DoesNotContain(request.ExecutionId, arguments[nameIndex + 1], StringComparison.Ordinal);
        }

        [Fact]
        public async Task Engine_Digest_Mismatch_Is_Refused_Before_Launch()
        {
            var profile = ContainerWorkerTestSupport.Profile(executableHash: new string('0', 64));
            await Assert.ThrowsAsync<InvalidOperationException>(() => ContainerWorkerTestSupport.Transport(profile)
                .InvokeAsync(ContainerWorkerTestSupport.Request(profile), _ => Task.CompletedTask));
        }

        [Fact]
        public async Task Uninstalled_Isolated_Profile_Cannot_Fall_Back_To_Trusted_Process()
        {
            var profile = ContainerWorkerTestSupport.Profile();
            var transport = new AiContainerWorkerTransport(
                new AiConfiguredContainerWorkerCatalog(Array.Empty<AiContainerWorkerProfile>()), new());
            await Assert.ThrowsAsync<NotSupportedException>(() => transport.InvokeAsync(
                ContainerWorkerTestSupport.Request(profile), _ => Task.CompletedTask));
        }

        [Fact]
        public async Task Preexpired_Deadline_Does_Not_Start_The_Container_Engine()
        {
            var profile = ContainerWorkerTestSupport.Profile();
            var request = ContainerWorkerTestSupport.Request(profile) with { DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };
            await Assert.ThrowsAsync<TimeoutException>(() => ContainerWorkerTestSupport.Transport(profile)
                .InvokeAsync(request, _ => Task.CompletedTask));
        }

        [Fact]
        public async Task Cancellation_Forces_Explicit_Container_Removal()
        {
            var marker = Path.Combine(Path.GetTempPath(), "multiplexed-container-cleanup-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                var profile = ContainerWorkerTestSupport.Profile(new Dictionary<string, string>
                {
                    ["CONTAINER_ENGINE_PROBE_MODE"] = "hang-after-ready",
                    ["CONTAINER_ENGINE_PROBE_CLEANUP_MARKER"] = marker
                });
                using var cancellation = new CancellationTokenSource();
                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var operation = ContainerWorkerTestSupport.Transport(profile).InvokeAsync(
                    ContainerWorkerTestSupport.Request(profile),
                    _ => { ready.TrySetResult(true); return Task.CompletedTask; }, cancellation.Token);
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(15)));
                Assert.True(File.Exists(marker));
                var cleanup = await File.ReadAllTextAsync(marker);
                Assert.StartsWith("rm\n--force\nmultiplexed-ai-", cleanup.Replace("\r\n", "\n"), StringComparison.Ordinal);
            }
            finally { try { File.Delete(marker); } catch { } }
        }

        [Fact]
        public async Task Unconfirmed_Container_Removal_Is_A_Cleanup_Failure()
        {
            var profile = ContainerWorkerTestSupport.Profile(new Dictionary<string, string>
            {
                ["CONTAINER_ENGINE_PROBE_MODE"] = "hang-after-ready",
                ["CONTAINER_ENGINE_PROBE_CLEANUP_FAIL"] = "1"
            });
            using var cancellation = new CancellationTokenSource();
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = ContainerWorkerTestSupport.Transport(profile).InvokeAsync(
                ContainerWorkerTestSupport.Request(profile),
                _ => { ready.TrySetResult(true); return Task.CompletedTask; }, cancellation.Token);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            cancellation.Cancel();
            await Assert.ThrowsAsync<AiWorkerProcessCleanupException>(() => operation.WaitAsync(TimeSpan.FromSeconds(15)));
        }

        [Theory]
        [InlineData("")]
        [InlineData("../escape")]
        [InlineData("contains space")]
        public void Invalid_Server_Container_Names_Are_Refused(string name)
        {
            Assert.Throws<ArgumentException>(() =>
                AiContainerWorkerLaunchPlan.Create(ContainerWorkerTestSupport.Profile(), name));
        }
    }
}
