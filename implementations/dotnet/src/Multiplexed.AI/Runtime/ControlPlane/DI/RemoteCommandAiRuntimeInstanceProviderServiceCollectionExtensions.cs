using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers
{
    /// <summary>
    /// Provides dependency injection registration for the remote command runtime instance provider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// IMPORTANT:
    /// This extension is intentionally separate from <c>AddAiRuntimeInstanceProviders</c>.
    /// </para>
    ///
    /// <para>
    /// The default local runtime host must continue to use:
    /// </para>
    ///
    /// <code>
    /// LocalAiRuntimeInstanceProvider
    ///     -> IAiSharedRuntimeInstanceRegistry
    ///     -> Local runtime instance
    ///     -> Local runtime queue
    /// </code>
    ///
    /// <para>
    /// The remote command provider should only be registered when the runtime instance
    /// is not directly addressable in memory, for example:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item>
    /// <description>MCP/control-plane pod dispatching to another runtime pod.</description>
    /// </item>
    /// <item>
    /// <description>Runtime instance hosted in another process.</description>
    /// </item>
    /// <item>
    /// <description>Runtime instance hosted on another node or machine.</description>
    /// </item>
    /// <item>
    /// <description>Future Redis, HTTP, gRPC, or Kubernetes command transports.</description>
    /// </item>
    /// </list>
    ///
    /// <para>
    /// This provider does not replace local runtime queues. It only sends commands
    /// to the runtime instance that owns its own local queue.
    /// </para>
    /// </remarks>
    public static class RemoteCommandAiRuntimeInstanceProviderServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the remote command runtime instance provider.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This method requires an <see cref="IAiRuntimeInstanceCommandTransport"/>
        /// to be registered in the service collection.
        /// </para>
        ///
        /// <para>
        /// Example future usage:
        /// </para>
        ///
        /// <code>
        /// services.AddAiRedisRuntimeInstanceCommandTransport(...);
        /// services.AddAiRemoteCommandRuntimeInstanceProvider();
        /// </code>
        ///
        /// <para>
        /// Do not call this method in the default local-only runtime host unless a real
        /// command transport has been registered.
        /// </para>
        /// </remarks>
        /// <param name="services">The service collection.</param>
        /// <returns>The same service collection for chaining.</returns>
        public static IServiceCollection AddAiRemoteCommandRuntimeInstanceProvider(
            this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<
                    IAiRuntimeInstanceProvider,
                    RemoteCommandAiRuntimeInstanceProvider>());

            return services;
        }

        /// <summary>
        /// Registers a command transport and the remote command runtime instance provider.
        /// </summary>
        /// <typeparam name="TTransport">
        /// The concrete command transport implementation.
        /// </typeparam>
        /// <remarks>
        /// <para>
        /// This overload is intended for transports that can be registered directly
        /// as a singleton implementation type.
        /// </para>
        ///
        /// <para>
        /// Example:
        /// </para>
        ///
        /// <code>
        /// services.AddAiRemoteCommandRuntimeInstanceProvider&lt;RedisAiRuntimeInstanceCommandTransport&gt;();
        /// </code>
        ///
        /// <para>
        /// The provider is registered as an <see cref="IAiRuntimeInstanceProvider"/>
        /// so it can be discovered by <see cref="IAiRuntimeInstanceProviderRouter"/>.
        /// </para>
        /// </remarks>
        /// <param name="services">The service collection.</param>
        /// <returns>The same service collection for chaining.</returns>
        public static IServiceCollection AddAiRemoteCommandRuntimeInstanceProvider<TTransport>(
            this IServiceCollection services)
            where TTransport : class, IAiRuntimeInstanceCommandTransport
        {
            ArgumentNullException.ThrowIfNull(services);

            services.TryAddSingleton<
                IAiRuntimeInstanceCommandTransport,
                TTransport>();

            services.AddAiRemoteCommandRuntimeInstanceProvider();

            return services;
        }

        /// <summary>
        /// Registers a command transport factory and the remote command runtime instance provider.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Use this overload when the transport needs options, external clients,
        /// Redis connections, HTTP clients, Kubernetes clients, or custom construction.
        /// </para>
        ///
        /// <para>
        /// Example:
        /// </para>
        ///
        /// <code>
        /// services.AddAiRemoteCommandRuntimeInstanceProvider(
        ///     provider =&gt; new RedisAiRuntimeInstanceCommandTransport(...));
        /// </code>
        /// </remarks>
        /// <param name="services">The service collection.</param>
        /// <param name="transportFactory">The command transport factory.</param>
        /// <returns>The same service collection for chaining.</returns>
        public static IServiceCollection AddAiRemoteCommandRuntimeInstanceProvider(
            this IServiceCollection services,
            Func<IServiceProvider, IAiRuntimeInstanceCommandTransport> transportFactory)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(transportFactory);

            services.TryAddSingleton(transportFactory);

            services.AddAiRemoteCommandRuntimeInstanceProvider();

            return services;
        }
    }
}