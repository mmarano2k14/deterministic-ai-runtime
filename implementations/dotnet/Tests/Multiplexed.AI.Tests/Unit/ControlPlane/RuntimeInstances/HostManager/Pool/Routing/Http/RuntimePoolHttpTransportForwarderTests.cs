using System.Net;
using System.Net.Http.Json;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Http;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Http
{
    /// <summary>
    /// Validates exact child HTTP URI construction and command transport.
    /// </summary>
    public sealed class RuntimePoolHttpTransportForwarderTests
    {
        /// <summary>
        /// Verifies the exact child endpoint and unchanged command DTO.
        /// </summary>
        [Fact]
        public async Task ForwardAsync_Should_Post_To_Exact_Child_Command_Endpoint()
        {
            var messageHandler =
                new RecordingHttpMessageHandler();

            using var httpClient =
                new HttpClient(messageHandler);

            var forwarder =
                new AiRuntimePoolHttpTransportForwarder(
                    httpClient);

            var route =
                CreateRoute(
                    "runtime-a2",
                    "http://127.0.0.1:6102");

            var request =
                RuntimePoolHttpCommandHandlerTests
                    .CreateRequest(
                        "runtime-a2");

            var result =
                await forwarder.ForwardAsync(
                    route,
                    request);

            Assert.True(result.Success);

            Assert.Equal(
                "runtime-a2",
                result.RuntimeInstanceId);

            Assert.Equal(
                new Uri(
                    "http://127.0.0.1:6102/runtime-instance/commands"),
                messageHandler.RequestUri);

            Assert.Equal(
                HttpMethod.Post,
                messageHandler.Method);

            Assert.Equal(
                "runtime-a2",
                messageHandler.Request?
                    .RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies rejection when the child response claims another runtime identity.
        /// </summary>
        [Fact]
        public async Task ForwardAsync_Should_Reject_Response_Identity_Mismatch()
        {
            var messageHandler =
                new RecordingHttpMessageHandler(
                    responseRuntimeInstanceId:
                        "runtime-a3");

            using var httpClient =
                new HttpClient(messageHandler);

            var forwarder =
                new AiRuntimePoolHttpTransportForwarder(
                    httpClient);

            await Assert.ThrowsAsync<
                InvalidOperationException>(
                () =>
                    forwarder.ForwardAsync(
                        CreateRoute(
                            "runtime-a2",
                            "http://127.0.0.1:6102"),
                        RuntimePoolHttpCommandHandlerTests
                            .CreateRequest(
                                "runtime-a2")));
        }

        /// <summary>
        /// Creates one exact ready HTTP route.
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
                TransportName = "http",
                TransportEndpoint = endpoint,
                Status = AiRuntimePoolRouteStatus.Ready
            };
        }

        /// <summary>
        /// Records the outbound request and returns one deterministic child response.
        /// </summary>
        private sealed class RecordingHttpMessageHandler :
            HttpMessageHandler
        {
            private readonly string responseRuntimeInstanceId;

            /// <summary>
            /// Initializes a new instance of the
            /// <see cref="RecordingHttpMessageHandler"/> class.
            /// </summary>
            public RecordingHttpMessageHandler(
                string responseRuntimeInstanceId =
                    "runtime-a2")
            {
                this.responseRuntimeInstanceId =
                    responseRuntimeInstanceId;
            }

            /// <summary>
            /// Gets the observed URI.
            /// </summary>
            public Uri? RequestUri { get; private set; }

            /// <summary>
            /// Gets the observed HTTP method.
            /// </summary>
            public HttpMethod? Method { get; private set; }

            /// <summary>
            /// Gets the deserialized existing command request.
            /// </summary>
            public AiRuntimeInstanceCommandRequest? Request
            {
                get;
                private set;
            }

            /// <inheritdoc />
            protected override async Task<HttpResponseMessage>
                SendAsync(
                    HttpRequestMessage request,
                    CancellationToken cancellationToken)
            {
                this.RequestUri = request.RequestUri;
                this.Method = request.Method;

                this.Request =
                    await request.Content!
                        .ReadFromJsonAsync<
                            AiRuntimeInstanceCommandRequest>(
                            cancellationToken);

                return new HttpResponseMessage(
                    HttpStatusCode.OK)
                {
                    Content =
                        JsonContent.Create(
                            new AiRuntimeInstanceCommandResult
                            {
                                Success = true,
                                Operation =
                                    this.Request!.Operation,
                                RuntimeInstanceId =
                                    this.responseRuntimeInstanceId,
                                StartedAtUtc =
                                    DateTimeOffset.UtcNow,
                                CompletedAtUtc =
                                    DateTimeOffset.UtcNow,
                                DurationMs = 0
                            })
                };
            }
        }
    }
}
