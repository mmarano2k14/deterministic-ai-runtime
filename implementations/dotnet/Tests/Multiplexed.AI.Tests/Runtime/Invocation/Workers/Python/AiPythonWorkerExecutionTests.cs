using System.Diagnostics;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.Python
{
    /// <summary>Actual published Python functions through the unchanged server process transport.</summary>
    [Trait("Category", "PythonProcess")]
    public sealed class AiPythonWorkerExecutionTests
    {
        [PythonWorkerFact]
        public async Task Published_Synchronous_Function_Returns_Real_Computed_Data()
        {
            var result = await PythonWorkerTestSupport.ExecuteAsync();
            Assert.True(result.Success); Assert.Equal("{\"value\":42}", result.PayloadJson);
        }

        [PythonWorkerFact]
        public async Task Published_Asynchronous_Function_Is_Awaited()
        {
            var source = "import asyncio\nasync def run(inputs, context):\n    await asyncio.sleep(0.02)\n    return {\"success\": True, \"payload\": inputs[\"amount\"] + 1}\n";
            var result = await PythonWorkerTestSupport.ExecuteAsync(source);
            Assert.True(result.Success); Assert.Equal("22", result.PayloadJson);
        }

        [PythonWorkerTheory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Explicit_Business_Success_Or_Failure_Stays_Typed(bool success)
        {
            var source = "def run(inputs, context):\n    return {\"success\": " + (success ? "True" : "False") + ", \"payload\": {\"reason\": \"decision\"}}";
            var result = await PythonWorkerTestSupport.ExecuteAsync(source);
            Assert.Equal(success, result.Success); Assert.Equal("{\"reason\":\"decision\"}", result.PayloadJson);
        }

        [PythonWorkerFact]
        public async Task Source_And_Vendored_Dependency_Are_Loaded_From_Their_Exact_Bytes()
        {
            var request = await PythonWorkerTestSupport.RequestAsync(
                "from vendor_rule import scale\ndef run(inputs, context):\n    return {\"success\": True, \"payload\": scale(inputs[\"amount\"])}");
            request = request with { Code = request.Code with { Dependencies = new[] {
                new AiWorkerDependency("rules", "1.0.0", new[] { PythonWorkerTestSupport.Source("vendor_rule.py", "def scale(n):\n    return n * 3") }) } } };
            var result = await (await PythonWorkerTestSupport.TransportAsync()).InvokeAsync(request, _ => Task.CompletedTask);
            Assert.Equal("63", result.PayloadJson);
        }

        [PythonWorkerFact]
        public async Task Ordinary_And_FileDescriptor_Stdout_Do_Not_Forge_Protocol_Frames()
        {
            var result = await PythonWorkerTestSupport.ExecuteAsync("import os, sys\nprint('import log')\ndef run(inputs, context):\n    print('function log')\n    sys.__stdout__.write('original stdout\\n')\n    os.write(1, b'raw stdout\\n')\n    return {\"success\": True, \"payload\": 42}");
            Assert.Equal("42", result.PayloadJson);
        }

        [PythonWorkerFact]
        public async Task Heartbeat_Frames_Keep_The_Existing_Transport_Alive()
        {
            var request = await PythonWorkerTestSupport.RequestAsync("import time\ndef run(inputs, context):\n    time.sleep(0.7)\n    return {\"success\": True, \"payload\": 42}");
            var heartbeats = 0;
            var result = await (await PythonWorkerTestSupport.TransportAsync()).InvokeAsync(request,
                _ => { Interlocked.Increment(ref heartbeats); return Task.CompletedTask; });
            Assert.True(result.Success); Assert.True(heartbeats >= 3);
        }

        [PythonWorkerFact]
        public async Task Worker_Context_Preserves_Logical_Identity_Without_Exporting_Lease_Or_Rbac()
        {
            var request = await PythonWorkerTestSupport.RequestAsync("def run(inputs, context):\n    result = dict(context)\n    result[\"target\"] = dict(result[\"target\"])\n    return {\"success\": True, \"payload\": result}");
            var result = await (await PythonWorkerTestSupport.TransportAsync()).InvokeAsync(request, _ => Task.CompletedTask);
            using var json = JsonDocument.Parse(result.PayloadJson); var root = json.RootElement;
            Assert.Equal(request.OperationId, root.GetProperty("operationId").GetString());
            Assert.Equal(request.EffectIdempotencyKey, root.GetProperty("effectIdempotencyKey").GetString());
            Assert.Equal(request.Code.Target.PublicationRef, root.GetProperty("target").GetProperty("publicationRef").GetString());
            Assert.False(root.TryGetProperty("lease", out _)); Assert.False(root.TryGetProperty("permissions", out _));
            Assert.False(root.TryGetProperty("code", out _));
        }

        [PythonWorkerFact]
        public async Task Separate_Assignments_Do_Not_Reuse_Module_Globals()
        {
            const string source = "counter = 0\ndef run(inputs, context):\n    global counter\n    counter += 1\n    return {\"success\": True, \"payload\": counter}";
            Assert.Equal("1", (await PythonWorkerTestSupport.ExecuteAsync(source)).PayloadJson);
            Assert.Equal("1", (await PythonWorkerTestSupport.ExecuteAsync(source)).PayloadJson);
        }

        [PythonWorkerTheory]
        [InlineData("def run(inputs, context):\n    raise ValueError('private data')")]
        [InlineData("async def run(inputs, context):\n    raise ValueError('private data')")]
        [InlineData("def run(inputs, context):\n    return True")]
        [InlineData("def run(inputs, context):\n    return {\"success\": 1, \"payload\": 42}")]
        [InlineData("def run(inputs, context):\n    return {\"success\": True, \"payload\": float('nan')}")]
        [InlineData("def run(inputs, context):\n    return {\"success\": True, \"payload\": None, \"park\": True}")]
        [InlineData("def other(inputs, context):\n    return {\"success\": True, \"payload\": 42}")]
        [InlineData("def run():\n    return {\"success\": True, \"payload\": 42}")]
        [InlineData("import dependency_not_published_xyz\ndef run(inputs, context):\n    return {\"success\": True, \"payload\": 42}")]
        [InlineData("def broken(")]
        public async Task Source_And_Contract_Errors_Are_Not_Authoritative_Business_Results(string source)
        {
            var failure = await Record.ExceptionAsync(() => PythonWorkerTestSupport.ExecuteAsync(source));
            Assert.NotNull(failure); Assert.IsNotType<AiWorkerProcessCleanupException>(failure);
        }

        [PythonWorkerFact]
        public async Task Corrupt_Source_Is_Refused_Before_Readiness()
        {
            var request = await PythonWorkerTestSupport.RequestAsync();
            var corrupt = request.Code.Sources[0] with { Sha256 = new string('0', 64) };
            request = request with { Code = request.Code with { Sources = new[] { corrupt } } };
            var callbacks = 0;
            var failure = await Record.ExceptionAsync(async () => await (await PythonWorkerTestSupport.TransportAsync()).InvokeAsync(
                request, _ => { callbacks++; return Task.CompletedTask; }));
            Assert.NotNull(failure); Assert.Equal(0, callbacks);
        }

        [PythonWorkerFact]
        public async Task Cancellation_Stops_A_Real_Python_Function()
        {
            var request = await PythonWorkerTestSupport.RequestAsync("import time\ndef run(inputs, context):\n    time.sleep(60)\n    return {\"success\": True, \"payload\": 1}");
            var transport = await PythonWorkerTestSupport.TransportAsync();
            using var cancellation = new CancellationTokenSource();
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = transport.InvokeAsync(request, _ => { ready.TrySetResult(true); return Task.CompletedTask; }, cancellation.Token);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15)); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        [PythonWorkerFact]
        public async Task Expired_Function_Is_Not_Accepted_As_A_Late_Result()
        {
            var request = await PythonWorkerTestSupport.RequestAsync("def run(inputs, context):\n    while True:\n        pass");
            request = request with { DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(2) };
            var operation = (await PythonWorkerTestSupport.TransportAsync()).InvokeAsync(request, _ => Task.CompletedTask);
            var failure = await Record.ExceptionAsync(() => operation.WaitAsync(TimeSpan.FromSeconds(12)));
            Assert.NotNull(failure); Assert.True(operation.IsCompleted);
            Assert.IsNotType<AiWorkerProcessCleanupException>(failure);
        }

        [PythonWorkerFact]
        public async Task Null_And_Unicode_Json_Values_Round_Trip_Without_Server_Objects()
        {
            var request = await PythonWorkerTestSupport.RequestAsync("def run(inputs, context):\n    return {\"success\": True, \"payload\": inputs}");
            request = request with { Inputs = JsonSerializer.SerializeToElement(new { name = "Liège ไทย", optional = (string?)null }) };
            var result = await (await PythonWorkerTestSupport.TransportAsync()).InvokeAsync(request, _ => Task.CompletedTask);
            using var json = JsonDocument.Parse(result.PayloadJson);
            Assert.Equal("Liège ไทย", json.RootElement.GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("optional").ValueKind);
        }
    }
}
