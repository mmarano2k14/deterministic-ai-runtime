using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers.Policies;
using Multiplexed.AI.Tests.Runtime.Invocation.Workers.DotNet;
using Multiplexed.AI.Tests.Runtime.Invocation.Workers.Python;
using Multiplexed.AI.Tests.Runtime.Invocation.Workers.TypeScript;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.Policies
{
    /// <summary>Real host processes execute the same published policy envelope as custom steps use for functions.</summary>
    public sealed class AiHostedConcurrencyPolicyRealProcessTests
    {
        [PythonWorkerFact]
        public async Task Python_Process_Executes_A_Custom_Concurrency_Policy()
        {
            const string source = "def run(inputs, context):\n    return {\"success\": True, \"payload\": {\"schemaVersion\": 1, \"requestId\": inputs[\"requestId\"], \"policyKind\": \"concurrency\", \"decision\": \"allow\", \"reason\": None}}\n";
            var worker = await PythonWorkerTestSupport.RequestAsync(source);
            var policy = new AiHostedConcurrencyPolicyTransport("python", new Preparer(worker.Code), await PythonWorkerTestSupport.TransportAsync());

            var response = await policy.EvaluateAsync(Request("python", worker.Code.Target.ImplementationRef));

            Assert.Equal("allow", response.GetProperty("decision").GetString());
            Assert.Equal("policy-request-real", response.GetProperty("requestId").GetString());
        }

        [TypeScriptWorkerFact]
        public async Task TypeScript_Process_Executes_A_Custom_Concurrency_Policy()
        {
            const string source = "export function run(inputs: { requestId: string }, context: unknown) { return { success: true, payload: { schemaVersion: 1, requestId: inputs.requestId, policyKind: 'concurrency', decision: 'allow', reason: null } }; }\n";
            var worker = await TypeScriptWorkerTestSupport.RequestAsync(source);
            var policy = new AiHostedConcurrencyPolicyTransport("typescript", new Preparer(worker.Code), await TypeScriptWorkerTestSupport.TransportAsync());

            var response = await policy.EvaluateAsync(Request("typescript", worker.Code.Target.ImplementationRef));

            Assert.Equal("allow", response.GetProperty("decision").GetString());
            Assert.Equal("policy-request-real", response.GetProperty("requestId").GetString());
        }

        [Fact]
        public async Task DotNet_Process_Executes_A_Custom_Concurrency_Policy()
        {
            var worker = await DotNetWorkerTestSupport.RequestAsync("PolicyAllow", includeDependency: true);
            var policy = new AiHostedConcurrencyPolicyTransport("dotnet", new Preparer(worker.Code), await DotNetWorkerTestSupport.TransportAsync());

            var response = await policy.EvaluateAsync(Request("dotnet", worker.Code.Target.ImplementationRef));

            Assert.Equal("allow", response.GetProperty("decision").GetString());
            Assert.Equal("policy-request-real", response.GetProperty("requestId").GetString());
        }

        private sealed class Preparer : IAiConcurrencyPolicyCodePreparer
        {
            private readonly AiWorkerCodeBundle _code;
            internal Preparer(AiWorkerCodeBundle code) => _code = code;
            public Task<AiWorkerCodeBundle> PrepareAsync(AiConcurrencyPolicyRequest request, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(_code);
            }
        }

        private static AiConcurrencyPolicyRequest Request(string language, string implementationRef) => new(
            "policy-request-real",
            "capacity-guard",
            "Pipeline",
            null,
            language,
            implementationRef,
            DateTimeOffset.UtcNow.AddSeconds(15),
            new AiConcurrencyPolicyInput(
                "tenant-a", "group-a", "execution-a", "policy-pipeline", "native", "native",
                "runtime-a", null, null, null),
            JsonSerializer.SerializeToElement(new { limit = 2 }));
    }
}
