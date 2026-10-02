using System.Net;
using Multiplexed.AI.Sdk.Transport;

namespace Multiplexed.AI.Sdk.Tests.Transport
{
    public sealed class AiSdkAccessContextRotationTests
    {
        [Fact]
        public async Task RotationHandler_Uses_Latest_Response_Handle_On_Next_Request()
        {
            var state = new AiSdkAccessContextState(
                "X-Access-Context",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["x-access-context"] = "ctx-initial"
                });

            var inner = new CapturingHandler();
            using var client = new HttpClient(
                new AiSdkAccessContextRotationHandler(state)
                {
                    InnerHandler = inner
                });

            using var first = await client.GetAsync("https://runtime.example/one");
            using var second = await client.GetAsync("https://runtime.example/two");

            Assert.Equal(
                new[] { "ctx-initial", "ctx-rotated-1" },
                inner.ObservedRequestContexts);

            Assert.Equal("ctx-rotated-2", state.Current);
        }

        [Fact]
        public void State_Ignores_Ambiguous_Rotation_Response()
        {
            var state = new AiSdkAccessContextState(
                "X-Access-Context",
                new Dictionary<string, string>
                {
                    ["X-Access-Context"] = "ctx-initial"
                });

            using var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.TryAddWithoutValidation(
                "X-Access-Context",
                new[] { "ctx-a", "ctx-b" });

            state.Observe(response.Headers);

            Assert.Equal("ctx-initial", state.Current);
        }

        [Fact]
        public void Options_Allow_Custom_Access_Context_Header_Name()
        {
            var state = new AiSdkAccessContextState(
                "X-Custom-Context",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["x-custom-context"] = "ctx-custom"
                });

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "https://runtime.example/mcp");

            state.Apply(request.Headers);

            Assert.True(
                request.Headers.TryGetValues(
                    "X-Custom-Context",
                    out var values));

            Assert.Equal("ctx-custom", Assert.Single(values));
        }

        private sealed class CapturingHandler : HttpMessageHandler
        {
            private int _requestIndex;

            public List<string?> ObservedRequestContexts { get; } = new();

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                request.Headers.TryGetValues(
                    "X-Access-Context",
                    out var requestValues);

                ObservedRequestContexts.Add(
                    requestValues?.SingleOrDefault());

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request
                };

                _requestIndex++;
                response.Headers.TryAddWithoutValidation(
                    "X-Access-Context",
                    $"ctx-rotated-{_requestIndex}");

                return Task.FromResult(response);
            }
        }
    }
}
