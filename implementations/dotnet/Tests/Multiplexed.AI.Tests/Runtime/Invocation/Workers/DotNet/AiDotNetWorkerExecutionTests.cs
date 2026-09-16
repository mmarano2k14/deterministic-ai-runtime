using System.Text;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.DotNet
{
    /// <summary>Actual published .NET assemblies through the unchanged process transport.</summary>
    [Trait("Category", "DotNetProcess")]
    public sealed class AiDotNetWorkerExecutionTests
    {
        [Fact]
        public async Task Published_Synchronous_Function_Returns_Real_Computed_Data()
        {
            var result = await DotNetWorkerTestSupport.ExecuteAsync();
            Assert.True(result.Success); Assert.Equal("{\"value\":42}", result.PayloadJson);
        }

        [Fact]
        public async Task Published_Asynchronous_Function_Is_Awaited()
        {
            var result = await DotNetWorkerTestSupport.ExecuteAsync("RunAsync");
            Assert.True(result.Success); Assert.Equal("22", result.PayloadJson);
        }

        [Fact]
        public async Task Explicit_Business_Failure_Remains_Authoritative()
        {
            var result = await DotNetWorkerTestSupport.ExecuteAsync("BusinessFail");
            Assert.False(result.Success); Assert.Equal("{\"reason\":\"decision\"}", result.PayloadJson);
        }

        [Fact]
        public async Task Published_Dependency_Assembly_Is_Loaded_From_Exact_Bytes()
        {
            var result = await DotNetWorkerTestSupport.ExecuteAsync("UseDependency");
            Assert.True(result.Success); Assert.Equal("63", result.PayloadJson);
        }

        [Fact]
        public async Task Missing_Published_Dependency_Is_A_Technical_Failure()
        {
            var failure = await Record.ExceptionAsync(() => DotNetWorkerTestSupport.ExecuteAsync("UseDependency", includeDependency: false));
            Assert.NotNull(failure); Assert.IsNotType<AiWorkerProcessCleanupException>(failure);
        }

        [Fact]
        public async Task Published_Stdout_Cannot_Forge_Parent_Protocol_Frames()
        {
            var result = await DotNetWorkerTestSupport.ExecuteAsync("Log");
            Assert.True(result.Success); Assert.Equal("42", result.PayloadJson);
        }

        [Fact]
        public async Task Portable_Context_Preserves_Logical_Identity_Without_Lease_Or_Rbac()
        {
            var request = await DotNetWorkerTestSupport.RequestAsync("Context");
            var result = await (await DotNetWorkerTestSupport.TransportAsync()).InvokeAsync(request, _ => Task.CompletedTask);
            using var json = JsonDocument.Parse(result.PayloadJson); var root = json.RootElement;
            Assert.Equal(request.OperationId, root.GetProperty("operationId").GetString());
            Assert.Equal(request.EffectIdempotencyKey, root.GetProperty("effectIdempotencyKey").GetString());
            Assert.Equal(request.Code.Target.PublicationRef, root.GetProperty("target").GetProperty("publicationRef").GetString());
            Assert.False(root.TryGetProperty("lease", out _)); Assert.False(root.TryGetProperty("permissions", out _));
            Assert.False(root.TryGetProperty("code", out _));
        }

        [Fact]
        public async Task Separate_Assignments_Do_Not_Reuse_Static_State()
        {
            Assert.Equal("1", (await DotNetWorkerTestSupport.ExecuteAsync("Counter")).PayloadJson);
            Assert.Equal("1", (await DotNetWorkerTestSupport.ExecuteAsync("Counter")).PayloadJson);
        }

        [Theory]
        [InlineData("Throw")]
        [InlineData("Invalid")]
        [InlineData("ExtraField")]
        [InlineData("RunWrongSignature")]
        [InlineData("MissingMethod")]
        public async Task Code_And_Contract_Errors_Are_Not_Business_Results(string method)
        {
            var failure = await Record.ExceptionAsync(() => DotNetWorkerTestSupport.ExecuteAsync(method));
            Assert.NotNull(failure); Assert.IsNotType<AiWorkerProcessCleanupException>(failure);
        }

        [Fact]
        public async Task Corrupt_Assembly_Is_Refused_Before_Readiness()
        {
            var request = await DotNetWorkerTestSupport.RequestAsync();
            request = request with { Code = request.Code with { Sources = new[] { request.Code.Sources[0] with { Sha256 = new string('0', 64) } } } };
            var callbacks = 0;
            var transport = await DotNetWorkerTestSupport.TransportAsync();
            var failure = await Record.ExceptionAsync(() => transport.InvokeAsync(request,
                _ => { callbacks++; return Task.CompletedTask; }));
            Assert.NotNull(failure); Assert.Equal(0, callbacks);
        }

        [Fact]
        public async Task Heartbeats_Keep_A_Long_Running_Function_Alive_Until_Cancellation()
        {
            var request = await DotNetWorkerTestSupport.RequestAsync("Slow");
            using var cancellation = new CancellationTokenSource(); var heartbeats = 0;
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = (await DotNetWorkerTestSupport.TransportAsync()).InvokeAsync(request, _ =>
            {
                Interlocked.Increment(ref heartbeats); ready.TrySetResult(true); return Task.CompletedTask;
            }, cancellation.Token);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15)); await Task.Delay(350); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(heartbeats >= 2);
        }

        [Fact]
        public async Task Null_And_Unicode_Json_Values_Round_Trip()
        {
            var request = await DotNetWorkerTestSupport.RequestAsync("NullAndUnicode");
            request = request with { Inputs = JsonSerializer.SerializeToElement(new { name = "Liège ไทย", optional = (string?)null }) };
            var result = await (await DotNetWorkerTestSupport.TransportAsync()).InvokeAsync(request, _ => Task.CompletedTask);
            using var json = JsonDocument.Parse(result.PayloadJson);
            Assert.Equal("Liège ไทย", json.RootElement.GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("optional").ValueKind);
        }
        [Fact]
        public async Task Packaged_Managed_Dependency_Closure_Is_Loaded_Without_NuGet_Restore()
        {
            var result = await DotNetWorkerTestSupport.ExecuteAsync(
                "UseDependency",
                includeDependency: true,
                packagedDependency: true);

            Assert.True(result.Success);
            Assert.Equal("63", result.PayloadJson);
        }

        [Fact]
        public async Task Wrong_Package_Kind_Is_Refused_Before_Readiness()
        {
            var request = await DotNetWorkerTestSupport.RequestAsync(
                "UseDependency",
                includeDependency: true,
                packagedDependency: true);
            var dependency = request.Code.Dependencies.Single();
            dependency = dependency with
            {
                Package = dependency.Package! with
                {
                    Kind = Multiplexed.Abstractions.AI.Publication.AiPublicationDependencyPackageKind.NodeLockedBundle
                }
            };
            request = request with
            {
                Code = request.Code with { Dependencies = new[] { dependency } }
            };

            var callbacks = 0;
            var transport = await DotNetWorkerTestSupport.TransportAsync();
            var failure = await Record.ExceptionAsync(() =>
                transport.InvokeAsync(
                    request,
                    _ =>
                    {
                        callbacks++;
                        return Task.CompletedTask;
                    }));

            Assert.NotNull(failure);
            Assert.Equal(0, callbacks);
        }

        [Fact]
        public async Task Packaged_Manifest_Digest_Mismatch_Is_Refused_Before_Readiness()
        {
            var request = await DotNetWorkerTestSupport.RequestAsync(
                "UseDependency",
                includeDependency: true,
                packagedDependency: true);
            var dependency = request.Code.Dependencies.Single();
            var assembly = dependency.Files.Single(file => file.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
            var manifest = dependency.Files.Single(file => file.Path == dependency.Package!.ManifestPath);
            var manifestJson = Encoding.UTF8.GetString(DecodeBase64Url(manifest.Base64Url));
            var changed = Encoding.UTF8.GetBytes(manifestJson.Replace(assembly.Sha256, new string('0', 64), StringComparison.Ordinal));
            var changedManifest = DotNetWorkerTestSupport.File(manifest.Path, changed);
            dependency = dependency with
            {
                Files = dependency.Files.Select(file => file.Path == manifest.Path ? changedManifest : file).ToArray()
            };
            request = request with { Code = request.Code with { Dependencies = new[] { dependency } } };

            var callbacks = 0;
            var transport = await DotNetWorkerTestSupport.TransportAsync();
            var failure = await Record.ExceptionAsync(() =>
                transport.InvokeAsync(
                    request,
                    _ =>
                    {
                        callbacks++;
                        return Task.CompletedTask;
                    }));

            Assert.NotNull(failure);
            Assert.Equal(0, callbacks);
        }

        private static byte[] DecodeBase64Url(string value) =>
            Convert.FromBase64String(
                value.Replace('-', '+').Replace('_', '/') +
                new string('=', (4 - value.Length % 4) % 4));

    }
}
