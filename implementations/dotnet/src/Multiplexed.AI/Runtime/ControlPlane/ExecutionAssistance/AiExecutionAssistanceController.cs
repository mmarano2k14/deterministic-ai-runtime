using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Control;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.ExecutionAssistance;
using Multiplexed.Abstractions.AI.Observability;

namespace Multiplexed.AI.Runtime.ControlPlane.ExecutionAssistance
{
    /// <summary>
    /// Default implementation of <see cref="IAiExecutionAssistanceController"/>.
    /// </summary>
    /// <remarks>
    /// The controller decides whether an idle runtime instance may assist an
    /// active execution owned by another primary runtime instance.
    /// </remarks>
    public sealed class AiExecutionAssistanceController : IAiExecutionAssistanceController
    {
        private readonly IAiExecutionAssistanceStore _store;
        private readonly AiExecutionAssistanceOptions _options;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiExecutionAssistanceController"/> class.
        /// </summary>
        /// <param name="store">The assistance lease store.</param>
        /// <param name="options">The assistance options.</param>
        public AiExecutionAssistanceController(
            IAiExecutionAssistanceStore store,
            IOptions<AiExecutionAssistanceOptions> options)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        }

        /// <inheritdoc />
        public async Task<AiExecutionAssistanceDecision> EvaluateAsync(
            AiExecutionAssistanceRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            cancellationToken.ThrowIfCancellationRequested();

            if (!_options.Enabled)
            {
                return Denied(
                    request,
                    "Execution assistance is disabled.");
            }

            if (string.Equals(
                    request.PrimaryRuntimeInstanceId,
                    request.HelperRuntimeInstanceId,
                    StringComparison.Ordinal))
            {
                return Denied(
                    request,
                    "The primary runtime instance cannot be registered as a helper for its own execution.");
            }

            if (_options.OnlyWhenLocalQueueIdle &&
                !request.HelperIsIdle)
            {
                return Denied(
                    request,
                    "The helper runtime instance is not idle.");
            }

            if (request.HelperQueueDepth > _options.MaxHelperQueueDepth)
            {
                return Denied(
                    request,
                    "The helper runtime instance queue depth is above the allowed assistance threshold.");
            }

            if (request.HelperAvailableWorkerSlots <= 0)
            {
                return Denied(
                    request,
                    "The helper runtime instance has no available worker slots.");
            }

            if (request.ReadyStepCount < _options.MinReadyStepsToAssist)
            {
                return Denied(
                    request,
                    "The execution does not have enough ready steps to justify assistance.");
            }

            if (request.RemainingStepCount < _options.MinRemainingStepsToAssist)
            {
                return Denied(
                    request,
                    "The execution does not have enough remaining work to justify assistance.");
            }

            if (request.ActiveHelperCount >= _options.MaxHelpersPerExecution)
            {
                return Denied(
                    request,
                    "The execution already has the maximum number of helper runtime instances.");
            }

            if (request.ActiveWorkerCountForExecution >= _options.MaxWorkersPerExecution)
            {
                return Denied(
                    request,
                    "The execution already reached the maximum worker budget.");
            }

            var maxWorkersForLease = Math.Min(
                _options.MaxWorkersPerHelperInstance,
                request.HelperAvailableWorkerSlots);

            var remainingExecutionWorkerBudget =
                _options.MaxWorkersPerExecution - request.ActiveWorkerCountForExecution;

            maxWorkersForLease = Math.Min(
                maxWorkersForLease,
                remainingExecutionWorkerBudget);

            if (maxWorkersForLease <= 0)
            {
                return Denied(
                    request,
                    "No worker budget remains for this execution.");
            }

            var lease = new AiExecutionAssistanceLease
            {
                LeaseId = Guid.NewGuid().ToString("N"),
                ExecutionId = request.ExecutionId,
                PrimaryRuntimeInstanceId = request.PrimaryRuntimeInstanceId,
                HelperRuntimeInstanceId = request.HelperRuntimeInstanceId,
                MaxWorkers = maxWorkersForLease,
                Status = AiExecutionAssistanceStatus.Granted,
                GrantedAtUtc = DateTimeOffset.UtcNow,
                ExpiresAtUtc = DateTimeOffset.UtcNow.Add(_options.LeaseTtl),
                Reason = request.Reason,
                Metadata = MergeMetadata(
                    request,
                    maxWorkersForLease)
            };

            await _store.RegisterAsync(
                    lease,
                    cancellationToken)
                .ConfigureAwait(false);

            return new AiExecutionAssistanceDecision
            {
                Allowed = true,
                ExecutionId = request.ExecutionId,
                PrimaryRuntimeInstanceId = request.PrimaryRuntimeInstanceId,
                HelperRuntimeInstanceId = request.HelperRuntimeInstanceId,
                Lease = lease,
                Reason = "Execution assistance was granted.",
                Metadata = lease.Metadata
            };
        }

        private static AiExecutionAssistanceDecision Denied(
            AiExecutionAssistanceRequest request,
            string reason)
        {
            return new AiExecutionAssistanceDecision
            {
                Allowed = false,
                ExecutionId = request.ExecutionId,
                PrimaryRuntimeInstanceId = request.PrimaryRuntimeInstanceId,
                HelperRuntimeInstanceId = request.HelperRuntimeInstanceId,
                Reason = reason,
                Metadata = new Dictionary<string, string>
                {
                    [AiExecutionMetadataKeys.ExecutionId] = request.ExecutionId,
                    [AiExecutionAssistanceMetadataKeys.PrimaryRuntimeInstanceId] = request.PrimaryRuntimeInstanceId,
                    [AiExecutionAssistanceMetadataKeys.HelperRuntimeInstanceId] = request.HelperRuntimeInstanceId,
                    ["assistance.allowed"] = "false",
                    ["assistance.reason"] = reason
                }
            };
        }

        private static IReadOnlyDictionary<string, string> MergeMetadata(
            AiExecutionAssistanceRequest request,
            int maxWorkersForLease)
        {
            var metadata = new Dictionary<string, string>(
                request.Metadata,
                StringComparer.Ordinal)
            {
                [AiExecutionMetadataKeys.ExecutionId] = request.ExecutionId,
                [AiExecutionAssistanceMetadataKeys.PrimaryRuntimeInstanceId] = request.PrimaryRuntimeInstanceId,
                [AiExecutionAssistanceMetadataKeys.HelperRuntimeInstanceId] = request.HelperRuntimeInstanceId,
                ["assistance.allowed"] = "true",
                ["assistance.max.workers"] = maxWorkersForLease.ToString(),
                ["ready.step.count"] = request.ReadyStepCount.ToString(),
                ["remaining.step.count"] = request.RemainingStepCount.ToString(),
                ["active.helper.count"] = request.ActiveHelperCount.ToString(),
                ["active.worker.count.for.execution"] = request.ActiveWorkerCountForExecution.ToString(),
                ["helper.is.idle"] = request.HelperIsIdle.ToString(),
                ["helper.queue.depth"] = request.HelperQueueDepth.ToString(),
                ["helper.available.worker.slots"] = request.HelperAvailableWorkerSlots.ToString()
            };

            if (!string.IsNullOrWhiteSpace(request.CorrelationId))
            {
                metadata[AiObservabilityMetadataKeys.CorrelationId] = request.CorrelationId;
            }

            if (!string.IsNullOrWhiteSpace(request.RequestedBy))
            {
                metadata[AiExecutionControlMetadataKeys.RequestedBy] = request.RequestedBy;
            }

            if (!string.IsNullOrWhiteSpace(request.Source))
            {
                metadata["source"] = request.Source;
            }

            if (!string.IsNullOrWhiteSpace(request.Reason))
            {
                metadata["reason"] = request.Reason;
            }

            return metadata;
        }
    }
}