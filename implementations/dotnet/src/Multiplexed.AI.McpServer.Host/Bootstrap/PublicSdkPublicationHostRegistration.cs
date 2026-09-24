using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.McpServer.Host.Configuration;
using Multiplexed.AI.Runtime.Publication;
using Multiplexed.AI.Runtime.Publication.DI;

namespace Multiplexed.AI.McpServer.Host.Bootstrap
{
    /// <summary>
    /// Ensures that the public SDK immutable-publication boundary exists independently
    /// from hosted custom-function worker provisioning.
    /// </summary>
    /// <remarks>
    /// Native-only published DAGs require immutable publication/run pinning even when
    /// <c>AiHostedInvocation</c> is disabled. Hosted invocation may still install an
    /// exact environment catalog and the same publication services first; in that case
    /// this registration validates and reuses the existing authority.
    /// </remarks>
    public static class PublicSdkPublicationHostRegistration
    {
        /// <summary>
        /// Registers the immutable publication authority for MCP control-plane modes.
        /// </summary>
        public static void Configure(
            IServiceCollection services,
            AiMcpHostOptions hostOptions)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(hostOptions);

            if (hostOptions.Mode == AiMcpHostMode.RuntimeInstanceOnly)
            {
                return;
            }

            if (services.Any(
                    descriptor =>
                        descriptor.ServiceType == typeof(AiPublicationOptions)))
            {
                RequireCompleteExistingRegistration(services);
                return;
            }

            if (!services.Any(
                    descriptor =>
                        descriptor.ServiceType ==
                        typeof(IAiPublicationEnvironmentCatalog)))
            {
                var emptyCatalog =
                    new AiConfiguredPublicationEnvironmentCatalog(
                        Array.Empty<AiPublicationEnvironment>());

                services.TryAddSingleton<IAiPublicationEnvironmentCatalog>(
                    emptyCatalog);

                services.TryAddSingleton<IAiPublicationExecutionEnvironmentCatalog>(
                    emptyCatalog);
            }

            services.AddAiImmutablePublications(
                new AiPublicationOptions(
                    new AiPublicationCapability(
                        "code",
                        "publication",
                        "publish"),
                    new AiPublicationCapability(
                        "code",
                        "publication",
                        "read"),
                    new AiPublicationCapability(
                        "code",
                        "publication",
                        "execute")));
        }

        private static void RequireCompleteExistingRegistration(
            IServiceCollection services)
        {
            var required = new[]
            {
                typeof(AiPipelinePublicationService),
                typeof(AiPublishedDagRunService)
            };

            var missing = required
                .Where(
                    requiredType =>
                        !services.Any(
                            descriptor =>
                                descriptor.ServiceType == requiredType))
                .Select(type => type.Name)
                .ToArray();

            if (missing.Length > 0)
            {
                throw new InvalidOperationException(
                    "Immutable publication options are registered without the complete " +
                    $"publication authority: {string.Join(", ", missing)}.");
            }
        }
    }
}
