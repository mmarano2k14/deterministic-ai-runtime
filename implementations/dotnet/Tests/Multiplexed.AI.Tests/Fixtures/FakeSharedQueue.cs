using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.Core.ExecutionContext;
using System;
using System.Collections.Generic;
using System.Text;

namespace Multiplexed.AI.Tests.Fixtures
{
    /// <summary>
    /// Fake shared queue used by transition service tests.
    /// </summary>
    public sealed class FakeSharedQueue : IAiSharedQueue
    {
        /// <summary>
        /// Gets or sets a value indicating whether recovery requeue should be rejected.
        /// </summary>
        public bool RejectRequeueDispatched { get; set; }

        /// <summary>
        /// Gets the number of recovery requeue calls.
        /// </summary>
        public int RequeueDispatchedCalls { get; private set; }

        /// <summary>
        /// Gets the last requeued shared run identifier.
        /// </summary>
        public string? LastRequeueSharedRunId { get; private set; }

        /// <summary>
        /// Gets the last requeue claim token.
        /// </summary>
        public string? LastRequeueClaimToken { get; private set; }

        /// <summary>
        /// Gets the last requeue reason.
        /// </summary>
        public string? LastRequeueReason { get; private set; }

        /// <summary>
        /// Gets the last recovery metadata.
        /// </summary>
        public IReadOnlyDictionary<string, string>? LastRequeueMetadata { get; private set; }

        /// <inheritdoc />
        public Task<AiSharedQueueItem> EnqueueAsync(
            AiSharedQueueItem item,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(item);
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(item);
        }

        /// <inheritdoc />
        public Task<AiSharedQueueItem?> GetAsync(
            string sharedRunId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult<AiSharedQueueItem?>(
                null);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiSharedQueueItem>> ListAsync(
            bool includeTerminal = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult<IReadOnlyList<AiSharedQueueItem>>(
                []);
        }

        /// <inheritdoc />
        public Task<AiSharedQueueItem?> ClaimNextAsync(
            AiSharedQueueClaimRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult<AiSharedQueueItem?>(
                null);
        }

        /// <inheritdoc />
        public Task<AiSharedQueueItem?> ClaimAsync(
            string sharedRunId,
            AiSharedQueueClaimRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult<AiSharedQueueItem?>(
                null);
        }

        /// <inheritdoc />
        public Task<AiSharedQueueItem?> MarkDispatchedAsync(
            string sharedRunId,
            string claimToken,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult<AiSharedQueueItem?>(
                CreateQueueItem(
                    sharedRunId,
                    AiSharedQueueItemStatus.Dispatched,
                    claimToken));
        }

        /// <inheritdoc />
        public Task<AiSharedQueueItem?> RequeueAsync(
            string sharedRunId,
            string claimToken,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult<AiSharedQueueItem?>(
                CreateQueueItem(
                    sharedRunId,
                    AiSharedQueueItemStatus.Pending,
                    claimToken));
        }

        /// <inheritdoc />
        public Task<AiSharedQueueItem?> CancelAsync(
            string sharedRunId,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult<AiSharedQueueItem?>(
                CreateQueueItem(
                    sharedRunId,
                    AiSharedQueueItemStatus.Cancelled));
        }

        /// <inheritdoc />
        public Task<AiSharedQueueItem?> RequeueDispatchedAsync(
            string sharedRunId,
            string claimToken,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            return RequeueDispatchedAsync(
                sharedRunId,
                claimToken,
                reason,
                metadata: null,
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<AiSharedQueueItem?> RequeueDispatchedAsync(
            string sharedRunId,
            string claimToken,
            string? reason,
            IReadOnlyDictionary<string, string>? metadata,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);
            cancellationToken.ThrowIfCancellationRequested();

            RequeueDispatchedCalls++;
            LastRequeueSharedRunId = sharedRunId;
            LastRequeueClaimToken = claimToken;
            LastRequeueReason = reason;
            LastRequeueMetadata = metadata;

            if (RejectRequeueDispatched)
            {
                return Task.FromResult<AiSharedQueueItem?>(
                    null);
            }

            return Task.FromResult<AiSharedQueueItem?>(
                CreateQueueItem(
                    sharedRunId,
                    AiSharedQueueItemStatus.Pending,
                    claimToken,
                    metadata));
        }

        /// <summary>
        /// Creates an execution context snapshot for fake queue items.
        /// </summary>
        /// <returns>The execution context snapshot.</returns>
        private static ExecutionContextSnapshot CreateSnapshot()
        {
            return new ExecutionContextSnapshot
            {
                ContextKey = "ctx-test",
                Project = "project-test",
                UserId = "user-test",
                TenantId = "tenant-a",
                TenantGroupId = "tenant-group-a",
                CurrentNamespace = "default",
                Namespaces = new List<NamespaceEntry>(),
                InFlightCount = 0,
                TtlSeconds = 300,
                CreatedAtUtc = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Creates a shared queue item.
        /// </summary>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <param name="status">The shared queue item status.</param>
        /// <param name="claimToken">The claim token.</param>
        /// <param name="metadata">The optional metadata.</param>
        /// <returns>The shared queue item.</returns>
        private static AiSharedQueueItem CreateQueueItem(
            string sharedRunId,
            AiSharedQueueItemStatus status,
            string? claimToken = null,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            return new AiSharedQueueItem
            {
                SharedRunId = sharedRunId,
                ControlPlaneId = "control-plane-test",
                Status = status,
                ExecutionContextSnapshot = CreateSnapshot(),
                PipelineKey = "pipeline-test",
                ClaimedByRuntimeInstanceId = "runtime-1",
                ClaimedByWorkerId = "worker-1",
                ClaimToken = claimToken,
                EnqueuedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-10),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                ClaimedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-5),
                ClaimExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
                Reason = "test",
                Metadata = metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };
        }
    }
}
