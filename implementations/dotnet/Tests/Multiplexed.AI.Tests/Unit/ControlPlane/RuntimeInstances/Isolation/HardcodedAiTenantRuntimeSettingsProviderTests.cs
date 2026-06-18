using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Isolation;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Isolation
{
    public sealed class HardcodedAiTenantRuntimeSettingsProviderTests
    {
        [Fact]
        public void GetSettings_Should_Return_Dedicated_Settings_For_Tenant_A()
        {
            var provider = new HardcodedAiTenantRuntimeSettingsProvider();

            var settings = provider.GetSettings("tenant-a", null);

            Assert.Equal("tenant-a", settings.TenantId);
            Assert.Equal(AiRuntimeInstanceIsolationMode.Dedicated, settings.IsolationMode);
            Assert.True(settings.PreferDedicatedCapacity);
            Assert.False(settings.AllowSharedFallback);
            Assert.Equal(3, settings.MaxRuntimeInstances);
            Assert.Equal(10, settings.WorkerCountPerInstance);
            Assert.Equal(5, settings.MaxConcurrentRunsPerInstance);
            Assert.Equal(500, settings.LocalQueueCapacity);
            Assert.Equal("tenant-a-runtime", settings.RuntimeInstanceIdPrefix);
        }

        [Fact]
        public void GetSettings_Should_Return_Hybrid_Settings_For_Tenant_B()
        {
            var provider = new HardcodedAiTenantRuntimeSettingsProvider();

            var settings = provider.GetSettings("tenant-b", null);

            Assert.Equal("tenant-b", settings.TenantId);
            Assert.Equal(AiRuntimeInstanceIsolationMode.Hybrid, settings.IsolationMode);
            Assert.True(settings.PreferDedicatedCapacity);
            Assert.True(settings.AllowSharedFallback);
            Assert.Equal(2, settings.MaxRuntimeInstances);
            Assert.Equal(5, settings.WorkerCountPerInstance);
            Assert.Equal(3, settings.MaxConcurrentRunsPerInstance);
            Assert.Equal(250, settings.LocalQueueCapacity);
            Assert.Equal("tenant-b-runtime", settings.RuntimeInstanceIdPrefix);
        }

        [Fact]
        public void GetSettings_Should_Return_Shared_Settings_For_Unknown_Tenant()
        {
            var provider = new HardcodedAiTenantRuntimeSettingsProvider();

            var settings = provider.GetSettings("tenant-x", null);

            Assert.Equal("tenant-x", settings.TenantId);
            Assert.Equal(AiRuntimeInstanceIsolationMode.Shared, settings.IsolationMode);
            Assert.False(settings.PreferDedicatedCapacity);
            Assert.True(settings.AllowSharedFallback);
            Assert.Equal(1, settings.MaxRuntimeInstances);
            Assert.Equal(10, settings.WorkerCountPerInstance);
            Assert.Equal(3, settings.MaxConcurrentRunsPerInstance);
            Assert.Null(settings.LocalQueueCapacity);
            Assert.Equal("runtime-instance", settings.RuntimeInstanceIdPrefix);
        }

        [Fact]
        public void GetSettings_Should_Return_Shared_Settings_When_Tenant_Is_Null()
        {
            var provider = new HardcodedAiTenantRuntimeSettingsProvider();

            var settings = provider.GetSettings(null, null);

            Assert.Equal("shared", settings.TenantId);
            Assert.Equal(AiRuntimeInstanceIsolationMode.Shared, settings.IsolationMode);
            Assert.False(settings.PreferDedicatedCapacity);
            Assert.True(settings.AllowSharedFallback);
            Assert.Equal("runtime-instance", settings.RuntimeInstanceIdPrefix);
        }

        [Fact]
        public void GetSettings_Should_Preserve_Tenant_Group_Id()
        {
            var provider = new HardcodedAiTenantRuntimeSettingsProvider();

            var settings = provider.GetSettings("tenant-a", "group-enterprise");

            Assert.Equal("tenant-a", settings.TenantId);
            Assert.Equal("group-enterprise", settings.TenantGroupId);
            Assert.Equal(AiRuntimeInstanceIsolationMode.Dedicated, settings.IsolationMode);
        }

        [Fact]
        public void GetSettings_Should_Be_Case_Insensitive_For_Known_Tenants()
        {
            var provider = new HardcodedAiTenantRuntimeSettingsProvider();

            var settings = provider.GetSettings("TENANT-A", null);

            Assert.Equal("tenant-a", settings.TenantId);
            Assert.Equal(AiRuntimeInstanceIsolationMode.Dedicated, settings.IsolationMode);
            Assert.False(settings.AllowSharedFallback);
        }
    }
}