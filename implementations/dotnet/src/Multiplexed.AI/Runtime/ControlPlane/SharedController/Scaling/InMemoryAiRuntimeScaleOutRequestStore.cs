using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;

namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Provides an in-memory implementation of <see cref="IAiRuntimeScaleOutRequestStore" />.
    /// </summary>
    /// <remarks>
    /// This store is intended for local development, tests, and single-process scenarios.
    /// Distributed runtime coordination should use a durable/shared implementation such as Redis.
    /// </remarks>
    public sealed class InMemoryAiRuntimeScaleOutRequestStore : IAiRuntimeScaleOutRequestStore
    {
        /// <summary>
        /// Synchronizes access to the in-memory request collection.
        /// </summary>
        private readonly object syncRoot = new();

        /// <summary>
        /// Stores scale-out requests by request identifier.
        /// </summary>
        private readonly Dictionary<string, AiRuntimeScaleOutRequestRecord> requests = new(StringComparer.Ordinal);

        /// <summary>
        /// Defines store behavior such as TTL, deduplication, and list limits.
        /// </summary>
        private readonly AiRuntimeScaleOutRequestStoreOptions options;

        /// <summary>
        /// Initializes a new instance of the <see cref="InMemoryAiRuntimeScaleOutRequestStore" /> class.
        /// </summary>
        /// <param name="options">The scale-out request store options.</param>
        public InMemoryAiRuntimeScaleOutRequestStore(
            IOptions<AiRuntimeScaleOutRequestStoreOptions>? options = null)
        {
            this.options = options?.Value ?? new AiRuntimeScaleOutRequestStoreOptions();
        }

        /// <inheritdoc />
        public Task<AiRuntimeScaleOutRequestRecord> CreateAsync(
            AiRuntimeScaleOutRequestRecord request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                this.RemoveExpiredUnsafe(DateTimeOffset.UtcNow);

                var normalized = this.NormalizeForCreate(request);

                if (this.options.EnableDeduplication)
                {
                    var duplicate = this.FindDuplicatePendingUnsafe(normalized, DateTimeOffset.UtcNow);

                    if (duplicate is not null)
                    {
                        return Task.FromResult(Clone(duplicate));
                    }
                }

                this.requests[normalized.RequestId] = Clone(normalized);

                return Task.FromResult(Clone(normalized));
            }
        }

        /// <inheritdoc />
        public Task<AiRuntimeScaleOutRequestRecord?> GetAsync(
            string requestId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                this.RemoveExpiredUnsafe(DateTimeOffset.UtcNow);

                return Task.FromResult(
                    this.requests.TryGetValue(requestId, out var request)
                        ? Clone(request)
                        : null);
            }
        }

        /// <inheritdoc />
        public Task<IReadOnlyCollection<AiRuntimeScaleOutRequestRecord>> ListAsync(
            AiRuntimeScaleOutRequestQuery query,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);

            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                this.RemoveExpiredUnsafe(DateTimeOffset.UtcNow);

                var results = this.requests.Values
                    .Where(request => MatchesQuery(request, query))
                    .OrderByDescending(request => request.CreatedAtUtc)
                    .Take(GetMaxResults(query, this.options))
                    .Select(Clone)
                    .ToArray();

                return Task.FromResult<IReadOnlyCollection<AiRuntimeScaleOutRequestRecord>>(results);
            }
        }

        /// <inheritdoc />
        public Task<IReadOnlyCollection<AiRuntimeScaleOutRequestRecord>> ListPendingAsync(
            AiRuntimeScaleOutRequestQuery query,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);

            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                this.RemoveExpiredUnsafe(DateTimeOffset.UtcNow);

                var results = this.requests.Values
                    .Where(request => request.Status is AiRuntimeScaleOutRequestStatus.Pending)
                    .Where(request => MatchesQuery(request, query))
                    .OrderBy(request => request.CreatedAtUtc)
                    .Take(GetMaxResults(query, this.options))
                    .Select(Clone)
                    .ToArray();

                return Task.FromResult<IReadOnlyCollection<AiRuntimeScaleOutRequestRecord>>(results);
            }
        }

        /// <inheritdoc />
        public Task<bool> MarkObservedAsync(
            string requestId,
            string observedBy,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
            ArgumentException.ThrowIfNullOrWhiteSpace(observedBy);

            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                return Task.FromResult(this.TryTransitionUnsafe(
                    requestId,
                    AiRuntimeScaleOutRequestStatus.Observed,
                    request =>
                    {
                        request.ObservedAtUtc ??= DateTimeOffset.UtcNow;
                        request.ObservedBy = observedBy;
                    }));
            }
        }

        /// <inheritdoc />
        public Task<bool> MarkFulfilledAsync(
            string requestId,
            string fulfilledBy,
            string? runtimeInstanceId = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
            ArgumentException.ThrowIfNullOrWhiteSpace(fulfilledBy);

            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                return Task.FromResult(this.TryTransitionUnsafe(
                    requestId,
                    AiRuntimeScaleOutRequestStatus.Fulfilled,
                    request =>
                    {
                        request.FulfilledAtUtc ??= DateTimeOffset.UtcNow;
                        request.FulfilledBy = fulfilledBy;
                        request.FulfilledRuntimeInstanceId = runtimeInstanceId;
                    }));
            }
        }

        /// <inheritdoc />
        public Task<bool> MarkRejectedAsync(
            string requestId,
            string rejectedBy,
            string reason,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
            ArgumentException.ThrowIfNullOrWhiteSpace(rejectedBy);
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);

            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                return Task.FromResult(this.TryTransitionUnsafe(
                    requestId,
                    AiRuntimeScaleOutRequestStatus.Rejected,
                    request =>
                    {
                        request.RejectedAtUtc ??= DateTimeOffset.UtcNow;
                        request.RejectedBy = rejectedBy;
                        request.RejectionReason = reason;
                    }));
            }
        }

        /// <inheritdoc />
        public Task<bool> MarkExpiredAsync(
            string requestId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                return Task.FromResult(this.TryTransitionUnsafe(
                    requestId,
                    AiRuntimeScaleOutRequestStatus.Expired,
                    request =>
                    {
                        request.ExpiredAtUtc ??= DateTimeOffset.UtcNow;
                    }));
            }
        }

        /// <inheritdoc />
        public Task<bool> MarkCancelledAsync(
            string requestId,
            string cancelledBy,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
            ArgumentException.ThrowIfNullOrWhiteSpace(cancelledBy);

            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                return Task.FromResult(this.TryTransitionUnsafe(
                    requestId,
                    AiRuntimeScaleOutRequestStatus.Cancelled,
                    request =>
                    {
                        request.CancelledAtUtc ??= DateTimeOffset.UtcNow;
                        request.Metadata["cancelledBy"] = cancelledBy;
                    }));
            }
        }

        /// <summary>
        /// Normalizes and validates a scale-out request before it is persisted.
        /// </summary>
        /// <param name="request">The request to normalize.</param>
        /// <returns>The normalized request.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when required request identity fields are missing.
        /// </exception>
        private AiRuntimeScaleOutRequestRecord NormalizeForCreate(AiRuntimeScaleOutRequestRecord request)
        {
            var now = DateTimeOffset.UtcNow;
            var normalized = Clone(request);

            if (string.IsNullOrWhiteSpace(normalized.RequestId))
            {
                normalized.RequestId = Guid.NewGuid().ToString("N");
            }

            if (string.IsNullOrWhiteSpace(normalized.ControlPlaneId))
            {
                throw new ArgumentException("Scale-out request control-plane id is required.", nameof(request));
            }

            if (string.IsNullOrWhiteSpace(normalized.SharedRunId))
            {
                throw new ArgumentException("Scale-out request shared run id is required.", nameof(request));
            }

            normalized.Status = AiRuntimeScaleOutRequestStatus.Pending;

            if (normalized.CreatedAtUtc == default)
            {
                normalized.CreatedAtUtc = now;
            }

            if (normalized.ExpiresAtUtc is null && this.options.DefaultTtl > TimeSpan.Zero)
            {
                normalized.ExpiresAtUtc = normalized.CreatedAtUtc.Add(this.options.DefaultTtl);
            }

            return normalized;
        }

        /// <summary>
        /// Finds an existing pending request matching the deduplication identity of the supplied request.
        /// </summary>
        /// <param name="request">The request used to compute the deduplication identity.</param>
        /// <param name="now">The current UTC time.</param>
        /// <returns>The duplicate pending request when found; otherwise, <see langword="null" />.</returns>
        /// <remarks>
        /// The caller must hold <see cref="syncRoot" /> before invoking this method.
        /// </remarks>
        private AiRuntimeScaleOutRequestRecord? FindDuplicatePendingUnsafe(
            AiRuntimeScaleOutRequestRecord request,
            DateTimeOffset now)
        {
            var lowerBound = now.Subtract(this.options.DeduplicationWindow);

            return this.requests.Values
                .Where(candidate => candidate.Status is AiRuntimeScaleOutRequestStatus.Pending)
                .Where(candidate => candidate.CreatedAtUtc >= lowerBound)
                .Where(candidate => string.Equals(candidate.ControlPlaneId, request.ControlPlaneId, StringComparison.Ordinal))
                .Where(candidate => string.Equals(candidate.TenantId, request.TenantId, StringComparison.Ordinal))
                .Where(candidate => string.Equals(candidate.PipelineKey, request.PipelineKey, StringComparison.Ordinal))
                .Where(candidate => string.Equals(candidate.Reason, request.Reason, StringComparison.Ordinal))
                .Where(candidate => string.Equals(candidate.ProviderHint, request.ProviderHint, StringComparison.Ordinal))
                .OrderByDescending(candidate => candidate.CreatedAtUtc)
                .FirstOrDefault();
        }

        /// <summary>
        /// Attempts to transition a scale-out request to a new lifecycle status.
        /// </summary>
        /// <param name="requestId">The scale-out request identifier.</param>
        /// <param name="targetStatus">The target lifecycle status.</param>
        /// <param name="apply">The mutation applied when the transition is valid.</param>
        /// <returns><see langword="true" /> when the transition was applied; otherwise, <see langword="false" />.</returns>
        /// <remarks>
        /// The caller must hold <see cref="syncRoot" /> before invoking this method.
        /// </remarks>
        private bool TryTransitionUnsafe(
            string requestId,
            AiRuntimeScaleOutRequestStatus targetStatus,
            Action<AiRuntimeScaleOutRequestRecord> apply)
        {
            this.RemoveExpiredUnsafe(DateTimeOffset.UtcNow);

            if (!this.requests.TryGetValue(requestId, out var request))
            {
                return false;
            }

            if (!CanTransition(request.Status, targetStatus))
            {
                return false;
            }

            request.Status = targetStatus;
            apply(request);

            this.requests[requestId] = Clone(request);

            return true;
        }

        /// <summary>
        /// Marks pending or observed requests as expired when their expiration time has passed.
        /// </summary>
        /// <param name="now">The current UTC time.</param>
        /// <remarks>
        /// The caller must hold <see cref="syncRoot" /> before invoking this method.
        /// </remarks>
        private void RemoveExpiredUnsafe(DateTimeOffset now)
        {
            var expiredRequestIds = this.requests
                .Where(pair => IsExpired(pair.Value, now))
                .Select(pair => pair.Key)
                .ToArray();

            foreach (var requestId in expiredRequestIds)
            {
                var request = this.requests[requestId];

                if (request.Status is AiRuntimeScaleOutRequestStatus.Pending or AiRuntimeScaleOutRequestStatus.Observed)
                {
                    request.Status = AiRuntimeScaleOutRequestStatus.Expired;
                    request.ExpiredAtUtc ??= now;
                    this.requests[requestId] = request;
                }
            }
        }

        /// <summary>
        /// Determines whether a request matches the supplied query filters.
        /// </summary>
        /// <param name="request">The request to evaluate.</param>
        /// <param name="query">The query filters.</param>
        /// <returns><see langword="true" /> when the request matches the query; otherwise, <see langword="false" />.</returns>
        private static bool MatchesQuery(
            AiRuntimeScaleOutRequestRecord request,
            AiRuntimeScaleOutRequestQuery query)
        {
            if (!string.IsNullOrWhiteSpace(query.ControlPlaneId) &&
                !string.Equals(request.ControlPlaneId, query.ControlPlaneId, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(query.TenantId) &&
                !string.Equals(request.TenantId, query.TenantId, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(query.PipelineKey) &&
                !string.Equals(request.PipelineKey, query.PipelineKey, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(query.SharedRunId) &&
                !string.Equals(request.SharedRunId, query.SharedRunId, StringComparison.Ordinal))
            {
                return false;
            }

            if (query.Statuses.Count > 0 && !query.Statuses.Contains(request.Status))
            {
                return false;
            }

            if (!query.IncludeExpired && request.Status is AiRuntimeScaleOutRequestStatus.Expired)
            {
                return false;
            }

            if (query.CreatedAfterUtc is not null && request.CreatedAtUtc < query.CreatedAfterUtc.Value)
            {
                return false;
            }

            if (query.CreatedBeforeUtc is not null && request.CreatedAtUtc > query.CreatedBeforeUtc.Value)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Determines whether a request has expired at the supplied UTC time.
        /// </summary>
        /// <param name="request">The request to evaluate.</param>
        /// <param name="now">The current UTC time.</param>
        /// <returns><see langword="true" /> when the request is expired; otherwise, <see langword="false" />.</returns>
        private static bool IsExpired(
            AiRuntimeScaleOutRequestRecord request,
            DateTimeOffset now)
        {
            return request.ExpiresAtUtc is not null &&
                   request.ExpiresAtUtc <= now &&
                   request.Status is AiRuntimeScaleOutRequestStatus.Pending or AiRuntimeScaleOutRequestStatus.Observed;
        }

        /// <summary>
        /// Gets the effective maximum result count for a query.
        /// </summary>
        /// <param name="query">The query options.</param>
        /// <param name="options">The store options.</param>
        /// <returns>The effective maximum result count.</returns>
        private static int GetMaxResults(
            AiRuntimeScaleOutRequestQuery query,
            AiRuntimeScaleOutRequestStoreOptions options)
        {
            if (query.MaxResults <= 0)
            {
                return options.MaxListResults;
            }

            return Math.Min(query.MaxResults, options.MaxListResults);
        }

        /// <summary>
        /// Determines whether a lifecycle transition is valid.
        /// </summary>
        /// <param name="currentStatus">The current request status.</param>
        /// <param name="targetStatus">The requested target status.</param>
        /// <returns><see langword="true" /> when the transition is valid; otherwise, <see langword="false" />.</returns>
        private static bool CanTransition(
            AiRuntimeScaleOutRequestStatus currentStatus,
            AiRuntimeScaleOutRequestStatus targetStatus)
        {
            if (currentStatus is AiRuntimeScaleOutRequestStatus.Fulfilled or
                AiRuntimeScaleOutRequestStatus.Rejected or
                AiRuntimeScaleOutRequestStatus.Expired or
                AiRuntimeScaleOutRequestStatus.Cancelled)
            {
                return false;
            }

            return targetStatus switch
            {
                AiRuntimeScaleOutRequestStatus.Observed =>
                    currentStatus is AiRuntimeScaleOutRequestStatus.Pending,

                AiRuntimeScaleOutRequestStatus.Fulfilled =>
                    currentStatus is AiRuntimeScaleOutRequestStatus.Pending or AiRuntimeScaleOutRequestStatus.Observed,

                AiRuntimeScaleOutRequestStatus.Rejected =>
                    currentStatus is AiRuntimeScaleOutRequestStatus.Pending or AiRuntimeScaleOutRequestStatus.Observed,

                AiRuntimeScaleOutRequestStatus.Expired =>
                    currentStatus is AiRuntimeScaleOutRequestStatus.Pending or AiRuntimeScaleOutRequestStatus.Observed,

                AiRuntimeScaleOutRequestStatus.Cancelled =>
                    currentStatus is AiRuntimeScaleOutRequestStatus.Pending or AiRuntimeScaleOutRequestStatus.Observed,

                _ => false
            };
        }

        /// <summary>
        /// Creates a defensive copy of a scale-out request record.
        /// </summary>
        /// <param name="request">The request to clone.</param>
        /// <returns>The cloned request.</returns>
        private static AiRuntimeScaleOutRequestRecord Clone(
            AiRuntimeScaleOutRequestRecord request)
        {
            ArgumentNullException.ThrowIfNull(request);

            return new AiRuntimeScaleOutRequestRecord
            {
                RequestId = request.RequestId,
                ControlPlaneId = request.ControlPlaneId,
                SharedRunId = request.SharedRunId,
                ExecutionContextSnapshot = request.ExecutionContextSnapshot,

                TenantId = request.TenantId,
                TenantGroupId = request.TenantGroupId,
                PipelineKey = request.PipelineKey,

                IsolationMode = request.IsolationMode,
                PreferDedicatedCapacity = request.PreferDedicatedCapacity,
                AllowSharedFallback = request.AllowSharedFallback,
                MaxRuntimeInstances = request.MaxRuntimeInstances,
                RuntimeInstanceIdPrefix = request.RuntimeInstanceIdPrefix,
                WorkerCountPerInstance = request.WorkerCountPerInstance,
                MaxConcurrentRunsPerInstance = request.MaxConcurrentRunsPerInstance,
                LocalQueueCapacity = request.LocalQueueCapacity,

                Status = request.Status,
                Reason = request.Reason,

                VisibleInstanceCount = request.VisibleInstanceCount,
                AvailableInstanceCount = request.AvailableInstanceCount,
                CurrentInstanceCount = request.CurrentInstanceCount,
                MaxInstanceCount = request.MaxInstanceCount,
                RequestedTargetInstanceCount = request.RequestedTargetInstanceCount,

                ProviderHint = request.ProviderHint,
                RequestedBy = request.RequestedBy,
                Source = request.Source,
                CorrelationId = request.CorrelationId,

                CreatedAtUtc = request.CreatedAtUtc,
                ObservedAtUtc = request.ObservedAtUtc,
                FulfilledAtUtc = request.FulfilledAtUtc,
                RejectedAtUtc = request.RejectedAtUtc,
                ExpiredAtUtc = request.ExpiredAtUtc,
                CancelledAtUtc = request.CancelledAtUtc,
                ExpiresAtUtc = request.ExpiresAtUtc,

                FulfilledRuntimeInstanceId = request.FulfilledRuntimeInstanceId,
                ObservedBy = request.ObservedBy,
                FulfilledBy = request.FulfilledBy,
                RejectedBy = request.RejectedBy,
                RejectionReason = request.RejectionReason,

                Metadata = new Dictionary<string, string>(
                    request.Metadata ?? new Dictionary<string, string>(),
                    StringComparer.OrdinalIgnoreCase)
            };
        }
    }
}