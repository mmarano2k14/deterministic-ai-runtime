using System.Security.Claims;
using System.Text.Json;
using Multiplexed.AI.McpServer.Host.Configuration;

namespace Multiplexed.AI.McpServer.Host.Bootstrap
{
    /// <summary>
    /// Immutable authorization identity projected from a validated JWT.
    /// </summary>
    internal sealed record StandaloneAccessContextClaims(
        string UserId,
        string TenantId,
        string TenantGroupId,
        string Project,
        string Namespace,
        IReadOnlyCollection<string> Trns)
    {
        internal static bool TryCreate(
            ClaimsPrincipal principal,
            AiMcpAuthenticationOptions options,
            out StandaloneAccessContextClaims? value,
            out string? error)
        {
            ArgumentNullException.ThrowIfNull(principal);
            ArgumentNullException.ThrowIfNull(options);

            value = null;

            if (principal.Identity?.IsAuthenticated != true)
            {
                error = "The request is not authenticated.";
                return false;
            }

            var userId = Required(principal, options.SubjectClaimType);
            var tenantId = Required(principal, options.TenantIdClaimType);
            var tenantGroupId = Required(principal, options.TenantGroupIdClaimType);
            var project = Required(principal, options.ProjectClaimType);
            var currentNamespace = Required(principal, options.NamespaceClaimType);

            if (userId is null ||
                tenantId is null ||
                tenantGroupId is null ||
                project is null ||
                currentNamespace is null)
            {
                error =
                    "The authenticated token is missing one or more required execution-context claims.";
                return false;
            }

            var trns = principal
                .FindAll(options.TrnClaimType)
                .SelectMany(claim => ExpandClaimValue(claim.Value))
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (trns.Length == 0)
            {
                error = "The authenticated token contains no TRN capability claims.";
                return false;
            }

            value = new StandaloneAccessContextClaims(
                userId,
                tenantId,
                tenantGroupId,
                project,
                currentNamespace,
                trns);

            error = null;
            return true;
        }

        private static string? Required(
            ClaimsPrincipal principal,
            string claimType)
        {
            if (string.IsNullOrWhiteSpace(claimType))
            {
                return null;
            }

            var value = principal.FindFirst(claimType)?.Value;
            return string.IsNullOrWhiteSpace(value)
                ? null
                : value.Trim();
        }

        private static IEnumerable<string> ExpandClaimValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Array.Empty<string>();
            }

            var trimmed = value.Trim();

            if (!trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                return new[] { trimmed };
            }

            try
            {
                using var document = JsonDocument.Parse(trimmed);

                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return new[] { trimmed };
                }

                return document.RootElement
                    .EnumerateArray()
                    .Where(element =>
                        element.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(element.GetString()))
                    .Select(element => element.GetString()!.Trim())
                    .ToArray();
            }
            catch (JsonException)
            {
                // Preserve backward-compatible opaque-claim behavior for malformed JSON-like values.
                return new[] { trimmed };
            }
        }
    }
}
