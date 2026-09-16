using System.Net;
using System.Text.Json;
using ModelContextProtocol.Client;
using Multiplexed.AI.Sdk.Authentication;
using Multiplexed.AI.Sdk.Contracts.Common;
using Multiplexed.AI.Sdk.Errors;
using Multiplexed.AI.Sdk.Protocol;

namespace Multiplexed.AI.Sdk.Transport
{
    /// <summary>
    /// Streamable-HTTP MCP transport for the external SDK. It may retry only transport failures for operations
    /// explicitly classified as safe reads by <see cref="AiSdkProtocol"/>.
    /// </summary>
    public sealed class AiSdkMcpHttpTransport : IAiSdkTransport
    {
        private readonly Uri _endpoint;
        private readonly AiSdkTransportOptions _options;

        public AiSdkMcpHttpTransport(Uri endpoint, AiSdkTransportOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            if (!endpoint.IsAbsoluteUri ||
                (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException("The MCP endpoint must be an absolute HTTP or HTTPS URI.", nameof(endpoint));
            }

            _options = options ?? new AiSdkTransportOptions();
            if (_options.SafeReadMaxAttempts < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "SafeReadMaxAttempts must be at least 1.");
            }
            if (_options.SafeReadRetryDelay < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "SafeReadRetryDelay cannot be negative.");
            }
            if (_options.ConnectionTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "ConnectionTimeout must be positive.");
            }

            _endpoint = endpoint;
        }

        public async ValueTask<AiSdkTransportResponse> InvokeAsync(
            AiSdkTransportRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (request.ProtocolVersion != AiSdkProtocolVersions.Current)
            {
                return Failure(
                    AiSdkErrorKind.UnsupportedSchema,
                    "unsupported_protocol",
                    $"Unsupported SDK protocol version '{request.ProtocolVersion}'. Expected '{AiSdkProtocolVersions.Current}'.");
            }

            AiSdkOperationDescriptor operation;
            try
            {
                operation = AiSdkProtocol.GetOperation(request.Operation);
            }
            catch (ArgumentException)
            {
                return Failure(
                    AiSdkErrorKind.InvalidRequest,
                    "unknown_operation",
                    $"Unknown public SDK operation '{request.Operation}'.");
            }

            if (request.Arguments.ValueKind != JsonValueKind.Object)
            {
                return Failure(
                    AiSdkErrorKind.InvalidRequest,
                    "invalid_arguments",
                    "SDK transport arguments must be a JSON object.");
            }

            var attempts = operation.TransportRetryMode == AiSdkTransportRetryMode.SafeRead
                ? _options.SafeReadMaxAttempts
                : 1;

            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return await InvokeOnceAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (IsRetryableTransportFailure(ex))
                {
                    if (attempt >= attempts)
                    {
                        return CreateTransportFailure(ex, retryable: operation.TransportRetryMode == AiSdkTransportRetryMode.SafeRead);
                    }

                    if (_options.SafeReadRetryDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(_options.SafeReadRetryDelay, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    return CreateTransportFailure(ex, retryable: false);
                }
            }

            return Failure(
                AiSdkErrorKind.Transport,
                "transport_failure",
                "The MCP transport did not produce a response.");
        }

        private async Task<AiSdkTransportResponse> InvokeOnceAsync(
            AiSdkTransportRequest request,
            CancellationToken cancellationToken)
        {
            var headers = await CreateHeadersAsync(cancellationToken).ConfigureAwait(false);
            if (headers.Error is not null)
            {
                return AiSdkTransportResponse.Failure(headers.Error);
            }

            var transportOptions = new HttpClientTransportOptions
            {
                Name = "Multiplexed.AI.Sdk",
                Endpoint = _endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                ConnectionTimeout = _options.ConnectionTimeout,
                AdditionalHeaders = headers.Headers
            };

            await using var clientTransport = new HttpClientTransport(transportOptions);
            await using var client = await McpClient.CreateAsync(
                clientTransport,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in request.Arguments.EnumerateObject())
            {
                arguments.Add(property.Name, property.Value.Clone());
            }

            var result = await client.CallToolAsync(
                request.Operation,
                arguments,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result.IsError is true)
            {
                return Failure(
                    AiSdkErrorKind.RemoteFailure,
                    "remote_tool_error",
                    $"The remote SDK operation '{request.Operation}' returned an error.");
            }

            if (result.StructuredContent is not { } structuredContent ||
                structuredContent.ValueKind != JsonValueKind.Object)
            {
                return Failure(
                    AiSdkErrorKind.InvalidResponse,
                    "missing_structured_content",
                    $"The remote SDK operation '{request.Operation}' did not return an object structured result.");
            }

            return AiSdkTransportResponse.Success(structuredContent.Clone());
        }

        private async Task<(Dictionary<string, string>? Headers, AiSdkError? Error)> CreateHeadersAsync(
            CancellationToken cancellationToken)
        {
            if (_options.CredentialProvider is null)
            {
                return (null, null);
            }

            AiSdkCredential? credential;
            try
            {
                credential = await _options.CredentialProvider
                    .GetCredentialAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return (
                    null,
                    new AiSdkError
                    {
                        Kind = AiSdkErrorKind.Authentication,
                        Code = "credential_provider_failure",
                        Message = "The SDK credential provider failed to supply a transport credential."
                    });
            }

            if (credential is null)
            {
                return (null, null);
            }

            if (string.IsNullOrWhiteSpace(credential.Scheme) || string.IsNullOrWhiteSpace(credential.Value))
            {
                return (
                    null,
                    new AiSdkError
                    {
                        Kind = AiSdkErrorKind.Authentication,
                        Code = "invalid_credential",
                        Message = "The SDK credential provider returned an invalid credential."
                    });
            }

            return (
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Authorization"] = $"{credential.Scheme} {credential.Value}"
                },
                null);
        }

        private static bool IsRetryableTransportFailure(Exception exception)
        {
            if (exception is TimeoutException or IOException)
            {
                return true;
            }

            if (exception is not HttpRequestException httpException)
            {
                return false;
            }

            if (httpException.StatusCode is null)
            {
                return true;
            }

            return httpException.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
                   (int)httpException.StatusCode >= 500;
        }

        private static AiSdkTransportResponse CreateTransportFailure(Exception exception, bool retryable)
        {
            if (exception is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized })
            {
                return Failure(
                    AiSdkErrorKind.Authentication,
                    "authentication_failed",
                    "The SDK transport was not authenticated.");
            }

            if (exception is HttpRequestException { StatusCode: HttpStatusCode.Forbidden })
            {
                return Failure(
                    AiSdkErrorKind.Authorization,
                    "authorization_failed",
                    "The SDK transport is not authorized for the requested operation.");
            }

            return AiSdkTransportResponse.Failure(new AiSdkError
            {
                Kind = AiSdkErrorKind.Transport,
                Code = exception is TimeoutException ? "transport_timeout" : "transport_failure",
                Message = exception.Message,
                IsRetryable = retryable
            });
        }

        private static AiSdkTransportResponse Failure(AiSdkErrorKind kind, string code, string message) =>
            AiSdkTransportResponse.Failure(new AiSdkError
            {
                Kind = kind,
                Code = code,
                Message = message
            });
    }
}
