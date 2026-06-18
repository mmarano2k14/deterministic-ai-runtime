using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations;
using Multiplexed.Abstractions.AI.ControlPlane.Execution;
using Multiplexed.Abstractions.AI.ControlPlane.ExecutionAssistance;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Replay;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Control;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Environment;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Identity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue.Redis;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Background;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.AI.Runtime.ControlPlane.Admission;
using Multiplexed.AI.Runtime.ControlPlane.Admission.Reservations;
using Multiplexed.AI.Runtime.ControlPlane.Execution;
using Multiplexed.AI.Runtime.ControlPlane.ExecutionAssistance;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.Replay;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Environment;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Identity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue.Redis;
using Multiplexed.AI.Runtime.ControlPlane.SharedController;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;
using Multiplexed.AI.Runtime.Observability.Logging;

namespace Multiplexed.AI.Runtime.ControlPlane.DI
{
    /// <summary>
    /// Provides dependency injection registration for AI runtime control-plane services.
    /// </summary>
    public static class AiControlPlaneServiceCollectionExtensions
    {
        /// <summary>
        /// Registers AI runtime control-plane services.
        ///
        /// By default, a no-op control-plane observer is registered so the runtime
        /// can operate without logging, metrics, tracing, or ledger exporters.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configureReplay">Optional replay control-plane options configuration.</param>
        /// <param name="configureExecution">Optional execution control-plane options configuration.</param>
        /// <param name="configureRuntimeQueue">Optional local runtime queue control-plane options configuration.</param>
        /// <param name="configureRuntimeInstance">Optional runtime instance control-plane options configuration.</param>
        /// <param name="configureAdmission">Optional run admission options configuration.</param>
        /// <param name="configureSharedController">Optional shared runtime controller options configuration.</param>
        /// <param name="configureSharedQueue">Optional shared queue options configuration.</param>
        /// <param name="configureSharedQueuePump">Optional shared queue pump options configuration.</param>
        /// <param name="configureExecutionAssistance">Optional execution assistance options configuration.</param>
        /// <returns>The same service collection for chaining.</returns>
        public static IServiceCollection AddAiControlPlane(
            this IServiceCollection services,
            Action<AiReplayControlOptions>? configureReplay = null,
            Action<AiExecutionControlPlaneOptions>? configureExecution = null,
            Action<AiRuntimeQueueControlPlaneOptions>? configureRuntimeQueue = null,
            Action<AiRuntimeInstanceControlPlaneOptions>? configureRuntimeInstance = null,
            Action<AiRunAdmissionOptions>? configureAdmission = null,
            Action<AiSharedRuntimeControllerOptions>? configureSharedController = null,
            Action<AiSharedQueueOptions>? configureSharedQueue = null,
            Action<AiSharedQueuePumpOptions>? configureSharedQueuePump = null,
            Action<AiExecutionAssistanceOptions>? configureExecutionAssistance = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            if (configureReplay is null)
            {
                services.AddOptions<AiReplayControlOptions>();
            }
            else
            {
                services.Configure(configureReplay);
            }

            if (configureExecution is null)
            {
                services.AddOptions<AiExecutionControlPlaneOptions>();
            }
            else
            {
                services.Configure(configureExecution);
            }

            if (configureRuntimeQueue is null)
            {
                services.AddOptions<AiRuntimeQueueControlPlaneOptions>();
            }
            else
            {
                services.Configure(configureRuntimeQueue);
            }

            if (configureRuntimeInstance is null)
            {
                services.AddOptions<AiRuntimeInstanceControlPlaneOptions>();
            }
            else
            {
                services.Configure(configureRuntimeInstance);
            }

            if (configureAdmission is null)
            {
                services.AddOptions<AiRunAdmissionOptions>();
            }
            else
            {
                services.Configure(configureAdmission);
            }

            if (configureSharedController is null)
            {
                services.AddOptions<AiSharedRuntimeControllerOptions>();
            }
            else
            {
                services.Configure(configureSharedController);
            }

            if (configureSharedQueue is null)
            {
                services.AddOptions<AiSharedQueueOptions>();
            }
            else
            {
                services.Configure(configureSharedQueue);
            }

            if (configureSharedQueuePump is null)
            {
                services.AddOptions<AiSharedQueuePumpOptions>();
            }
            else
            {
                services.Configure(configureSharedQueuePump);
            }

            if (configureExecutionAssistance is null)
            {
                services.AddOptions<AiExecutionAssistanceOptions>();
            }
            else
            {
                services.Configure(configureExecutionAssistance);
            }

            services.AddOptions<AiRuntimeScaleOutRequestStoreOptions>();
            services.AddOptions<AiRuntimeScaleOutRequestWatcherOptions>();
            services.AddOptions<SimulatedAiRuntimeScaleOutProviderOptions>();
            services.AddOptions<RedisAiRuntimeRunExecutionIndexOptions>();

            services.TryAddSingleton<IAiControlPlaneObserver, NoopAiControlPlaneObserver>();

            services.TryAddSingleton<IAiReplayControlPlane, AiReplayControlPlane>();
            services.TryAddSingleton<IAiExecutionControlPlane, AiExecutionControlPlane>();
            services.TryAddSingleton<IAiRuntimeRunExecutionIndex, InMemoryAiRuntimeRunExecutionIndex>();
            services.TryAddSingleton<IAiRuntimeQueueControlPlane, AiRuntimeQueueControlPlane>();

            services.TryAddSingleton<IAiRuntimeInstanceRegistry, RedisAiRuntimeInstanceRegistry>();
            services.TryAddSingleton<IAiRuntimeInstanceControlPlane, AiRuntimeInstanceControlPlane>();
            services.TryAddSingleton<IAiRuntimeEnvironmentProvider, LocalAiRuntimeEnvironmentProvider>();
            services.TryAddSingleton<IAiSharedRuntimeInstanceRegistry, InMemoryAiSharedRuntimeInstanceRegistry>();

            services.TryAddSingleton<IAiRuntimeAdmissionReservationStore, InMemoryAiRuntimeAdmissionReservationStore>();
            services.TryAddSingleton<IAiRunAdmissionController, AiRunAdmissionController>();

            services.TryAddSingleton<IAiExecutionAssistanceStore, InMemoryAiExecutionAssistanceStore>();
            services.TryAddSingleton<IAiExecutionAssistanceCandidateStore, InMemoryAiExecutionAssistanceCandidateStore>();
            services.TryAddSingleton<IAiExecutionAssistanceController, AiExecutionAssistanceController>();
            services.TryAddSingleton<IAiExecutionAssistanceWorker, AiExecutionAssistanceWorker>();
            services.TryAddSingleton<AiExecutionAssistancePump>();
            services.TryAddSingleton<AiExecutionAssistanceCoordinator>();

            services.TryAddSingleton<IAiSharedRunStore, InMemoryAiSharedRunStore>();
            services.TryAddSingleton<IAiSharedQueue, InMemoryAiSharedQueue>();
            services.TryAddSingleton<IAiSharedRunDispatcher, LocalAiSharedRunDispatcher>();
            services.TryAddSingleton<IAiSharedQueueDispatcher, AiSharedQueueDispatcher>();
            services.TryAddSingleton<IAiSharedQueuePump, AiSharedQueuePump>();

            services.TryAddSingleton<IAiRuntimeScaleOutRequestStore, InMemoryAiRuntimeScaleOutRequestStore>();
            services.TryAddSingleton<IAiRuntimeScaleOutRequestPublisher, StoreBackedAiRuntimeScaleOutRequestPublisher>();
            services.TryAddSingleton<IAiRuntimeScaleOutProviderSelector, AiRuntimeScaleOutProviderSelector>();
            services.TryAddSingleton<IAiScaleOutFulfilledRunRequeueService, AiScaleOutFulfilledRunRequeueService>();
            services.TryAddSingleton<IAiRuntimeScaleOutProvider, SimulatedAiRuntimeScaleOutProvider>();

            services.TryAddSingleton<IAiTenantRuntimeSettingsProvider, HardcodedAiTenantRuntimeSettingsProvider>();
            services.TryAddSingleton<IAiRuntimeInstanceVisibilityEvaluator, AiRuntimeInstanceVisibilityEvaluator>();

            services.TryAddSingleton<IAiSharedRuntimeController, AiSharedRuntimeController>();

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<
                    IAiRuntimeInstanceCapacityStore,
                    RedisAiRuntimeInstanceCapacityStore>());

            services.TryAddSingleton<IAiRuntimeHostIdentity, AiRuntimeHostIdentity>();
            services.TryAddSingleton<IAiControlPlaneHostIdentity, AiControlPlaneHostIdentity>();

            services.AddAiRuntimeInstanceProviders();

            return services;
        }

        /// <summary>
        /// Replaces the default in-memory runtime run execution index with the Redis-backed implementation.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">Optional Redis runtime run execution index options configuration.</param>
        /// <returns>The same service collection for chaining.</returns>
        /// <remarks>
        /// The default control-plane registration keeps <see cref="InMemoryAiRuntimeRunExecutionIndex"/>
        /// for local tests and lightweight hosts.
        ///
        /// Call this method only for Redis-backed control-plane hosts that need the runtime RunId to
        /// ExecutionId bridge to survive across runtime instances, HTTP providers, MCP hosts, and
        /// Kubernetes-like multi-instance execution.
        /// </remarks>
        public static IServiceCollection AddAiRedisRuntimeRunExecutionIndex(
            this IServiceCollection services,
            Action<RedisAiRuntimeRunExecutionIndexOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            if (configure is null)
            {
                services.AddOptions<RedisAiRuntimeRunExecutionIndexOptions>();
            }
            else
            {
                services.Configure(configure);
            }

            services.RemoveAll<IAiRuntimeRunExecutionIndex>();
            services.AddSingleton<IAiRuntimeRunExecutionIndex, RedisAiRuntimeRunExecutionIndex>();

            return services;
        }

        /// <summary>
        /// Registers the shared queue hosted background service.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">Optional background service options configuration.</param>
        /// <returns>The same service collection for chaining.</returns>
        public static IServiceCollection AddAiSharedQueueBackgroundService(
            this IServiceCollection services,
            Action<AiSharedQueueBackgroundServiceOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            if (configure is null)
            {
                services
                    .AddOptions<AiSharedQueueBackgroundServiceOptions>()
                    .Validate(
                        options => options.MaxDispatchesPerCycle > 0,
                        "Shared queue max dispatches per cycle must be positive.")
                    .Validate(
                        options => options.ClaimTtl > TimeSpan.Zero,
                        "Shared queue claim TTL must be positive.")
                    .Validate(
                        options => options.IdleDelay >= TimeSpan.Zero,
                        "Shared queue idle delay must be zero or positive.")
                    .Validate(
                        options => options.ActiveDelay >= TimeSpan.Zero,
                        "Shared queue active delay must be zero or positive.")
                    .Validate(
                        options => options.ErrorDelay > TimeSpan.Zero,
                        "Shared queue error delay must be positive.")
                    .Validate(
                        options => options.RuntimeReadinessPollInterval > TimeSpan.Zero,
                        "Runtime readiness poll interval must be positive.")
                    .Validate(
                        options => options.RuntimeReadinessTimeout is null ||
                            options.RuntimeReadinessTimeout > TimeSpan.Zero,
                        "Runtime readiness timeout must be null or positive.");
            }
            else
            {
                services
                    .AddOptions<AiSharedQueueBackgroundServiceOptions>()
                    .Configure(configure)
                    .Validate(
                        options => options.MaxDispatchesPerCycle > 0,
                        "Shared queue max dispatches per cycle must be positive.")
                    .Validate(
                        options => options.ClaimTtl > TimeSpan.Zero,
                        "Shared queue claim TTL must be positive.")
                    .Validate(
                        options => options.IdleDelay >= TimeSpan.Zero,
                        "Shared queue idle delay must be zero or positive.")
                    .Validate(
                        options => options.ActiveDelay >= TimeSpan.Zero,
                        "Shared queue active delay must be zero or positive.")
                    .Validate(
                        options => options.ErrorDelay > TimeSpan.Zero,
                        "Shared queue error delay must be positive.")
                    .Validate(
                        options => options.RuntimeReadinessPollInterval > TimeSpan.Zero,
                        "Runtime readiness poll interval must be positive.")
                    .Validate(
                        options => options.RuntimeReadinessTimeout is null ||
                            options.RuntimeReadinessTimeout > TimeSpan.Zero,
                        "Runtime readiness timeout must be null or positive.");
            }

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService, AiSharedQueueBackgroundService>());

            return services;
        }

        /// <summary>
        /// Registers the runtime scale-out request watcher hosted service.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">Optional watcher options configuration.</param>
        /// <returns>The same service collection for chaining.</returns>
        /// <remarks>
        /// This method only registers the watcher hosted service.
        /// The watcher remains inactive unless <see cref="AiRuntimeScaleOutRequestWatcherOptions.Enabled" />
        /// is set to <see langword="true" />.
        /// </remarks>
        public static IServiceCollection AddAiRuntimeScaleOutRequestWatcher(
            this IServiceCollection services,
            Action<AiRuntimeScaleOutRequestWatcherOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            if (configure is null)
            {
                services
                    .AddOptions<AiRuntimeScaleOutRequestWatcherOptions>()
                    .Validate(
                        options => options.Interval > TimeSpan.Zero,
                        "Scale-out request watcher interval must be positive.")
                    .Validate(
                        options => options.MaxRequestsPerCycle > 0,
                        "Scale-out request watcher max requests per cycle must be positive.");
            }
            else
            {
                services
                    .AddOptions<AiRuntimeScaleOutRequestWatcherOptions>()
                    .Configure(configure)
                    .Validate(
                        options => options.Interval > TimeSpan.Zero,
                        "Scale-out request watcher interval must be positive.")
                    .Validate(
                        options => options.MaxRequestsPerCycle > 0,
                        "Scale-out request watcher max requests per cycle must be positive.");
            }

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService, AiRuntimeScaleOutRequestWatcherHostedService>());

            return services;
        }

        /// <summary>
        /// Registers the runtime instance registration hosted service.
        ///
        /// This service publishes runtime instance registration and heartbeats
        /// used by MCP tools, dashboards, autoscaling, diagnostics, and future
        /// Kubernetes controllers.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">Optional runtime instance registration options configuration.</param>
        /// <returns>The same service collection for chaining.</returns>
        public static IServiceCollection AddAiRuntimeInstanceRegistrationHostedService(
            this IServiceCollection services,
            Action<AiRuntimeInstanceRegistrationOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            if (configure is null)
            {
                services
                    .AddOptions<AiRuntimeInstanceRegistrationOptions>()
                    .Validate(
                        options => options.HeartbeatInterval > TimeSpan.Zero,
                        "Runtime instance heartbeat interval must be positive.")
                    .Validate(
                        options => options.RegistryTtl > TimeSpan.Zero,
                        "Runtime instance registry TTL must be positive.")
                    .Validate(
                        options => options.CapacityTtl > TimeSpan.Zero,
                        "Runtime instance capacity TTL must be positive.");
            }
            else
            {
                services
                    .AddOptions<AiRuntimeInstanceRegistrationOptions>()
                    .Configure(configure)
                    .Validate(
                        options => options.HeartbeatInterval > TimeSpan.Zero,
                        "Runtime instance heartbeat interval must be positive.")
                    .Validate(
                        options => options.RegistryTtl > TimeSpan.Zero,
                        "Runtime instance registry TTL must be positive.")
                    .Validate(
                        options => options.CapacityTtl > TimeSpan.Zero,
                        "Runtime instance capacity TTL must be positive.");
            }

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService, AiRuntimeInstanceRegistrationHostedService>());

            return services;
        }

        /// <summary>
        /// Enables structured logging for AI control-plane events.
        ///
        /// This replaces the default no-op observer with a logging observer
        /// that forwards control-plane events to the runtime logging layer.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <returns>The same service collection for chaining.</returns>
        public static IServiceCollection AddAiControlPlaneLogging(
            this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.TryAddSingleton<IAiControlPlaneLogger, AiControlPlaneLogger>();

            services.RemoveAll<IAiControlPlaneObserver>();
            services.AddSingleton<IAiControlPlaneObserver, LoggedAiControlPlaneObserver>();

            return services;
        }
    }
}