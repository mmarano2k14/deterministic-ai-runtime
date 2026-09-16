using Multiplexed.AI.Runtime.Invocation.Workers.Isolation;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.Isolation
{
    /// <summary>Fail-closed applied-isolation checks performed before tenant request release.</summary>
    [Trait("Category", "ContainerProcess")]
    public sealed class AiContainerWorkerIsolationEnforcementTests
    {
        [Fact]
        public void Launch_Plan_Bounds_Memory_And_Swap_To_The_Same_Server_Limit()
        {
            var profile = ContainerWorkerTestSupport.Profile();
            var plan = AiContainerWorkerLaunchPlan.Create(profile, "multiplexed-ai-test");
            Assert.Contains("--memory=268435456", plan.Arguments);
            Assert.Contains("--memory-swap=268435456", plan.Arguments);
        }

        [Fact]
        public async Task Applied_Isolation_Is_Attested_Before_The_Invocation_Request_Is_Released()
        {
            var marker = Path.Combine(Path.GetTempPath(), "multiplexed-container-request-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                var profile = ContainerWorkerTestSupport.Profile(new Dictionary<string, string>
                {
                    ["CONTAINER_ENGINE_PROBE_REQUEST_MARKER"] = marker
                });
                var result = await ContainerWorkerTestSupport.Transport(profile).InvokeAsync(
                    ContainerWorkerTestSupport.Request(profile), _ => Task.CompletedTask);
                Assert.True(result.Success);
                Assert.True(File.Exists(marker));
            }
            finally { try { File.Delete(marker); } catch { } }
        }

        [Theory]
        [InlineData("network")]
        [InlineData("readonly")]
        [InlineData("memory")]
        [InlineData("memory-swap")]
        [InlineData("cpu")]
        [InlineData("pids")]
        [InlineData("tmpfs")]
        [InlineData("user")]
        [InlineData("privileged")]
        [InlineData("capdrop")]
        [InlineData("security")]
        [InlineData("image")]
        [InlineData("bind")]
        [InlineData("autoremove")]
        public async Task Applied_Isolation_Mismatch_Is_Refused_Before_Tenant_Request(string tamper)
        {
            var marker = Path.Combine(Path.GetTempPath(), "multiplexed-container-request-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                var profile = ContainerWorkerTestSupport.Profile(new Dictionary<string, string>
                {
                    ["CONTAINER_ENGINE_PROBE_ATTESTATION_TAMPER"] = tamper,
                    ["CONTAINER_ENGINE_PROBE_REQUEST_MARKER"] = marker
                });
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    ContainerWorkerTestSupport.Transport(profile).InvokeAsync(
                        ContainerWorkerTestSupport.Request(profile), _ => Task.CompletedTask));
                Assert.False(File.Exists(marker));
            }
            finally { try { File.Delete(marker); } catch { } }
        }

        [Fact]
        public void Attestation_Rejects_Unexpected_Persistent_Mounts()
        {
            var profile = ContainerWorkerTestSupport.Profile();
            var json = $$"""
            [{
              "Config":{"Image":"{{profile.ImageReference}}","User":"{{profile.ContainerUser}}"},
              "HostConfig":{
                "ReadonlyRootfs":true,"Privileged":false,"AutoRemove":true,"NetworkMode":"none",
                "Memory":268435456,"MemorySwap":268435456,"NanoCpus":750000000,"PidsLimit":32,
                "Binds":null,"CapAdd":[],"CapDrop":["ALL"],"SecurityOpt":["no-new-privileges"],
                "Tmpfs":{"/tmp":"rw,noexec,nosuid,nodev,size=33554432"}
              },
              "Mounts":[{"Type":"volume","Destination":"/data"}]
            }]
            """;
            Assert.Throws<InvalidOperationException>(() => AiContainerWorkerIsolationAttestation.Validate(json, profile));
        }
    }
}
