using System.Net;
using Multiplexed.AI.Sdk.Authentication;
using Multiplexed.AI.Sdk.Errors;

namespace Multiplexed.AI.Sdk.Tests.Authentication
{
    public sealed class AiSdkAccessContextBootstrapperTests
    {
        [Fact]
        public async Task CreateAsync_Sends_Bearer_And_Returns_Access_Context_Header()
        {
            var handler = new RecordingHandler(
                HttpStatusCode.OK,
                ("X-Access-Context", "ctx-created"));
            using var client = new HttpClient(handler);

            var result = await AiSdkAccessContextBootstrapper.CreateAsync(
                Options("token-123"),
                client);

            Assert.Equal("Bearer", handler.AuthorizationScheme);
            Assert.Equal("token-123", handler.AuthorizationValue);
            Assert.Equal("ctx-created", result.AccessContext);
            Assert.Equal("X-Access-Context", result.HeaderName);
            Assert.Equal(1, handler.RequestCount);
        }

        [Fact]
        public async Task CreateAsync_Unauthorized_Is_Normalized_As_Authentication_Error()
        {
            var handler = new RecordingHandler(HttpStatusCode.Unauthorized);
            using var client = new HttpClient(handler);

            var exception = await Assert.ThrowsAsync<AiSdkException>(() =>
                AiSdkAccessContextBootstrapper.CreateAsync(
                    Options("bad-token"),
                    client));

            Assert.Equal(AiSdkErrorKind.Authentication, exception.Error.Kind);
            Assert.Equal(
                "access_context_bootstrap_unauthenticated",
                exception.Error.Code);
            Assert.False(exception.Error.IsRetryable);
            Assert.Equal(1, handler.RequestCount);
        }

        [Fact]
        public async Task CreateAsync_Does_Not_Retry_Remote_Failure()
        {
            var handler = new RecordingHandler(HttpStatusCode.ServiceUnavailable);
            using var client = new HttpClient(handler);

            var exception = await Assert.ThrowsAsync<AiSdkException>(() =>
                AiSdkAccessContextBootstrapper.CreateAsync(
                    Options("token-123"),
                    client));

            Assert.Equal(AiSdkErrorKind.RemoteFailure, exception.Error.Kind);
            Assert.False(exception.Error.IsRetryable);
            Assert.Equal(1, handler.RequestCount);
        }

        [Fact]
        public async Task CreateAsync_Requires_One_Unambiguous_Access_Context_Header()
        {
            var handler = new RecordingHandler(
                HttpStatusCode.OK,
                ("X-Access-Context", "ctx-a"),
                ("X-Access-Context", "ctx-b"));
            using var client = new HttpClient(handler);

            var exception = await Assert.ThrowsAsync<AiSdkException>(() =>
                AiSdkAccessContextBootstrapper.CreateAsync(
                    Options("token-123"),
                    client));

            Assert.Equal(AiSdkErrorKind.InvalidResponse, exception.Error.Kind);
            Assert.Equal(
                "access_context_bootstrap_missing_handle",
                exception.Error.Code);
        }

        [Fact]
        public async Task CreateAsync_Supports_Custom_Access_Context_Header()
        {
            var handler = new RecordingHandler(
                HttpStatusCode.OK,
                ("X-Custom-Context", "ctx-custom"));
            using var client = new HttpClient(handler);

            var options = Options("token-123") with
            {
                AccessContextHeaderName = "X-Custom-Context"
            };

            var result = await AiSdkAccessContextBootstrapper.CreateAsync(
                options,
                client);

            Assert.Equal("ctx-custom", result.AccessContext);
            Assert.Equal("X-Custom-Context", result.HeaderName);
        }

        private static AiSdkAccessContextBootstrapOptions Options(string token) =>
            new()
            {
                Endpoint = new Uri("https://runtime.example/auth/access-context"),
                CredentialProvider = new AiSdkStaticCredentialProvider(
                    new AiSdkCredential("Bearer", token))
            };

        private sealed class RecordingHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _statusCode;
            private readonly IReadOnlyList<(string Name, string Value)> _responseHeaders;

            public RecordingHandler(
                HttpStatusCode statusCode,
                params (string Name, string Value)[] responseHeaders)
            {
                _statusCode = statusCode;
                _responseHeaders = responseHeaders;
            }

            public int RequestCount { get; private set; }
            public string? AuthorizationScheme { get; private set; }
            public string? AuthorizationValue { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                RequestCount++;
                AuthorizationScheme = request.Headers.Authorization?.Scheme;
                AuthorizationValue = request.Headers.Authorization?.Parameter;

                var response = new HttpResponseMessage(_statusCode)
                {
                    RequestMessage = request
                };

                foreach (var (name, value) in _responseHeaders)
                {
                    response.Headers.TryAddWithoutValidation(name, value);
                }

                return Task.FromResult(response);
            }
        }
    }
}
