using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;

namespace Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Relations
{
    /// <summary>
    /// Provides unit tests for <see cref="AiChildExecutionRelation"/> contract behavior.
    /// </summary>
    public sealed class AiChildExecutionRelationTests
    {
        /// <summary>
        /// Verifies that a newly constructed relation starts in the policy-pending lifecycle without continuation work.
        /// </summary>
        [Fact]
        public void Relation_Should_Default_To_Policy_Pending_Without_Continuation()
        {
            var relation = CreateRelation();

            Assert.Equal(
                AiChildExecutionRelationStatus.DelegationPolicyPending,
                relation.Status);
            Assert.Equal(
                AiChildContinuationStatus.None,
                relation.ContinuationStatus);
            Assert.Null(relation.ChildExecutionId);
            Assert.Null(relation.ChildResult);
            Assert.Null(relation.ChildFailureReason);
        }

        /// <summary>
        /// Verifies that the relation reconstructs the exact authoritative typed invocation identity.
        /// </summary>
        [Fact]
        public void ToInvocationIdentity_Should_Reconstruct_Authoritative_Tuple()
        {
            var relation = CreateRelation();

            var identity = relation.ToInvocationIdentity();

            Assert.Equal(relation.TenantId, identity.TenantId);
            Assert.Equal(relation.ParentExecutionId, identity.ParentExecutionId);
            Assert.Equal(relation.ParentCallSiteId, identity.ParentCallSiteId);
            Assert.Equal(relation.ChildDagId, identity.ChildDagId);
            Assert.Equal(relation.ChildDagDefinitionVersion, identity.ChildDagDefinitionVersion);
            Assert.Equal(relation.CanonicalLogicalInvocationKey, identity.CanonicalLogicalInvocationKey);
            Assert.Equal(relation.InvocationGeneration, identity.InvocationGeneration);
        }

        /// <summary>
        /// Verifies that the relation directly reuses the existing execution payload contract for frozen definition and input data.
        /// </summary>
        [Fact]
        public void Relation_Should_Reuse_AiStoredPayload_For_Frozen_Definition_Input_And_Policy_Binding()
        {
            var relation = CreateRelation();

            Assert.IsType<AiStoredPayload>(relation.FrozenChildDagDefinition);
            Assert.IsType<AiStoredPayload>(relation.FrozenInvocationInput);
            Assert.IsType<AiStoredPayload>(relation.DelegationPolicyBindingSnapshot);
            Assert.Equal("definition-hash", relation.FrozenChildDagDefinition.ContentHash);
            Assert.Equal("input-hash", relation.FrozenInvocationInput.ContentHash);
            Assert.Equal("policy-binding-hash", relation.DelegationPolicyBindingSnapshot.ContentHash);
        }

        /// <summary>
        /// Creates a complete child execution relation suitable for contract tests.
        /// </summary>
        /// <returns>A generation-zero child execution relation.</returns>
        private static AiChildExecutionRelation CreateRelation()
        {
            return new AiChildExecutionRelation
            {
                TenantId = "tenant-a",
                ParentExecutionId = "parent-execution-001",
                ParentCallSiteId = "portfolio-analysis",
                ChildDagId = "market-analysis",
                ChildDagDefinitionVersion = "2026-08-14.1",
                FrozenChildDagDefinition = new AiStoredPayload
                {
                    IsInline = true,
                    InlineValue = "{\"name\":\"market-analysis\"}",
                    ContentHash = "definition-hash",
                    ContentType = "application/json"
                },
                CanonicalLogicalInvocationKey = "portfolio-42|MSFT|fundamental-research",
                ChildInvocationKey = "child-invocation-test",
                InvocationGeneration = 0,
                FrozenInvocationInput = new AiStoredPayload
                {
                    IsInline = true,
                    InlineValue = "{\"ticker\":\"MSFT\"}",
                    ContentHash = "input-hash",
                    ContentType = "application/json"
                },
                DelegationPolicyBindingSnapshot = new AiStoredPayload
                {
                    IsInline = true,
                    InlineValue = "{\"Policies\":[]}",
                    ContentHash = "policy-binding-hash",
                    ContentType = "application/json"
                },
                CreatedAtUtc = new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero)
            };
        }
    }
}
