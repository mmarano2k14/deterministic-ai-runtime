using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;

namespace Multiplexed.AI.Runtime.ControlPlane.DI
{
    /// <summary>
    /// Provides additive, opt-in dependency-injection registration for the process-host Runtime
    /// Pool Manager.
    /// </summary>
    public static class AiRuntimeProcessPoolServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the production process-host Runtime Pool Manager and its hosted lifecycle.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="poolOptions">The fixed process-pool lifecycle options.</param>
        /// <param name="runtimeInstanceOptions">
        /// The RuntimeInstanceOnly child launch and readiness options.
        /// </param>
        /// <returns>The same service collection for chaining.</returns>
        /// <remarks>
        /// This method is deliberately not called by <c>AddAiControlPlane</c>. Existing Process and
        /// Kubernetes hosting behavior therefore remains unchanged unless the application calls
        /// this extension explicitly.
        ///
        /// Register custom implementations of the child factory, process launcher, start-plan
        /// factory, or port allocator before calling this method to replace the production defaults.
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// Thrown when any argument is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when either option model is invalid.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the pool is disabled or has already been registered.
        /// </exception>
        public static IServiceCollection AddAiRuntimeProcessPool(
            this IServiceCollection services,
            AiRuntimeProcessPoolOptions poolOptions,
            AiRuntimeProcessPoolRuntimeInstanceOptions runtimeInstanceOptions)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(poolOptions);
            ArgumentNullException.ThrowIfNull(runtimeInstanceOptions);

            AiRuntimeProcessPoolOptionsValidator.Validate(poolOptions);
            AiRuntimeProcessPoolRuntimeInstanceOptionsValidator.Validate(
                runtimeInstanceOptions);

            if (!poolOptions.Enabled)
            {
                throw new InvalidOperationException(
                    "AddAiRuntimeProcessPool requires AiRuntimeProcessPoolOptions.Enabled to be true.");
            }

            if (services.Any(
                    descriptor =>
                        descriptor.ServiceType ==
                        typeof(IAiRuntimeProcessPoolManager)))
            {
                throw new InvalidOperationException(
                    "The process-host Runtime Pool Manager has already been registered.");
            }

            var immutablePoolOptions = CopyPoolOptions(poolOptions);
            var immutableRuntimeOptions =
                CopyRuntimeInstanceOptions(runtimeInstanceOptions);

            services.AddLogging();

            services.TryAddSingleton<
                IAiRuntimeProcessPoolPortAllocator,
                AiRuntimeProcessPoolPortAllocator>();

            services.TryAddSingleton<
                IAiRuntimeProcessPoolChildProcessLauncher,
                SystemAiRuntimeProcessPoolChildProcessLauncher>();

            services.TryAddSingleton<
                IAiRuntimeProcessPoolRuntimeInstanceStartPlanFactory>(
                serviceProvider =>
                    new AiRuntimeProcessPoolRuntimeInstanceStartPlanFactory(
                        immutableRuntimeOptions,
                        serviceProvider.GetRequiredService<
                            IAiRuntimeProcessPoolPortAllocator>()));

            services.TryAddSingleton<IAiRuntimeProcessPoolChildFactory>(
                serviceProvider =>
                    new RuntimeInstanceOnlyAiRuntimeProcessPoolChildFactory(
                        serviceProvider.GetRequiredService<
                            IAiRuntimeProcessPoolRuntimeInstanceStartPlanFactory>(),
                        serviceProvider.GetRequiredService<
                            IAiRuntimeProcessPoolChildProcessLauncher>(),
                        serviceProvider.GetRequiredService<
                            Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Readiness.IAiRuntimeInstanceReadinessWaiter>()));

            services.AddSingleton<IAiRuntimeProcessPoolManager>(
                serviceProvider =>
                    new AiRuntimeProcessPoolManager(
                        immutablePoolOptions,
                        serviceProvider.GetRequiredService<
                            IAiRuntimeProcessPoolChildFactory>()));

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<
                    IHostedService,
                    AiRuntimeProcessPoolHostedService>());

            return services;
        }

        /// <summary>
        /// Copies mutable process-pool options so later caller mutation cannot change production
        /// composition.
        /// </summary>
        /// <param name="options">The source process-pool options.</param>
        /// <returns>An isolated options copy.</returns>
        private static AiRuntimeProcessPoolOptions CopyPoolOptions(
            AiRuntimeProcessPoolOptions options)
        {
            return new AiRuntimeProcessPoolOptions
            {
                Enabled = options.Enabled,
                PoolId = options.PoolId,
                HostIdPrefix = options.HostIdPrefix,
                RuntimeInstanceIdPrefix = options.RuntimeInstanceIdPrefix,
                InitialProcessCount = options.InitialProcessCount,
                MinimumProcessCount = options.MinimumProcessCount,
                MaximumProcessCount = options.MaximumProcessCount,
                StartupParallelism = options.StartupParallelism,
                ShutdownTimeoutSeconds = options.ShutdownTimeoutSeconds
            };
        }

        /// <summary>
        /// Copies mutable RuntimeInstanceOnly child options so later caller mutation cannot change
        /// production composition.
        /// </summary>
        /// <param name="options">The source runtime instance options.</param>
        /// <returns>An isolated options copy.</returns>
        private static AiRuntimeProcessPoolRuntimeInstanceOptions
            CopyRuntimeInstanceOptions(
                AiRuntimeProcessPoolRuntimeInstanceOptions options)
        {
            return new AiRuntimeProcessPoolRuntimeInstanceOptions
            {
                DotnetExecutablePath = options.DotnetExecutablePath,
                RuntimeHostAssemblyPath = options.RuntimeHostAssemblyPath,
                WorkingDirectory = options.WorkingDirectory,
                BasePort = options.BasePort,
                MaxPort = options.MaxPort,
                EndpointHost = options.EndpointHost,
                ControlPlaneId = options.ControlPlaneId,
                EnableControlPlaneDiscovery =
                    options.EnableControlPlaneDiscovery,
                RequireControlPlaneDiscovery =
                    options.RequireControlPlaneDiscovery,
                DiscoveryResolutionTimeout =
                    options.DiscoveryResolutionTimeout,
                DiscoveryResolutionPollInterval =
                    options.DiscoveryResolutionPollInterval,
                ExecutionContextSnapshot = options.ExecutionContextSnapshot,
                ProviderName = options.ProviderName,
                TransportName = options.TransportName,
                RuntimeVersion = options.RuntimeVersion,
                WorkerCountPerInstance = options.WorkerCountPerInstance,
                MaxConcurrentRunsPerInstance =
                    options.MaxConcurrentRunsPerInstance,
                LocalQueueCapacity = options.LocalQueueCapacity,
                IsolationMode = options.IsolationMode,
                PreferDedicatedCapacity = options.PreferDedicatedCapacity,
                AllowSharedFallback = options.AllowSharedFallback,
                StartupTimeout = options.StartupTimeout,
                ReadinessPollInterval = options.ReadinessPollInterval,
                HeartbeatInterval = options.HeartbeatInterval,
                RedirectOutput = options.RedirectOutput,
                CreateNoWindow = options.CreateNoWindow,
                KillEntireProcessTreeOnStop =
                    options.KillEntireProcessTreeOnStop,
                StopTimeoutSeconds = options.StopTimeoutSeconds,
                EnvironmentVariables =
                    new Dictionary<string, string>(
                        options.EnvironmentVariables,
                        StringComparer.OrdinalIgnoreCase)
            };
        }
    }
}
