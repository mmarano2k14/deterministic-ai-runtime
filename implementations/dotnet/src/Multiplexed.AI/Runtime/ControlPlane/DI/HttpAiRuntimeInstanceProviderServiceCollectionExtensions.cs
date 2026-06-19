using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Http;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.SharedInstance;

namespace Multiplexed.AI.Runtime.ControlPlane.DI
{
    /// <summary>
    /// Provides dependency injection registration for the HTTP runtime instance provider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// IMPORTANT:
    /// This registration is intentionally opt-in and must not be part of the default
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
    /// The HTTP provider should only be registered when runtime instances are
    /// addressable through HTTP endpoints.
    /// </para>
    ///
    /// <para>
    /// This provider is selected when a runtime instance capacity descriptor contains:
    /// </para>
    ///
    /// <code>
    /// provider.name = http
    /// transport.endpoint = http://runtime-instance-1:8080
    /// </code>
    ///
    /// <para>
    /// The HTTP provider does not replace local runtime queues. It sends commands to
    /// the runtime instance that owns its own local queue, worker pool, and DAG engine.
    /// </para>
    /// </remarks>
    public static class HttpAiRuntimeInstanceProviderServiceCollectionExtensions
    {
        /// <summary>
        /// Defines the configuration section used by the HTTP runtime instance provider options.
        /// </summary>
        private const string OptionsSectionName =
            "AiHttpRuntimeInstanceProvider";

        /// <summary>
        /// Registers the HTTP runtime instance provider as an opt-in runtime instance provider.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This method registers <see cref="HttpAiRuntimeInstanceProvider"/> with
        /// <see cref="HttpClient"/> support and exposes it as an
        /// <see cref="IAiRuntimeInstanceProvider"/>.
        /// </para>
        ///
        /// <para>
        /// This method also binds <see cref="AiHttpRuntimeInstanceProviderOptions"/>
        /// from the <c>AiHttpRuntimeInstanceProvider</c> configuration section.
        /// </para>
        ///
        /// <para>
        /// Supported hardening settings include dispatch timeout, retry behavior,
        /// timeout retry policy, and circuit breaker settings.
        /// </para>
        ///
        /// <para>
        /// This method also registers the runtime-side HTTP command handler:
        /// </para>
        ///
        /// <code>
        /// IAiRuntimeInstanceHttpCommandHandler
        ///     -> AiRuntimeInstanceHttpCommandHandler
        /// </code>
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

            services.AddHttpClient<HttpAiRuntimeInstanceProvider>();

            services.TryAddEnumerable(
                ServiceDescriptor.Transient<
                    IAiRuntimeInstanceProvider,
                    HttpAiRuntimeInstanceProvider>());

            services.TryAddSingleton<IAiSharedRuntimeInstance>(serviceProvider =>
            {
                var options = serviceProvider
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

            services.TryAddSingleton<
                AiRuntimeInstanceHttpCommandHandler>();

            services.TryAddSingleton<
                IAiRuntimeInstanceHttpCommandHandler>(
                    serviceProvider =>
                        serviceProvider.GetRequiredService<AiRuntimeInstanceHttpCommandHandler>());

            return services;
        }
    }
}