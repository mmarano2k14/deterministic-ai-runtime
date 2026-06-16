using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Multiplexed.AI.McpServer.Tests.Integration.Auth
{
    public sealed class FakeAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string AuthenticationScheme = "Fake";

        public FakeAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            Console.WriteLine("[FAKE AUTH] HandleAuthenticateAsync called.");

            Console.WriteLine("[FAKE AUTH] Request headers:");
            foreach (var header in Request.Headers)
            {
                Console.WriteLine($"[FAKE AUTH] Header '{header.Key}' = '{header.Value}'");
            }

            var userId = Request.Headers["X-Demo-UserId"].ToString();

            if (string.IsNullOrWhiteSpace(userId))
            {
                userId = "mcp-integration-test";
            }

            Console.WriteLine($"[FAKE AUTH] Authenticated userId='{userId}'.");

            var claims = new[]
            {
                new Claim("sub", userId),
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Name, userId)
            };

            var identity = new ClaimsIdentity(
                claims,
                AuthenticationScheme);

            var principal = new ClaimsPrincipal(identity);

            var ticket = new AuthenticationTicket(
                principal,
                AuthenticationScheme);

            return Task.FromResult(
                AuthenticateResult.Success(ticket));
        }
    }
}