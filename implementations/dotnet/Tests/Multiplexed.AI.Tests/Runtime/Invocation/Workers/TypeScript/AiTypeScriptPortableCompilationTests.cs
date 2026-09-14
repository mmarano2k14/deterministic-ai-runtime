using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers.TypeScript;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.TypeScript
{
    /// <summary>Real compiler/child-process coverage without Node native TypeScript support.</summary>
    [Trait("Category", "TypeScriptProcess")]
    public sealed class AiTypeScriptPortableCompilationTests
    {
        [TypeScriptWorkerTheory]
        [InlineData("enum Rule { Twice = 2 }; const factor = Rule.Twice;")]
        [InlineData("enum Rule { Twice = 'twice' }; const factor = Rule.Twice === 'twice' ? 2 : 0;")]
        [InlineData("namespace Rules { export const factor = 2; }; const factor = Rules.factor;")]
        [InlineData("class Rule { constructor(public factor: number) {} }; const factor = new Rule(2).factor;")]
        [InlineData("const enum Rule { Twice = 2 }; const factor = Rule.Twice;")]
        public async Task TypeScript_Transformations_Do_Not_Depend_On_Node_Flags(string declaration)
        {
            var result = await TypeScriptWorkerTestSupport.ExecuteAsync(declaration +
                " export function run(inputs: { amount: number }, context: unknown) { return { success: true, payload: inputs.amount * factor }; }");
            Assert.True(result.Success);
            Assert.Equal("42", result.PayloadJson);
        }

        [TypeScriptWorkerFact]
        public async Task Relative_Imports_Are_Resolved_From_The_Emitted_Closure()
        {
            var request = await TypeScriptWorkerTestSupport.RequestAsync(
                "import { scale } from './math.ts'; export function run(inputs: any, context: unknown) { return { success: true, payload: scale(inputs.amount) }; }");
            request = request with { Code = request.Code with { Sources = request.Code.Sources.Concat(new[]
            {
                TypeScriptWorkerTestSupport.Source("math.ts", "export function scale(value: number) { return value * 2; }")
            }).ToArray() } };
            var result = await (await TypeScriptWorkerTestSupport.TransportAsync()).InvokeAsync(request, _ => Task.CompletedTask);
            Assert.True(result.Success);
            Assert.Equal("42", result.PayloadJson);
        }

        [TypeScriptWorkerFact]
        public async Task Alias_Subpaths_Execute_The_Exact_Published_Dependency()
        {
            var request = await TypeScriptWorkerTestSupport.RequestAsync(
                "import { scale } from '#rules/math.ts'; export function run(inputs: any, context: unknown) { return { success: true, payload: scale(inputs.amount) }; }");
            request = request with { Code = request.Code with { Dependencies = new[]
            {
                new AiWorkerDependency("rules", "1.0.0", new[]
                {
                    TypeScriptWorkerTestSupport.Source("index.ts", "export { scale } from './math.ts';"),
                    TypeScriptWorkerTestSupport.Source("math.ts", "export function scale(value: number) { return value * 2; }")
                })
            } } };
            var result = await (await TypeScriptWorkerTestSupport.TransportAsync()).InvokeAsync(request, _ => Task.CompletedTask);
            Assert.True(result.Success);
            Assert.Equal("42", result.PayloadJson);
        }

        [TypeScriptWorkerFact]
        public async Task Child_Executes_JavaScript_Without_Inherited_TypeScript_Flags()
        {
            var result = await TypeScriptWorkerTestSupport.ExecuteAsync(
                "export function run(inputs: unknown, context: unknown) { return { success: true, payload: { url: import.meta.url, args: process.execArgv } }; }");
            using var payload = JsonDocument.Parse(result.PayloadJson);
            Assert.Contains("main.js?implementation=", payload.RootElement.GetProperty("url").GetString()!);
            Assert.Equal(0, payload.RootElement.GetProperty("args").GetArrayLength());
        }

        [TypeScriptWorkerFact]
        public async Task Installed_Compiler_Bytes_Match_The_Server_Profile_Pin()
        {
            var profile = await TypeScriptWorkerTestSupport.ProfileAsync();
            var compiler = Path.Combine(Path.GetDirectoryName(TypeScriptWorkerTestSupport.FindWorkerScript())!,
                "vendor", AiTypeScriptWorkerProcessProfile.CompilerFileName);
            Assert.Equal(AiTypeScriptWorkerProcessProfile.CompilerSha256, WorkerTestSupport.FileHash(compiler));
            Assert.Equal(AiTypeScriptWorkerProcessProfile.CompilerSha256, profile.VerifiedHostFiles[compiler]);
        }

        [TypeScriptWorkerFact]
        public async Task Corrupt_Host_Compiler_Fails_Before_Process_Readiness()
        {
            var installed = await TypeScriptWorkerTestSupport.ProfileAsync();
            var root = Path.Combine(Path.GetTempPath(), "multiplexed-ts-compiler-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "vendor"));
            try
            {
                var script = Path.Combine(root, "worker.mjs");
                File.Copy(TypeScriptWorkerTestSupport.FindWorkerScript(), script);
                File.WriteAllText(Path.Combine(root, "vendor", AiTypeScriptWorkerProcessProfile.CompilerFileName), "module.exports = {};");
                var profile = AiTypeScriptWorkerProcessProfile.Create(installed.Runtime, installed.ExecutablePath,
                    installed.ExecutableSha256, script, WorkerTestSupport.FileHash(script), root,
                    environment: installed.Environment);
                var transport = new AiWorkerProcessTransport(new AiConfiguredWorkerProcessCatalog(new[] { profile }), new());
                var request = await TypeScriptWorkerTestSupport.RequestAsync();
                var callbacks = 0;
                var error = await Record.ExceptionAsync(() => transport.InvokeAsync(request,
                    _ => { callbacks++; return Task.CompletedTask; }));
                Assert.NotNull(error);
                Assert.Equal(0, callbacks);
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }
}
