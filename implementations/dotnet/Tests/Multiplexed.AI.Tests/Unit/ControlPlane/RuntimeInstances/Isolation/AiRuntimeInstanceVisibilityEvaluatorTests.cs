using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Isolation;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Isolation
{
    public sealed class AiRuntimeInstanceVisibilityEvaluatorTests
    {
        [Fact]
        public void IsVisible_Should_Return_True_For_Shared_Instance_And_Unknown_Tenant()
        {
            var evaluator = CreateEvaluator();

            var descriptor = new AiRuntimeInstanceVisibilityDescriptor
            {
                RuntimeInstanceId = "shared-runtime-1",
                IsolationMode = AiRuntimeInstanceIsolationMode.Shared
            };

            var visible = evaluator.IsVisible("tenant-x", null, descriptor);

            Assert.True(visible);
        }

        [Fact]
        public void IsVisible_Should_Return_False_For_Shared_Instance_And_Dedicated_Tenant_When_Fallback_Disabled()
        {
            var evaluator = CreateEvaluator();

            var descriptor = new AiRuntimeInstanceVisibilityDescriptor
            {
                RuntimeInstanceId = "shared-runtime-1",
                IsolationMode = AiRuntimeInstanceIsolationMode.Shared
            };

            var visible = evaluator.IsVisible("tenant-a", null, descriptor);

            Assert.False(visible);
        }

        [Fact]
        public void IsVisible_Should_Return_True_For_Shared_Instance_And_Hybrid_Tenant_When_Fallback_Enabled()
        {
            var evaluator = CreateEvaluator();

            var descriptor = new AiRuntimeInstanceVisibilityDescriptor
            {
                RuntimeInstanceId = "shared-runtime-1",
                IsolationMode = AiRuntimeInstanceIsolationMode.Shared
            };

            var visible = evaluator.IsVisible("tenant-b", null, descriptor);

            Assert.True(visible);
        }

        [Fact]
        public void IsVisible_Should_Return_True_For_Dedicated_Instance_When_Tenant_Matches()
        {
            var evaluator = CreateEvaluator();

            var descriptor = new AiRuntimeInstanceVisibilityDescriptor
            {
                RuntimeInstanceId = "tenant-a-runtime-1",
                TenantId = "tenant-a",
                IsolationMode = AiRuntimeInstanceIsolationMode.Dedicated
            };

            var visible = evaluator.IsVisible("tenant-a", null, descriptor);

            Assert.True(visible);
        }

        [Fact]
        public void IsVisible_Should_Return_False_For_Dedicated_Instance_When_Tenant_Does_Not_Match()
        {
            var evaluator = CreateEvaluator();

            var descriptor = new AiRuntimeInstanceVisibilityDescriptor
            {
                RuntimeInstanceId = "tenant-a-runtime-1",
                TenantId = "tenant-a",
                IsolationMode = AiRuntimeInstanceIsolationMode.Dedicated
            };

            var visible = evaluator.IsVisible("tenant-b", null, descriptor);

            Assert.False(visible);
        }

        [Fact]
        public void IsVisible_Should_Return_True_For_Dedicated_Instance_When_Tenant_Group_Matches()
        {
            var evaluator = CreateEvaluator();

            var descriptor = new AiRuntimeInstanceVisibilityDescriptor
            {
                RuntimeInstanceId = "enterprise-runtime-1",
                TenantGroupId = "enterprise-group",
                IsolationMode = AiRuntimeInstanceIsolationMode.Dedicated
            };

            var visible = evaluator.IsVisible("tenant-x", "enterprise-group", descriptor);

            Assert.True(visible);
        }

        [Fact]
        public void IsVisible_Should_Return_True_For_Hybrid_Instance_When_Tenant_Matches()
        {
            var evaluator = CreateEvaluator();

            var descriptor = new AiRuntimeInstanceVisibilityDescriptor
            {
                RuntimeInstanceId = "tenant-b-runtime-1",
                TenantId = "tenant-b",
                IsolationMode = AiRuntimeInstanceIsolationMode.Hybrid,
                AllowSharedFallback = true
            };

            var visible = evaluator.IsVisible("tenant-b", null, descriptor);

            Assert.True(visible);
        }

        [Fact]
        public void IsVisible_Should_Return_True_For_Shared_Instance_When_Hybrid_Tenant_Allows_Fallback()
        {
            var evaluator = CreateEvaluator();

            var descriptor = new AiRuntimeInstanceVisibilityDescriptor
            {
                RuntimeInstanceId = "shared-runtime-1",
                IsolationMode = AiRuntimeInstanceIsolationMode.Shared,
                AllowSharedFallback = true
            };

            var visible = evaluator.IsVisible("tenant-b", null, descriptor);

            Assert.True(visible);
        }

        [Fact]
        public void IsVisible_Should_Return_False_For_Hybrid_Instance_When_Fallback_Is_Disabled()
        {
            var evaluator = CreateEvaluator();

            var descriptor = new AiRuntimeInstanceVisibilityDescriptor
            {
                RuntimeInstanceId = "hybrid-runtime-1",
                IsolationMode = AiRuntimeInstanceIsolationMode.Hybrid,
                AllowSharedFallback = false
            };

            var visible = evaluator.IsVisible("tenant-b", null, descriptor);

            Assert.False(visible);
        }

        [Fact]
        public void CreateDescriptor_Should_Default_To_Shared_When_Metadata_Is_Missing()
        {
            var evaluator = CreateEvaluator();

            var descriptor = evaluator.CreateDescriptor("runtime-1", null);

            Assert.Equal("runtime-1", descriptor.RuntimeInstanceId);
            Assert.Equal(AiRuntimeInstanceIsolationMode.Shared, descriptor.IsolationMode);
            Assert.True(descriptor.AllowSharedFallback);
            Assert.False(descriptor.PreferDedicatedCapacity);
            Assert.Null(descriptor.TenantId);
            Assert.Null(descriptor.TenantGroupId);
        }

        [Fact]
        public void CreateDescriptor_Should_Parse_Metadata()
        {
            var evaluator = CreateEvaluator();

            var metadata = new Dictionary<string, string>
            {
                [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = "tenant-a",
                [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = "enterprise",
                [AiRuntimeInstanceIsolationMetadataKeys.IsolationMode] = "Dedicated",
                [AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback] = "false",
                [AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity] = "true"
            };

            var descriptor = evaluator.CreateDescriptor("runtime-1", metadata);

            Assert.Equal("runtime-1", descriptor.RuntimeInstanceId);
            Assert.Equal("tenant-a", descriptor.TenantId);
            Assert.Equal("enterprise", descriptor.TenantGroupId);
            Assert.Equal(AiRuntimeInstanceIsolationMode.Dedicated, descriptor.IsolationMode);
            Assert.False(descriptor.AllowSharedFallback);
            Assert.True(descriptor.PreferDedicatedCapacity);
        }

        /// <summary>
        /// Verifies that a Dedicated tenant cannot see shared runtime capacity when shared fallback is disabled.
        /// </summary>
        [Fact]
        public void IsVisible_Should_Return_False_For_Shared_Instance_When_Dedicated_Fallback_Is_Disabled()
        {
            var evaluator =
                new AiRuntimeInstanceVisibilityEvaluator(
                    new FixedTenantRuntimeSettingsProvider(
                        isolationMode: AiRuntimeInstanceIsolationMode.Dedicated,
                        allowSharedFallback: false));

            var descriptor =
                new AiRuntimeInstanceVisibilityDescriptor
                {
                    RuntimeInstanceId = "shared-runtime-1",
                    TenantId = null,
                    TenantGroupId = null,
                    IsolationMode = AiRuntimeInstanceIsolationMode.Shared
                };

            var visible =
                evaluator.IsVisible(
                    tenantId: "tenant-dedicated-a",
                    tenantGroupId: "tenant-group-dedicated-a",
                    descriptor: descriptor);

            Assert.False(visible);
        }

        /// <summary>
        /// Verifies that a Hybrid tenant behaves like Dedicated when shared fallback is disabled.
        /// </summary>
        [Fact]
        public void IsVisible_Should_Return_False_For_Shared_Instance_When_Hybrid_Fallback_Is_Disabled()
        {
            var evaluator =
                new AiRuntimeInstanceVisibilityEvaluator(
                    new FixedTenantRuntimeSettingsProvider(
                        isolationMode: AiRuntimeInstanceIsolationMode.Hybrid,
                        allowSharedFallback: false));

            var descriptor =
                new AiRuntimeInstanceVisibilityDescriptor
                {
                    RuntimeInstanceId = "shared-runtime-1",
                    TenantId = null,
                    TenantGroupId = null,
                    IsolationMode = AiRuntimeInstanceIsolationMode.Shared
                };

            var visible =
                evaluator.IsVisible(
                    tenantId: "tenant-hybrid-a",
                    tenantGroupId: "tenant-group-hybrid-a",
                    descriptor: descriptor);

            Assert.False(visible);
        }

        /// <summary>
        /// Verifies that a Hybrid tenant can see shared runtime capacity when shared fallback is enabled.
        /// </summary>
        [Fact]
        public void IsVisible_Should_Return_True_For_Shared_Instance_When_Hybrid_Fallback_Is_Enabled()
        {
            var evaluator =
                new AiRuntimeInstanceVisibilityEvaluator(
                    new FixedTenantRuntimeSettingsProvider(
                        isolationMode: AiRuntimeInstanceIsolationMode.Hybrid,
                        allowSharedFallback: true));

            var descriptor =
                new AiRuntimeInstanceVisibilityDescriptor
                {
                    RuntimeInstanceId = "shared-runtime-1",
                    TenantId = null,
                    TenantGroupId = null,
                    IsolationMode = AiRuntimeInstanceIsolationMode.Shared
                };

            var visible =
                evaluator.IsVisible(
                    tenantId: "tenant-hybrid-a",
                    tenantGroupId: "tenant-group-hybrid-a",
                    descriptor: descriptor);

            Assert.True(visible);
        }

        private static AiRuntimeInstanceVisibilityEvaluator CreateEvaluator()
        {
            return new AiRuntimeInstanceVisibilityEvaluator(
                new HardcodedAiTenantRuntimeSettingsProvider());
        }

        /// <summary>
        /// Provides fixed tenant runtime settings for visibility evaluator tests.
        /// </summary>
        private sealed class FixedTenantRuntimeSettingsProvider : IAiTenantRuntimeSettingsProvider
        {
            private readonly AiRuntimeInstanceIsolationMode isolationMode;
            private readonly bool allowSharedFallback;

            /// <summary>
            /// Initializes a new instance of the <see cref="FixedTenantRuntimeSettingsProvider"/> class.
            /// </summary>
            /// <param name="isolationMode">The tenant isolation mode to return.</param>
            /// <param name="allowSharedFallback">Whether shared fallback is allowed.</param>
            public FixedTenantRuntimeSettingsProvider(
                AiRuntimeInstanceIsolationMode isolationMode,
                bool allowSharedFallback)
            {
                this.isolationMode = isolationMode;
                this.allowSharedFallback = allowSharedFallback;
            }

            /// <inheritdoc />
            public AiTenantRuntimeSettings GetSettings(
                string? tenantId,
                string? tenantGroupId)
            {
                return new AiTenantRuntimeSettings
                {
                    TenantId = tenantId,
                    TenantGroupId = tenantGroupId,
                    IsolationMode = this.isolationMode,
                    PreferDedicatedCapacity = this.isolationMode is AiRuntimeInstanceIsolationMode.Dedicated or AiRuntimeInstanceIsolationMode.Hybrid,
                    AllowSharedFallback = this.allowSharedFallback,
                    MaxRuntimeInstances = 1,
                    WorkerCountPerInstance = 1,
                    MaxConcurrentRunsPerInstance = 1,
                    LocalQueueCapacity = 10,
                    RuntimeInstanceIdPrefix = "test-runtime"
                };
            }
        }
    }
}