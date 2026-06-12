using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Pool;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Local;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.Providers
{
    /// <summary>
    /// Provides scale-out capability tests for <see cref="LocalAiRuntimeInstanceProvider" />.
    /// </summary>
    public sealed class LocalAiRuntimeInstanceProviderScaleOutTests
    {
        /// <summary>
        /// Verifies that the local provider can handle local scale-out descriptors.
        /// </summary>
        [Fact]
        public void CanHandle_Should_Return_True_For_Local_Provider_Metadata()
        {
            var provider =
                new LocalAiRuntimeInstanceProvider(
                    new TestSharedRuntimeInstanceRegistry());

            var descriptor =
                new AiRuntimeInstanceCapacityDescriptor
                {
                    RuntimeInstanceId = string.Empty,
                    Metadata = new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = "local"
                    }
                };

            var canHandle =
                provider.CanHandle(
                    descriptor);

            Assert.True(canHandle);
        }

        /// <summary>
        /// Verifies that the local provider rejects scale-out when no local scaler is registered.
        /// </summary>
        [Fact]
        public async Task RequestScaleOutAsync_Should_Reject_When_Local_Scaler_Is_Not_Registered()
        {
            var provider =
                new LocalAiRuntimeInstanceProvider(
                    new TestSharedRuntimeInstanceRegistry());

            var result =
                await provider
                    .RequestScaleOutAsync(
                        CreateScaleOutRequest())
                    .ConfigureAwait(false);

            Assert.False(result.Success);
            Assert.True(result.Rejected);
            Assert.Equal(
                "local-runtime-instance-scaler-not-registered",
                result.FailureReason);

            Assert.Equal(
                "local-scaleout-rejected-request-1",
                result.ProviderOperationId);

            Assert.NotNull(result.Metadata);
            Assert.Equal(
                "local",
                result.Metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName]);

            Assert.Equal(
                "request-1",
                result.Metadata["scaleOutRequestId"]);
        }

        /// <summary>
        /// Creates a scale-out provider request.
        /// </summary>
        /// <returns>The created request.</returns>
        private static AiRuntimeScaleOutProviderRequest CreateScaleOutRequest()
        {
            return new AiRuntimeScaleOutProviderRequest
            {
                RequestId = "request-1",
                ControlPlaneId = "cp-test",
                SharedRunId = "shared-run-1",
                TenantId = "tenant-test",
                PipelineKey = "pipeline-test",
                VisibleInstanceCount = 0,
                AvailableInstanceCount = 0,
                CurrentInstanceCount = 0,
                MaxInstanceCount = 3,
                RequestedTargetInstanceCount = 1,
                ProviderHint = "local",
                RequestedBy = "unit-test",
                Source = "unit-test",
                CorrelationId = "correlation-test",
                Reason = "No runtime capacity was available for admission.",
                Metadata = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["test"] = "true"
                }
            };
        }

        /// <summary>
        /// Verifies that the local provider delegates scale-out to the local scaler when registered.
        /// </summary>
        [Fact]
        public async Task RequestScaleOutAsync_Should_Delegate_To_Local_Scaler_When_Registered()
        {
            var scaler =
                new TestLocalRuntimeInstanceScaler();

            var provider =
                new LocalAiRuntimeInstanceProvider(
                    new TestSharedRuntimeInstanceRegistry(),
                    scaler);

            var result =
                await provider
                    .RequestScaleOutAsync(
                        CreateScaleOutRequest())
                    .ConfigureAwait(false);

            Assert.True(result.Success);
            Assert.False(result.Rejected);
            Assert.Equal("test-local-runtime-1", result.RuntimeInstanceId);
            Assert.Equal(1, scaler.CallCount);
        }

        /// <summary>
        /// Provides an empty shared runtime instance registry for local provider tests.
        /// </summary>
        /// <summary>
        /// Provides an empty shared runtime instance registry for local provider tests.
        /// </summary>
        private sealed class TestSharedRuntimeInstanceRegistry : IAiSharedRuntimeInstanceRegistry
        {
            /// <inheritdoc />
            public Task RegisterAsync(
                IAiSharedRuntimeInstance instance,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(instance);

                cancellationToken.ThrowIfCancellationRequested();

                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task<bool> UnregisterAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(true);
            }

            /// <inheritdoc />
            public Task<IAiSharedRuntimeInstance?> GetAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult<IAiSharedRuntimeInstance?>(null);
            }

            /// <inheritdoc />
            public Task<IReadOnlyCollection<IAiSharedRuntimeInstance>> ListAsync(
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult<IReadOnlyCollection<IAiSharedRuntimeInstance>>(
                    Array.Empty<IAiSharedRuntimeInstance>());
            }
        }

        /// <summary>
        /// Provides a test local runtime instance scaler.
        /// </summary>
        private sealed class TestLocalRuntimeInstanceScaler : IAiLocalRuntimeInstanceScaler
        {
            /// <summary>
            /// Gets the number of calls received by the scaler.
            /// </summary>
            public int CallCount { get; private set; }

            /// <inheritdoc />
            public int ActiveInstanceCount { get; private set; }

            /// <inheritdoc />
            public Task<AiRuntimeScaleOutProviderResult> EnsureCapacityAsync(
                AiRuntimeScaleOutProviderRequest request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);

                cancellationToken.ThrowIfCancellationRequested();

                this.CallCount++;
                this.ActiveInstanceCount = 1;

                return Task.FromResult(
                    new AiRuntimeScaleOutProviderResult
                    {
                        Success = true,
                        Rejected = false,
                        RuntimeInstanceId = "test-local-runtime-1",
                        ProviderOperationId = $"test-local-scaleout-{request.RequestId}",
                        Message = "Test local scale-out fulfilled.",
                        Metadata = new Dictionary<string, string>(
                            StringComparer.OrdinalIgnoreCase)
                        {
                            ["scaleOutRequestId"] = request.RequestId,
                            ["sharedRunId"] = request.SharedRunId,
                            ["controlPlaneId"] = request.ControlPlaneId,
                            ["provider"] = "local"
                        }
                    });
            }

            /// <inheritdoc />
            public Task StopAsync(
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }
        }
    }
}