using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.ControlPlane.ExecutionAssistance;

namespace Multiplexed.AI.Runtime.ControlPlane.ExecutionAssistance
{
    /// <summary>
    /// Runs helper workers for a granted execution assistance lease.
    /// </summary>
    /// <remarks>
    /// The pump is responsible for starting helper workers under the worker budget
    /// granted by an assistance lease. It does not own the shared run lifecycle and
    /// must never change the local run ownership of the primary runtime instance.
    /// </remarks>
    public sealed class AiExecutionAssistancePump
    {
        private readonly IAiExecutionAssistanceWorker _worker;
        private readonly IAiExecutionAssistanceStore _store;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiExecutionAssistancePump"/> class.
        /// </summary>
        /// <param name="worker">The assistance worker.</param>
        /// <param name="store">The execution assistance lease store.</param>
        public AiExecutionAssistancePump(
            IAiExecutionAssistanceWorker worker,
            IAiExecutionAssistanceStore store)
        {
            _worker = worker ?? throw new ArgumentNullException(nameof(worker));
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>
        /// Runs assistance workers for the specified assistance lease.
        /// </summary>
        /// <param name="lease">The granted assistance lease.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The assistance pump result.</returns>
        public async Task<AiExecutionAssistancePumpResult> PumpAsync(
            AiExecutionAssistanceLease lease,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(lease);

            var startedAtUtc = DateTimeOffset.UtcNow;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (lease.MaxWorkers <= 0)
                {
                    return Failure(
                        lease,
                        startedAtUtc,
                        "Execution assistance lease does not allow any helper workers.");
                }

                if (lease.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                {
                    await _store.UpdateStatusAsync(
                            lease.LeaseId,
                            AiExecutionAssistanceStatus.Expired,
                            "Execution assistance lease expired before pump started.",
                            cancellationToken)
                        .ConfigureAwait(false);

                    return new AiExecutionAssistancePumpResult
                    {
                        Success = false,
                        LeaseId = lease.LeaseId,
                        ExecutionId = lease.ExecutionId,
                        HelperRuntimeInstanceId = lease.HelperRuntimeInstanceId,
                        Status = AiExecutionAssistanceStatus.Expired,
                        StartedWorkerCount = 0,
                        FailureReason = "Execution assistance lease expired before pump started.",
                        StartedAtUtc = startedAtUtc,
                        CompletedAtUtc = DateTimeOffset.UtcNow,
                        Metadata = CreateMetadata(
                            lease,
                            0)
                    };
                }

                var workerTasks = Enumerable
                    .Range(0, lease.MaxWorkers)
                    .Select(_ => _worker.AssistAsync(
                        lease,
                        cancellationToken))
                    .ToArray();

                await Task.WhenAll(workerTasks)
                    .ConfigureAwait(false);

                var updatedLease = await _store.GetAsync(
                        lease.LeaseId,
                        cancellationToken)
                    .ConfigureAwait(false);

                return new AiExecutionAssistancePumpResult
                {
                    Success = true,
                    LeaseId = lease.LeaseId,
                    ExecutionId = lease.ExecutionId,
                    HelperRuntimeInstanceId = lease.HelperRuntimeInstanceId,
                    Status = updatedLease?.Status ?? AiExecutionAssistanceStatus.Released,
                    StartedWorkerCount = lease.MaxWorkers,
                    StartedAtUtc = startedAtUtc,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    Metadata = CreateMetadata(
                        lease,
                        lease.MaxWorkers)
                };
            }
            catch (OperationCanceledException)
            {
                await _store.UpdateStatusAsync(
                        lease.LeaseId,
                        AiExecutionAssistanceStatus.Revoked,
                        "Execution assistance pump was cancelled.",
                        CancellationToken.None)
                    .ConfigureAwait(false);

                throw;
            }
            catch (Exception ex)
            {
                await _store.UpdateStatusAsync(
                        lease.LeaseId,
                        AiExecutionAssistanceStatus.Failed,
                        ex.Message,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                return Failure(
                    lease,
                    startedAtUtc,
                    ex.Message);
            }
        }

        private static AiExecutionAssistancePumpResult Failure(
            AiExecutionAssistanceLease lease,
            DateTimeOffset startedAtUtc,
            string failureReason)
        {
            return new AiExecutionAssistancePumpResult
            {
                Success = false,
                LeaseId = lease.LeaseId,
                ExecutionId = lease.ExecutionId,
                HelperRuntimeInstanceId = lease.HelperRuntimeInstanceId,
                Status = AiExecutionAssistanceStatus.Failed,
                StartedWorkerCount = 0,
                FailureReason = failureReason,
                StartedAtUtc = startedAtUtc,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Metadata = CreateMetadata(
                    lease,
                    0)
            };
        }

        private static IReadOnlyDictionary<string, string> CreateMetadata(
            AiExecutionAssistanceLease lease,
            int startedWorkerCount)
        {
            return new Dictionary<string, string>
            {
                [AiExecutionMetadataKeys.ExecutionId] = lease.ExecutionId,
                ["assistance.lease.id"] = lease.LeaseId,
                [AiExecutionAssistanceMetadataKeys.PrimaryRuntimeInstanceId] = lease.PrimaryRuntimeInstanceId,
                [AiExecutionAssistanceMetadataKeys.HelperRuntimeInstanceId] = lease.HelperRuntimeInstanceId,
                ["assistance.max.workers"] = lease.MaxWorkers.ToString(),
                ["assistance.started.worker.count"] = startedWorkerCount.ToString()
            };
        }
    }
}