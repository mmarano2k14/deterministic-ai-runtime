using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http.ScaleOut;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Tests.Fixtures;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Provides unit tests for <see cref="AiRuntimeScaleOutProviderSelector" />.
    /// </summary>
    public sealed class AiRuntimeScaleOutProviderSelectorTests
    {
        /// <summary>
        /// Verifies that the selector resolves a scale-out provider from the request provider hint.
        /// </summary>
        [Fact]
        public async Task RequestScaleOutAsync_Should_Resolve_Provider_From_Request_ProviderHint()
        {
            var selector =
                CreateSelector(
                    new TestSimulatedRuntimeScaleOutProvider());

            var result =
                await selector
                    .RequestScaleOutAsync(
                        CreateRequest(providerHint: "simulated"))
                    .ConfigureAwait(false);

            Assert.True(result.Success);
            Assert.False(result.Rejected);
            Assert.False(string.IsNullOrWhiteSpace(result.RuntimeInstanceId));
            Assert.StartsWith("test-runtime-", result.RuntimeInstanceId, StringComparison.Ordinal);
        }

        /// <summary>
        /// Verifies that the selector rejects the request when no scale-out provider can be resolved.
        /// </summary>
        [Fact]
        public async Task RequestScaleOutAsync_Should_Reject_When_Provider_Is_Not_Found()
        {
            var selector =
                CreateSelector(
                    new TestLocalRuntimeScaleOutProvider());

            var result =
                await selector
                    .RequestScaleOutAsync(
                        CreateRequest(providerHint: "missing"))
                    .ConfigureAwait(false);

            Assert.False(result.Success);
            Assert.True(result.Rejected);
            Assert.Equal("scale-out-provider-not-found", result.FailureReason);
        }

        /// <summary>
        /// Verifies that the selector falls back to runtime instance registration provider name.
        /// </summary>
        [Fact]
        public async Task RequestScaleOutAsync_Should_Use_Registration_ProviderName_When_Request_Hint_Is_Missing()
        {
            var selector =
                CreateSelector(
                    new TestLocalRuntimeScaleOutProvider(),
                    registrationProviderName: "local");

            var result =
                await selector
                    .RequestScaleOutAsync(
                        CreateRequest(providerHint: null))
                    .ConfigureAwait(false);

            Assert.True(result.Success);
            Assert.False(result.Rejected);
            Assert.False(string.IsNullOrWhiteSpace(result.RuntimeInstanceId));
            Assert.StartsWith("test-runtime-", result.RuntimeInstanceId, StringComparison.Ordinal);
        }

        /// <summary>
        /// Verifies that the selector resolves the HTTP runtime scale-out provider from the request provider hint.
        /// </summary>
        [Fact]
        public async Task RequestScaleOutAsync_Should_Resolve_Http_Provider_From_Request_ProviderHint()
        {
            var provisioner =
                new TestHttpRuntimeScaleOutProvisioner();

            var httpProvider =
                new HttpAiRuntimeInstanceProvider(
                    new HttpClient(),
                    NullLogger<HttpAiRuntimeInstanceProvider>.Instance,
                    Options.Create(new AiHttpRuntimeInstanceProviderOptions()),
                    provisioner);

            var selector =
                CreateSelector(
                    httpProvider);

            var result =
                await selector
                    .RequestScaleOutAsync(
                        CreateRequest(providerHint: "http"))
                    .ConfigureAwait(false);

            Assert.True(
                result.Success);

            Assert.False(
                result.Rejected);

            Assert.Equal(
                "test-http-runtime-1",
                result.RuntimeInstanceId);

            Assert.Equal(
                "test-http-scaleout-request-1",
                result.ProviderOperationId);

            Assert.Equal(
                1,
                provisioner.CallCount);

            Assert.NotNull(
                provisioner.LastRequest);

            Assert.Equal(
                "tenant-test",
                provisioner.LastRequest!.TenantId);

            Assert.Equal(
                "tenant-group-test",
                provisioner.LastRequest.TenantGroupId);

            Assert.Equal(
                "tenant-test",
                provisioner.LastRequest.ExecutionContextSnapshot.TenantId);

            Assert.Equal(
                "tenant-group-test",
                provisioner.LastRequest.ExecutionContextSnapshot.TenantGroupId);

            Assert.Equal(
                "cp-test",
                provisioner.LastRequest.ControlPlaneId);

            Assert.Equal(
                "shared-run-1",
                provisioner.LastRequest.SharedRunId);

            Assert.Equal(
                "http",
                provisioner.LastRequest.ProviderHint);
        }

        /// <summary>
        /// Creates a selector with a test provider.
        /// </summary>
        /// <param name="provider">The test provider.</param>
        /// <param name="registrationProviderName">The optional registration provider name.</param>
        /// <returns>The created selector.</returns>
        private static AiRuntimeScaleOutProviderSelector CreateSelector(
            IAiRuntimeScaleOutProvider provider,
            string? registrationProviderName = null)
        {
            var providers =
                new IAiRuntimeInstanceProvider[]
                {
                    provider
                };

            var providerRouter =
                new AiRuntimeInstanceProviderRouter(
                    providers);

            return new AiRuntimeScaleOutProviderSelector(
                providerRouter,
                Options.Create(new AiRuntimeInstanceRegistrationOptions
                {
                    ProviderName = registrationProviderName
                }));
        }

        /// <summary>
        /// Creates a scale-out provider request.
        /// </summary>
        /// <param name="providerHint">The provider hint.</param>
        /// <returns>The created request.</returns>
        private static AiRuntimeScaleOutProviderRequest CreateRequest(
            string? providerHint)
        {
            return new AiRuntimeScaleOutProviderRequest
            {
                RequestId = "request-1",
                ControlPlaneId = "cp-test",
                SharedRunId = "shared-run-1",
                ExecutionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create(
                    contextKey: "unit-test:tenant-test:context",
                    project: "unit-test",
                    userId: "unit-test",
                    tenantId: "tenant-test",
                    tenantGroupId: "tenant-group-test",
                    currentNamespace: "unit-test"),
                TenantId = "tenant-test",
                TenantGroupId = "tenant-group-test",
                PipelineKey = "pipeline-test",
                VisibleInstanceCount = 0,
                AvailableInstanceCount = 0,
                CurrentInstanceCount = 0,
                MaxInstanceCount = 3,
                RequestedTargetInstanceCount = 1,
                ProviderHint = providerHint,
                CorrelationId = "correlation-test",
                RequestedBy = "unit-test",
                Source = "unit-test",
                Reason = "No runtime capacity was available for admission.",
                Metadata = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["test"] = "true"
                }
            };
        }

        /// <summary>
        /// Provides a test HTTP runtime scale-out provisioner.
        /// </summary>
        private sealed class TestHttpRuntimeScaleOutProvisioner :
            IAiHttpRuntimeScaleOutProvisioner
        {
            /// <summary>
            /// Gets the number of calls received by the provisioner.
            /// </summary>
            public int CallCount { get; private set; }

            /// <summary>
            /// Gets the last scale-out request received by the provisioner.
            /// </summary>
            public AiRuntimeScaleOutProviderRequest? LastRequest { get; private set; }

            /// <inheritdoc />
            public Task<AiRuntimeScaleOutProviderResult> ProvisionAsync(
                AiRuntimeScaleOutProviderRequest request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);

                cancellationToken.ThrowIfCancellationRequested();

                this.CallCount++;
                this.LastRequest = request;

                return Task.FromResult(
                    new AiRuntimeScaleOutProviderResult
                    {
                        Success = true,
                        Rejected = false,
                        RuntimeInstanceId = "test-http-runtime-1",
                        ProviderOperationId = $"test-http-scaleout-{request.RequestId}",
                        Message = "Test HTTP scale-out fulfilled.",
                        Metadata = new Dictionary<string, string>(
                            StringComparer.OrdinalIgnoreCase)
                        {
                            [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = "http",
                            ["provider.name"] = "http",
                            ["scaleOutRequestId"] = request.RequestId,
                            ["sharedRunId"] = request.SharedRunId,
                            ["controlPlaneId"] = request.ControlPlaneId,
                            ["tenantId"] = request.TenantId ?? string.Empty
                        }
                    });
            }
        }

        /// <summary>
        /// Provides a simulated test runtime scale-out provider.
        /// </summary>
        [AiRuntimeInstanceProvider("simulated")]
        private sealed class TestSimulatedRuntimeScaleOutProvider :
            TestRuntimeScaleOutProvider
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="TestSimulatedRuntimeScaleOutProvider" /> class.
            /// </summary>
            public TestSimulatedRuntimeScaleOutProvider()
                : base("simulated")
            {
            }
        }

        /// <summary>
        /// Provides a local test runtime scale-out provider.
        /// </summary>
        [AiRuntimeInstanceProvider("local")]
        private sealed class TestLocalRuntimeScaleOutProvider :
            TestRuntimeScaleOutProvider
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="TestLocalRuntimeScaleOutProvider" /> class.
            /// </summary>
            public TestLocalRuntimeScaleOutProvider()
                : base("local")
            {
            }
        }

        /// <summary>
        /// Provides a test runtime scale-out provider base implementation.
        /// </summary>
        private abstract class TestRuntimeScaleOutProvider :
            IAiRuntimeScaleOutProvider
        {
            /// <summary>
            /// The provider name.
            /// </summary>
            private readonly string providerName;

            /// <summary>
            /// Initializes a new instance of the <see cref="TestRuntimeScaleOutProvider" /> class.
            /// </summary>
            /// <param name="providerName">The provider name.</param>
            protected TestRuntimeScaleOutProvider(
                string providerName)
            {
                this.providerName =
                    providerName;
            }

            /// <inheritdoc />
            public bool CanHandle(
                AiRuntimeInstanceCapacityDescriptor descriptor)
            {
                ArgumentNullException.ThrowIfNull(descriptor);

                return descriptor.Metadata.TryGetValue(
                        AiRuntimeInstanceProviderMetadataKeys.ProviderName,
                        out var requestedProviderName) &&
                    string.Equals(
                        requestedProviderName,
                        this.providerName,
                        StringComparison.OrdinalIgnoreCase);
            }

            /// <inheritdoc />
            public Task<AiRuntimeScaleOutProviderResult> RequestScaleOutAsync(
                AiRuntimeScaleOutProviderRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(
                    new AiRuntimeScaleOutProviderResult
                    {
                        Success = true,
                        Rejected = false,
                        RuntimeInstanceId = $"test-runtime-{Guid.NewGuid():N}",
                        ProviderOperationId = $"test-scaleout-{Guid.NewGuid():N}",
                        Message = "Test scale-out fulfilled."
                    });
            }
        }
    }
}