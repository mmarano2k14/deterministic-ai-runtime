using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Identity;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations.Persistence;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Results;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Persistence.Mongo;
using Multiplexed.AI.Stores;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Helpers
{
    /// <summary>
    /// Provides reusable production-test helpers for observing durable nested child DAG relations.
    /// </summary>
    internal static class ProductionChildDagScenarioHelpers
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

        /// <summary>
        /// Creates a relation-store reader over the MongoDB authority already configured for the test host.
        /// </summary>
        /// <param name="services">The production scenario host services.</param>
        /// <returns>The authoritative child execution relation store.</returns>
        public static IAiChildExecutionRelationStore CreateRelationStore(IServiceProvider services)
        {
            ArgumentNullException.ThrowIfNull(services);

            return new MongoAiChildExecutionRelationStore(
                services.GetRequiredService<IMongoDatabase>(),
                Options.Create(new AiChildExecutionRelationMongoOptions()));
        }


        /// <summary>
        /// Waits for the exact relation at one nested depth to reach the durable Waiting state.
        /// </summary>
        /// <param name="relationStore">The authoritative relation store.</param>
        /// <param name="tenantId">The tenant that owns the nested chain.</param>
        /// <param name="parentExecutionId">The originally submitted parent execution identifier.</param>
        /// <param name="parentPipelineName">The originally submitted parent pipeline name.</param>
        /// <param name="childDepth">The total configured nested child depth.</param>
        /// <param name="targetDepth">The one-based nested relation depth to observe.</param>
        /// <param name="timeout">The maximum time allowed for the waiting relation chain to appear.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The authoritative waiting relation at the requested depth.</returns>
        public static async Task<AiChildExecutionRelation> WaitForWaitingRelationAtDepthAsync(
            IAiChildExecutionRelationStore relationStore,
            string tenantId,
            string parentExecutionId,
            string parentPipelineName,
            int childDepth,
            int targetDepth,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(relationStore);
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
            ArgumentException.ThrowIfNullOrWhiteSpace(parentExecutionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(parentPipelineName);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(childDepth);

            if (targetDepth <= 0 || targetDepth > childDepth)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(targetDepth),
                    targetDepth,
                    $"Target depth must be between 1 and '{childDepth}'.");
            }

            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timeout),
                    timeout,
                    "The waiting child relation timeout must be greater than zero.");
            }

            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            var currentParentExecutionId = parentExecutionId;
            var currentParentPipelineName = parentPipelineName;

            for (var depth = 1; depth <= targetDepth; depth++)
            {
                var remainingDepth = childDepth - depth + 1;
                var identity = CreateInvocationIdentity(
                    tenantId,
                    currentParentExecutionId,
                    currentParentPipelineName,
                    remainingDepth);

                var relation = await WaitForWaitingRelationAsync(
                        relationStore,
                        identity,
                        deadline,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (depth == targetDepth)
                {
                    return relation;
                }

                if (string.IsNullOrWhiteSpace(relation.ChildExecutionId))
                {
                    throw new InvalidOperationException(
                        $"Waiting child relation '{relation.ChildInvocationKey}' does not contain a ChildExecutionId.");
                }

                currentParentExecutionId = relation.ChildExecutionId;
                currentParentPipelineName = relation.ChildDagId;
            }

            throw new InvalidOperationException("The requested child relation depth could not be resolved.");
        }

        /// <summary>
        /// Waits for submitted parent executions to reach a durable terminal state across normal external-wait
        /// continuations that may use a different physical runtime run identifier.
        /// </summary>
        /// <param name="mcp">The tenant MCP client used only to resolve an execution id when the shared-run snapshot has not exposed it yet.</param>
        /// <param name="dagStore">The shared authoritative DAG execution store.</param>
        /// <param name="dispatchedRuns">The original submitted shared runs.</param>
        /// <param name="timeout">The maximum time allowed for all durable executions to become terminal.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Terminal observations shaped for the existing production-run result builder.</returns>
        /// <remarks>
        /// Historical zero-depth scenarios continue to use physical runtime-run polling. This helper is opt-in for
        /// child composition because a parked parent releases its original physical run and may resume through a
        /// later continuation run while keeping the same durable ExecutionId.
        /// </remarks>
        public static async Task<IReadOnlyList<AiRuntimeQueueControlPlaneResult>> WaitForDurableParentCompletionAsync(
            McpTestClient mcp,
            IAiDagExecutionStore dagStore,
            IReadOnlyList<AiSharedRunRecord> dispatchedRuns,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(mcp);
            ArgumentNullException.ThrowIfNull(dagStore);
            ArgumentNullException.ThrowIfNull(dispatchedRuns);

            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timeout),
                    timeout,
                    "The durable parent completion timeout must be greater than zero.");
            }

            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            var terminal = new AiRuntimeQueueControlPlaneResult?[dispatchedRuns.Count];
            var executionIds = new string?[dispatchedRuns.Count];

            for (var index = 0; index < dispatchedRuns.Count; index++)
            {
                var run = dispatchedRuns[index];
                if (!string.IsNullOrWhiteSpace(run.ExecutionId))
                {
                    executionIds[index] = run.ExecutionId;
                    continue;
                }

                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                var status = await McpTestWaitHelpers
                    .WaitForRuntimeRunExecutionIdAsync(mcp, run, remaining)
                    .ConfigureAwait(false);

                executionIds[index] = status.ExecutionId ?? status.RunState?.ExecutionId;
            }

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                for (var index = 0; index < dispatchedRuns.Count; index++)
                {
                    if (terminal[index] is not null)
                    {
                        continue;
                    }

                    var run = dispatchedRuns[index];
                    var executionId = executionIds[index];
                    if (string.IsNullOrWhiteSpace(executionId))
                    {
                        continue;
                    }

                    var record = await dagStore
                        .GetRecordAsync(executionId, cancellationToken)
                        .ConfigureAwait(false);

                    if (record?.IsTerminal != true)
                    {
                        continue;
                    }

                    terminal[index] = CreateDurableTerminalObservation(run, record);
                }

                if (terminal.All(item => item is not null))
                {
                    return terminal.Select(item => item!).ToArray();
                }

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }

            var unresolved = Enumerable
                .Range(0, dispatchedRuns.Count)
                .Where(index => terminal[index] is null)
                .Select(index =>
                {
                    var run = dispatchedRuns[index];
                    return $"SharedRunId='{run.SharedRunId}', ExecutionId='{executionIds[index] ?? run.ExecutionId}'";
                })
                .ToArray();

            throw new TimeoutException(
                $"Durable parent executions did not become terminal within '{timeout}'. " +
                $"Unresolved='{string.Join(" | ", unresolved)}'.");
        }

        /// <summary>
        /// Waits for the exact nested relation chain created below one submitted parent execution.
        /// </summary>
        /// <param name="relationStore">The authoritative relation store.</param>
        /// <param name="tenantId">The tenant that owns the complete chain.</param>
        /// <param name="parentExecutionId">The submitted parent execution identifier.</param>
        /// <param name="parentPipelineName">The submitted parent pipeline name.</param>
        /// <param name="childDepth">The expected number of nested child levels.</param>
        /// <param name="timeout">The maximum time allowed for the durable chain to converge.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The ordered authoritative relation results from the first child to the deepest child.</returns>
        public static async Task<IReadOnlyList<ProductionChildDagScenarioResult>> WaitForNestedRelationsAsync(
            IAiChildExecutionRelationStore relationStore,
            string tenantId,
            string parentExecutionId,
            string parentPipelineName,
            int childDepth,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(relationStore);
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
            ArgumentException.ThrowIfNullOrWhiteSpace(parentExecutionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(parentPipelineName);
            ArgumentOutOfRangeException.ThrowIfNegative(childDepth);

            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timeout),
                    timeout,
                    "The child DAG observation timeout must be greater than zero.");
            }

            if (childDepth == 0)
            {
                return Array.Empty<ProductionChildDagScenarioResult>();
            }

            var results = new List<ProductionChildDagScenarioResult>(childDepth);
            var currentParentExecutionId = parentExecutionId;
            var currentParentPipelineName = parentPipelineName;
            var deadline = DateTimeOffset.UtcNow.Add(timeout);

            for (var depth = 1; depth <= childDepth; depth++)
            {
                var remainingDepth = childDepth - depth + 1;
                var identity = CreateInvocationIdentity(
                    tenantId,
                    currentParentExecutionId,
                    currentParentPipelineName,
                    remainingDepth);

                var relation = await WaitForCompletedRelationAsync(
                        relationStore,
                        identity,
                        deadline,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(relation.ChildExecutionId))
                {
                    throw new InvalidOperationException(
                        $"Completed child relation '{relation.ChildInvocationKey}' does not contain a ChildExecutionId.");
                }

                results.Add(new ProductionChildDagScenarioResult
                {
                    Depth = depth,
                    TenantId = relation.TenantId,
                    ParentExecutionId = relation.ParentExecutionId,
                    ChildExecutionId = relation.ChildExecutionId,
                    ChildInvocationKey = relation.ChildInvocationKey,
                    ChildDagId = relation.ChildDagId,
                    ChildDagDefinitionVersion = relation.ChildDagDefinitionVersion,
                    InvocationGeneration = relation.InvocationGeneration,
                    RelationStatus = relation.Status,
                    ContinuationStatus = relation.ContinuationStatus,
                    ChildResultDigest = relation.ChildResult?.ContentHash,
                    ChildFailureReason = relation.ChildFailureReason
                });

                currentParentExecutionId = relation.ChildExecutionId;
                currentParentPipelineName = relation.ChildDagId;
            }

            return results;
        }


        private static AiChildInvocationIdentity CreateInvocationIdentity(
            string tenantId,
            string parentExecutionId,
            string parentPipelineName,
            int remainingDepth)
        {
            return new AiChildInvocationIdentity
            {
                TenantId = tenantId,
                ParentExecutionId = parentExecutionId,
                ParentCallSiteId = McpTestPipelineFactory.ChildDagStepName,
                ChildDagId = McpTestPipelineFactory.CreateChildPipelineName(
                    parentPipelineName,
                    remainingDepth),
                ChildDagDefinitionVersion = McpTestPipelineFactory.PipelineVersion,
                CanonicalLogicalInvocationKey = McpTestPipelineFactory.CreateChildLogicalInvocationKey(
                    parentPipelineName,
                    remainingDepth),
                InvocationGeneration = 0
            };
        }

        private static async Task<AiChildExecutionRelation> WaitForWaitingRelationAsync(
            IAiChildExecutionRelationStore relationStore,
            AiChildInvocationIdentity identity,
            DateTimeOffset deadline,
            CancellationToken cancellationToken)
        {
            AiChildExecutionRelation? lastRelation = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                lastRelation = await relationStore
                    .GetAsync(identity, cancellationToken)
                    .ConfigureAwait(false);

                if (lastRelation is not null &&
                    lastRelation.Status == AiChildExecutionRelationStatus.Waiting &&
                    !string.IsNullOrWhiteSpace(lastRelation.ChildExecutionId))
                {
                    return lastRelation;
                }

                if (lastRelation?.Status == AiChildExecutionRelationStatus.Completed)
                {
                    throw new InvalidOperationException(
                        $"Child relation '{lastRelation.ChildInvocationKey}' completed before the physical runtime failure window was observed.");
                }

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"Child relation did not reach Waiting before timeout. " +
                $"TenantId='{identity.TenantId}', ParentExecutionId='{identity.ParentExecutionId}', " +
                $"ChildDagId='{identity.ChildDagId}', Generation='{identity.InvocationGeneration}', " +
                $"LastStatus='{lastRelation?.Status}', ChildExecutionId='{lastRelation?.ChildExecutionId ?? string.Empty}'.");
        }

        /// <summary>
        /// Creates a control-plane-shaped observation from the authoritative durable execution record.
        /// </summary>
        private static AiRuntimeQueueControlPlaneResult CreateDurableTerminalObservation(
            AiSharedRunRecord run,
            AiExecutionRecord record)
        {
            if (string.IsNullOrWhiteSpace(run.AssignedRuntimeInstanceId) ||
                string.IsNullOrWhiteSpace(run.LocalRunId))
            {
                throw new InvalidOperationException(
                    $"Dispatched shared run '{run.SharedRunId}' does not expose its original physical runtime binding.");
            }

            var completedAtUtc = record.CompletedAtUtc == default
                ? (DateTimeOffset?)null
                : new DateTimeOffset(record.CompletedAtUtc);

            return new AiRuntimeQueueControlPlaneResult
            {
                Operation = AiRuntimeQueueControlPlaneOperation.GetRunStatus,
                Success = true,
                RuntimeInstanceId = run.AssignedRuntimeInstanceId,
                RunId = run.LocalRunId,
                ExecutionId = record.ExecutionId,
                RunState = new AiRuntimePipelineRunState
                {
                    RunId = run.LocalRunId,
                    ExecutionId = record.ExecutionId,
                    PipelineKey = run.PipelineKey,
                    PipelineName = record.PipelineName,
                    RuntimeInstanceId = run.AssignedRuntimeInstanceId,
                    Status = record.Status.ToString().ToLowerInvariant(),
                    IsQueued = false,
                    IsRunning = false,
                    CompletedAtUtc = completedAtUtc
                },
                StartedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow
            };
        }

        /// <summary>
        /// Waits until one exact typed invocation reaches a completed relation with a terminal continuation state.
        /// </summary>
        private static async Task<AiChildExecutionRelation> WaitForCompletedRelationAsync(
            IAiChildExecutionRelationStore relationStore,
            AiChildInvocationIdentity identity,
            DateTimeOffset deadline,
            CancellationToken cancellationToken)
        {
            AiChildExecutionRelation? lastRelation = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                lastRelation = await relationStore
                    .GetAsync(identity, cancellationToken)
                    .ConfigureAwait(false);

                if (lastRelation is not null &&
                    lastRelation.Status == AiChildExecutionRelationStatus.Completed &&
                    (lastRelation.ContinuationStatus == AiChildContinuationStatus.Resumed ||
                     lastRelation.ContinuationStatus == AiChildContinuationStatus.Suppressed))
                {
                    return lastRelation;
                }

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"Child relation did not converge before timeout. " +
                $"TenantId='{identity.TenantId}', ParentExecutionId='{identity.ParentExecutionId}', " +
                $"ParentCallSiteId='{identity.ParentCallSiteId}', ChildDagId='{identity.ChildDagId}', " +
                $"Generation='{identity.InvocationGeneration}', LastStatus='{lastRelation?.Status}', " +
                $"LastContinuationStatus='{lastRelation?.ContinuationStatus}'.");
        }
    }
}
