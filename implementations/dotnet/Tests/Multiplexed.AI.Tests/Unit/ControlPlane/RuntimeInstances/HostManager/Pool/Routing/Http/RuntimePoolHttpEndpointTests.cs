using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Http;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Http
{
    /// <summary>
    /// Validates the stable Runtime Pool HTTP endpoint contract.
    /// </summary>
    public sealed class RuntimePoolHttpEndpointTests
    {
        /// <summary>
        /// Verifies stable endpoint mapping and existing DTO serialization.
        /// </summary>
        [Fact]
        public async Task Endpoint_Should_Use_Stable_Path_And_Existing_Command_Dtos()
        {
            var builder =
                WebApplication.CreateBuilder();

            builder.WebHost.UseTestServer();

            builder.Services.AddSingleton<
                IAiRuntimePoolHttpCommandHandler,
                FakeCommandHandler>();

            await using var app =
                builder.Build();

            app.MapAiRuntimePoolHttpCommandEndpoint();

            await app.StartAsync();

            using var client =
                app.GetTestClient();

            var response =
                await client.PostAsJsonAsync(
                    AiRuntimePoolHttpCommandEndpointRouteBuilderExtensions
                        .DefaultCommandEndpointPath,
                    RuntimePoolHttpCommandHandlerTests
                        .CreateRequest(
                            "runtime-a2"));

            response.EnsureSuccessStatusCode();

            var result =
                await response.Content
                    .ReadFromJsonAsync<
                        AiRuntimeInstanceCommandResult>();

            Assert.NotNull(result);
            Assert.True(result.Success);

            Assert.Equal(
                "runtime-a2",
                result.RuntimeInstanceId);

            await app.StopAsync();
        }

        /// <summary>
        /// Provides one deterministic endpoint handler.
        /// </summary>
        private sealed class FakeCommandHandler :
            IAiRuntimePoolHttpCommandHandler
        {
            /// <inheritdoc />
            public Task<AiRuntimeInstanceCommandResult> HandleAsync(
                AiRuntimeInstanceCommandRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    new AiRuntimeInstanceCommandResult
                    {
                        Success = true,
                        Operation = request.Operation,
                        RuntimeInstanceId =
                            request.RuntimeInstanceId,
                        StartedAtUtc =
                            DateTimeOffset.UtcNow,
                        CompletedAtUtc =
                            DateTimeOffset.UtcNow,
                        DurationMs = 0
                    });
            }
        }
    }
}
