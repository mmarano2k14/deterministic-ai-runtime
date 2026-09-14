using System.Text.Json;
using Multiplexed.AI.Runtime.Invocation.Workers;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.ExecutionRequirements
{
    /// <summary>Uses the existing standalone protocol probe, not a simulated Process.Start or a new language loader.</summary>
    public sealed class AiWorkerExecutionRequirementsProcessTests
    {
        [Fact]
        public async Task Explicit_Trusted_Profile_Completes_A_Real_Protocol_Exchange_With_Validated_Launch_Paths()
        {
            var installed = WorkerTestSupport.ProbeProfile();
            var runtime = installed.Runtime with { RuntimeSha256 = installed.ExecutableSha256 };
            var descriptor = ExecutionRequirementsTestSupport.Descriptor(runtime);
            var roots = new[] { Path.GetDirectoryName(installed.ExecutablePath)!, installed.WorkingDirectory }
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var profile = new AiWorkerProcessProfile(runtime, installed.ExecutablePath, installed.ExecutableSha256,
                installed.Arguments, installed.WorkingDirectory, installed.Environment, installed.VerifiedHostFiles,
                descriptor, roots);
            var request = WorkerTestSupport.Request() with
                { Code = WorkerTestSupport.Bundle() with { Runtime = runtime, ExecutionDescriptor = descriptor } };
            var transport = new AiWorkerProcessTransport(new AiConfiguredWorkerProcessCatalog(new[] { profile }), new());
            var heartbeats = 0;
            var result = await transport.InvokeAsync(request, _ => { heartbeats++; return Task.CompletedTask; });
            Assert.True(result.Success);
            Assert.True(heartbeats > 0);
            using var payload = JsonDocument.Parse(result.PayloadJson);
            Assert.Equal(42, payload.RootElement.GetProperty("value").GetInt32());
        }
    }
}
