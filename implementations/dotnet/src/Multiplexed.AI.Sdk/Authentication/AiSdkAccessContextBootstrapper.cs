using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Multiplexed.AI.Sdk.Errors;

namespace Multiplexed.AI.Sdk.Authentication
{
    /// <summary>
    /// Creates the initial runtime access-context handle over the public authenticated HTTP boundary.
    /// </summary>
    /// <remarks>
    /// This operation is deliberately explicit and is never retried automatically because creating an
    /// access context is a state-changing HTTP operation. Subsequent handle rotation is owned by the
    /// physical SDK transport.
    /// </remarks>
    public static class AiSdkAccessContextBootstrapper
    {
        public static async Task<AiSdkAccessContextBootstrapResult> CreateAsync(
            AiSdkAccessContextBootstrapOptions options,
            HttpClient? httpClient = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            Validate(options);

            var ownsClient = httpClient is null;
            httpClient ??= new HttpClient();

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.Timeout);

                using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint);

                var credential = await GetCredentialAsync(
                        options.CredentialProvider,
                        timeout.Token)
                    .ConfigureAwait(false);

                if (credential is not null)
                {
                    request.Headers.Authorization = CreateAuthorizationHeader(credential);
                }

                HttpResponseMessage response;
                try
                {
                    response = await httpClient
                        .SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            timeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException ex)
                {
                    throw Error(
                        AiSdkErrorKind.Transport,
                        "access_context_bootstrap_timeout",
                        "The access-context bootstrap request timed out.",
                        retryable: false,
                        ex);
                }
                catch (HttpRequestException ex)
                {
                    throw Error(
                        AiSdkErrorKind.Transport,
                        "access_context_bootstrap_transport_failure",
                        ex.Message,
                        retryable: false,
                        ex);
                }

                using (response)
                {
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        throw Error(
                            AiSdkErrorKind.Authentication,
                            "access_context_bootstrap_unauthenticated",
                            "The access-context bootstrap request was not authenticated.");
                    }

                    if (response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        throw Error(
                            AiSdkErrorKind.Authorization,
                            "access_context_bootstrap_forbidden",
                            "The authenticated identity is not allowed to create an access context.");
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        throw Error(
                            AiSdkErrorKind.RemoteFailure,
                            "access_context_bootstrap_failed",
                            $"The access-context bootstrap endpoint returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
                    }

                    var values = response.Headers
                        .TryGetValues(options.AccessContextHeaderName, out var headerValues)
                            ? headerValues
                                .Where(IsValidHeaderValue)
                                .Select(value => value.Trim())
                                .Distinct(StringComparer.Ordinal)
                                .ToArray()
                            : Array.Empty<string>();

                    if (values.Length != 1)
                    {
                        throw Error(
                            AiSdkErrorKind.InvalidResponse,
                            "access_context_bootstrap_missing_handle",
                            $"The access-context bootstrap response did not contain one unambiguous '{options.AccessContextHeaderName}' header.");
                    }

                    return new AiSdkAccessContextBootstrapResult
                    {
                        AccessContext = values[0],
                        HeaderName = options.AccessContextHeaderName
                    };
                }
            }
            finally
            {
                if (ownsClient)
                {
                    httpClient.Dispose();
                }
            }
        }

        private static async ValueTask<AiSdkCredential?> GetCredentialAsync(
            IAiSdkCredentialProvider? credentialProvider,
            CancellationToken cancellationToken)
        {
            if (credentialProvider is null)
            {
                return null;
            }

            try
            {
                return await credentialProvider
                    .GetCredentialAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw Error(
                    AiSdkErrorKind.Authentication,
                    "credential_provider_failure",
                    "The SDK credential provider failed to supply a bootstrap credential.",
                    innerException: ex);
            }
        }

        private static AuthenticationHeaderValue CreateAuthorizationHeader(
            AiSdkCredential credential)
        {
            if (string.IsNullOrWhiteSpace(credential.Scheme) ||
                string.IsNullOrWhiteSpace(credential.Value) ||
                credential.Scheme.Any(character =>
                    char.IsControl(character) ||
                    char.IsWhiteSpace(character) ||
                    character == ':') ||
                credential.Value.Any(character => character is '\r' or '\n'))
            {
                throw Error(
                    AiSdkErrorKind.Authentication,
                    "invalid_credential",
                    "The SDK credential provider returned an invalid bootstrap credential.");
            }

            try
            {
                return new AuthenticationHeaderValue(
                    credential.Scheme.Trim(),
                    credential.Value.Trim());
            }
            catch (FormatException ex)
            {
                throw Error(
                    AiSdkErrorKind.Authentication,
                    "invalid_credential",
                    "The SDK credential provider returned an invalid bootstrap credential.",
                    innerException: ex);
            }
        }

        private static void Validate(AiSdkAccessContextBootstrapOptions options)
        {
            if (!options.Endpoint.IsAbsoluteUri ||
                (options.Endpoint.Scheme != Uri.UriSchemeHttp &&
                 options.Endpoint.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException(
                    "The access-context bootstrap endpoint must be an absolute HTTP or HTTPS URI.",
                    nameof(options));
            }

            if (options.Timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "The access-context bootstrap timeout must be positive.");
            }

            if (string.IsNullOrWhiteSpace(options.AccessContextHeaderName) ||
                options.AccessContextHeaderName.Any(character =>
                    char.IsControl(character) || character == ':'))
            {
                throw new ArgumentException(
                    "AccessContextHeaderName must be a valid non-empty HTTP header name.",
                    nameof(options));
            }
        }

        private static bool IsValidHeaderValue(string? value) =>
            !string.IsNullOrWhiteSpace(value) &&
            !value.Any(character => character is '\r' or '\n');

        private static AiSdkException Error(
            AiSdkErrorKind kind,
            string code,
            string message,
            bool retryable = false,
            Exception? innerException = null) =>
            new(
                new AiSdkError
                {
                    Kind = kind,
                    Code = code,
                    Message = message,
                    IsRetryable = retryable
                },
                innerException);
    }
}
