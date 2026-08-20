using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.ExecutionAssistance;

namespace Multiplexed.AI.Runtime.ControlPlane.ExecutionAssistance
{
    /// <summary>
    /// Coordinates automatic cross-instance execution assistance for an idle runtime instance.
    /// </summary>
    /// <remarks>
    /// The coordinator performs a single evaluation cycle for one helper runtime instance.
    /// It discovers active assistance candidates, evaluates whether the current runtime
    /// instance may help them, and starts bounded helper pumps when assistance is allowed.
    ///
    /// This component does not own shared runs and must not change primary run ownership.
    /// It only grants helper leases and advances existing execution identifiers through
    /// the assistance pump.
    /// </remarks>
    public sealed class AiExecutionAssistanceCoordinator
    {
        private readonly IAiExecutionAssistanceCandidateStore _candidateStore;
        private readonly IAiExecutionAssistanceStore _assistanceStore;
        private readonly IAiExecutionAssistanceController _controller;
        private readonly AiExecutionAssistancePump _pump;
        private readonly AiExecutionAssistanceOptions _options;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiExecutionAssistanceCoordinator"/> class.
        /// </summary>
        /// <param name="candidateStore">The assistance candidate store.</param>
        /// <param name="assistanceStore">The assistance lease store.</param>
        /// <param name="controller">The assistance decision controller.</param>
        /// <param name="pump">The assistance pump.</param>
        /// <param name="options">The assistance options.</param>
        public AiExecutionAssistanceCoordinator(
            IAiExecutionAssistanceCandidateStore candidateStore,
            IAiExecutionAssistanceStore assistanceStore,
            IAiExecutionAssistanceController controller,
            AiExecutionAssistancePump pump,
            IOptions<AiExecutionAssistanceOptions> options)
        {
            _candidateStore = candidateStore ?? throw new ArgumentNullException(nameof(candidateStore));
            _assistanceStore = assistanceStore ?? throw new ArgumentNullException(nameof(assistanceStore));
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            _pump = pump ?? throw new ArgumentNullException(nameof(pump));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>
        /// Runs one automatic assistance evaluation cycle for a helper runtime instance.
        /// </summary>
        /// <param name="helperRuntimeInstanceId">The helper runtime instance identifier.</param>
        /// <param name="helperIsIdle">Whether the helper runtime instance is currently idle.</param>
        /// <param name="helperQueueDepth">The helper local queue depth.</param>
        /// <param name="helperAvailableWorkerSlots">The number of available helper worker slots.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The coordinator cycle result.</returns>
        public async Task<AiExecutionAssistanceCoordinatorResult> RunOnceAsync(
            string helperRuntimeInstanceId,
            bool helperIsIdle,
            int helperQueueDepth,
            int helperAvailableWorkerSlots,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(helperRuntimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            if (!_options.Enabled)
            {
                return new AiExecutionAssistanceCoordinatorResult
                {
                    Enabled = false,
                    HelperRuntimeInstanceId = helperRuntimeInstanceId,
                    Metadata = new Dictionary<string, string>
                    {
                        ["execution.assistance.enabled"] = "false",
                        [AiExecutionAssistanceMetadataKeys.HelperRuntimeInstanceId] = helperRuntimeInstanceId
                    }
                };
            }

            if (_options.OnlyWhenLocalQueueIdle && !helperIsIdle)
            {
                return new AiExecutionAssistanceCoordinatorResult
                {
                    Enabled = true,
                    HelperRuntimeInstanceId = helperRuntimeInstanceId,
                    Metadata = new Dictionary<string, string>
                    {
                        ["execution.assistance.enabled"] = "true",
                        [AiExecutionAssistanceMetadataKeys.HelperRuntimeInstanceId] = helperRuntimeInstanceId,
                        ["helper.skipped.reason"] = "helper-not-idle"
                    }
                };
            }

            var candidates = await _candidateStore.ListActiveAsync(
                    cancellationToken)
                .ConfigureAwait(false);

            var decisions = new List<AiExecutionAssistanceDecision>();
            var pumpResults = new List<AiExecutionAssistancePumpResult>();

            var skippedCandidateCount = 0;

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.Equals(
                        candidate.PrimaryRuntimeInstanceId,
                        helperRuntimeInstanceId,
                        StringComparison.Ordinal))
                {
                    skippedCandidateCount++;
                    continue;
                }

                var activeLeases = await _assistanceStore.ListByExecutionAsync(
                        candidate.ExecutionId,
                        includeTerminal: false,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (activeLeases.Any(lease =>
                        string.Equals(
                            lease.HelperRuntimeInstanceId,
                            helperRuntimeInstanceId,
                            StringComparison.Ordinal)))
                {
                    skippedCandidateCount++;
                    continue;
                }

                var activeWorkerCount =
                    candidate.EstimatedActiveWorkerCount +
                    activeLeases.Sum(lease => lease.MaxWorkers);

                var request = new AiExecutionAssistanceRequest
                {
                    ExecutionId = candidate.ExecutionId,
                    PrimaryRuntimeInstanceId = candidate.PrimaryRuntimeInstanceId,
                    HelperRuntimeInstanceId = helperRuntimeInstanceId,
                    ReadyStepCount = candidate.EstimatedReadyStepCount,
                    RemainingStepCount = candidate.EstimatedRemainingStepCount,
                    ActiveHelperCount = activeLeases
                        .Select(lease => lease.HelperRuntimeInstanceId)
                        .Distinct(StringComparer.Ordinal)
                        .Count(),
                    ActiveWorkerCountForExecution = activeWorkerCount,
                    HelperIsIdle = helperIsIdle,
                    HelperQueueDepth = helperQueueDepth,
                    HelperAvailableWorkerSlots = helperAvailableWorkerSlots,
                    RequestedBy = "execution-assistance-coordinator",
                    Source = "execution-assistance-coordinator",
                    Reason = "Automatic execution assistance evaluation.",
                    Metadata = new Dictionary<string, string>
                    {
                        ["candidate.execution.id"] = candidate.ExecutionId,
                        ["candidate.primary.runtime.instance.id"] = candidate.PrimaryRuntimeInstanceId,
                        [AiExecutionAssistanceMetadataKeys.HelperRuntimeInstanceId] = helperRuntimeInstanceId,
                        ["candidate.pipeline.name"] = candidate.PipelineName
                    }
                };

                var decision = await _controller.EvaluateAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);

                decisions.Add(decision);

                if (!decision.Allowed || decision.Lease is null)
                {
                    continue;
                }

                var pumpResult = await _pump.PumpAsync(
                        decision.Lease,
                        cancellationToken)
                    .ConfigureAwait(false);

                pumpResults.Add(pumpResult);

                if (pumpResults.Count >= _options.MaxHelpersPerExecution)
                {
                    break;
                }
            }

            return new AiExecutionAssistanceCoordinatorResult
            {
                Enabled = true,
                HelperRuntimeInstanceId = helperRuntimeInstanceId,
                CandidateCount = candidates.Count,
                SkippedCandidateCount = skippedCandidateCount,
                EvaluatedDecisionCount = decisions.Count,
                GrantedLeaseCount = decisions.Count(decision => decision.Allowed && decision.Lease is not null),
                StartedPumpCount = pumpResults.Count,
                Decisions = decisions,
                PumpResults = pumpResults,
                Metadata = new Dictionary<string, string>
                {
                    ["execution.assistance.enabled"] = "true",
                    [AiExecutionAssistanceMetadataKeys.HelperRuntimeInstanceId] = helperRuntimeInstanceId,
                    ["candidate.count"] = candidates.Count.ToString(),
                    ["decision.count"] = decisions.Count.ToString(),
                    ["started.pump.count"] = pumpResults.Count.ToString()
                }
            };
        }
    }
}