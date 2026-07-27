using System.Text.Json;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Grpc;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Grpc
{
    /// <summary>
    /// Validates exact child gRPC envelope forwarding.
    /// </summary>
    public sealed class RuntimePoolGrpcTransportForwarderTests
    {
        private static readonly JsonSerializerOptions JsonOptions =
            new(JsonSerializerDefaults.Web);

        /// <summary>
        /// Verifies unchanged existing command JSON and exact endpoint selection.
        /// </summary>
        [Fact]
        public async Task ForwardAsync_Should_Use_Exact_Child_Endpoint_And_Existing_Envelope()
        {
            var factory =
                new RecordingClientFactory(
                    responseRuntimeInstanceId:
                        "runtime-a2");

            var forwarder =
                new AiRuntimePoolGrpcTransportForwarder(
                    factory);

            var request =
                RuntimePoolGrpcCommandHandlerTests
                    .CreateRequest(
                        "runtime-a2");

            var result =
                await forwarder.ForwardAsync(
                    CreateRoute(
                        "runtime-a2",
                        "http://127.0.0.1:6202"),
                    request);

            Assert.True(result.Success);

            Assert.Equal(
                "runtime-a2",
                result.RuntimeInstanceId);

            Assert.Equal(
                "http://127.0.0.1:6202",
                factory.ObservedEndpoint);

            Assert.NotNull(
                factory.Client.ObservedRequest);

            var forwardedRequest =
                JsonSerializer.Deserialize<
                    AiRuntimeInstanceCommandRequest>(
                    factory.Client.ObservedRequest!
                        .RequestJson,
                    JsonOptions);

            Assert.NotNull(forwardedRequest);

            Assert.Equal(
                request.Operation,
                forwardedRequest.Operation);

            Assert.Equal(
                request.RuntimeInstanceId,
                forwardedRequest.RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies rejection when the child response claims another runtime identity.
        /// </summary>
        [Fact]
        public async Task ForwardAsync_Should_Reject_Response_Identity_Mismatch()
        {
            var forwarder =
                new AiRuntimePoolGrpcTransportForwarder(
                    new RecordingClientFactory(
                        responseRuntimeInstanceId:
                            "runtime-a3"));

            await Assert.ThrowsAsync<
                InvalidOperationException>(
                () =>
                    forwarder.ForwardAsync(
                        CreateRoute(
                            "runtime-a2",
                            "http://127.0.0.1:6202"),
                        RuntimePoolGrpcCommandHandlerTests
                            .CreateRequest(
                                "runtime-a2")));
        }

        /// <summary>
        /// Verifies that the child client is disposed after forwarding.
        /// </summary>
        [Fact]
        public async Task ForwardAsync_Should_Dispose_Exact_Child_Client()
        {
            var factory =
                new RecordingClientFactory(
                    responseRuntimeInstanceId:
                        "runtime-a2");

            var forwarder =
                new AiRuntimePoolGrpcTransportForwarder(
                    factory);

            await forwarder.ForwardAsync(
                CreateRoute(
                    "runtime-a2",
                    "http://127.0.0.1:6202"),
                RuntimePoolGrpcCommandHandlerTests
                    .CreateRequest(
                        "runtime-a2"));

            Assert.True(
                factory.Client.Disposed);
        }

        /// <summary>
        /// Creates one exact ready gRPC route.
        /// </summary>
        private static AiRuntimePoolRouteDescriptor
            CreateRoute(
                string runtimeInstanceId,
                string endpoint)
        {
            return new AiRuntimePoolRouteDescriptor
            {
                RouteId = "route-a2",
                PoolId = "pool-01",
                HostId = "host-01",
                RuntimeInstanceId =
                    runtimeInstanceId,
                TransportName = "grpc",
                TransportEndpoint = endpoint,
                Status = AiRuntimePoolRouteStatus.Ready
            };
        }

        /// <summary>
        /// Creates recording child gRPC clients.
        /// </summary>
        private sealed class RecordingClientFactory :
            IAiRuntimePoolGrpcClientFactory
        {
            private readonly string responseRuntimeInstanceId;

            /// <summary>
            /// Initializes a new instance of the
            /// <see cref="RecordingClientFactory"/> class.
            /// </summary>
            public RecordingClientFactory(
                string responseRuntimeInstanceId)
            {
                this.responseRuntimeInstanceId =
                    responseRuntimeInstanceId;

                this.Client =
                    new RecordingClient(
                        responseRuntimeInstanceId);
            }

            /// <summary>
            /// Gets the created client.
            /// </summary>
            public RecordingClient Client { get; }

            /// <summary>
            /// Gets the exact observed endpoint.
            /// </summary>
            public string? ObservedEndpoint { get; private set; }

            /// <inheritdoc />
            public IAiRuntimePoolGrpcClient Create(
                string transportEndpoint)
            {
                this.ObservedEndpoint =
                    transportEndpoint;

                return this.Client;
            }
        }

        /// <summary>
        /// Records one existing gRPC request and returns a deterministic response.
        /// </summary>
        internal sealed class RecordingClient :
            IAiRuntimePoolGrpcClient
        {
            private readonly string responseRuntimeInstanceId;

            /// <summary>
            /// Initializes a new instance of the
            /// <see cref="RecordingClient"/> class.
            /// </summary>
            public RecordingClient(
                string responseRuntimeInstanceId)
            {
                this.responseRuntimeInstanceId =
                    responseRuntimeInstanceId;
            }

            /// <summary>
            /// Gets the observed existing gRPC request.
            /// </summary>
            public AiRuntimeInstanceGrpcCommandRequest?
                ObservedRequest { get; private set; }

            /// <summary>
            /// Gets whether the client was disposed.
            /// </summary>
            public bool Disposed { get; private set; }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceGrpcCommandResponse>
                ExecuteCommandAsync(
                    AiRuntimeInstanceGrpcCommandRequest request,
                    CancellationToken cancellationToken = default)
            {
                this.ObservedRequest = request;

                var commandRequest =
                    JsonSerializer.Deserialize<
                        AiRuntimeInstanceCommandRequest>(
                        request.RequestJson,
                        JsonOptions)
                    ?? throw new InvalidOperationException(
                        "The forwarded gRPC request JSON was empty.");

                return Task.FromResult(
                    new AiRuntimeInstanceGrpcCommandResponse
                    {
                        ResponseJson =
                            JsonSerializer.Serialize(
                                new AiRuntimeInstanceCommandResult
                                {
                                    Success = true,
                                    Operation =
                                        commandRequest.Operation,
                                    RuntimeInstanceId =
                                        this.responseRuntimeInstanceId,
                                    StartedAtUtc =
                                        DateTimeOffset.UtcNow,
                                    CompletedAtUtc =
                                        DateTimeOffset.UtcNow,
                                    DurationMs = 0
                                },
                                JsonOptions)
                    });
            }

            /// <inheritdoc />
            public ValueTask DisposeAsync()
            {
                this.Disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }
}
