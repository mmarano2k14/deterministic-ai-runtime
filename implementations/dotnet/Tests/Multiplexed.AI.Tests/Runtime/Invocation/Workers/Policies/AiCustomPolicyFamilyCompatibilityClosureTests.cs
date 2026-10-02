using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers.Policies;
using Multiplexed.AI.Tests.Runtime.Invocation.Workers.DotNet;
using Multiplexed.AI.Tests.Runtime.Invocation.Workers.Python;
using Multiplexed.AI.Tests.Runtime.Invocation.Workers.TypeScript;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.Policies
{
    /// <summary>Closes hosted custom policy-family compatibility across the supported language processes.</summary>
    public sealed class AiCustomPolicyFamilyCompatibilityClosureTests
    {
        [Fact]
        public void Capability_Matrix_Closes_Only_The_Implemented_Runtime_Checkpoints()
        {
            Assert.Equal(AiCustomPolicyFamilyAvailability.Hosted, AiCustomPolicyFamilyCapabilities.Get(AiPolicyKind.Concurrency).Availability);
            Assert.Equal(AiCustomPolicyFamilyAvailability.Hosted, AiCustomPolicyFamilyCapabilities.Get(AiPolicyKind.Retry).Availability);
            Assert.Equal(AiCustomPolicyFamilyAvailability.Hosted, AiCustomPolicyFamilyCapabilities.Get(AiPolicyKind.Delegation).Availability);
            Assert.Equal(AiCustomPolicyFamilyAvailability.NativeOnly, AiCustomPolicyFamilyCapabilities.Get(AiPolicyKind.Retention).Availability);

            foreach (var kind in new[]
            {
                AiPolicyKind.Timeout,
                AiPolicyKind.CircuitBreaker,
                AiPolicyKind.RateLimit,
                AiPolicyKind.Validation,
                AiPolicyKind.Routing
            })
            {
                var capability = AiCustomPolicyFamilyCapabilities.Get(kind);
                Assert.Equal(AiCustomPolicyFamilyAvailability.NoRuntimeCheckpoint, capability.Availability);
                Assert.False(capability.SupportsCustomPublication);
                Assert.False(capability.SupportsHostedExecution);
                Assert.Null(capability.ContractId);
            }
        }

        [Fact]
        public void Hosted_Families_Keep_Distinct_Closed_Contracts()
        {
            Assert.Equal(AiCustomPolicyFamilyContracts.ConcurrencyV1, AiCustomPolicyFamilyCapabilities.Get(AiPolicyKind.Concurrency).ContractId);
            Assert.Equal(AiCustomPolicyFamilyContracts.RetryV1, AiCustomPolicyFamilyCapabilities.Get(AiPolicyKind.Retry).ContractId);
            Assert.Equal(AiCustomPolicyFamilyContracts.DelegationV1, AiCustomPolicyFamilyCapabilities.Get(AiPolicyKind.Delegation).ContractId);
            Assert.Equal(3, new[]
            {
                AiCustomPolicyFamilyContracts.ConcurrencyV1,
                AiCustomPolicyFamilyContracts.RetryV1,
                AiCustomPolicyFamilyContracts.DelegationV1
            }.Distinct(StringComparer.Ordinal).Count());
        }

        [PythonWorkerFact]
        public async Task Python_Process_Executes_All_Hosted_Custom_Policy_Families()
        {
            const string source = "def run(inputs, context):\n    kind = inputs['policyKind']\n    if kind == 'concurrency':\n        payload = {'schemaVersion': 1, 'requestId': inputs['requestId'], 'policyKind': 'concurrency', 'decision': 'allow', 'reason': None}\n    elif kind == 'retry':\n        payload = {'schemaVersion': 1, 'requestId': inputs['requestId'], 'policyKind': 'retry', 'decision': 'retry', 'reason': 'transient', 'suggestedDelayMs': 250}\n    elif kind == 'delegation':\n        payload = {'schemaVersion': 1, 'requestId': inputs['requestId'], 'policyKind': 'delegation', 'decision': 'approve', 'reason': None}\n    else:\n        raise ValueError('unsupported policy family')\n    return {'success': True, 'payload': payload}\n";
            var worker = await PythonWorkerTestSupport.RequestAsync(source);
            await ExecuteAllFamiliesAsync("python", worker.Code, await PythonWorkerTestSupport.TransportAsync());
        }

        [TypeScriptWorkerFact]
        public async Task TypeScript_Process_Executes_All_Hosted_Custom_Policy_Families()
        {
            const string source = "export function run(inputs: { requestId: string; policyKind: string }, context: unknown) { let payload: unknown; if (inputs.policyKind === 'concurrency') payload = { schemaVersion: 1, requestId: inputs.requestId, policyKind: 'concurrency', decision: 'allow', reason: null }; else if (inputs.policyKind === 'retry') payload = { schemaVersion: 1, requestId: inputs.requestId, policyKind: 'retry', decision: 'retry', reason: 'transient', suggestedDelayMs: 250 }; else if (inputs.policyKind === 'delegation') payload = { schemaVersion: 1, requestId: inputs.requestId, policyKind: 'delegation', decision: 'approve', reason: null }; else throw new Error('unsupported policy family'); return { success: true, payload }; }\n";
            var worker = await TypeScriptWorkerTestSupport.RequestAsync(source);
            await ExecuteAllFamiliesAsync("typescript", worker.Code, await TypeScriptWorkerTestSupport.TransportAsync());
        }

        [Fact]
        public async Task DotNet_Process_Executes_All_Hosted_Custom_Policy_Families()
        {
            var worker = await DotNetWorkerTestSupport.RequestAsync("PolicyFamily", includeDependency: true);
            await ExecuteAllFamiliesAsync("dotnet", worker.Code, await DotNetWorkerTestSupport.TransportAsync());
        }

        private static async Task ExecuteAllFamiliesAsync(
            string language,
            AiWorkerCodeBundle code,
            IAiWorkerInvocationTransport workerTransport)
        {
            var preparer = new Preparer(code);
            var implementationRef = code.Target.ImplementationRef;

            var concurrency = new AiHostedConcurrencyPolicyTransport(language, preparer, workerTransport);
            var concurrencyResponse = await concurrency.EvaluateAsync(ConcurrencyRequest(language, implementationRef));
            Assert.Equal("concurrency", concurrencyResponse.GetProperty("policyKind").GetString());
            Assert.Equal("allow", concurrencyResponse.GetProperty("decision").GetString());

            var retry = new AiHostedRetryPolicyTransport(language, preparer, workerTransport);
            var retryRequest = RetryRequest(language, implementationRef);
            var retryResponse = await retry.EvaluateAsync(retryRequest);
            var retryResult = AiCustomPolicyFamilyContracts.ReadRetryV1(retryResponse, retryRequest.RequestId);
            Assert.Equal(AiRetryPolicyTransportDecision.Retry, retryResult.Decision);
            Assert.Equal(TimeSpan.FromMilliseconds(250), retryResult.SuggestedDelay);

            var delegation = new AiHostedDelegationPolicyTransport(language, preparer, workerTransport);
            var delegationRequest = DelegationRequest(language, implementationRef);
            var delegationResponse = await delegation.EvaluateAsync(delegationRequest);
            var delegationResult = AiCustomPolicyFamilyContracts.ReadDelegationV1(delegationResponse, delegationRequest.RequestId);
            Assert.Equal(AiDelegationPolicyTransportDecision.Approve, delegationResult.Decision);

            Assert.Equal(3, preparer.Calls);
        }

        private static AiConcurrencyPolicyRequest ConcurrencyRequest(string language, string implementationRef) => new(
            "closure-concurrency",
            "capacity-guard",
            "Pipeline",
            null,
            language,
            implementationRef,
            DateTimeOffset.UtcNow.AddSeconds(20),
            new AiConcurrencyPolicyInput(
                "tenant-a", "group-a", "execution-a", "closure-pipeline", "native", "native",
                "runtime-a", null, null, null),
            JsonSerializer.SerializeToElement(new { limit = 2 }));

        private static AiRetryPolicyRequest RetryRequest(string language, string implementationRef) => new(
            "closure-retry",
            "retry-guard",
            "Pipeline",
            null,
            language,
            implementationRef,
            DateTimeOffset.UtcNow.AddSeconds(20),
            new AiRetryPolicyInput(
                "tenant-a", "group-a", "execution-a", "closure-pipeline", "step-a", "native",
                1, 3, "transient", "TimeoutException", DateTimeOffset.UtcNow),
            JsonSerializer.SerializeToElement(new { mode = "transient" }));

        private static AiDelegationPolicyRequest DelegationRequest(string language, string implementationRef) => new(
            "closure-delegation",
            "delegation-guard",
            "Step",
            "invoke-child",
            language,
            implementationRef,
            DateTimeOffset.UtcNow.AddSeconds(20),
            new AiDelegationPolicyInput(
                "tenant-a", "group-a", "execution-a", "invoke-child", "child-a", "1", "child-key", 0),
            JsonSerializer.SerializeToElement(new { region = "eu" }));

        private sealed class Preparer :
            IAiConcurrencyPolicyCodePreparer,
            IAiRetryPolicyCodePreparer,
            IAiDelegationPolicyCodePreparer
        {
            private readonly AiWorkerCodeBundle _code;
            internal int Calls;

            internal Preparer(AiWorkerCodeBundle code) => _code = code;

            public Task<AiWorkerCodeBundle> PrepareAsync(
                AiConcurrencyPolicyRequest request,
                CancellationToken cancellationToken = default) => PrepareAsync(cancellationToken);

            public Task<AiWorkerCodeBundle> PrepareAsync(
                AiRetryPolicyRequest request,
                CancellationToken cancellationToken = default) => PrepareAsync(cancellationToken);

            public Task<AiWorkerCodeBundle> PrepareAsync(
                AiDelegationPolicyRequest request,
                CancellationToken cancellationToken = default) => PrepareAsync(cancellationToken);

            private Task<AiWorkerCodeBundle> PrepareAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref Calls);
                return Task.FromResult(_code);
            }
        }
    }
}
