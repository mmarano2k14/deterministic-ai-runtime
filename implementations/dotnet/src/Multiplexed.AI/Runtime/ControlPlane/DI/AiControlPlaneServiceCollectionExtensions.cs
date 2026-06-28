using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.Execution;
using Multiplexed.Abstractions.AI.ControlPlane.ExecutionAssistance;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Replay;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Control;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Environment;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Health;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Identity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue.Redis;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Background;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.Admission;
using Multiplexed.AI.Runtime.ControlPlane.Admission.Reservations;
using Multiplexed.AI.Runtime.ControlPlane.Execution;
using Multiplexed.AI.Runtime.ControlPlane.ExecutionAssistance;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.Replay;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Environment;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Health;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Process;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Identity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue.Redis;
using Multiplexed.AI.Runtime.ControlPlane.SharedController;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Ownership;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue;
using Multiplexed.AI.Runtime.Observability.Logging;
using StackExchange.Redis;

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
        /// <param name="configureRuntimeInstanceHealthReconciliation">Optional runtime instance health reconciliation options configuration.</param>
        /// <param name="configureRuntimeExecutionRecoveryReconciliation">Optional runtime execution recovery reconciliation options configuration.</param>
        /// <param name="configureAdmission">Optional run admission options configuration.</param>
        /// <param name="configureSharedController">Optional shared runtime controller options configuration.</param>
        /// <param name="configureSharedQueue">Optional shared queue options configuration.</param>
        /// <param name="configureSharedQueuePump">Optional shared queue pump options configuration.</param>
        /// <param name="configureExecutionAssistance">Optional execution assistance options configuration.</param>
        /// <param name="configuration">Optional configuration used to bind runtime host creation options.</param>
        /// <returns>The same service collection for chaining.</returns>
        public static IServiceCollection AddAiControlPlane(
            this IServiceCollection services,
            Action<AiReplayControlOptions>? configureReplay = null,
            Action<AiExecutionControlPlaneOptions>? configureExecution = null,
            Action<AiRuntimeQueueControlPlaneOptions>? configureRuntimeQueue = null,
            Action<AiRuntimeInstanceControlPlaneOptions>? configureRuntimeInstance = null,
            Action<AiRuntimeInstanceHealthReconciliationOptions>? configureRuntimeInstanceHealthReconciliation = null,
            Action<AiRuntimeExecutionRecoveryReconciliationOptions>? configureRuntimeExecutionRecoveryReconciliation = null,
            Action<AiRunAdmissionOptions>? configureAdmission = null,
            Action<AiSharedRuntimeControllerOptions>? configureSharedController = null,
            Action<AiSharedQueueOptions>? configureSharedQueue = null,
            Action<AiSharedQueuePumpOptions>? configureSharedQueuePump = null,
            Action<AiExecutionAssistanceOptions>? configureExecutionAssistance = null,
            IConfiguration? configuration = null)
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

            services.AddAiRuntimeInstanceHealthReconciliation(
                configureRuntimeInstanceHealthReconciliation);

            services.AddAiRuntimeExecutionRecoveryReconciliation(
                configureRuntimeExecutionRecoveryReconciliation);

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

            if (configuration is null)
            {
                services.AddOptions<AiRuntimeProcessHostCreationOptions>();
            }
            else
            {
                services.Configure<AiRuntimeProcessHostCreationOptions>(
                    configuration.GetSection("AiRuntimeProcessHostCreation"));
            }

            services.AddOptions<AiRuntimeScaleOutRequestStoreOptions>();
            services.AddOptions<AiRuntimeScaleOutRequestWatcherOptions>();
            services.AddOptions<SimulatedAiRuntimeScaleOutProviderOptions>();
            services.AddOptions<RedisAiRuntimeRunExecutionIndexOptions>();

            services.TryAddSingleton<IAiControlPlaneObserver, CompositeAiControlPlaneObserver>();
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IAiControlPlaneEventSink, RuntimeObservabilityAiControlPlaneEventSink>());

            services.TryAddSingleton<IAiReplayControlPlane, AiReplayControlPlane>();
            services.TryAddSingleton<IAiExecutionControlPlane, AiExecutionControlPlane>();
            services.TryAddSingleton<IAiRuntimeRunExecutionIndex, InMemoryAiRuntimeRunExecutionIndex>();
            services.TryAddSingleton<IAiRuntimeQueueControlPlane, AiRuntimeQueueControlPlane>();

            services.TryAddSingleton<IAiRuntimeInstanceRegistry>(serviceProvider =>
            {
                var redis =
                    serviceProvider.GetRequiredService<IConnectionMultiplexer>();

                var registrationOptions =
                    serviceProvider.GetRequiredService<IOptions<AiRuntimeInstanceRegistrationOptions>>();

                var controlPlaneIdResolver =
                    serviceProvider.GetRequiredService<IAiControlPlaneIdResolver>();

                var visibilityEvaluator =
                    serviceProvider.GetRequiredService<IAiRuntimeInstanceVisibilityEvaluator>();

                var executionContextSnapshotProvider =
                    serviceProvider.GetService<IExecutionContextSnapshotProvider>();

                return new RedisAiRuntimeInstanceRegistry(
                    redis,
                    registrationOptions,
                    controlPlaneIdResolver,
                    visibilityEvaluator,
                    executionContextSnapshotProvider);
            });

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
            services.TryAddSingleton<IAiSharedRunOwnershipResolver, AiSharedRunOwnershipResolver>();
            services.TryAddSingleton<IAiSharedRunDispatcher, LocalAiSharedRunDispatcher>();
            services.TryAddSingleton<IAiSharedQueueDispatcher, AiSharedQueueDispatcher>();
            services.TryAddSingleton<IAiSharedQueuePump, AiSharedQueuePump>();

            services.TryAddSingleton<IAiRuntimeScaleOutRequestStore, InMemoryAiRuntimeScaleOutRequestStore>();
            services.TryAddSingleton<IAiRuntimeScaleOutRequestPublisher, StoreBackedAiRuntimeScaleOutRequestPublisher>();
            services.TryAddSingleton<IAiRuntimeScaleOutProviderSelector, AiRuntimeScaleOutProviderSelector>();
            services.TryAddSingleton<IAiScaleOutFulfilledRunRequeueService, AiScaleOutFulfilledRunRequeueService>();
            services.TryAddSingleton<IAiRuntimeScaleOutProvider, SimulatedAiRuntimeScaleOutProvider>();

            if (configuration is null)
            {
                services.TryAddSingleton<IAiTenantRuntimeSettingsProvider, HardcodedAiTenantRuntimeSettingsProvider>();
            }
            else
            {
                ConfigureTenantRuntimeSettingsProvider(services, configuration);
            }

            services.TryAddSingleton<IAiRuntimeInstanceVisibilityEvaluator, AiRuntimeInstanceVisibilityEvaluator>();

            services.TryAddSingleton<IAiSharedRuntimeController, AiSharedRuntimeController>();

            services.TryAddSingleton<IAiRuntimeInstanceCapacityStore>(serviceProvider =>
            {
                var redis =
                    serviceProvider.GetRequiredService<IConnectionMultiplexer>();

                var registrationOptions =
                    serviceProvider.GetRequiredService<IOptions<AiRuntimeInstanceRegistrationOptions>>();

                var controlPlaneIdResolver =
                    serviceProvider.GetRequiredService<IAiControlPlaneIdResolver>();

                var visibilityEvaluator =
                    serviceProvider.GetRequiredService<IAiRuntimeInstanceVisibilityEvaluator>();

                var executionContextSnapshotProvider =
                    serviceProvider.GetService<IExecutionContextSnapshotProvider>();

                return new RedisAiRuntimeInstanceCapacityStore(
                    redis,
                    registrationOptions,
                    controlPlaneIdResolver,
                    visibilityEvaluator,
                    executionContextSnapshotProvider);
            });

            services.TryAddSingleton<IAiRuntimeHostIdentity, AiRuntimeHostIdentity>();
            services.TryAddSingleton<IAiControlPlaneHostIdentity, AiControlPlaneHostIdentity>();

            if (configuration is null)
            {
                services.AddAiRuntimeInstanceProviders();
            }
            else
            {
                services.AddAiRuntimeInstanceProviders(configuration);
            }

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
        /// Registers runtime instance health reconciliation services.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">Optional runtime instance health reconciliation options configuration.</param>
        /// <returns>The same service collection for chaining.</returns>
        /// <remarks>
        /// This method registers the pure health reconciler service only.
        /// It does not register a hosted service, timer loop, execution recovery,
        /// run requeue, host restart, process kill, or dead-letter queue behavior.
        /// </remarks>
        public static IServiceCollection AddAiRuntimeInstanceHealthReconciliation(
            this IServiceCollection services,
            Action<AiRuntimeInstanceHealthReconciliationOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            if (configure is null)
            {
                services.AddOptions<AiRuntimeInstanceHealthReconciliationOptions>();
            }
            else
            {
                services.Configure(configure);
            }

            services.TryAddSingleton<IAiRuntimeInstanceHealthReconciler, AiRuntimeInstanceHealthReconciler>();

            return services;
        }

        /// <summary>
        /// Registers runtime execution recovery reconciliation services.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">Optional runtime execution recovery reconciliation options configuration.</param>
        /// <returns>The same service collection for chaining.</returns>
        /// <remarks>
        /// This method registers the runtime execution recovery reconciler only.
        /// It does not register a hosted service, does not requeue unfinished runs by default,
        /// does not own runtime health detection, and does not manage provider or host lifecycle.
        /// </remarks>
        public static IServiceCollection AddAiRuntimeExecutionRecoveryReconciliation(
            this IServiceCollection services,
            Action<AiRuntimeExecutionRecoveryReconciliationOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            if (configure is null)
            {
                services.AddOptions<AiRuntimeExecutionRecoveryReconciliationOptions>();
            }
            else
            {
                services.Configure(configure);
            }

            services.TryAddSingleton<IAiSharedRunStore, InMemoryAiSharedRunStore>();
            services.TryAddSingleton<IAiSharedQueue, InMemoryAiSharedQueue>();
            services.TryAddSingleton<IAiSharedRunOwnershipResolver, AiSharedRunOwnershipResolver>();
            services.TryAddSingleton<IAiRuntimeExecutionRecoveryTransitionService, AiRuntimeExecutionRecoveryTransitionService>();
            services.TryAddSingleton<IAiRuntimeExecutionRecoveryReconciler, AiRuntimeExecutionRecoveryReconciler>();

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
        /// Registers the runtime instance health reconciler hosted service.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">Optional hosted service options configuration.</param>
        /// <returns>The same service collection for chaining.</returns>
        /// <remarks>
        /// This method registers only the hosted health reconciliation loop.
        /// The hosted service remains inactive unless <see cref="AiRuntimeInstanceHealthReconcilerHostedServiceOptions.Enabled" />
        /// is set to <see langword="true" />.
        /// </remarks>
        public static IServiceCollection AddAiRuntimeInstanceHealthReconcilerHostedService(
            this IServiceCollection services,
            Action<AiRuntimeInstanceHealthReconcilerHostedServiceOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            if (configure is null)
            {
                services
                    .AddOptions<AiRuntimeInstanceHealthReconcilerHostedServiceOptions>()
                    .Validate(
                        options => options.Interval > TimeSpan.Zero,
                        "Runtime instance health reconciler interval must be positive.")
                    .Validate(
                        options => options.ErrorDelay > TimeSpan.Zero,
                        "Runtime instance health reconciler error delay must be positive.");
            }
            else
            {
                services
                    .AddOptions<AiRuntimeInstanceHealthReconcilerHostedServiceOptions>()
                    .Configure(configure)
                    .Validate(
                        options => options.Interval > TimeSpan.Zero,
                        "Runtime instance health reconciler interval must be positive.")
                    .Validate(
                        options => options.ErrorDelay > TimeSpan.Zero,
                        "Runtime instance health reconciler error delay must be positive.");
            }

            services.AddAiRuntimeInstanceHealthReconciliation();

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService, AiRuntimeInstanceHealthReconcilerHostedService>());

            return services;
        }

        /// <summary>
        /// Enables structured logging for AI control-plane events.
        ///
        /// This adds a logging sink to the composite control-plane observer
        /// so control-plane events are forwarded to the runtime logging layer
        /// without replacing other observability sinks.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <returns>The same service collection for chaining.</returns>
        public static IServiceCollection AddAiControlPlaneLogging(
            this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.TryAddSingleton<IAiControlPlaneLogger, AiControlPlaneLogger>();

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IAiControlPlaneEventSink, LoggingAiControlPlaneEventSink>());

            return services;
        }

        /// <summary>
        /// Configures the tenant runtime settings provider.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configuration">The application configuration.</param>
        private static void ConfigureTenantRuntimeSettingsProvider(
            IServiceCollection services,
            IConfiguration configuration)
        {
            services.RemoveAll<IAiTenantRuntimeSettingsProvider>();

            var provider =
                configuration["AiTenantRuntimeSettings:Provider"];

            if (string.Equals(provider, "Configuration", StringComparison.OrdinalIgnoreCase))
            {
                services.AddSingleton<IAiTenantRuntimeSettingsProvider, ConfigurationAiTenantRuntimeSettingsProvider>();
                return;
            }

            services.AddSingleton<IAiTenantRuntimeSettingsProvider, HardcodedAiTenantRuntimeSettingsProvider>();
        }
    }
}