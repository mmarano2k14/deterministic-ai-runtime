using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Multiplexed.AI.McpServer.PublicSdk;
using Multiplexed.AI.McpServer.Tools;
using Multiplexed.AI.Sdk.Contracts.Control;
using Multiplexed.AI.Sdk.Contracts.Executions;
using Multiplexed.AI.Sdk.Contracts.Observation;
using Multiplexed.AI.Sdk.Contracts.Publication;

namespace Multiplexed.AI.Tests.Runtime.PublicSdk
{
    public sealed class PublicSdkMcpToolsTests
    {
        [Fact]
        public async Task Tools_Delegate_Using_Only_Public_Contract_Types()
        {
            var boundary = new Boundary();
            var services = new ServiceCollection().AddSingleton<IAiPublicSdkBoundary>(boundary).BuildServiceProvider();
            var tools = new PublicSdkMcpTools(services.GetRequiredService<IServiceScopeFactory>());
            await tools.PublishPipelineAsync(new AiSdkPipelinePublicationRequest());
            await tools.SubmitExecutionAsync(new AiSdkExecutionSubmissionRequest { PublicationRef = "publication:abc" });
            await tools.ObserveExecutionAsync("execution-a");
            await tools.GetExecutionResultAsync("execution-a");
            await tools.CancelExecutionAsync("execution-a", new AiSdkExecutionCancellationRequest());
            Assert.Equal(5, boundary.Calls);

            var methods = typeof(PublicSdkMcpTools).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null).ToArray();
            Assert.Equal(5, methods.Length);
            foreach (var method in methods)
            {
                Assert.All(method.GetParameters().Where(parameter => parameter.ParameterType != typeof(CancellationToken)), parameter =>
                    Assert.True(parameter.ParameterType == typeof(string) || parameter.ParameterType.Namespace?.StartsWith("Multiplexed.AI.Sdk.Contracts", StringComparison.Ordinal) == true));
            }
        }

        private sealed class Boundary : IAiPublicSdkBoundary
        {
            internal int Calls { get; private set; }
            public Task<AiSdkPipelinePublicationResponse> PublishAsync(AiSdkPipelinePublicationRequest request, CancellationToken cancellationToken = default) { Calls++; return Task.FromResult(new AiSdkPipelinePublicationResponse()); }
            public Task<AiSdkExecutionSubmissionResponse> SubmitAsync(AiSdkExecutionSubmissionRequest request, CancellationToken cancellationToken = default) { Calls++; return Task.FromResult(new AiSdkExecutionSubmissionResponse()); }
            public Task<AiSdkExecutionObservation> ObserveAsync(string executionId, CancellationToken cancellationToken = default) { Calls++; return Task.FromResult(new AiSdkExecutionObservation()); }
            public Task<AiSdkExecutionResult> GetResultAsync(string executionId, CancellationToken cancellationToken = default) { Calls++; return Task.FromResult(new AiSdkExecutionResult()); }
            public Task<AiSdkExecutionCancellationResponse> CancelAsync(string executionId, AiSdkExecutionCancellationRequest request, CancellationToken cancellationToken = default) { Calls++; return Task.FromResult(new AiSdkExecutionCancellationResponse()); }
        }
    }
}
