using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Multiplexed.AI.McpServer.Host.Bootstrap;
using Multiplexed.AI.McpServer.Host.Configuration;

namespace Multiplexed.AI.McpServer.Tests.Integration.Bootstrap
{
    public sealed class StandaloneAccessContextClaimsTests
    {
        [Fact]
        public void TryCreate_Projects_Authenticated_Jwt_Claims_Into_Access_Context()
        {
            var identity = new ClaimsIdentity(
                new[]
                {
                    new Claim("sub", "demo-user"),
                    new Claim("tenant_id", "demo-tenant"),
                    new Claim("tenant_group_id", "demo-group"),
                    new Claim("project", "interactive-agent"),
                    new Claim("namespace", "default"),
                    new Claim("trn", "trn:interactive-agent:default:code:publication:publish"),
                    new Claim("trn", "trn:interactive-agent:default:shared-run:execution:submit")
                },
                JwtBearerDefaults.AuthenticationScheme);

            var principal = new ClaimsPrincipal(identity);

            var ok = StandaloneAccessContextClaims.TryCreate(
                principal,
                new AiMcpAuthenticationOptions(),
                out var result,
                out var error);

            Assert.True(ok, error);
            Assert.NotNull(result);
            Assert.Equal("demo-user", result.UserId);
            Assert.Equal("demo-tenant", result.TenantId);
            Assert.Equal("interactive-agent", result.Project);
            Assert.Equal(2, result.Trns.Count);
        }

        [Fact]
        public void TryCreate_Rejects_Token_Without_Trn_Capabilities()
        {
            var identity = new ClaimsIdentity(
                new[]
                {
                    new Claim("sub", "demo-user"),
                    new Claim("tenant_id", "demo-tenant"),
                    new Claim("tenant_group_id", "demo-group"),
                    new Claim("project", "interactive-agent"),
                    new Claim("namespace", "default")
                },
                JwtBearerDefaults.AuthenticationScheme);

            var ok = StandaloneAccessContextClaims.TryCreate(
                new ClaimsPrincipal(identity),
                new AiMcpAuthenticationOptions(),
                out var result,
                out var error);

            Assert.False(ok);
            Assert.Null(result);
            Assert.Contains("no TRN", error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TryCreate_Accepts_Json_Array_Trn_Claim()
        {
            var identity = new ClaimsIdentity(
                new[]
                {
                    new Claim("sub", "demo-user"),
                    new Claim("tenant_id", "demo-tenant"),
                    new Claim("tenant_group_id", "demo-group"),
                    new Claim("project", "interactive-agent"),
                    new Claim("namespace", "default"),
                    new Claim(
                        "trn",
                        "[\"trn:interactive-agent:default:execution:control:read\"," +
                        "\"trn:interactive-agent:default:execution:control:input\"]")
                },
                JwtBearerDefaults.AuthenticationScheme);

            var ok = StandaloneAccessContextClaims.TryCreate(
                new ClaimsPrincipal(identity),
                new AiMcpAuthenticationOptions(),
                out var result,
                out var error);

            Assert.True(ok, error);
            Assert.Equal(2, result!.Trns.Count);
        }
    }
}
