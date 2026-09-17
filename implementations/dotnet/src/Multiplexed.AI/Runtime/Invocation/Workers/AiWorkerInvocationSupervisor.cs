using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Durable;

namespace Multiplexed.AI.Runtime.Invocation.Workers
{
    public enum AiWorkerDispatchDisposition
    {
        Accepted, AlreadyAccepted, AlreadyTerminal, NotReady, Busy,
        ReconciliationRequired, LeaseLost, TechnicalFailure, CapacityQuarantined
    }
    public sealed record AiWorkerDispatchResult(AiWorkerDispatchDisposition Disposition,
        string? OperationId = null, long? Epoch = null, string? ErrorType = null);

    /// <summary>
    /// One bounded attempt under the existing journal lease. It never prepares a new operation,
    /// changes generation, fabricates a business result or acknowledges a DAG continuation.
    /// </summary>
    public sealed class AiWorkerInvocationSupervisor
    {
        private readonly AiDurableInvocationJournal _journal;
        private readonly IAiWorkerInvocationPreparer _preparer;
        private readonly IAiWorkerInvocationTransport _transport;
        private readonly IAiControlPlaneIdResolver _controlPlane;
        private readonly AiWorkerProcessCapacity _capacity;
        private readonly AiWorkerSupervisionOptions _options;
        private readonly TimeProvider _time;
        private readonly ILogger<AiWorkerInvocationSupervisor> _logger;
        public AiWorkerInvocationSupervisor(AiDurableInvocationJournal journal, IAiWorkerInvocationPreparer preparer,
            IAiWorkerInvocationTransport transport, IAiControlPlaneIdResolver controlPlane,
            AiWorkerProcessCapacity capacity, AiWorkerSupervisionOptions options,
            ILogger<AiWorkerInvocationSupervisor> logger, TimeProvider? timeProvider = null)
        {
            _journal = journal; _preparer = preparer; _transport = transport; _controlPlane = controlPlane;
            _capacity = capacity; _options = options; _logger = logger; _time = timeProvider ?? TimeProvider.System;
        }
        public async Task<AiWorkerDispatchResult> DispatchAsync(AiDurableInvocationScope scope,
            AiDurableInvocationIdentity identity, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); AiDurableInvocationValidation.ValidateAddress(scope, identity);
            if (await _controlPlane.ResolveAsync(cancellationToken).ConfigureAwait(false) != scope.ControlPlaneId)
                throw new UnauthorizedAccessException("Worker dispatch belongs to a different logical control plane.");
            var current = await _journal.GetAsync(scope, identity, cancellationToken).ConfigureAwait(false);
            if (current is null) return new(AiWorkerDispatchDisposition.NotReady);
            if (AiDurableInvocationValidation.Terminal(current)) return new(AiWorkerDispatchDisposition.AlreadyTerminal, current.OperationId);
            if (current.Lease?.ExpiresAtUtc > _time.GetUtcNow()) return new(AiWorkerDispatchDisposition.NotReady, current.OperationId);
            if (current.Lease is not null && (!_options.AllowExpiredLeaseReassignment || current.Lease.Epoch >= _options.MaxAssignmentEpoch))
                return new(AiWorkerDispatchDisposition.ReconciliationRequired, current.OperationId, current.Lease.Epoch);
            using var slot = _capacity.TryEnter();
            if (slot is null) return new(AiWorkerDispatchDisposition.Busy, current.OperationId);
            AiDurableInvocationRecord? leased = null;
            var failurePhase = "acquire-worker-lease";
            try
            {
                // A new physical worker identity never changes the logical operation/effect identities.
                var workerId = "hosted-" + Guid.NewGuid().ToString("N");
                leased = await _journal.TryAcquireWorkerLeaseAsync(scope, identity, workerId, _options.LeaseDuration,
                    _options.AllowExpiredLeaseReassignment, _options.MaxAssignmentEpoch, cancellationToken).ConfigureAwait(false);
                if (leased is null) return new(AiWorkerDispatchDisposition.NotReady, current.OperationId);
                using var guard = new AiWorkerLeaseGuard(_journal, leased, _options, _time);
                var dispatchDeadline = _time.GetUtcNow() + _options.ExecutionTimeout;
                using var deadline = new CancellationTokenSource(_options.ExecutionTimeout, _time);
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, guard.LostToken, deadline.Token);
                try
                {
                    failurePhase = "prepare-worker-code";
                    var code = await _preparer.PrepareAsync(leased, stop.Token).ConfigureAwait(false);
                    if (code.Target != leased.Definition.Target) throw new InvalidOperationException("Worker preparation substituted an immutable target.");
                    stop.Token.ThrowIfCancellationRequested();
                    using var inputs = JsonDocument.Parse(leased.Definition.InputsJson);
                    var request = new AiWorkerInvocationRequest(1, "invoke", Guid.NewGuid().ToString("N"), leased.OperationId,
                        leased.EffectIdempotencyKey, workerId, leased.Lease!.Epoch, identity.TenantId,
                        identity.ExecutionId, identity.StepName, identity.Generation,
                        dispatchDeadline,
                        Activity.Current?.IdFormat == ActivityIdFormat.W3C ? Activity.Current.Id : null,
                        inputs.RootElement.Clone(), code);
                    // Materialization can consume time. No process may start after the safety deadline.
                    failurePhase = "pre-launch-heartbeat";
                    await guard.HeartbeatAsync(stop.Token).ConfigureAwait(false);
                    _logger.LogDebug("Worker dispatch started. OperationId={OperationId}, Epoch={Epoch}, Language={Language}.",
                        leased.OperationId, leased.Lease.Epoch, code.Target.ExecutionLanguage);
                    failurePhase = "invoke-worker-transport";
                    var result = await _transport.InvokeAsync(request, guard.HeartbeatAsync, stop.Token).ConfigureAwait(false);
                    stop.Token.ThrowIfCancellationRequested();
                    failurePhase = "complete-journal-result";
                    var completion = await _journal.CompleteAsync(scope, identity, guard.Lease, result, stop.Token).ConfigureAwait(false);
                    var disposition = completion switch
                    {
                        AiDurableInvocationCompletionStatus.Accepted => AiWorkerDispatchDisposition.Accepted,
                        AiDurableInvocationCompletionStatus.AlreadyAccepted => AiWorkerDispatchDisposition.AlreadyAccepted,
                        _ => AiWorkerDispatchDisposition.LeaseLost
                    };
                    _logger.LogDebug("Worker dispatch finished. OperationId={OperationId}, Epoch={Epoch}, Disposition={Disposition}.",
                        leased.OperationId, leased.Lease.Epoch, disposition);
                    return new(disposition, leased.OperationId, leased.Lease.Epoch);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return new(guard.IsLost ? AiWorkerDispatchDisposition.LeaseLost : AiWorkerDispatchDisposition.TechnicalFailure,
                        leased.OperationId, leased.Lease!.Epoch, guard.IsLost ? "LeaseAuthorityExpired" : "ExecutionDeadlineElapsed");
                }
            }
            catch (AiWorkerProcessCleanupException exception)
            {
                slot.Quarantine();
                LogFailure(exception);
                return new(AiWorkerDispatchDisposition.CapacityQuarantined, current.OperationId, leased?.Lease?.Epoch, exception.GetType().FullName);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                LogFailure(exception);
                return new(AiWorkerDispatchDisposition.TechnicalFailure, current.OperationId, leased?.Lease?.Epoch, exception.GetType().FullName);
            }
            void LogFailure(Exception exception) => _logger.LogWarning(
                "Worker dispatch retained for reconciliation. OperationId={OperationId}, Epoch={Epoch}, Phase={Phase}, ExceptionType={ExceptionType}.",
                current.OperationId, leased?.Lease?.Epoch, failurePhase, exception.GetType().FullName);
        }
    }
}
