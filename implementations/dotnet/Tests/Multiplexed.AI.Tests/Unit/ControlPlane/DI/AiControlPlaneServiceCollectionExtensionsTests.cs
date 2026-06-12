using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Replay;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Control;
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
using Multiplexed.AI.Runtime.ControlPlane.SharedController;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;
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
            Assert.Equal(
                typeof(RedisAiRuntimeInstanceRegistry),
                descriptor.ImplementationType);
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
    }
}