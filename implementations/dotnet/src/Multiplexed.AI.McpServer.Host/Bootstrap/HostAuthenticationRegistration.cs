using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Multiplexed.AI.McpServer.Host.Configuration;

namespace Multiplexed.AI.McpServer.Host.Bootstrap
{
    /// <summary>
    /// Registers the HTTP authentication and authorization boundary used by MCP control-plane hosts.
    /// </summary>
    public static class HostAuthenticationRegistration
    {
        public static void Configure(
            IServiceCollection services,
            IConfiguration configuration,
            AiMcpHostOptions hostOptions)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(hostOptions);

            if (hostOptions.Mode == AiMcpHostMode.RuntimeInstanceOnly)
            {
                return;
            }

            services.Configure<AiMcpAuthenticationOptions>(
                configuration.GetSection(AiMcpAuthenticationOptions.SectionName));

            var matrix = configuration
                .GetSection("AiMatrixHarness")
                .Get<AiMatrixHarnessOptions>()
                ?? new AiMatrixHarnessOptions();

            // The matrix owns its explicitly isolated authentication scheme.
            if (matrix.Enabled)
            {
                services.AddAuthentication();
                services.AddAuthorization();
                return;
            }

            var authentication = configuration
                .GetSection(AiMcpAuthenticationOptions.SectionName)
                .Get<AiMcpAuthenticationOptions>()
                ?? new AiMcpAuthenticationOptions();

            if (!authentication.Enabled)
            {
                // Keep the middleware pipeline constructible while remaining fail-closed:
                // no default scheme means no authenticated principal can be manufactured.
                services.AddAuthentication();
                services.AddAuthorization();
                return;
            }

            Validate(authentication);

            services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                    options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
                })
                .AddJwtBearer(
                    JwtBearerDefaults.AuthenticationScheme,
                    jwt =>
                    {
                        jwt.MapInboundClaims = false;
                        jwt.RequireHttpsMetadata = authentication.RequireHttpsMetadata;
                        jwt.SaveToken = false;

                        if (!string.IsNullOrWhiteSpace(authentication.Authority))
                        {
                            jwt.Authority = authentication.Authority;
                            jwt.Audience = authentication.Audience;
                            jwt.TokenValidationParameters.NameClaimType =
                                authentication.SubjectClaimType;
                            jwt.TokenValidationParameters.ClockSkew =
                                TimeSpan.FromSeconds(authentication.ClockSkewSeconds);
                            return;
                        }

                        var key = new SymmetricSecurityKey(
                            Encoding.UTF8.GetBytes(authentication.SymmetricSigningKey!));

                        jwt.TokenValidationParameters = new TokenValidationParameters
                        {
                            ValidateIssuerSigningKey = true,
                            IssuerSigningKey = key,
                            ValidateIssuer = true,
                            ValidIssuer = authentication.Issuer,
                            ValidateAudience = true,
                            ValidAudience = authentication.Audience,
                            ValidateLifetime = true,
                            RequireExpirationTime = true,
                            ClockSkew = TimeSpan.FromSeconds(authentication.ClockSkewSeconds),
                            NameClaimType = authentication.SubjectClaimType
                        };
                    });

            services.AddAuthorization();
        }

        internal static void Validate(AiMcpAuthenticationOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            if (string.IsNullOrWhiteSpace(options.Audience))
            {
                throw new InvalidOperationException(
                    "AiMcpAuthentication:Audience is required when standalone authentication is enabled.");
            }

            if (options.ClockSkewSeconds < 0)
            {
                throw new InvalidOperationException(
                    "AiMcpAuthentication:ClockSkewSeconds cannot be negative.");
            }

            if (options.ExecutionContextTtlSeconds <= 0)
            {
                throw new InvalidOperationException(
                    "AiMcpAuthentication:ExecutionContextTtlSeconds must be greater than zero.");
            }

            if (string.IsNullOrWhiteSpace(options.AccessContextPath) ||
                !options.AccessContextPath.StartsWith("/", StringComparison.Ordinal) ||
                options.AccessContextPath.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "AiMcpAuthentication:AccessContextPath must be an absolute non-MCP path.");
            }

            if (!string.IsNullOrWhiteSpace(options.Authority))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(options.Issuer))
            {
                throw new InvalidOperationException(
                    "AiMcpAuthentication:Issuer is required for symmetric-key JWT validation.");
            }

            if (string.IsNullOrWhiteSpace(options.SymmetricSigningKey) ||
                Encoding.UTF8.GetByteCount(options.SymmetricSigningKey) < 32)
            {
                throw new InvalidOperationException(
                    "AiMcpAuthentication:SymmetricSigningKey must contain at least 32 UTF-8 bytes.");
            }
        }
    }
}
