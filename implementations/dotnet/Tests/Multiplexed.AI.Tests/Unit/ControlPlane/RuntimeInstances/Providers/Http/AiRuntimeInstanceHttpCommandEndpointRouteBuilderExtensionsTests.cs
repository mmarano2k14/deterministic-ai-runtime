using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Http;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Providers.Http
{
    /// <summary>
    /// Unit tests for <see cref="AiRuntimeInstanceHttpCommandEndpointRouteBuilderExtensions"/>.
    /// </summary>
    public sealed class AiRuntimeInstanceHttpCommandEndpointRouteBuilderExtensionsTests
    {
        /// <summary>
        /// Verifies that the default runtime instance HTTP command endpoint routes requests to the handler.
        /// </summary>
        [Fact]
        public async Task MapAiRuntimeInstanceHttpCommandEndpoint_WithDefaultPath_ShouldRouteToHandler()
        {
            var handler =
                new TestRuntimeInstanceHttpCommandHandler();

            await using var application =
                await CreateApplicationAsync(
                    handler,
                    app => app.MapAiRuntimeInstanceHttpCommandEndpoint());

            var client =
                application.GetTestClient();

            var response =
                await client.PostAsJsonAsync(
                    AiRuntimeInstanceHttpCommandEndpointRouteBuilderExtensions.DefaultCommandEndpointPath,
                    CreateCommandRequest());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var result =
                await response.Content.ReadFromJsonAsync<AiRuntimeInstanceCommandResult>();

            Assert.NotNull(result);
            Assert.True(result!.Success);
            Assert.Equal(AiRuntimeInstanceCommandOperation.GetQueueStatus, result.Operation);
            Assert.Equal("runtime-http-1", result.RuntimeInstanceId);
            Assert.Equal(1, handler.HandleCallCount);
            Assert.NotNull(handler.LastRequest);
            Assert.Equal(AiRuntimeInstanceCommandOperation.GetQueueStatus, handler.LastRequest!.Operation);
        }

        /// <summary>
        /// Verifies that the runtime instance HTTP command endpoint can be mapped with a custom path.
        /// </summary>
        [Fact]
        public async Task MapAiRuntimeInstanceHttpCommandEndpoint_WithCustomPath_ShouldRouteToHandler()
        {
            var handler =
                new TestRuntimeInstanceHttpCommandHandler();

            await using var application =
                await CreateApplicationAsync(
                    handler,
                    app => app.MapAiRuntimeInstanceHttpCommandEndpoint("/custom/runtime/commands"));

            var client =
                application.GetTestClient();

            var response =
                await client.PostAsJsonAsync(
                    "/custom/runtime/commands",
                    CreateCommandRequest());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var result =
                await response.Content.ReadFromJsonAsync<AiRuntimeInstanceCommandResult>();

            Assert.NotNull(result);
            Assert.True(result!.Success);
            Assert.Equal("runtime-http-1", result.RuntimeInstanceId);
            Assert.Equal(1, handler.HandleCallCount);
        }

        /// <summary>
        /// Verifies that handler failures are returned as command results.
        /// </summary>
        [Fact]
        public async Task MapAiRuntimeInstanceHttpCommandEndpoint_WhenHandlerReturnsFailure_ShouldReturnFailureResult()
        {
            var handler =
                new TestRuntimeInstanceHttpCommandHandler
                {
                    NextResult = new AiRuntimeInstanceCommandResult
                    {
                        Success = false,
                        Operation = AiRuntimeInstanceCommandOperation.GetRunStatus,
                        RuntimeInstanceId = "runtime-http-1",
                        Message = "Test failure.",
                        FailureReason = "test-failure",
                        StartedAtUtc = DateTimeOffset.UtcNow,
                        CompletedAtUtc = DateTimeOffset.UtcNow,
                        DurationMs = 0
                    }
                };

            await using var application =
                await CreateApplicationAsync(
                    handler,
                    app => app.MapAiRuntimeInstanceHttpCommandEndpoint());

            var client =
                application.GetTestClient();

            var response =
                await client.PostAsJsonAsync(
                    AiRuntimeInstanceHttpCommandEndpointRouteBuilderExtensions.DefaultCommandEndpointPath,
                    new AiRuntimeInstanceCommandRequest
                    {
                        Operation = AiRuntimeInstanceCommandOperation.GetRunStatus,
                        RuntimeInstanceId = "runtime-http-1"
                    });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var result =
                await response.Content.ReadFromJsonAsync<AiRuntimeInstanceCommandResult>();

            Assert.NotNull(result);
            Assert.False(result!.Success);
            Assert.Equal("test-failure", result.FailureReason);
            Assert.Equal(1, handler.HandleCallCount);
        }

        /// <summary>
        /// Creates a test web application.
        /// </summary>
        /// <param name="handler">The test HTTP command handler.</param>
        /// <param name="mapEndpoints">The endpoint mapping action.</param>
        /// <returns>The web application.</returns>
        private static async Task<WebApplication> CreateApplicationAsync(
            IAiRuntimeInstanceHttpCommandHandler handler,
            Action<WebApplication> mapEndpoints)
        {
            var builder =
                WebApplication.CreateBuilder();

            builder.WebHost.UseTestServer();

            builder.Services.AddSingleton<IAiRuntimeInstanceHttpCommandHandler>(handler);

            var app =
                builder.Build();

            mapEndpoints(app);

            await app
                .StartAsync()
                .ConfigureAwait(false);

            return app;
        }

        /// <summary>
        /// Creates a runtime instance command request.
        /// </summary>
        /// <returns>The command request.</returns>
        private static AiRuntimeInstanceCommandRequest CreateCommandRequest()
        {
            return new AiRuntimeInstanceCommandRequest
            {
                Operation = AiRuntimeInstanceCommandOperation.GetQueueStatus,
                RuntimeInstanceId = "runtime-http-1",
                Metadata = new Dictionary<string, string>
                {
                    ["test.source"] = "endpoint-test"
                }
            };
        }

        /// <summary>
        /// Test runtime instance HTTP command handler.
        /// </summary>
        private sealed class TestRuntimeInstanceHttpCommandHandler : IAiRuntimeInstanceHttpCommandHandler
        {
            /// <summary>
            /// Gets or sets the next command result.
            /// </summary>
            public AiRuntimeInstanceCommandResult? NextResult { get; set; }

            /// <summary>
            /// Gets the last command request handled by this handler.
            /// </summary>
            public AiRuntimeInstanceCommandRequest? LastRequest { get; private set; }

            /// <summary>
            /// Gets the number of handle calls.
            /// </summary>
            public int HandleCallCount { get; private set; }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceCommandResult> HandleAsync(
                AiRuntimeInstanceCommandRequest request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);

                HandleCallCount++;
                LastRequest = request;

                if (NextResult is not null)
                {
                    return Task.FromResult(NextResult);
                }

                var now =
                    DateTimeOffset.UtcNow;

                return Task.FromResult(
                    new AiRuntimeInstanceCommandResult
                    {
                        Success = true,
                        Operation = request.Operation,
                        RuntimeInstanceId = request.RuntimeInstanceId,
                        Message = "Endpoint command handled.",
                        StartedAtUtc = now,
                        CompletedAtUtc = now,
                        DurationMs = 0,
                        Metadata = request.Metadata
                    });
            }
        }
    }
}