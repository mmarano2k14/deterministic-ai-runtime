using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc.ScaleOut;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.SharedInstance;

namespace Multiplexed.AI.Runtime.ControlPlane.DI
{
    /// <summary>
    /// Provides dependency injection registration for the gRPC runtime instance provider and runtime-side gRPC command handling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// IMPORTANT:
    /// The gRPC runtime instance provider registration is intentionally opt-in and must not be part of the default
    /// local runtime instance provider registration.
    /// </para>
    ///
    /// <para>
    /// Runtime-side gRPC command handling is separated from the control-plane gRPC provider registration so
    /// <c>RuntimeInstanceOnly</c> hosts can expose the gRPC command service without also registering
    /// control-plane gRPC dispatch provider or scale-out provider services.
    /// </para>
    /// </remarks>
    public static class GrpcRuntimeInstanceProviderServiceCollectionExtensions
    {
        /// <summary>
        /// Defines the configuration section used by the gRPC runtime instance provider options.
        /// </summary>
        private const string OptionsSectionName =
            "AiGrpcRuntimeInstanceProvider";

        /// <summary>
        /// Defines the configuration section used by the gRPC runtime scale-out technical options.
        /// </summary>
        private const string ScaleOutOptionsSectionName =
            "AiGrpcRuntimeScaleOut";

        /// <summary>
        /// Registers the gRPC runtime instance provider as an opt-in control-plane runtime instance provider.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This method registers the gRPC provider for control-plane dispatch/status/control resolution.
        /// </para>
        ///
        /// <para>
        /// This method intentionally does not register <see cref="IAiRuntimeScaleOutProvider" />.
        /// Scale-out registration is separated into <see cref="AddAiGrpcRuntimeInstanceScaleOutProvider(IServiceCollection)" />
        /// to avoid breaking HTTP scenarios through DI provider collisions.
        /// </para>
        /// </remarks>
        /// <param name="services">The service collection.</param>
        /// <returns>The same service collection for chaining.</returns>
        public static IServiceCollection AddAiGrpcRuntimeInstanceProvider(
            this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            services
                .AddOptions<AiGrpcRuntimeInstanceProviderOptions>()
                .BindConfiguration(OptionsSectionName);

            services
                .AddOptions<AiGrpcRuntimeScaleOutOptions>()
                .BindConfiguration(ScaleOutOptionsSectionName);

            services.TryAddSingleton<
                IAiTenantRuntimeSettingsProvider,
                HardcodedAiTenantRuntimeSettingsProvider>();

            services.TryAddSingleton<
                IAiGrpcRuntimeScaleOutProvisioner,
                AiGrpcRuntimeScaleOutProvisioner>();

            services.TryAddTransient<AiGrpcRuntimeInstanceProvider>();

            services.TryAddEnumerable(
                ServiceDescriptor.Transient<
                    IAiRuntimeInstanceProvider,
                    AiGrpcRuntimeInstanceProvider>());

            services.AddAiRuntimeInstanceGrpcCommandHandling();

            return services;
        }

        /// <summary>
        /// Registers the gRPC runtime instance provider as a scale-out provider.
        /// </summary>
        /// <remarks>
        /// Use this method only in gRPC-specific control-plane scenarios that need gRPC scale-out.
        /// Do not call this method from HTTP control-plane host registration.
        /// </remarks>
        /// <param name="services">The service collection.</param>
        /// <returns>The same service collection for chaining.</returns>
        public static IServiceCollection AddAiGrpcRuntimeInstanceScaleOutProvider(
            this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.AddAiGrpcRuntimeInstanceProvider();

            services.TryAddEnumerable(
                ServiceDescriptor.Transient<
                    IAiRuntimeScaleOutProvider,
                    AiGrpcRuntimeInstanceProvider>());

            return services;
        }

        /// <summary>
        /// Registers runtime-side gRPC command handling services.
        /// </summary>
        /// <remarks>
        /// This method is intended for runtime hosts that expose the gRPC runtime command service without registering
        /// the control-plane gRPC provider or scale-out provider.
        /// </remarks>
        /// <param name="services">The service collection.</param>
        /// <returns>The same service collection for chaining.</returns>
        public static IServiceCollection AddAiRuntimeInstanceGrpcCommandHandling(
            this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.AddGrpc();

            services.TryAddSingleton<IAiSharedRuntimeInstance>(serviceProvider =>
            {
                var options =
                    serviceProvider
                        .GetRequiredService<IOptions<AiRuntimeInstanceRegistrationOptions>>()
                        .Value;

                var runtimeInstanceId =
                    !string.IsNullOrWhiteSpace(options.RuntimeInstanceId)
                        ? options.RuntimeInstanceId
                        : "runtime-grpc-1";

                return ActivatorUtilities.CreateInstance<LocalAiSharedRuntimeInstance>(
                    serviceProvider,
                    runtimeInstanceId);
            });

            return services;
        }

        /// <summary>
        /// Adds the gRPC runtime instance transport services used by runtime hosts.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <returns>The same service collection for chaining.</returns>
        public static IServiceCollection AddGrpcRuntimeInstanceTransport(
            this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            return services.AddAiRuntimeInstanceGrpcCommandHandling();
        }
    }
}