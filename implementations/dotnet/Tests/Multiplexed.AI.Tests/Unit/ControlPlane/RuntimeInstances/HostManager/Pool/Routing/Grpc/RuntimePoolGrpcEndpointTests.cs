using System.Text.Json;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Grpc;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Grpc
{
    /// <summary>
    /// Validates the stable Runtime Pool gRPC service contract.
    /// </summary>
    public sealed class RuntimePoolGrpcEndpointTests
    {
        private static readonly JsonSerializerOptions JsonOptions =
            new(JsonSerializerDefaults.Web);

        /// <summary>
        /// Verifies service mapping and reuse of the existing gRPC and command DTO envelopes.
        /// </summary>
        [Fact]
        public async Task Service_Should_Reuse_Existing_Grpc_And_Command_Contracts()
        {
            var builder =
                WebApplication.CreateBuilder();

            builder.WebHost.UseTestServer();

            builder.Services.AddGrpc();

            builder.Services.AddSingleton<
                IAiRuntimePoolGrpcCommandHandler,
                FakeCommandHandler>();

            await using var application =
                builder.Build();

            application.MapAiRuntimePoolGrpcCommandService();

            await application.StartAsync();

            using var channel =
                GrpcChannel.ForAddress(
                    "http://localhost",
                    new GrpcChannelOptions
                    {
                        HttpHandler =
                            application
                                .GetTestServer()
                                .CreateHandler()
                    });

            var client =
                new AiRuntimeInstanceCommandGrpc
                    .AiRuntimeInstanceCommandGrpcClient(
                        channel);

            var response =
                await client.ExecuteCommandAsync(
                    new AiRuntimeInstanceGrpcCommandRequest
                    {
                        RequestJson =
                            JsonSerializer.Serialize(
                                RuntimePoolGrpcCommandHandlerTests
                                    .CreateRequest(
                                        "runtime-a2"),
                                JsonOptions)
                    });

            var result =
                JsonSerializer.Deserialize<
                    AiRuntimeInstanceCommandResult>(
                    response.ResponseJson,
                    JsonOptions);

            Assert.NotNull(result);
            Assert.True(result.Success);

            Assert.Equal(
                "runtime-a2",
                result.RuntimeInstanceId);

            await application.StopAsync();
        }

        /// <summary>
        /// Verifies explicit failure for malformed outer JSON.
        /// </summary>
        [Fact]
        public async Task Service_Should_Return_Explicit_Failure_For_Invalid_Json()
        {
            var builder =
                WebApplication.CreateBuilder();

            builder.WebHost.UseTestServer();
            builder.Services.AddGrpc();

            builder.Services.AddSingleton<
                IAiRuntimePoolGrpcCommandHandler,
                FakeCommandHandler>();

            await using var application =
                builder.Build();

            application.MapAiRuntimePoolGrpcCommandService();

            await application.StartAsync();

            using var channel =
                GrpcChannel.ForAddress(
                    "http://localhost",
                    new GrpcChannelOptions
                    {
                        HttpHandler =
                            application
                                .GetTestServer()
                                .CreateHandler()
                    });

            var client =
                new AiRuntimeInstanceCommandGrpc
                    .AiRuntimeInstanceCommandGrpcClient(
                        channel);

            var response =
                await client.ExecuteCommandAsync(
                    new AiRuntimeInstanceGrpcCommandRequest
                    {
                        RequestJson = "{invalid-json"
                    });

            var result =
                JsonSerializer.Deserialize<
                    AiRuntimeInstanceCommandResult>(
                    response.ResponseJson,
                    JsonOptions);

            Assert.NotNull(result);
            Assert.False(result.Success);

            Assert.Equal(
                AiRuntimePoolGrpcRoutingFailureReasons
                    .RequestJsonInvalid,
                result.FailureReason);

            await application.StopAsync();
        }

        /// <summary>
        /// Provides one deterministic exact command handler.
        /// </summary>
        private sealed class FakeCommandHandler :
            IAiRuntimePoolGrpcCommandHandler
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
