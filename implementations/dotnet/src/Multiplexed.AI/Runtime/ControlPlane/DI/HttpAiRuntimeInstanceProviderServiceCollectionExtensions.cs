using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Http;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http.ScaleOut;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.SharedInstance;

namespace Multiplexed.AI.Runtime.ControlPlane.DI
{
    /// <summary>
    /// Provides dependency injection registration for the HTTP runtime instance provider and runtime-side HTTP command handling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// IMPORTANT:
    /// The HTTP runtime instance provider registration is intentionally opt-in and must not be part of the default
    /// local runtime instance provider registration.
    /// </para>
    ///
    /// <para>
    /// The default local runtime host should continue to use:
    /// </para>
    ///
    /// <code>
    /// provider.name = local
    /// LocalAiRuntimeInstanceProvider
    /// </code>
    ///
    /// <para>
    /// The HTTP provider should only be registered when the control plane dispatches to runtime instances
    /// addressable through HTTP endpoints.
    /// </para>
    ///
    /// <para>
    /// Runtime-side HTTP command handling is separated from the control-plane HTTP provider registration so
    /// <c>RuntimeInstanceOnly</c> hosts can expose:
    /// </para>
    ///
    /// <code>
    /// POST /runtime-instance/commands
    /// </code>
    ///
    /// <para>
    /// without also registering control-plane HTTP dispatch provider or scale-out provider services.
    /// </para>
    /// </remarks>
    public static class HttpAiRuntimeInstanceProviderServiceCollectionExtensions
    {
        /// <summary>
        /// Defines the configuration section used by the HTTP runtime instance provider hardening options.
        /// </summary>
        private const string OptionsSectionName =
            "AiHttpRuntimeInstanceProvider";

        /// <summary>
        /// Defines the configuration section used by the HTTP runtime scale-out technical options.
        /// </summary>
        private const string ScaleOutOptionsSectionName =
            "AiHttpRuntimeScaleOut";

        /// <summary>
        /// Registers the HTTP runtime instance provider as an opt-in control-plane runtime instance provider.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This method registers <see cref="HttpAiRuntimeInstanceProvider"/> with
        /// <see cref="HttpClient"/> support and exposes it as an
        /// <see cref="IAiRuntimeInstanceProvider"/>.
        /// </para>
        ///
        /// <para>
        /// This method binds <see cref="AiHttpRuntimeInstanceProviderOptions"/>
        /// from the <c>AiHttpRuntimeInstanceProvider</c> configuration section.
        /// Supported hardening settings include dispatch timeout, retry behavior,
        /// timeout retry policy, and circuit breaker settings.
        /// </para>
        ///
        /// <para>
        /// This method also binds <see cref="AiHttpRuntimeScaleOutOptions"/>
        /// from the <c>AiHttpRuntimeScaleOut</c> configuration section.
        /// HTTP scale-out options are technical provider defaults only. Tenant-aware
        /// runtime settings must be resolved earlier by admission and carried through
        /// the scale-out provider request.
        /// </para>
        ///
        /// <para>
        /// This method also calls <see cref="AddAiRuntimeInstanceHttpCommandHandling(IServiceCollection)"/>
        /// so HTTP fixture/runtime scenarios that host the command endpoint in the same service collection
        /// continue to work.
        /// </para>
        ///
        /// <para>
        /// The provider router can then resolve descriptors with:
        /// </para>
        ///
        /// <code>
        /// provider.name = http
        /// </code>
        ///
        /// <para>
        /// Example:
        /// </para>
        ///
        /// <code>
        /// services.AddAiRuntimeInstanceProviders();
        /// services.AddAiHttpRuntimeInstanceProvider();
        /// </code>
        ///
        /// <para>
        /// The enumerable registration intentionally uses an implementation type
        /// instead of a factory so <c>TryAddEnumerable</c> can distinguish this provider
        /// from other <see cref="IAiRuntimeInstanceProvider"/> registrations.
        /// </para>
        /// </remarks>
        /// <param name="services">The service collection.</param>
        /// <returns>The same service collection for chaining.</returns>
        public static IServiceCollection AddAiHttpRuntimeInstanceProvider(
            this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            services
                .AddOptions<AiHttpRuntimeInstanceProviderOptions>()
                .BindConfiguration(OptionsSectionName);

            services
                .AddOptions<AiHttpRuntimeScaleOutOptions>()
                .BindConfiguration(ScaleOutOptionsSectionName);

            services.AddHttpClient<HttpAiRuntimeInstanceProvider>();

            services.TryAddSingleton<
                IAiHttpRuntimeScaleOutProvisioner,
                AiHttpRuntimeScaleOutProvisioner>();

            services.TryAddEnumerable(
                ServiceDescriptor.Transient<
                    IAiRuntimeInstanceProvider,
                    HttpAiRuntimeInstanceProvider>());

            services.AddAiRuntimeInstanceHttpCommandHandling();

            return services;
        }

        /// <summary>
        /// Registers runtime-side HTTP command handling services.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This method is intended for runtime hosts that expose:
        /// </para>
        ///
        /// <code>
        /// POST /runtime-instance/commands
        /// </code>
        ///
        /// <para>
        /// without registering the control-plane HTTP provider, scale-out provisioner, or HTTP dispatch provider.
        /// This is required by <c>RuntimeInstanceOnly</c> process hosts.
        /// </para>
        ///
        /// <para>
        /// The command handler dispatches incoming HTTP commands to the local runtime instance abstraction owned
        /// by the runtime process.
        /// </para>
        /// </remarks>
        /// <param name="services">The service collection.</param>
        /// <returns>The same service collection for chaining.</returns>
        public static IServiceCollection AddAiRuntimeInstanceHttpCommandHandling(
            this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.TryAddSingleton<IAiSharedRuntimeInstance>(serviceProvider =>
            {
                var options =
                    serviceProvider
                        .GetRequiredService<IOptions<AiRuntimeInstanceRegistrationOptions>>()
                        .Value;

                var runtimeInstanceId =
                    !string.IsNullOrWhiteSpace(options.RuntimeInstanceId)
                        ? options.RuntimeInstanceId
                        : "runtime-http-1";

                return ActivatorUtilities.CreateInstance<LocalAiSharedRuntimeInstance>(
                    serviceProvider,
                    runtimeInstanceId);
            });

            services.TryAddSingleton<AiRuntimeInstanceHttpCommandHandler>();

            services.TryAddSingleton<IAiRuntimeInstanceHttpCommandHandler>(
                serviceProvider =>
                    serviceProvider.GetRequiredService<AiRuntimeInstanceHttpCommandHandler>());

            return services;
        }
    }
}