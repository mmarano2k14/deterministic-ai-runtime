using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.SharedTests
{
    /// <summary>
    /// Contains tests for <see cref="ProductionTenantRuntimeModeMapper"/>.
    /// </summary>
    public sealed class ProductionTenantRuntimeModeMapperTests
    {
        /// <summary>
        /// Verifies that Dedicated runtime mode maps to dedicated isolation without shared fallback.
        /// </summary>
        [Fact]
        public void Resolve_Should_Map_Dedicated_Runtime_Mode()
        {
            var isolationMode =
                ProductionTenantRuntimeModeMapper.ResolveIsolationMode(
                    ProductionTenantRuntimeMode.Dedicated);

            var preferDedicatedCapacity =
                ProductionTenantRuntimeModeMapper.ResolvePreferDedicatedCapacity(
                    ProductionTenantRuntimeMode.Dedicated);

            var allowSharedFallback =
                ProductionTenantRuntimeModeMapper.ResolveAllowSharedFallback(
                    ProductionTenantRuntimeMode.Dedicated);

            Assert.Equal(
                AiRuntimeInstanceIsolationMode.Dedicated,
                isolationMode);

            Assert.True(
                preferDedicatedCapacity);

            Assert.False(
                allowSharedFallback);
        }

        /// <summary>
        /// Verifies that Shared runtime mode maps to shared isolation with shared fallback enabled.
        /// </summary>
        [Fact]
        public void Resolve_Should_Map_Shared_Runtime_Mode()
        {
            var isolationMode =
                ProductionTenantRuntimeModeMapper.ResolveIsolationMode(
                    ProductionTenantRuntimeMode.Shared);

            var preferDedicatedCapacity =
                ProductionTenantRuntimeModeMapper.ResolvePreferDedicatedCapacity(
                    ProductionTenantRuntimeMode.Shared);

            var allowSharedFallback =
                ProductionTenantRuntimeModeMapper.ResolveAllowSharedFallback(
                    ProductionTenantRuntimeMode.Shared);

            Assert.Equal(
                AiRuntimeInstanceIsolationMode.Shared,
                isolationMode);

            Assert.False(
                preferDedicatedCapacity);

            Assert.True(
                allowSharedFallback);
        }

        /// <summary>
        /// Verifies that Hybrid runtime mode maps to hybrid isolation with dedicated preference and shared fallback enabled.
        /// </summary>
        [Fact]
        public void Resolve_Should_Map_Hybrid_Runtime_Mode()
        {
            var isolationMode =
                ProductionTenantRuntimeModeMapper.ResolveIsolationMode(
                    ProductionTenantRuntimeMode.Hybrid);

            var preferDedicatedCapacity =
                ProductionTenantRuntimeModeMapper.ResolvePreferDedicatedCapacity(
                    ProductionTenantRuntimeMode.Hybrid);

            var allowSharedFallback =
                ProductionTenantRuntimeModeMapper.ResolveAllowSharedFallback(
                    ProductionTenantRuntimeMode.Hybrid);

            Assert.Equal(
                AiRuntimeInstanceIsolationMode.Hybrid,
                isolationMode);

            Assert.True(
                preferDedicatedCapacity);

            Assert.True(
                allowSharedFallback);
        }
    }
}