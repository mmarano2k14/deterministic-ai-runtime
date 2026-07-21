using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Readiness;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc.ScaleOut;
using Multiplexed.AI.Tests.Fixtures;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Providers.Grpc
{
    /// <summary>
    /// Unit tests for process-wide process-host startup gating in gRPC scale-out.
    /// </summary>
    public sealed class GrpcProcessHostStartupConcurrencyGateTests
    {
        /// <summary>
        /// Verifies that separate provisioner instances share one process-wide gate and keep the slot through readiness.
        /// </summary>
        [Fact]
        public async Task ProvisionAsync_Should_Bound_Process_Host_Startup_Across_Provisioner_Instances_Until_Readiness_Completes()
        {
            const int maxConcurrency = 2;

            var gateKey = $"grpc-process-host-startup-test-{Guid.NewGuid():N}";
            var readinessWaiter = new BlockingRuntimeInstanceReadinessWaiter();
            var options = CreateOptions(gateKey, maxConcurrency);

            var provisioners =
                Enumerable.Range(1, 3)
                    .Select(_ => CreateProvisioner(readinessWaiter, options))
                    .ToArray();

            var tasks =
                provisioners
                    .Select((provisioner, index) =>
                        provisioner.ProvisionAsync(
                            CreateRequest(
                                $"grpc-process-host-gate-request-{index + 1}",
                                $"shared-run-{index + 1}")))
                    .ToArray();

            try
            {
                await readinessWaiter.WaitForEnteredCountAsync(2, TimeSpan.FromSeconds(5));
                await Task.Delay(100);

                Assert.Equal(2, readinessWaiter.EnteredCount);
                Assert.Equal(2, readinessWaiter.MaxObservedActiveCount);

                readinessWaiter.Release(1);

                await readinessWaiter.WaitForEnteredCountAsync(3, TimeSpan.FromSeconds(5));

                Assert.Equal(3, readinessWaiter.EnteredCount);
                Assert.Equal(2, readinessWaiter.MaxObservedActiveCount);

                readinessWaiter.Release(2);

                var results = await Task.WhenAll(tasks);

                Assert.All(results, result => Assert.True(result.Success));
                Assert.Equal(0, readinessWaiter.ActiveCount);
                Assert.Equal(2, readinessWaiter.MaxObservedActiveCount);
            }
            finally
            {
                readinessWaiter.Release(3);
            }
        }

        private static AiGrpcRuntimeScaleOutProvisioner CreateProvisioner(
            BlockingRuntimeInstanceReadinessWaiter readinessWaiter,
            AiGrpcRuntimeScaleOutOptions options)
        {
            return new AiGrpcRuntimeScaleOutProvisioner(
                new FakeRuntimeInstanceRegistry(),
                new FakeRuntimeInstanceCapacityStore(),
                new FakeRuntimeHostManager(),
                readinessWaiter,
                new FakeTenantRuntimeSettingsProvider
                {
                    WorkerCountPerInstance = 1,
                    MaxConcurrentRunsPerInstance = 1,
                    LocalQueueCapacity = 2,
                    RuntimeInstanceIdPrefix = "tenant-runtime"
                },
                Options.Create(options),
                NullLogger<AiGrpcRuntimeScaleOutProvisioner>.Instance);
        }

        private static AiGrpcRuntimeScaleOutOptions CreateOptions(
            string gateKey,
            int maxConcurrency)
        {
            return new AiGrpcRuntimeScaleOutOptions
            {
                Enabled = true,
                Mode = AiGrpcRuntimeScaleOutModes.HostManager,
                HostCreationMode = AiRuntimeHostCreationMode.Process,
                RequireReadiness = true,
                ReadinessTimeoutSeconds = 30,
                ReadinessPollIntervalMilliseconds = 100,
                EndpointTemplate = "http://127.0.0.1:50051/{runtimeInstanceId}",
                DefaultRuntimeInstanceIdPrefix = "tenant-runtime",
                MaxConcurrentProcessHostStartups = maxConcurrency,
                ProcessHostStartupConcurrencyKey = gateKey
            };
        }

        private static AiRuntimeScaleOutProviderRequest CreateRequest(
            string requestId,
            string sharedRunId)
        {
            return new AiRuntimeScaleOutProviderRequest
            {
                RequestId = requestId,
                ControlPlaneId = "control-plane-process-host-gate",
                SharedRunId = sharedRunId,
                TenantId = "tenant-a",
                TenantGroupId = "tenant-a-group",
                RequestedTargetInstanceCount = 1,
                CurrentInstanceCount = 0,
                WorkerCountPerInstance = 1,
                MaxConcurrentRunsPerInstance = 1,
                LocalQueueCapacity = 2,
                PreferDedicatedCapacity = true,
                AllowSharedFallback = false,
                IsolationMode = AiRuntimeInstanceIsolationMode.Dedicated,
                ExecutionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create()
            };
        }

        private sealed class BlockingRuntimeInstanceReadinessWaiter : IAiRuntimeInstanceReadinessWaiter
        {
            private readonly SemaphoreSlim enteredSignal = new(0);
            private readonly SemaphoreSlim releaseSignal = new(0);
            private int activeCount;
            private int enteredCount;
            private int maxObservedActiveCount;

            public int ActiveCount => Volatile.Read(ref activeCount);

            public int EnteredCount => Volatile.Read(ref enteredCount);

            public int MaxObservedActiveCount => Volatile.Read(ref maxObservedActiveCount);

            public async Task<AiRuntimeInstanceReadinessResult> WaitUntilReadyAsync(
                AiRuntimeInstanceReadinessRequest request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);

                var currentActiveCount = Interlocked.Increment(ref activeCount);
                Interlocked.Increment(ref enteredCount);
                UpdateMaxObservedActiveCount(currentActiveCount);
                enteredSignal.Release();

                try
                {
                    await releaseSignal
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);

                    return new AiRuntimeInstanceReadinessResult
                    {
                        Success = true,
                        RuntimeInstanceId = request.RuntimeInstanceId,
                        TransportEndpoint = request.TransportEndpoint,
                        ExecutionContextSnapshot = request.ExecutionContextSnapshot
                    };
                }
                finally
                {
                    Interlocked.Decrement(ref activeCount);
                }
            }

            public async Task WaitForEnteredCountAsync(
                int expectedCount,
                TimeSpan timeout)
            {
                using var timeoutCancellation = new CancellationTokenSource(timeout);

                while (EnteredCount < expectedCount)
                {
                    await enteredSignal
                        .WaitAsync(timeoutCancellation.Token)
                        .ConfigureAwait(false);
                }
            }

            public void Release(int count)
            {
                for (var index = 0; index < count; index++)
                {
                    releaseSignal.Release();
                }
            }

            private void UpdateMaxObservedActiveCount(int active)
            {
                while (true)
                {
                    var currentMaximum = MaxObservedActiveCount;

                    if (active <= currentMaximum)
                    {
                        return;
                    }

                    if (Interlocked.CompareExchange(
                            ref maxObservedActiveCount,
                            active,
                            currentMaximum) == currentMaximum)
                    {
                        return;
                    }
                }
            }
        }
    }
}
