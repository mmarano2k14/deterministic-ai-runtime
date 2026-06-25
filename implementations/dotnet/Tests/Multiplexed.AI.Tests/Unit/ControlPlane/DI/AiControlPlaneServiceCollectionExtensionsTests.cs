using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Replay;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Control;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Health;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Health;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue;
using Multiplexed.AI.Runtime.ControlPlane.SharedController;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue;
using Multiplexed.AI.Runtime.Observability.Logging;
using StackExchange.Redis;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.DI
{
    public sealed class AiControlPlaneServiceCollectionExtensionsTests
    {
        [Fact]
        public void AddAiControlPlane_Should_Register_Noop_Observer_By_Default()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiControlPlane();

            using var provider = services.BuildServiceProvider();

            var observer = provider.GetRequiredService<IAiControlPlaneObserver>();

            Assert.IsType<NoopAiControlPlaneObserver>(observer);
        }

        [Fact]
        public void AddAiControlPlaneLogging_Should_Replace_Noop_Observer_With_Logged_Observer()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services
                .AddAiControlPlane()
                .AddAiControlPlaneLogging();

            using var provider = services.BuildServiceProvider();

            var observer = provider.GetRequiredService<IAiControlPlaneObserver>();
            var logger = provider.GetRequiredService<IAiControlPlaneLogger>();

            Assert.IsType<LoggedAiControlPlaneObserver>(observer);
            Assert.IsType<AiControlPlaneLogger>(logger);
        }

        [Fact]
        public void AddAiControlPlane_Should_Register_Replay_ControlPlane()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiControlPlane();

            var descriptor = services.SingleOrDefault(service =>
                service.ServiceType == typeof(IAiReplayControlPlane));

            Assert.NotNull(descriptor);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        }

        [Fact]
        public void AddAiControlPlaneLogging_Should_Not_Register_Duplicate_Observers()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services
                .AddAiControlPlane()
                .AddAiControlPlaneLogging();

            var observerDescriptors = services
                .Where(service => service.ServiceType == typeof(IAiControlPlaneObserver))
                .ToArray();

            Assert.Single(observerDescriptors);
            Assert.Equal(
                typeof(LoggedAiControlPlaneObserver),
                observerDescriptors[0].ImplementationType);
        }

        [Fact]
        public void AddAiControlPlane_Should_Register_RuntimeQueue_ControlPlane()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiControlPlane();

            var descriptor = services.SingleOrDefault(service =>
                service.ServiceType == typeof(IAiRuntimeQueueControlPlane));

            Assert.NotNull(descriptor);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        }

        [Fact]
        public void AddAiControlPlane_Should_Register_RuntimeInstance_Registry()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiControlPlane();

            var descriptor = services.SingleOrDefault(service =>
                service.ServiceType == typeof(IAiRuntimeInstanceRegistry));

            Assert.NotNull(descriptor);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
            Assert.NotNull(descriptor.ImplementationFactory);
        }

        [Fact]
        public void AddAiControlPlane_Should_Register_RuntimeInstance_ControlPlane()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiControlPlane();

            var descriptor = services.SingleOrDefault(service =>
                service.ServiceType == typeof(IAiRuntimeInstanceControlPlane));

            Assert.NotNull(descriptor);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        }

        [Fact]
        public void AddAiControlPlane_Should_Register_RunAdmission_Controller()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiControlPlane();

            var descriptor = services.SingleOrDefault(service =>
                service.ServiceType == typeof(IAiRunAdmissionController));

            Assert.NotNull(descriptor);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        }

        [Fact]
        public void AddAiControlPlane_Should_Register_SharedRuntime_Controller()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiControlPlane();

            var descriptor = services.SingleOrDefault(service =>
                service.ServiceType == typeof(IAiSharedRuntimeController));

            Assert.NotNull(descriptor);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        }

        [Fact]
        public void AddAiControlPlane_Should_Register_SharedRun_Store()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiControlPlane();

            var descriptor = services.SingleOrDefault(service =>
                service.ServiceType == typeof(IAiSharedRunStore));

            Assert.NotNull(descriptor);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        }

        [Fact]
        public void AddAiControlPlane_Should_Register_InMemory_SharedRun_Store_By_Default()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiControlPlane();

            var descriptor = services.SingleOrDefault(service =>
                service.ServiceType == typeof(IAiSharedRunStore));

            Assert.NotNull(descriptor);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
            Assert.Equal(
                typeof(InMemoryAiSharedRunStore),
                descriptor.ImplementationType);
        }

        [Fact]
        public void AddRedisAiSharedRunStore_Should_Replace_InMemory_SharedRun_Store()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiControlPlane();
            services.AddRedisAiSharedRunStore();

            var descriptors = services
                .Where(service => service.ServiceType == typeof(IAiSharedRunStore))
                .ToArray();

            Assert.Single(descriptors);

            Assert.Equal(
                typeof(RedisAiSharedRunStore),
                descriptors[0].ImplementationType);
        }

        [Fact]
        public void AddAiControlPlane_Should_Register_InMemory_SharedQueue_By_Default()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiControlPlane();

            var descriptor = services.SingleOrDefault(service =>
                service.ServiceType == typeof(IAiSharedQueue));

            Assert.NotNull(descriptor);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
            Assert.Equal(
                typeof(InMemoryAiSharedQueue),
                descriptor.ImplementationType);
        }

        [Fact]
        public void AddAiControlPlane_Should_Register_SharedQueue_Dispatcher_By_Default()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiControlPlane();

            var descriptor = services.SingleOrDefault(service =>
                service.ServiceType == typeof(IAiSharedQueueDispatcher));

            Assert.NotNull(descriptor);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
            Assert.Equal(
                typeof(AiSharedQueueDispatcher),
                descriptor.ImplementationType);
        }

        [Fact]
        public void AddAiControlPlane_Should_Register_SharedQueue_Pump_By_Default()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiControlPlane();

            var descriptor = services.SingleOrDefault(service =>
                service.ServiceType == typeof(IAiSharedQueuePump));

            Assert.NotNull(descriptor);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
            Assert.Equal(
                typeof(AiSharedQueuePump),
                descriptor.ImplementationType);
        }

        [Fact]
        public void AddAiSharedQueueBackgroundService_Should_Register_Hosted_Service()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiControlPlane();

            services.AddAiSharedQueueBackgroundService(options =>
            {
                options.Enabled = true;
                options.RuntimeInstanceId = "runtime-1";
                options.WorkerId = "worker-1";
            });

            var descriptor = services.SingleOrDefault(service =>
                service.ServiceType == typeof(IHostedService) &&
                service.ImplementationType == typeof(AiSharedQueueBackgroundService));

            Assert.NotNull(descriptor);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        }

        [Fact]
        public void AddAiControlPlane_Should_Register_InMemory_ScaleOut_Request_Store_By_Default()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiControlPlane();

            var descriptor = services.SingleOrDefault(service =>
                service.ServiceType == typeof(IAiRuntimeScaleOutRequestStore));

            Assert.NotNull(descriptor);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
            Assert.Equal(
                typeof(InMemoryAiRuntimeScaleOutRequestStore),
                descriptor.ImplementationType);
        }

        [Fact]
        public void AddAiControlPlane_Should_Register_StoreBacked_ScaleOut_Publisher_By_Default()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiControlPlane();

            var descriptor = services.SingleOrDefault(service =>
                service.ServiceType == typeof(IAiRuntimeScaleOutRequestPublisher));

            Assert.NotNull(descriptor);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
            Assert.Equal(
                typeof(StoreBackedAiRuntimeScaleOutRequestPublisher),
                descriptor.ImplementationType);
        }

        [Fact]
        public void AddRedisAiRuntimeScaleOutRequestStore_Should_Replace_InMemory_ScaleOut_Request_Store()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiControlPlane();
            services.AddRedisAiRuntimeScaleOutRequestStore();

            var descriptors = services
                .Where(service => service.ServiceType == typeof(IAiRuntimeScaleOutRequestStore))
                .ToArray();

            Assert.Single(descriptors);

            Assert.Equal(
                typeof(RedisAiRuntimeScaleOutRequestStore),
                descriptors[0].ImplementationType);
        }

        [Fact]
        public void AddRedisAiControlPlaneStores_Should_Replace_InMemory_ScaleOut_Request_Store()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiControlPlane();
            services.AddRedisAiControlPlaneStores();

            var descriptors = services
                .Where(service => service.ServiceType == typeof(IAiRuntimeScaleOutRequestStore))
                .ToArray();

            Assert.Single(descriptors);

            Assert.Equal(
                typeof(RedisAiRuntimeScaleOutRequestStore),
                descriptors[0].ImplementationType);
        }

        /// <summary>
        /// Verifies that the default control-plane registration provides a simulated scale-out provider.
        /// </summary>
        [Fact]
        public void AddAiControlPlane_Should_Register_Simulated_ScaleOut_Provider_By_Default()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiControlPlane();

            var descriptor = services.SingleOrDefault(service =>
                service.ServiceType == typeof(IAiRuntimeScaleOutProvider));

            Assert.NotNull(descriptor);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
            Assert.Equal(
                typeof(SimulatedAiRuntimeScaleOutProvider),
                descriptor.ImplementationType);
        }

        /// <summary>
        /// Verifies that the default control-plane registration provides scale-out watcher options.
        /// </summary>
        [Fact]
        public void AddAiControlPlane_Should_Register_ScaleOut_Watcher_Options_By_Default()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiControlPlane();

            using var provider = services.BuildServiceProvider();

            var options =
                provider.GetRequiredService<IOptions<AiRuntimeScaleOutRequestWatcherOptions>>().Value;

            Assert.NotNull(options);
            Assert.False(options.Enabled);
            Assert.Equal("scale-out-request-watcher", options.WatcherId);
            Assert.Equal(TimeSpan.FromSeconds(5), options.Interval);
            Assert.Equal(10, options.MaxRequestsPerCycle);
            Assert.True(options.RejectOnProviderFailure);
        }

        /// <summary>
        /// Verifies that the default control-plane registration provides simulated scale-out provider options.
        /// </summary>
        [Fact]
        public void AddAiControlPlane_Should_Register_Simulated_ScaleOut_Provider_Options_By_Default()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiControlPlane();

            using var provider = services.BuildServiceProvider();

            var options =
                provider.GetRequiredService<IOptions<SimulatedAiRuntimeScaleOutProviderOptions>>().Value;

            Assert.NotNull(options);
            Assert.True(options.Succeed);
            Assert.Equal("simulated-runtime", options.RuntimeInstanceIdPrefix);
            Assert.Equal(TimeSpan.Zero, options.Delay);
            Assert.Equal(
                "Simulated scale-out provider failure.",
                options.FailureReason);
        }

        /// <summary>
        /// Verifies that registering the scale-out request watcher adds the hosted service.
        /// </summary>
        [Fact]
        public void AddAiRuntimeScaleOutRequestWatcher_Should_Register_Hosted_Service()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiControlPlane();
            services.AddAiRuntimeScaleOutRequestWatcher();

            var descriptors = services
                .Where(service => service.ServiceType == typeof(IHostedService))
                .ToArray();

            Assert.Contains(
                descriptors,
                descriptor => descriptor.ImplementationType == typeof(AiRuntimeScaleOutRequestWatcherHostedService));
        }

        /// <summary>
        /// Verifies that registering the scale-out request watcher applies configured options.
        /// </summary>
        [Fact]
        public void AddAiRuntimeScaleOutRequestWatcher_Should_Configure_Options()
        {
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddAiControlPlane();
            services.AddAiRuntimeScaleOutRequestWatcher(options =>
            {
                options.Enabled = true;
                options.ControlPlaneId = "cp-test";
                options.WatcherId = "watcher-test";
                options.Interval = TimeSpan.FromSeconds(1);
                options.MaxRequestsPerCycle = 5;
                options.RejectOnProviderFailure = false;
                options.IgnoreWhenControlPlaneIdMissing = false;
            });

            using var provider = services.BuildServiceProvider();

            var options =
                provider.GetRequiredService<IOptions<AiRuntimeScaleOutRequestWatcherOptions>>().Value;

            Assert.True(options.Enabled);
            Assert.Equal("cp-test", options.ControlPlaneId);
            Assert.Equal("watcher-test", options.WatcherId);
            Assert.Equal(TimeSpan.FromSeconds(1), options.Interval);
            Assert.Equal(5, options.MaxRequestsPerCycle);
            Assert.False(options.RejectOnProviderFailure);
            Assert.False(options.IgnoreWhenControlPlaneIdMissing);
        }

        [Fact]
        public void AddAiRuntimeInstanceHealthReconciliation_Should_Register_RuntimeInstanceHealthReconciler()
        {
            var services = new ServiceCollection();

            services.AddSingleton<IAiRuntimeInstanceRegistry, InMemoryAiRuntimeInstanceRegistry>();
            services.AddAiRuntimeInstanceHealthReconciliation();

            using var provider = services.BuildServiceProvider();

            var reconciler = provider.GetRequiredService<IAiRuntimeInstanceHealthReconciler>();

            Assert.IsType<AiRuntimeInstanceHealthReconciler>(reconciler);
        }

        /// <summary>
        /// Verifies that runtime instance health reconciliation options can be configured.
        /// </summary>
        [Fact]
        public void AddAiRuntimeInstanceHealthReconciliation_Should_Configure_Options()
        {
            var services = new ServiceCollection();

            services.AddSingleton<IAiRuntimeInstanceRegistry, InMemoryAiRuntimeInstanceRegistry>();
            services.AddAiRuntimeInstanceHealthReconciliation(options =>
            {
                options.Enabled = false;
                options.StaleHeartbeatThreshold = TimeSpan.FromMinutes(2);
                options.DryRun = true;
            });

            using var provider = services.BuildServiceProvider();

            var options = provider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<AiRuntimeInstanceHealthReconciliationOptions>>()
                .Value;

            Assert.False(options.Enabled);
            Assert.Equal(TimeSpan.FromMinutes(2), options.StaleHeartbeatThreshold);
            Assert.True(options.DryRun);
        }

        /// <summary>
        /// Verifies that AddAiControlPlane exposes runtime instance health reconciliation options.
        /// </summary>
        [Fact]
        public void AddAiControlPlane_Should_Configure_RuntimeInstanceHealthReconciliation_Options()
        {
            var services = new ServiceCollection();

            services.AddAiControlPlane(
                configureRuntimeInstanceHealthReconciliation: options =>
                {
                    options.Enabled = false;
                    options.StaleHeartbeatThreshold = TimeSpan.FromSeconds(45);
                    options.MarkStaleRuntimeUnhealthy = false;
                    options.DryRun = true;
                });

            using var provider = services.BuildServiceProvider();

            var options = provider
                .GetRequiredService<IOptions<AiRuntimeInstanceHealthReconciliationOptions>>()
                .Value;

            Assert.False(options.Enabled);
            Assert.Equal(TimeSpan.FromSeconds(45), options.StaleHeartbeatThreshold);
            Assert.False(options.MarkStaleRuntimeUnhealthy);
            Assert.True(options.DryRun);
        }

        /// <summary>
        /// Verifies that AddAiControlPlane registers the runtime instance health reconciler service.
        /// </summary>
        [Fact]
        public void AddAiControlPlane_Should_Register_RuntimeInstanceHealthReconciler()
        {
            var services = new ServiceCollection();

            services.AddSingleton<IAiRuntimeInstanceRegistry, InMemoryAiRuntimeInstanceRegistry>();
            services.AddAiControlPlane();

            using var provider = services.BuildServiceProvider();

            var reconciler = provider.GetRequiredService<IAiRuntimeInstanceHealthReconciler>();

            Assert.IsType<AiRuntimeInstanceHealthReconciler>(reconciler);
        }

        /// <summary>
        /// Verifies that the runtime instance health reconciler hosted service can be registered.
        /// </summary>
        [Fact]
        public void AddAiRuntimeInstanceHealthReconcilerHostedService_Should_Register_HostedService()
        {
            var services = new ServiceCollection();

            services.AddSingleton<IAiRuntimeInstanceRegistry, InMemoryAiRuntimeInstanceRegistry>();
            services.AddAiRuntimeInstanceHealthReconcilerHostedService();

            using var provider = services.BuildServiceProvider();

            var hostedServices = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>();

            Assert.Contains(hostedServices, service => service is AiRuntimeInstanceHealthReconcilerHostedService);
        }

        /// <summary>
        /// Verifies that runtime instance health reconciler hosted service options can be configured.
        /// </summary>
        [Fact]
        public void AddAiRuntimeInstanceHealthReconcilerHostedService_Should_Configure_Options()
        {
            var services = new ServiceCollection();

            services.AddSingleton<IAiRuntimeInstanceRegistry, InMemoryAiRuntimeInstanceRegistry>();
            services.AddAiRuntimeInstanceHealthReconcilerHostedService(options =>
            {
                options.Enabled = true;
                options.Interval = TimeSpan.FromSeconds(3);
                options.ErrorDelay = TimeSpan.FromSeconds(2);
            });

            using var provider = services.BuildServiceProvider();

            var options = provider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<AiRuntimeInstanceHealthReconcilerHostedServiceOptions>>()
                .Value;

            Assert.True(options.Enabled);
            Assert.Equal(TimeSpan.FromSeconds(3), options.Interval);
            Assert.Equal(TimeSpan.FromSeconds(2), options.ErrorDelay);
        }

        /// <summary>
        /// Verifies that runtime execution recovery reconciliation can be registered.
        /// </summary>
        [Fact]
        public void AddAiRuntimeExecutionRecoveryReconciliation_Should_Register_RuntimeExecutionRecoveryReconciler()
        {
            var services = new ServiceCollection();

            services.AddSingleton<IAiRuntimeInstanceRegistry, InMemoryAiRuntimeInstanceRegistry>();
            services.AddSingleton<IAiRuntimeRunExecutionIndex, InMemoryAiRuntimeRunExecutionIndex>();
            services.AddAiRuntimeExecutionRecoveryReconciliation();

            using var provider = services.BuildServiceProvider();

            var reconciler = provider.GetRequiredService<IAiRuntimeExecutionRecoveryReconciler>();

            Assert.IsType<AiRuntimeExecutionRecoveryReconciler>(reconciler);
        }

        /// <summary>
        /// Verifies that runtime execution recovery reconciliation options can be configured.
        /// </summary>
        [Fact]
        public void AddAiRuntimeExecutionRecoveryReconciliation_Should_Configure_Options()
        {
            var services = new ServiceCollection();

            services.AddSingleton<IAiRuntimeInstanceRegistry, InMemoryAiRuntimeInstanceRegistry>();
            services.AddSingleton<IAiRuntimeRunExecutionIndex, InMemoryAiRuntimeRunExecutionIndex>();
            services.AddAiRuntimeExecutionRecoveryReconciliation(options =>
            {
                options.Enabled = true;
                options.IncludeUnhealthyRuntimeInstances = true;
                options.IncludeStoppedRuntimeInstances = true;
                options.IncludeDrainingRuntimeInstances = true;
                options.RequeueUnfinishedRuns = false;
                options.DryRun = true;
            });

            using var provider = services.BuildServiceProvider();

            var options = provider
                .GetRequiredService<IOptions<AiRuntimeExecutionRecoveryReconciliationOptions>>()
                .Value;

            Assert.True(options.Enabled);
            Assert.True(options.IncludeUnhealthyRuntimeInstances);
            Assert.True(options.IncludeStoppedRuntimeInstances);
            Assert.True(options.IncludeDrainingRuntimeInstances);
            Assert.False(options.RequeueUnfinishedRuns);
            Assert.True(options.DryRun);
        }

        /// <summary>
        /// Verifies that AddAiControlPlane configures runtime execution recovery reconciliation options.
        /// </summary>
        [Fact]
        public void AddAiControlPlane_Should_Configure_RuntimeExecutionRecoveryReconciliation_Options()
        {
            var services = new ServiceCollection();

            services.AddAiControlPlane(
                configureRuntimeExecutionRecoveryReconciliation: options =>
                {
                    options.Enabled = true;
                    options.IncludeStoppedRuntimeInstances = true;
                    options.IncludeDrainingRuntimeInstances = true;
                    options.RequeueUnfinishedRuns = false;
                    options.DryRun = true;
                });

            using var provider = services.BuildServiceProvider();

            var options = provider
                .GetRequiredService<IOptions<AiRuntimeExecutionRecoveryReconciliationOptions>>()
                .Value;

            Assert.True(options.Enabled);
            Assert.True(options.IncludeUnhealthyRuntimeInstances);
            Assert.True(options.IncludeStoppedRuntimeInstances);
            Assert.True(options.IncludeDrainingRuntimeInstances);
            Assert.False(options.RequeueUnfinishedRuns);
            Assert.True(options.DryRun);
        }
    }
}