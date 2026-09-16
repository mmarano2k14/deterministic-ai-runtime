using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.TypeScript
{
    /// <summary>Actual published TypeScript functions through the unchanged server process transport.</summary>
    [Trait("Category", "TypeScriptProcess")]
    public sealed class AiTypeScriptWorkerExecutionTests
    {
        [TypeScriptWorkerFact]
        public async Task Published_Synchronous_Function_Returns_Real_Computed_Data()
        {
            var result = await TypeScriptWorkerTestSupport.ExecuteAsync();
            Assert.True(result.Success);
            Assert.Equal("{\"value\":42}", result.PayloadJson);
        }

        [TypeScriptWorkerFact]
        public async Task Published_Asynchronous_Function_Is_Awaited()
        {
            var source = "export async function run(inputs: { amount: number }, context: unknown) { await new Promise(resolve => setTimeout(resolve, 20)); return { success: true, payload: inputs.amount + 1 }; }\n";
            var result = await TypeScriptWorkerTestSupport.ExecuteAsync(source);
            Assert.True(result.Success);
            Assert.Equal("22", result.PayloadJson);
        }

        [TypeScriptWorkerTheory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Explicit_Business_Success_Or_Failure_Stays_Typed(bool success)
        {
            var source = "export function run(inputs: unknown, context: unknown) { return { success: " +
                (success ? "true" : "false") + ", payload: { reason: 'decision' } }; }\n";
            var result = await TypeScriptWorkerTestSupport.ExecuteAsync(source);
            Assert.Equal(success, result.Success);
            Assert.Equal("{\"reason\":\"decision\"}", result.PayloadJson);
        }

        [TypeScriptWorkerFact]
        public async Task Source_And_Vendored_Dependency_Are_Loaded_From_Their_Exact_Bytes()
        {
            var request = await TypeScriptWorkerTestSupport.RequestAsync(
                "import { scale } from '#rules'; export function run(inputs: { amount: number }, context: unknown) { return { success: true, payload: scale(inputs.amount) }; }\n");
            request = request with
            {
                Code = request.Code with
                {
                    Dependencies = new[]
                    {
                        new AiWorkerDependency("rules", "1.0.0", new[]
                        {
                            TypeScriptWorkerTestSupport.Source("index.ts", "export function scale(value: number): number { return value * 3; }\n")
                        })
                    }
                }
            };
            var result = await (await TypeScriptWorkerTestSupport.TransportAsync()).InvokeAsync(request, _ => Task.CompletedTask);
            Assert.Equal("63", result.PayloadJson);
        }

        [TypeScriptWorkerFact]
        public async Task Locked_Node_Dependency_Executes_Without_Registry_Resolution()
        {
            var source = "import { scale } from '#rules'; export function run(inputs: { amount: number }, context: unknown) { return { success: true, payload: scale(inputs.amount) }; }\n";
            var request = await TypeScriptWorkerTestSupport.RequestAsync(
                source,
                new[] { TypeScriptWorkerTestSupport.LockedDependency() });

            var result = await (await TypeScriptWorkerTestSupport.TransportAsync()).InvokeAsync(
                request, _ => Task.CompletedTask);

            Assert.True(result.Success);
            Assert.Equal("84", result.PayloadJson);
        }

        [TypeScriptWorkerFact]
        public async Task Console_And_Process_Stdout_Do_Not_Forge_Protocol_Frames()
        {
            var source = "export function run(inputs: unknown, context: unknown) { console.log('log'); process.stdout.write('output\\n'); return { success: true, payload: 42 }; }\n";
            var result = await TypeScriptWorkerTestSupport.ExecuteAsync(source);
            Assert.Equal("42", result.PayloadJson);
        }

        [TypeScriptWorkerFact]
        public async Task Heartbeat_Frames_Keep_The_Existing_Transport_Alive()
        {
            var request = await TypeScriptWorkerTestSupport.RequestAsync(
                "export async function run(inputs: unknown, context: unknown) { await new Promise(resolve => setTimeout(resolve, 700)); return { success: true, payload: 42 }; }\n");
            var heartbeats = 0;
            var result = await (await TypeScriptWorkerTestSupport.TransportAsync()).InvokeAsync(request,
                _ => { Interlocked.Increment(ref heartbeats); return Task.CompletedTask; });
            Assert.True(result.Success);
            Assert.True(heartbeats >= 3);
        }

        [TypeScriptWorkerFact]
        public async Task Worker_Context_Preserves_Logical_Identity_Without_Exporting_Lease_Or_Rbac()
        {
            var request = await TypeScriptWorkerTestSupport.RequestAsync(
                "export function run(inputs: unknown, context: any) { return { success: true, payload: context }; }\n");
            var result = await (await TypeScriptWorkerTestSupport.TransportAsync()).InvokeAsync(request, _ => Task.CompletedTask);
            using var json = JsonDocument.Parse(result.PayloadJson);
            var root = json.RootElement;
            Assert.Equal(request.OperationId, root.GetProperty("operationId").GetString());
            Assert.Equal(request.EffectIdempotencyKey, root.GetProperty("effectIdempotencyKey").GetString());
            Assert.Equal(request.Code.Target.PublicationRef, root.GetProperty("target").GetProperty("publicationRef").GetString());
            Assert.False(root.TryGetProperty("lease", out _));
            Assert.False(root.TryGetProperty("permissions", out _));
            Assert.False(root.TryGetProperty("code", out _));
        }

        [TypeScriptWorkerFact]
        public async Task Separate_Assignments_Do_Not_Reuse_Module_Globals()
        {
            const string source = "let counter = 0; export function run(inputs: unknown, context: unknown) { counter += 1; return { success: true, payload: counter }; }\n";
            Assert.Equal("1", (await TypeScriptWorkerTestSupport.ExecuteAsync(source)).PayloadJson);
            Assert.Equal("1", (await TypeScriptWorkerTestSupport.ExecuteAsync(source)).PayloadJson);
        }

        [TypeScriptWorkerTheory]
        [InlineData("export function run(inputs: unknown, context: unknown) { throw new Error('private data'); }")]
        [InlineData("export async function run(inputs: unknown, context: unknown) { throw new Error('private data'); }")]
        [InlineData("export function run(inputs: unknown, context: unknown) { return true; }")]
        [InlineData("export function run(inputs: unknown, context: unknown) { return { success: 1, payload: 42 }; }")]
        [InlineData("export function run(inputs: unknown, context: unknown) { return { success: true, payload: Number.NaN }; }")]
        [InlineData("export function run(inputs: unknown, context: unknown) { return { success: true, payload: null, park: true }; }")]
        [InlineData("export function other(inputs: unknown, context: unknown) { return { success: true, payload: 42 }; }")]
        [InlineData("export function run() { return { success: true, payload: 42 }; }")]
        [InlineData("import value from './missing.ts'; export function run(inputs: unknown, context: unknown) { return { success: true, payload: value }; }")]
        [InlineData("export function run(")]
        public async Task Source_And_Contract_Errors_Are_Not_Authoritative_Business_Results(string source)
        {
            var failure = await Record.ExceptionAsync(() => TypeScriptWorkerTestSupport.ExecuteAsync(source));
            Assert.NotNull(failure);
            Assert.IsNotType<AiWorkerProcessCleanupException>(failure);
        }

        [TypeScriptWorkerFact]
        public async Task Corrupt_Source_Is_Refused_Before_Readiness()
        {
            var request = await TypeScriptWorkerTestSupport.RequestAsync();
            var corrupt = request.Code.Sources[0] with { Sha256 = new string('0', 64) };
            request = request with { Code = request.Code with { Sources = new[] { corrupt } } };
            var callbacks = 0;
            var failure = await Record.ExceptionAsync(async () => await (await TypeScriptWorkerTestSupport.TransportAsync()).InvokeAsync(
                request, _ => { callbacks++; return Task.CompletedTask; }));
            Assert.NotNull(failure);
            Assert.Equal(0, callbacks);
        }

        [TypeScriptWorkerFact]
        public async Task Cancellation_Stops_A_Real_TypeScript_Function()
        {
            var request = await TypeScriptWorkerTestSupport.RequestAsync(
                "export async function run(inputs: unknown, context: unknown) { await new Promise(resolve => setTimeout(resolve, 60000)); return { success: true, payload: 1 }; }\n");
            var transport = await TypeScriptWorkerTestSupport.TransportAsync();
            using var cancellation = new CancellationTokenSource();
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = transport.InvokeAsync(request, _ => { ready.TrySetResult(true); return Task.CompletedTask; }, cancellation.Token);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        [TypeScriptWorkerFact]
        public async Task Expired_Function_Is_Not_Accepted_As_A_Late_Result()
        {
            var request = await TypeScriptWorkerTestSupport.RequestAsync(
                "export function run(inputs: unknown, context: unknown) { while (true) { } }\n");
            request = request with { DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(2) };
            var operation = (await TypeScriptWorkerTestSupport.TransportAsync()).InvokeAsync(request, _ => Task.CompletedTask);
            var failure = await Record.ExceptionAsync(() => operation.WaitAsync(TimeSpan.FromSeconds(12)));
            Assert.NotNull(failure);
            Assert.True(operation.IsCompleted);
            Assert.IsNotType<AiWorkerProcessCleanupException>(failure);
        }

        [TypeScriptWorkerFact]
        public async Task Null_And_Unicode_Json_Values_Round_Trip_Without_Server_Objects()
        {
            var request = await TypeScriptWorkerTestSupport.RequestAsync(
                "export function run(inputs: unknown, context: unknown) { return { success: true, payload: inputs }; }\n");
            request = request with { Inputs = JsonSerializer.SerializeToElement(new { name = "Liège ไทย", optional = (string?)null }) };
            var result = await (await TypeScriptWorkerTestSupport.TransportAsync()).InvokeAsync(request, _ => Task.CompletedTask);
            using var json = JsonDocument.Parse(result.PayloadJson);
            Assert.Equal("Liège ไทย", json.RootElement.GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("optional").ValueKind);
        }
    }
}
