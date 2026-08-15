using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Execution.Payloads.Resolvers;
using Multiplexed.Abstractions.AI.Execution.Payloads.Stores;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Identity;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Snapshots;
using Multiplexed.AI.Runtime.Execution.Payloads;
using Multiplexed.AI.Runtime.Execution.State;

namespace Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Support
{
    /// <summary>
    /// Creates consistent durable parent, child, relation, and payload state for child-composition unit tests.
    /// </summary>
    internal static class ChildDagCompositionTestData
    {
        public const string TenantId = "tenant-1";
        public const string ParentExecutionId = "parent-execution-1";
        public const string ParentCallSiteId = "research-call-site";
        public const string ChildExecutionId = "child-execution-1";

        public static AiChildExecutionRelation CreateRelation(
            AiChildExecutionRelationStatus status,
            AiChildContinuationStatus continuationStatus = AiChildContinuationStatus.None,
            DateTimeOffset? childAllocatedAtUtc = null,
            int invocationGeneration = 0,
            string? childFailureReason = null)
        {
            var identity = new Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Identity.AiChildInvocationIdentity
            {
                TenantId = TenantId,
                ParentExecutionId = ParentExecutionId,
                ParentCallSiteId = ParentCallSiteId,
                ChildDagId = "child-analysis",
                ChildDagDefinitionVersion = "v1",
                CanonicalLogicalInvocationKey = "portfolio-42|MSFT|analysis",
                InvocationGeneration = invocationGeneration
            };
            var hasAllocatedChild = status is AiChildExecutionRelationStatus.ChildAllocated
                or AiChildExecutionRelationStatus.Waiting
                or AiChildExecutionRelationStatus.Completed;
            var hasWaited = status is AiChildExecutionRelationStatus.Waiting
                or AiChildExecutionRelationStatus.Completed;
            var isCompleted = status == AiChildExecutionRelationStatus.Completed;

            return new AiChildExecutionRelation
            {
                TenantId = identity.TenantId,
                ParentExecutionId = identity.ParentExecutionId,
                ParentCallSiteId = identity.ParentCallSiteId,
                ChildDagId = identity.ChildDagId,
                ChildDagDefinitionVersion = identity.ChildDagDefinitionVersion,
                FrozenChildDagDefinition = Snapshot(),
                CanonicalLogicalInvocationKey = identity.CanonicalLogicalInvocationKey,
                ChildInvocationKey = AiChildInvocationKeyFactory.Create(identity),
                InvocationGeneration = identity.InvocationGeneration,
                FrozenInvocationInput = Snapshot(),
                DelegationPolicyBindingSnapshot = Snapshot(),
                DelegationPolicyDecisionSnapshot = Snapshot(),
                Status = status,
                ChildExecutionId = hasAllocatedChild ? ChildExecutionId : null,
                ContinuationStatus = continuationStatus,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                DelegationEvaluatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-4),
                ChildAllocatedAtUtc = hasAllocatedChild
                    ? childAllocatedAtUtc ?? DateTimeOffset.UtcNow.AddMinutes(-3)
                    : null,
                WaitingAtUtc = hasWaited
                    ? DateTimeOffset.UtcNow.AddMinutes(-2)
                    : null,
                CompletedAtUtc = isCompleted
                    ? DateTimeOffset.UtcNow.AddMinutes(-1)
                    : null,
                ChildResult = isCompleted
                    ? Snapshot()
                    : null,
                ChildFailureReason = isCompleted
                    ? childFailureReason
                    : null,
                ParentContinuationScheduledAtUtc = continuationStatus is AiChildContinuationStatus.Scheduled or AiChildContinuationStatus.Resumed
                    ? DateTimeOffset.UtcNow.AddSeconds(-30)
                    : null,
                ParentContinuationScheduledStepVersion = continuationStatus is AiChildContinuationStatus.Scheduled or AiChildContinuationStatus.Resumed
                    ? 10
                    : null,
                ParentResumedAtUtc = continuationStatus == AiChildContinuationStatus.Resumed
                    ? DateTimeOffset.UtcNow.AddSeconds(-10)
                    : null,
                ParentContinuationSuppressedAtUtc = continuationStatus == AiChildContinuationStatus.Suppressed
                    ? DateTimeOffset.UtcNow.AddSeconds(-10)
                    : null,
                ParentContinuationSuppressionReason = continuationStatus == AiChildContinuationStatus.Suppressed
                    ? "Parent execution is terminal."
                    : null
            };
        }

        public static AiExecutionRecord CreateParentRecord(AiExecutionStatus status = AiExecutionStatus.Waiting)
        {
            return new AiExecutionRecord
            {
                ExecutionId = ParentExecutionId,
                PipelineName = "parent-pipeline",
                ExecutionMode = AiExecutionMode.Dag,
                Status = status,
                Steps = [ParentCallSiteId],
                ExecutionContextSnapshot = ExecutionContext(),
                CompletedAtUtc = status is AiExecutionStatus.Completed or AiExecutionStatus.Failed or AiExecutionStatus.Cancelled
                    ? DateTime.UtcNow
                    : default
            };
        }

        public static AiExecutionState CreateParentState(
            AiStepExecutionStatus stepStatus,
            int? claimTimeoutSeconds = 30,
            DateTime? updatedAtUtc = null,
            long version = 0)
        {
            return new AiExecutionState
            {
                ExecutionId = ParentExecutionId,
                PipelineName = "parent-pipeline",
                Steps = new Dictionary<string, AiStepState>(StringComparer.Ordinal)
                {
                    [ParentCallSiteId] = new AiStepState
                    {
                        StepName = ParentCallSiteId,
                        Status = stepStatus,
                        ClaimTimeoutSeconds = claimTimeoutSeconds,
                        RecoveryCount = 3,
                        UpdatedAtUtc = updatedAtUtc,
                        Version = version
                    }
                }
            };
        }

        public static AiExecutionRecord CreateChildRecord(AiExecutionStatus status = AiExecutionStatus.Completed)
        {
            return new AiExecutionRecord
            {
                ExecutionId = ChildExecutionId,
                PipelineName = "child-analysis",
                ExecutionMode = AiExecutionMode.Dag,
                Status = status,
                ExecutionContextSnapshot = ExecutionContext(),
                CompletedAtUtc = status is AiExecutionStatus.Completed or AiExecutionStatus.Failed or AiExecutionStatus.Cancelled
                    ? DateTime.UtcNow
                    : default
            };
        }

        public static AiExecutionState CreateChildState(string value)
        {
            return new AiExecutionState
            {
                ExecutionId = ChildExecutionId,
                PipelineName = "child-analysis",
                Data = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["result"] = value
                }
            };
        }

        /// <summary>
        /// Creates a minimal execution context for child-composition unit tests that need normal state helpers.
        /// </summary>
        /// <param name="record">The durable execution record.</param>
        /// <param name="state">The mutable execution state.</param>
        /// <returns>An execution context backed by the shared inline-only test payload resolver.</returns>
        public static AiExecutionContext CreateExecutionContext(
            AiExecutionRecord record,
            AiExecutionState state)
        {
            return new AiExecutionContext(
                record,
                state,
                EmptyServiceProvider.Instance,
                new DefaultAiExecutionStateReader(InlinePayloadResolver.Instance),
                new DefaultAiExecutionStateWriter(),
                CancellationToken.None);
        }

        public static AiChildDagSnapshotService CreateSnapshotService()
        {
            var store = new InMemoryAiPayloadStore();
            var options = Options.Create(
                new AiPayloadStoreOptions
                {
                    Enabled = true,
                    Provider = "inmemory",
                    RequireReplaySafePayloads = false,
                    MaxInlineSizeBytes = 64 * 1024
                });

            return new AiChildDagSnapshotService(
                new FixedPayloadStoreResolver(store),
                options);
        }

        public static AiStoredPayload Snapshot()
        {
            return AiStoredPayload.Inline(
                "{}",
                contentType: "application/json",
                contentHash: "44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a");
        }

        private static ExecutionContextSnapshot ExecutionContext()
        {
            return new ExecutionContextSnapshot
            {
                ContextKey = "parent-context",
                Project = "tests",
                UserId = "user-1",
                TenantId = TenantId,
                TenantGroupId = "tenant-group-1",
                CurrentNamespace = "default",
                Namespaces = [],
                TtlSeconds = 300
            };
        }

        private sealed class EmptyServiceProvider : IServiceProvider
        {
            public static EmptyServiceProvider Instance { get; } = new();

            public object? GetService(Type serviceType) => null;
        }

        private sealed class InlinePayloadResolver : IAiExecutionPayloadResolver
        {
            public static InlinePayloadResolver Instance { get; } = new();

            public Task<object?> ResolveAsync(
                AiStoredPayload payload,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(payload.InlineValue);
            }
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
