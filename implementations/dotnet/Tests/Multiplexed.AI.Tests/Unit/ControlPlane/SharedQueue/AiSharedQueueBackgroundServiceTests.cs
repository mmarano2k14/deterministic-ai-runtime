using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Background;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;
using Multiplexed.AI.Tests.Fixtures;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.SharedQueue
{
    public sealed class AiSharedQueueBackgroundServiceTests
    {
        [Fact]
        public async Task StartAsync_Should_Not_Call_Pump_When_Disabled()
        {
            var pump = new FakeSharedQueuePump();

            var service = new AiSharedQueueBackgroundService(
             pump,
             Options.Create(new AiSharedQueueBackgroundServiceOptions
             {
                 Enabled = false,
                 RuntimeInstanceId = "runtime-1",
                 WorkerId = "worker-1"
             }),
             new StaticAiControlPlaneIdResolver("test-control-plane"),
             new InMemoryAiRuntimeInstanceRegistry(),
             Array.Empty<IAiRuntimeInstanceCapacityStore>(),
             NullLogger<AiSharedQueueBackgroundService>.Instance);

            await service.StartAsync(CancellationToken.None);
            await service.StopAsync(CancellationToken.None);

            Assert.Equal(0, pump.CallCount);
        }

        [Fact]
        public async Task StartAsync_Should_Call_Pump_When_Enabled()
        {
            using var cts = new CancellationTokenSource();

            var pump = new FakeSharedQueuePump(
                onCall: () => cts.Cancel());

            var service = new AiSharedQueueBackgroundService(
                pump,
                Options.Create(new AiSharedQueueBackgroundServiceOptions
                {
                    Enabled = true,
                    RuntimeInstanceId = "runtime-1",
                    WorkerId = "worker-1",
                    IdleDelay = TimeSpan.FromMilliseconds(1),
                    ActiveDelay = TimeSpan.FromMilliseconds(1),
                    ErrorDelay = TimeSpan.FromMilliseconds(1)
                }),
                new StaticAiControlPlaneIdResolver("test-control-plane"),
                new InMemoryAiRuntimeInstanceRegistry(),
                Array.Empty<IAiRuntimeInstanceCapacityStore>(),
                NullLogger<AiSharedQueueBackgroundService>.Instance);

            await service.StartAsync(cts.Token);

            await WaitUntilAsync(
                () => pump.CallCount > 0,
                TimeSpan.FromSeconds(2));

            await service.StopAsync(CancellationToken.None);

            Assert.True(pump.CallCount > 0);
            Assert.NotNull(pump.LastRequest);
            Assert.Equal("runtime-1", pump.LastRequest!.PumpRuntimeInstanceId);
            Assert.Equal("worker-1", pump.LastRequest.PumpWorkerId);
        }

        [Fact]
        public async Task StartAsync_Should_Pass_Options_To_Pump_Request()
        {
            using var cts = new CancellationTokenSource();

            var pump = new FakeSharedQueuePump(
                onCall: () => cts.Cancel());

            var service = new AiSharedQueueBackgroundService(
                pump,
                Options.Create(new AiSharedQueueBackgroundServiceOptions
                {
                    Enabled = true,
                    RuntimeInstanceId = "runtime-1",
                    WorkerId = "worker-1",
                    TenantId = "tenant-1",
                    PipelineKey = "pipeline-1",
                    MaxDispatchesPerCycle = 7,
                    ClaimTtl = TimeSpan.FromSeconds(45),
                    RequestedBy = "tester",
                    Source = "unit-test-background-service",
                    Metadata = new Dictionary<string, string>
                    {
                        ["component"] = "background-service-test"
                    },
                    IdleDelay = TimeSpan.FromMilliseconds(1),
                    ActiveDelay = TimeSpan.FromMilliseconds(1),
                    ErrorDelay = TimeSpan.FromMilliseconds(1)
                }),
                new StaticAiControlPlaneIdResolver("test-control-plane"),
                new InMemoryAiRuntimeInstanceRegistry(),
                Array.Empty<IAiRuntimeInstanceCapacityStore>(),
                NullLogger<AiSharedQueueBackgroundService>.Instance);

            await service.StartAsync(cts.Token);

            await WaitUntilAsync(
                () => pump.CallCount > 0,
                TimeSpan.FromSeconds(2));

            await service.StopAsync(CancellationToken.None);

            Assert.NotNull(pump.LastRequest);
            Assert.Equal("runtime-1", pump.LastRequest!.PumpRuntimeInstanceId);
            Assert.Equal("worker-1", pump.LastRequest.PumpWorkerId);
            Assert.Equal("tenant-1", pump.LastRequest.TenantId);
            Assert.Equal("pipeline-1", pump.LastRequest.PipelineKey);
            Assert.Equal(7, pump.LastRequest.MaxDispatches);
            Assert.Equal(TimeSpan.FromSeconds(45), pump.LastRequest.ClaimTtl);
            Assert.Equal("tester", pump.LastRequest.RequestedBy);
            Assert.Equal("unit-test-background-service", pump.LastRequest.Source);
            Assert.Equal("background-service-test", pump.LastRequest.Metadata["component"]);
            Assert.False(string.IsNullOrWhiteSpace(pump.LastRequest.CorrelationId));
        }

        [Fact]
        public async Task StartAsync_Should_Use_Default_Runtime_And_Worker_When_Not_Configured()
        {
            using var cts = new CancellationTokenSource();

            var pump = new FakeSharedQueuePump(
                onCall: () => cts.Cancel());

            var service = new AiSharedQueueBackgroundService(
                pump,
                Options.Create(new AiSharedQueueBackgroundServiceOptions
                {
                    Enabled = true,
                    IdleDelay = TimeSpan.FromMilliseconds(1),
                    ActiveDelay = TimeSpan.FromMilliseconds(1),
                    ErrorDelay = TimeSpan.FromMilliseconds(1)
                }),
                new StaticAiControlPlaneIdResolver("test-control-plane"),
                new InMemoryAiRuntimeInstanceRegistry(),
                Array.Empty<IAiRuntimeInstanceCapacityStore>(),
                NullLogger<AiSharedQueueBackgroundService>.Instance);

            await service.StartAsync(cts.Token);

            await WaitUntilAsync(
                () => pump.CallCount > 0,
                TimeSpan.FromSeconds(2));

            await service.StopAsync(CancellationToken.None);

            Assert.NotNull(pump.LastRequest);
            Assert.False(string.IsNullOrWhiteSpace(pump.LastRequest!.PumpRuntimeInstanceId));
            Assert.False(string.IsNullOrWhiteSpace(pump.LastRequest.PumpWorkerId));
            Assert.Contains("shared-queue-worker", pump.LastRequest.PumpWorkerId);
        }

        [Fact]
        public async Task StartAsync_Should_Continue_After_Pump_Exception()
        {
            using var cts = new CancellationTokenSource();

            var pump = new FakeSharedQueuePump(
                throwOnFirstCall: true,
                onCall: () =>
                {
                    if (pumpReference?.CallCount >= 2)
                    {
                        cts.Cancel();
                    }
                });

            pumpReference = pump;

            var service = new AiSharedQueueBackgroundService(
                pump,
                Options.Create(new AiSharedQueueBackgroundServiceOptions
                {
                    Enabled = true,
                    RuntimeInstanceId = "runtime-1",
                    WorkerId = "worker-1",
                    IdleDelay = TimeSpan.FromMilliseconds(1),
                    ActiveDelay = TimeSpan.FromMilliseconds(1),
                    ErrorDelay = TimeSpan.FromMilliseconds(1)
                }),
                new StaticAiControlPlaneIdResolver("test-control-plane"),   
                new InMemoryAiRuntimeInstanceRegistry(),
                Array.Empty<IAiRuntimeInstanceCapacityStore>(),
                NullLogger<AiSharedQueueBackgroundService>.Instance);

            await service.StartAsync(cts.Token);

            await WaitUntilAsync(
                () => pump.CallCount >= 2,
                TimeSpan.FromSeconds(2));

            await service.StopAsync(CancellationToken.None);

            Assert.True(pump.CallCount >= 2);

            pumpReference = null;
        }

        private static FakeSharedQueuePump? pumpReference;

        private static async Task WaitUntilAsync(
            Func<bool> condition,
            TimeSpan timeout)
        {
            var startedAtUtc = DateTimeOffset.UtcNow;

            while (!condition())
            {
                if (DateTimeOffset.UtcNow - startedAtUtc > timeout)
                {
                    throw new TimeoutException("Condition was not reached in time.");
                }

                await Task.Delay(10);
            }
        }

        private sealed class FakeSharedQueuePump : IAiSharedQueuePump
        {
            private readonly Action? _onCall;
            private readonly bool _throwOnFirstCall;

            public FakeSharedQueuePump(
                Action? onCall = null,
                bool throwOnFirstCall = false)
            {
                _onCall = onCall;
                _throwOnFirstCall = throwOnFirstCall;
            }

            public int CallCount { get; private set; }

            public AiSharedQueuePumpRequest? LastRequest { get; private set; }

            public Task<AiSharedQueuePumpResult> PumpOnceAsync(
                AiSharedQueuePumpRequest request,
                CancellationToken cancellationToken = default)
            {
                CallCount++;
                LastRequest = request;

                _onCall?.Invoke();

                if (_throwOnFirstCall &&
                    CallCount == 1)
                {
                    throw new InvalidOperationException("Pump failed.");
                }

                return Task.FromResult(new AiSharedQueuePumpResult
                {
                    Success = true,
                    RuntimeInstanceId = request.PumpRuntimeInstanceId,
                    AttemptedDispatchCount = 1,
                    SuccessfulDispatchCount = 0,
                    FailedDispatchCount = 0,
                    StoppedBecauseNoItemAvailable = true,
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow
                });
            }
        }
    }
}