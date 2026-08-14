using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Delegation;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Execution.Payloads.Resolvers;
using Multiplexed.Abstractions.AI.Execution.Payloads.Stores;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Snapshots;
using Multiplexed.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.Execution.Payloads;

namespace Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Snapshots
{
    /// <summary>
    /// Validates deterministic immutable child DAG snapshot creation.
    /// </summary>
    public sealed class AiChildDagSnapshotServiceTests
    {
        [Fact]
        public async Task FreezeDefinitionAsync_Should_Produce_Stable_Canonical_Inline_Snapshot()
        {
            var store = new InMemoryAiPayloadStore();
            var service = CreateService(store, maxInlineSizeBytes: 64 * 1024);
            var firstDefinition = CreateDefinition(
                new Dictionary<string, object?>
                {
                    ["zeta"] = 2,
                    ["alpha"] = 1
                });
            var secondDefinition = CreateDefinition(
                new Dictionary<string, object?>
                {
                    ["alpha"] = 1,
                    ["zeta"] = 2
                });

            var first = await service.FreezeDefinitionAsync(firstDefinition, "parent-1");
            var second = await service.FreezeDefinitionAsync(secondDefinition, "parent-1");

            Assert.True(first.IsInline);
            Assert.True(second.IsInline);
            Assert.Equal(first.ContentHash, second.ContentHash);
            Assert.Equal(first.InlineValue, second.InlineValue);
            Assert.False(string.IsNullOrWhiteSpace(first.ContentHash));
        }

        [Fact]
        public async Task FreezeDefinitionAsync_Should_RoundTrip_Through_Existing_Runtime_Pipeline_Json_Resolver()
        {
            var service = CreateService(new InMemoryAiPayloadStore(), maxInlineSizeBytes: 64 * 1024);
            var definition = new AiPipelineDefinition
            {
                Name = "child-analysis",
                Version = "v7",
                ExecutionMode = AiExecutionMode.Dag,
                Config = new Dictionary<string, object?>
                {
                    ["market"] = "multi-asset",
                    ["riskLimit"] = 0.01m
                },
                Steps =
                [
                    new AiPipelineStepDefinition
                    {
                        Name = "analyze",
                        StepKey = "analysis",
                        Order = 0,
                        Config = new Dictionary<string, object?>
                        {
                            ["timeframe"] = "1h"
                        }
                    }
                ]
            };

            var snapshot = await service.FreezeDefinitionAsync(definition, "parent-1");
            var pipelineJson = await service.LoadDefinitionJsonAsync(snapshot);
            var resolver = new AiRuntimePipelineRunDefinitionResolver();

            var resolved = await resolver.ResolveAsync(
                new AiRuntimePipelineRunRequest
                {
                    PipelineName = definition.Name,
                    PipelineJson = pipelineJson
                });

            Assert.Equal(definition.Name, resolved.Name);
            Assert.Equal(definition.Version, resolved.Version);
            Assert.Equal(AiExecutionMode.Dag, resolved.ExecutionMode);
            Assert.Equal("analyze", Assert.Single(resolved.Steps).Name);
            Assert.True(resolved.Config.ContainsKey("market"));
            Assert.True(resolved.Config.ContainsKey("riskLimit"));
        }

        [Fact]
        public async Task FreezeInvocationInputAsync_Should_Reuse_Same_Content_Addressed_Artifact()
        {
            var store = new InMemoryAiPayloadStore();
            var service = CreateService(store, maxInlineSizeBytes: 1);
            var input = new Dictionary<string, object?>
            {
                ["portfolio"] = "portfolio-42",
                ["ticker"] = "MSFT"
            };

            var first = await service.FreezeInvocationInputAsync(input, "parent-1");
            var second = await service.FreezeInvocationInputAsync(input, "parent-1");

            Assert.False(first.IsInline);
            Assert.False(second.IsInline);
            Assert.Equal(first.ArtifactId, second.ArtifactId);
            Assert.Equal(first.ContentHash, second.ContentHash);
            Assert.StartsWith("immutable-sha256-", first.ArtifactId!);

            var content = await service.LoadAndVerifyAsync(second);
            Assert.False(string.IsNullOrWhiteSpace(content));
        }

        [Fact]
        public async Task FreezeDelegationPolicyBindingAsync_Should_RoundTrip_Exact_Frozen_Binding()
        {
            var service = CreateService(new InMemoryAiPayloadStore(), maxInlineSizeBytes: 64 * 1024);
            var definition = new AiChildDelegationPolicyDefinition
            {
                Policies =
                [
                    new AiConfiguredPolicyDefinition
                    {
                        Name = "delegation.test",
                        Config = new Dictionary<string, object?>
                        {
                            ["maxRisk"] = 0.01m,
                            ["requiredApproval"] = true
                        }
                    }
                ]
            };

            var snapshot = await service.FreezeDelegationPolicyBindingAsync(definition, "parent-1");
            var restored = await service.LoadDelegationPolicyBindingAsync(snapshot);

            var policy = Assert.Single(restored.Policies);
            Assert.Equal("delegation.test", policy.Name);
            Assert.Equal(2, policy.Config.Count);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.ContentHash));
        }

        [Fact]
        public async Task FreezeDelegationPolicyDecisionAsync_Should_Produce_Stable_Historical_Snapshot()
        {
            var service = CreateService(new InMemoryAiPayloadStore(), maxInlineSizeBytes: 64 * 1024);
            var results = new[]
            {
                AiPolicyResult.Success("approved")
            };

            var first = await service.FreezeDelegationPolicyDecisionAsync(
                approved: true,
                reason: "approved",
                results,
                "parent-1");
            var second = await service.FreezeDelegationPolicyDecisionAsync(
                approved: true,
                reason: "approved",
                results,
                "parent-1");

            Assert.Equal(first.ContentHash, second.ContentHash);
            Assert.Equal(first.InlineValue, second.InlineValue);
            Assert.Contains("approved", Assert.IsType<string>(first.InlineValue), StringComparison.Ordinal);
        }

        [Fact]
        public async Task FreezeDefinitionAsync_Should_Require_Explicit_Definition_Version()
        {
            var service = CreateService(new InMemoryAiPayloadStore(), maxInlineSizeBytes: 1024);
            var definition = new AiPipelineDefinition
            {
                Name = "child-analysis",
                Version = null,
                Steps = Array.Empty<AiPipelineStepDefinition>()
            };

            await Assert.ThrowsAnyAsync<ArgumentException>(
                () => service.FreezeDefinitionAsync(definition, "parent-1"));
        }

        private static AiChildDagSnapshotService CreateService(
            IAiPayloadStore store,
            int maxInlineSizeBytes)
        {
            var options = Options.Create(
                new AiPayloadStoreOptions
                {
                    Enabled = true,
                    Provider = "inmemory",
                    RequireReplaySafePayloads = false,
                    MaxInlineSizeBytes = maxInlineSizeBytes
                });

            return new AiChildDagSnapshotService(
                new FixedPayloadStoreResolver(store),
                options);
        }

        private static AiPipelineDefinition CreateDefinition(
            IReadOnlyDictionary<string, object?> config)
        {
            return new AiPipelineDefinition
            {
                Name = "child-analysis",
                Version = "v1",
                Config = config,
                Steps =
                [
                    new AiPipelineStepDefinition
                    {
                        Name = "analyze",
                        StepKey = "analysis",
                        Order = 0
                    }
                ]
            };
        }

        private sealed class FixedPayloadStoreResolver : IAiPayloadStoreResolver
        {
            private readonly IAiPayloadStore store;

            public FixedPayloadStoreResolver(IAiPayloadStore store)
            {
                this.store = store;
            }

            public IAiPayloadStore Resolve() => this.store;
        }
    }
}
