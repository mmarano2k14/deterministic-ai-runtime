using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Multiplexed.AI.McpServer.Host.Configuration;

namespace Multiplexed.AI.McpServer.Host.Bootstrap
{
    /// <summary>
    /// Harness-only static bearer authentication. It is registered only when AiMatrixHarness is explicitly enabled;
    /// normal runtime deployments retain their configured authentication boundary.
    /// </summary>
    public sealed class MatrixStaticBearerAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "MatrixBearer";
        private readonly IConfiguration _configuration;

        public MatrixStaticBearerAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder,
            IConfiguration configuration)
            : base(options, logger, encoder)
        {
            _configuration = configuration;
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var settings = _configuration.GetSection("AiMatrixHarness").Get<AiMatrixHarnessOptions>()
                ?? new AiMatrixHarnessOptions();
            var expected = settings.BearerToken;
            var authorization = Request.Headers.Authorization.ToString();
            if (string.IsNullOrWhiteSpace(authorization))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            if (string.IsNullOrWhiteSpace(expected) ||
                !string.Equals(authorization, "Bearer " + expected, StringComparison.Ordinal))
            {
                return Task.FromResult(AuthenticateResult.Fail("Invalid matrix harness bearer token."));
            }

            var identity = new ClaimsIdentity(
                new[] { new Claim("sub", settings.UserId) },
                SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
