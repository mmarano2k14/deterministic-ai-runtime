using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.AI.Stores;
using System.Reflection;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Ownership;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.SharedController.Ownership
{
    /// <summary>
    /// Unit tests for <see cref="AiSharedRunOwnershipResolver"/>.
    /// </summary>
    public sealed class AiSharedRunOwnershipResolverTests
    {
        /// <summary>
        /// Verifies that shared run ownership is resolved from runtime instance id, local run id, and execution id.
        /// </summary>
        [Fact]
        public async Task ResolveAsync_Should_Resolve_Dispatched_Shared_Run_Ownership()
        {
            var sharedQueue = new InMemoryAiSharedQueue();
            var sharedRunStore = new InMemoryAiSharedRunStore();
            var resolver = new AiSharedRunOwnershipResolver(sharedQueue, sharedRunStore);

            const string runtimeInstanceId = "runtime-tenant-a-1";
            const string sharedRunId = "shared-run-1";
            const string localRunId = "local-run-1";
            const string executionId = "execution-1";

            var contextSnapshot = CreateExecutionContextSnapshot("tenant-a", "tenant-group-a");

            await sharedRunStore.CreateAsync(new AiSharedRunRecord
            {
                SharedRunId = sharedRunId,
                Status = AiSharedRunStatus.QueuedGlobally,
                RunRequest = CreateRunRequest(contextSnapshot),
                ExecutionContextSnapshot = contextSnapshot,
                PipelineKey = "ownership-test",
                CorrelationId = "correlation-ownership-test",
                RequestedBy = "test",
                Source = "unit-test",
                Reason = "created-for-ownership-resolution",
                SubmittedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>
                {
                    ["scenario"] = "ownership-resolution"
                }
            });

            await sharedQueue.EnqueueAsync(new AiSharedQueueItem
            {
                SharedRunId = sharedRunId,
                Status = AiSharedQueueItemStatus.Pending,
                ExecutionContextSnapshot = contextSnapshot,
                PipelineKey = "ownership-test",
                Priority = 0,
                EnqueuedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>
                {
                    ["scenario"] = "ownership-resolution"
                }
            });

            var claimed = await sharedQueue.ClaimNextAsync(new AiSharedQueueClaimRequest
            {
                RuntimeInstanceId = runtimeInstanceId,
                WorkerId = "worker-1",
                TenantId = "tenant-a",
                PipelineKey = "ownership-test",
                ClaimTtl = TimeSpan.FromMinutes(5),
                Reason = "test-claim"
            });

            Assert.NotNull(claimed);
            Assert.False(string.IsNullOrWhiteSpace(claimed!.ClaimToken));

            await sharedQueue.MarkDispatchedAsync(
                sharedRunId,
                claimed.ClaimToken!,
                reason: "test-dispatch");

            await sharedRunStore.MarkDispatchedAsync(
                sharedRunId,
                runtimeInstanceId,
                localRunId,
                executionId,
                reason: "test-dispatch");

            var result = await resolver.ResolveAsync(new AiSharedRunOwnershipResolutionRequest
            {
                RuntimeInstanceId = runtimeInstanceId,
                LocalRunId = localRunId,
                ExecutionId = executionId,
                TenantId = "tenant-a",
                TenantGroupId = "tenant-group-a"
            });

            Assert.True(result.Resolved);
            Assert.True(result.CanRecover);
            Assert.Equal(sharedRunId, result.SharedRunId);
            Assert.Equal(runtimeInstanceId, result.RuntimeInstanceId);
            Assert.Equal(localRunId, result.LocalRunId);
            Assert.Equal(executionId, result.ExecutionId);
            Assert.Equal("tenant-a", result.TenantId);
            Assert.Equal("tenant-group-a", result.TenantGroupId);
            Assert.Equal(AiSharedQueueItemStatus.Dispatched, result.QueueStatus);
            Assert.Equal(AiSharedRunStatus.Dispatched, result.SharedRunStatus);
            Assert.Equal(claimed.ClaimToken, result.ClaimToken);
            Assert.False(result.IsExternalWaitContinuation);
            Assert.Equal("shared-run-ownership-resolved", result.Reason);
        }

        /// <summary>
        /// Verifies that ownership resolution preserves the authoritative external-wait continuation semantic.
        /// </summary>
        [Fact]
        public async Task ResolveAsync_Should_Identify_External_Wait_Continuation_Ownership()
        {
            var sharedQueue = new InMemoryAiSharedQueue();
            var sharedRunStore = new InMemoryAiSharedRunStore();
            var resolver = new AiSharedRunOwnershipResolver(sharedQueue, sharedRunStore);

            const string runtimeInstanceId = "runtime-tenant-a-1";
            const string sharedRunId = "child-continuation-child-invocation-1";
            const string localRunId = "local-run-continuation-1";
            const string executionId = "parent-execution-1";

            var contextSnapshot =
                CreateExecutionContextSnapshot(
                    "tenant-a",
                    "tenant-group-a");

            await sharedRunStore.CreateAsync(
                new AiSharedRunRecord
                {
                    SharedRunId = sharedRunId,
                    Status = AiSharedRunStatus.QueuedGlobally,
                    RunRequest =
                        new AiRuntimePipelineRunRequest
                        {
                            PipelineName = "ownership-test",
                            ExecutionContextSnapshot = contextSnapshot,
                            ExternalWaitContinuation =
                                new AiRuntimeExternalWaitContinuation
                                {
                                    ExecutionId = executionId,
                                    StepName = "execute-child-dag",
                                    ContinuationId =
                                        "child-continuation:child-invocation-1"
                                }
                        },
                    ExecutionContextSnapshot = contextSnapshot,
                    PipelineKey = "ownership-test",
                    CorrelationId = "child-continuation:child-invocation-1",
                    SubmittedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });

            await sharedQueue.EnqueueAsync(
                new AiSharedQueueItem
                {
                    SharedRunId = sharedRunId,
                    Status = AiSharedQueueItemStatus.Pending,
                    ExecutionContextSnapshot = contextSnapshot,
                    PipelineKey = "ownership-test",
                    Priority = 0,
                    EnqueuedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });

            var claimed =
                await sharedQueue.ClaimNextAsync(
                    new AiSharedQueueClaimRequest
                    {
                        RuntimeInstanceId = "mcp-control-plane",
                        WorkerId = "worker-1",
                        TenantId = "tenant-a",
                        PipelineKey = "ownership-test",
                        ClaimTtl = TimeSpan.FromMinutes(5),
                        Reason = "test-claim"
                    });

            Assert.NotNull(claimed);

            await sharedQueue.MarkDispatchedAsync(
                sharedRunId,
                claimed!.ClaimToken!,
                reason: "test-dispatch");

            await sharedRunStore.MarkDispatchedAsync(
                sharedRunId,
                runtimeInstanceId,
                localRunId,
                executionId,
                reason: "test-dispatch");

            var result =
                await resolver.ResolveAsync(
                    new AiSharedRunOwnershipResolutionRequest
                    {
                        RuntimeInstanceId = runtimeInstanceId,
                        SharedRunId = sharedRunId,
                        LocalRunId = localRunId,
                        ExecutionId = executionId,
                        TenantId = "tenant-a",
                        TenantGroupId = "tenant-group-a"
                    });

            Assert.True(result.Resolved);
            Assert.True(result.CanRecover);
            Assert.True(result.IsExternalWaitContinuation);
            Assert.Equal(sharedRunId, result.SharedRunId);
            Assert.Equal(executionId, result.ExecutionId);
            Assert.Equal(localRunId, result.LocalRunId);
            Assert.Equal(runtimeInstanceId, result.RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that an external-wait continuation remains recoverable while its parent is still non-terminal.
        /// This preserves the finalization re-drive contract.
        /// </summary>
        [Fact]
        public async Task ResolveAsync_Should_Keep_External_Wait_Continuation_Recoverable_While_Parent_Is_NonTerminal()
        {
            var fixture =
                await CreateDispatchedExternalWaitContinuationFixtureAsync();

            using var serviceProvider =
                CreateDagStoreServiceProvider(
                    new AiExecutionRecord
                    {
                        ExecutionId = fixture.ExecutionId,
                        PipelineName = "ownership-test",
                        ExecutionMode = AiExecutionMode.Dag,
                        Status = AiExecutionStatus.Running,
                        Steps = new List<string>
                        {
                            "execute-child-dag"
                        }
                    });

            var resolver =
                new AiSharedRunOwnershipResolver(
                    fixture.SharedQueue,
                    fixture.SharedRunStore,
                    serviceProvider.GetRequiredService<IServiceScopeFactory>());

            var result =
                await resolver.ResolveAsync(fixture.Request);

            Assert.True(result.Resolved);
            Assert.True(result.IsExternalWaitContinuation);
            Assert.True(result.CanRecover);
            Assert.Equal(
                "shared-run-ownership-resolved",
                result.Reason);
        }

        /// <summary>
        /// Verifies that a stale physical continuation delivery cannot be recovered after its authoritative
        /// parent execution has already reached a terminal lifecycle state.
        /// </summary>
        [Theory]
        [InlineData(AiExecutionStatus.Completed)]
        [InlineData(AiExecutionStatus.Failed)]
        [InlineData(AiExecutionStatus.Cancelled)]
        public async Task ResolveAsync_Should_Reject_External_Wait_Continuation_Recovery_When_Parent_Is_Terminal(
            AiExecutionStatus parentStatus)
        {
            var fixture =
                await CreateDispatchedExternalWaitContinuationFixtureAsync();

            using var serviceProvider =
                CreateDagStoreServiceProvider(
                    new AiExecutionRecord
                    {
                        ExecutionId = fixture.ExecutionId,
                        PipelineName = "ownership-test",
                        ExecutionMode = AiExecutionMode.Dag,
                        Status = parentStatus,
                        Steps = new List<string>
                        {
                            "execute-child-dag"
                        }
                    });

            var resolver =
                new AiSharedRunOwnershipResolver(
                    fixture.SharedQueue,
                    fixture.SharedRunStore,
                    serviceProvider.GetRequiredService<IServiceScopeFactory>());

            var result =
                await resolver.ResolveAsync(fixture.Request);

            Assert.True(result.Resolved);
            Assert.True(result.IsExternalWaitContinuation);
            Assert.False(result.CanRecover);
            Assert.Equal(fixture.SharedRunId, result.SharedRunId);
            Assert.Equal(fixture.ExecutionId, result.ExecutionId);
            Assert.Equal(fixture.LocalRunId, result.LocalRunId);
            Assert.Equal(fixture.RuntimeInstanceId, result.RuntimeInstanceId);
            Assert.Equal(
                "external-wait-continuation-parent-terminal",
                result.Reason);
        }

        /// <summary>
        /// Verifies that missing parent evidence does not suppress continuation recovery.
        /// Recovery is denied only from positive authoritative terminal evidence.
        /// </summary>
        [Fact]
        public async Task ResolveAsync_Should_Keep_External_Wait_Continuation_Recoverable_When_Parent_Record_Is_Missing()
        {
            var fixture =
                await CreateDispatchedExternalWaitContinuationFixtureAsync();

            using var serviceProvider =
                CreateDagStoreServiceProvider(parentRecord: null);

            var resolver =
                new AiSharedRunOwnershipResolver(
                    fixture.SharedQueue,
                    fixture.SharedRunStore,
                    serviceProvider.GetRequiredService<IServiceScopeFactory>());

            var result =
                await resolver.ResolveAsync(fixture.Request);

            Assert.True(result.Resolved);
            Assert.True(result.IsExternalWaitContinuation);
            Assert.True(result.CanRecover);
            Assert.Equal(
                "shared-run-ownership-resolved",
                result.Reason);
        }

        /// <summary>
        /// Verifies that ownership is unresolved when the runtime instance does not match the shared queue claim.
        /// </summary>
        [Fact]
        public async Task ResolveAsync_Should_Return_Unresolved_When_Runtime_Instance_Does_Not_Match()
        {
            var sharedQueue = new InMemoryAiSharedQueue();
            var sharedRunStore = new InMemoryAiSharedRunStore();
            var resolver = new AiSharedRunOwnershipResolver(sharedQueue, sharedRunStore);

            const string sharedRunId = "shared-run-1";
            const string localRunId = "local-run-1";
            const string executionId = "execution-1";

            var contextSnapshot = CreateExecutionContextSnapshot("tenant-a", "tenant-group-a");

            await sharedRunStore.CreateAsync(new AiSharedRunRecord
            {
                SharedRunId = sharedRunId,
                Status = AiSharedRunStatus.QueuedGlobally,
                RunRequest = CreateRunRequest(contextSnapshot),
                ExecutionContextSnapshot = contextSnapshot,
                PipelineKey = "ownership-test",
                SubmittedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });

            await sharedQueue.EnqueueAsync(new AiSharedQueueItem
            {
                SharedRunId = sharedRunId,
                Status = AiSharedQueueItemStatus.Pending,
                ExecutionContextSnapshot = contextSnapshot,
                PipelineKey = "ownership-test",
                Priority = 0,
                EnqueuedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });

            var claimed = await sharedQueue.ClaimNextAsync(new AiSharedQueueClaimRequest
            {
                RuntimeInstanceId = "runtime-owner",
                WorkerId = "worker-1",
                TenantId = "tenant-a",
                PipelineKey = "ownership-test",
                ClaimTtl = TimeSpan.FromMinutes(5),
                Reason = "test-claim"
            });

            Assert.NotNull(claimed);

            await sharedQueue.MarkDispatchedAsync(
                sharedRunId,
                claimed!.ClaimToken!,
                reason: "test-dispatch");

            await sharedRunStore.MarkDispatchedAsync(
                sharedRunId,
                "runtime-owner",
                localRunId,
                executionId,
                reason: "test-dispatch");

            var result = await resolver.ResolveAsync(new AiSharedRunOwnershipResolutionRequest
            {
                RuntimeInstanceId = "runtime-other",
                LocalRunId = localRunId,
                ExecutionId = executionId,
                TenantId = "tenant-a",
                TenantGroupId = "tenant-group-a"
            });

            Assert.False(result.Resolved);
            Assert.False(result.CanRecover);
            Assert.Null(result.SharedRunId);
            Assert.Equal("runtime-other", result.RuntimeInstanceId);
            Assert.Equal(localRunId, result.LocalRunId);
            Assert.Equal(executionId, result.ExecutionId);
            Assert.Equal("shared-run-ownership-not-found", result.Reason);
        }

        /// <summary>
        /// Verifies that ownership is unresolved when both local run id and execution id are missing.
        /// </summary>
        [Fact]
        public async Task ResolveAsync_Should_Return_Unresolved_When_LocalRunId_And_ExecutionId_Are_Missing()
        {
            var sharedQueue = new InMemoryAiSharedQueue();
            var sharedRunStore = new InMemoryAiSharedRunStore();
            var resolver = new AiSharedRunOwnershipResolver(sharedQueue, sharedRunStore);

            var result = await resolver.ResolveAsync(new AiSharedRunOwnershipResolutionRequest
            {
                RuntimeInstanceId = "runtime-1"
            });

            Assert.False(result.Resolved);
            Assert.False(result.CanRecover);
            Assert.Equal("runtime-1", result.RuntimeInstanceId);
            Assert.Equal("missing-shared-run-id-local-run-id-and-execution-id", result.Reason);
        }

        /// <summary>
        /// Verifies that ownership is resolved but not recoverable when the shared run itself is not dispatched.
        /// </summary>
        [Fact]
        public async Task ResolveAsync_Should_Resolve_As_NotRecoverable_When_SharedRun_Is_Not_Dispatched()
        {
            var sharedQueue = new InMemoryAiSharedQueue();
            var sharedRunStore = new InMemoryAiSharedRunStore();
            var resolver = new AiSharedRunOwnershipResolver(sharedQueue, sharedRunStore);

            const string runtimeInstanceId = "runtime-tenant-a-1";
            const string sharedRunId = "shared-run-1";
            const string localRunId = "local-run-1";
            const string executionId = "execution-1";

            var contextSnapshot = CreateExecutionContextSnapshot("tenant-a", "tenant-group-a");

            await sharedRunStore.CreateAsync(new AiSharedRunRecord
            {
                SharedRunId = sharedRunId,
                Status = AiSharedRunStatus.QueuedGlobally,
                RunRequest = CreateRunRequest(contextSnapshot),
                ExecutionContextSnapshot = contextSnapshot,
                LocalRunId = localRunId,
                ExecutionId = executionId,
                AssignedRuntimeInstanceId = runtimeInstanceId,
                PipelineKey = "ownership-test",
                SubmittedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });

            await sharedQueue.EnqueueAsync(new AiSharedQueueItem
            {
                SharedRunId = sharedRunId,
                Status = AiSharedQueueItemStatus.Pending,
                ExecutionContextSnapshot = contextSnapshot,
                PipelineKey = "ownership-test",
                Priority = 0,
                EnqueuedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });

            var result = await resolver.ResolveAsync(new AiSharedRunOwnershipResolutionRequest
            {
                RuntimeInstanceId = runtimeInstanceId,
                LocalRunId = localRunId,
                ExecutionId = executionId,
                TenantId = "tenant-a",
                TenantGroupId = "tenant-group-a"
            });

            Assert.True(result.Resolved);
            Assert.False(result.CanRecover);
            Assert.Equal(sharedRunId, result.SharedRunId);
            Assert.Equal(runtimeInstanceId, result.RuntimeInstanceId);
            Assert.Equal(localRunId, result.LocalRunId);
            Assert.Equal(executionId, result.ExecutionId);
            Assert.Equal("tenant-a", result.TenantId);
            Assert.Equal("tenant-group-a", result.TenantGroupId);
            Assert.Equal(AiSharedQueueItemStatus.Pending, result.QueueStatus);
            Assert.Equal(AiSharedRunStatus.QueuedGlobally, result.SharedRunStatus);
            Assert.StartsWith("shared-run-ownership-resolved-not-recover", result.Reason, StringComparison.Ordinal);
        }

        /// <summary>
        /// Verifies that ownership is resolved but not recoverable when the shared run is dispatched
        /// but the shared queue item is not dispatched.
        /// </summary>
        [Fact]
        public async Task ResolveAsync_Should_Resolve_As_NotRecoverable_When_SharedRun_Is_Dispatched_And_Queue_Is_Not_Dispatched()
        {
            var sharedQueue = new InMemoryAiSharedQueue();
            var sharedRunStore = new InMemoryAiSharedRunStore();
            var resolver = new AiSharedRunOwnershipResolver(sharedQueue, sharedRunStore);

            const string runtimeInstanceId = "runtime-tenant-a-1";
            const string sharedRunId = "shared-run-1";
            const string localRunId = "local-run-1";
            const string executionId = "execution-1";

            var contextSnapshot = CreateExecutionContextSnapshot("tenant-a", "tenant-group-a");

            await sharedRunStore.CreateAsync(new AiSharedRunRecord
            {
                SharedRunId = sharedRunId,
                Status = AiSharedRunStatus.Dispatched,
                RunRequest = CreateRunRequest(contextSnapshot),
                ExecutionContextSnapshot = contextSnapshot,
                LocalRunId = localRunId,
                ExecutionId = executionId,
                AssignedRuntimeInstanceId = runtimeInstanceId,
                PipelineKey = "ownership-test",
                SubmittedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });

            await sharedQueue.EnqueueAsync(new AiSharedQueueItem
            {
                SharedRunId = sharedRunId,
                Status = AiSharedQueueItemStatus.Pending,
                ExecutionContextSnapshot = contextSnapshot,
                PipelineKey = "ownership-test",
                Priority = 0,
                EnqueuedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });

            var result = await resolver.ResolveAsync(new AiSharedRunOwnershipResolutionRequest
            {
                RuntimeInstanceId = runtimeInstanceId,
                LocalRunId = localRunId,
                ExecutionId = executionId,
                TenantId = "tenant-a",
                TenantGroupId = "tenant-group-a"
            });

            Assert.True(result.Resolved);
            Assert.False(result.CanRecover);
            Assert.Equal(sharedRunId, result.SharedRunId);
            Assert.Equal(runtimeInstanceId, result.RuntimeInstanceId);
            Assert.Equal(localRunId, result.LocalRunId);
            Assert.Equal(executionId, result.ExecutionId);
            Assert.Equal("tenant-a", result.TenantId);
            Assert.Equal("tenant-group-a", result.TenantGroupId);
            Assert.Equal(AiSharedQueueItemStatus.Pending, result.QueueStatus);
            Assert.Equal(AiSharedRunStatus.Dispatched, result.SharedRunStatus);
            Assert.StartsWith("shared-run-ownership-resolved-not-recover", result.Reason, StringComparison.Ordinal);
        }

        private static async Task<ExternalWaitOwnershipFixture>
            CreateDispatchedExternalWaitContinuationFixtureAsync()
        {
            var sharedQueue = new InMemoryAiSharedQueue();
            var sharedRunStore = new InMemoryAiSharedRunStore();

            const string runtimeInstanceId = "runtime-continuation-owner";
            const string sharedRunId =
                "child-continuation-child-invocation-terminal-parent";
            const string localRunId = "local-run-continuation-terminal-parent";
            const string executionId = "parent-execution-terminal-check";

            var contextSnapshot =
                CreateExecutionContextSnapshot(
                    "tenant-a",
                    "tenant-group-a");

            await sharedRunStore.CreateAsync(
                new AiSharedRunRecord
                {
                    SharedRunId = sharedRunId,
                    Status = AiSharedRunStatus.QueuedGlobally,
                    RunRequest =
                        new AiRuntimePipelineRunRequest
                        {
                            PipelineName = "ownership-test",
                            ExecutionContextSnapshot = contextSnapshot,
                            ExternalWaitContinuation =
                                new AiRuntimeExternalWaitContinuation
                                {
                                    ExecutionId = executionId,
                                    StepName = "execute-child-dag",
                                    ContinuationId =
                                        "child-continuation:child-invocation-terminal-parent"
                                }
                        },
                    ExecutionContextSnapshot = contextSnapshot,
                    PipelineKey = "ownership-test",
                    CorrelationId =
                        "child-continuation:child-invocation-terminal-parent",
                    SubmittedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });

            await sharedQueue.EnqueueAsync(
                new AiSharedQueueItem
                {
                    SharedRunId = sharedRunId,
                    Status = AiSharedQueueItemStatus.Pending,
                    ExecutionContextSnapshot = contextSnapshot,
                    PipelineKey = "ownership-test",
                    Priority = 0,
                    EnqueuedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });

            var claimed =
                await sharedQueue.ClaimNextAsync(
                    new AiSharedQueueClaimRequest
                    {
                        RuntimeInstanceId = "mcp-control-plane",
                        WorkerId = "worker-1",
                        TenantId = "tenant-a",
                        PipelineKey = "ownership-test",
                        ClaimTtl = TimeSpan.FromMinutes(5),
                        Reason = "test-claim"
                    });

            Assert.NotNull(claimed);

            await sharedQueue.MarkDispatchedAsync(
                sharedRunId,
                claimed!.ClaimToken!,
                reason: "test-dispatch");

            await sharedRunStore.MarkDispatchedAsync(
                sharedRunId,
                runtimeInstanceId,
                localRunId,
                executionId,
                reason: "test-dispatch");

            return new ExternalWaitOwnershipFixture(
                sharedQueue,
                sharedRunStore,
                new AiSharedRunOwnershipResolutionRequest
                {
                    RuntimeInstanceId = runtimeInstanceId,
                    SharedRunId = sharedRunId,
                    LocalRunId = localRunId,
                    ExecutionId = executionId,
                    TenantId = "tenant-a",
                    TenantGroupId = "tenant-group-a"
                },
                runtimeInstanceId,
                sharedRunId,
                localRunId,
                executionId);
        }

        private static ServiceProvider CreateDagStoreServiceProvider(
            AiExecutionRecord? parentRecord)
        {
            var dagStore =
                DispatchProxy.Create<
                    IAiDagExecutionStore,
                    DagExecutionStoreProxy>();

            ((DagExecutionStoreProxy)(object)dagStore).ParentRecord =
                parentRecord;

            return new ServiceCollection()
                .AddSingleton<IAiDagExecutionStore>(dagStore)
                .BuildServiceProvider();
        }

        private sealed record ExternalWaitOwnershipFixture(
            InMemoryAiSharedQueue SharedQueue,
            InMemoryAiSharedRunStore SharedRunStore,
            AiSharedRunOwnershipResolutionRequest Request,
            string RuntimeInstanceId,
            string SharedRunId,
            string LocalRunId,
            string ExecutionId);

        private class DagExecutionStoreProxy : DispatchProxy
        {
            public AiExecutionRecord? ParentRecord { get; set; }

            protected override object? Invoke(
                MethodInfo? targetMethod,
                object?[]? args)
            {
                ArgumentNullException.ThrowIfNull(targetMethod);

                if (targetMethod.Name ==
                    nameof(IAiDagExecutionStore.GetRecordAsync))
                {
                    return Task.FromResult(ParentRecord);
                }

                throw new NotSupportedException(
                    $"Focused ownership DAG-store proxy does not support '{targetMethod.Name}'.");
            }
        }

        /// <summary>
        /// Creates an execution context snapshot for tenant-aware test records.
        /// </summary>
        /// <param name="tenantId">The tenant identifier.</param>
        /// <param name="tenantGroupId">The tenant group identifier.</param>
        /// <returns>The execution context snapshot.</returns>
        private static ExecutionContextSnapshot CreateExecutionContextSnapshot(
            string tenantId,
            string tenantGroupId)
        {
            return new ExecutionContextSnapshot
            {
                ContextKey = $"ctx-{tenantId}",
                Project = "ownership-tests",
                UserId = "test-user",
                TenantId = tenantId,
                TenantGroupId = tenantGroupId,
                CurrentNamespace = "default",
                Namespaces = new List<NamespaceEntry>(),
                InFlightCount = 0,
                TtlSeconds = 300,
                CreatedAtUtc = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Creates a pipeline run request for shared-run test records.
        /// </summary>
        /// <param name="contextSnapshot">The execution context snapshot.</param>
        /// <returns>The pipeline run request.</returns>
        private static AiRuntimePipelineRunRequest CreateRunRequest(
            ExecutionContextSnapshot contextSnapshot)
        {
            return new AiRuntimePipelineRunRequest
            {
                PipelineName = "ownership-test",
                ExecutionContextSnapshot = contextSnapshot,
                Input = new Dictionary<string, object?>
                {
                    ["scenario"] = "ownership-resolution"
                }
            };
        }
    }
}