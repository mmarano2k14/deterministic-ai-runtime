using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.AI.McpServer.Host.Configuration;
using Multiplexed.AI.Runtime.Invocation.Durable;

namespace Multiplexed.AI.McpServer.Host.Bootstrap
{
    /// <summary>
    /// Produces matrix-only result-acceptance evidence through the production durable invocation store.
    /// </summary>
    internal sealed class MatrixJournalResultAcceptanceProbe
    {
        private readonly IAiDurableInvocationStore store;
        private readonly AiMatrixHarnessOptions options;
        private readonly string controlPlaneId;

        public MatrixJournalResultAcceptanceProbe(
            IAiDurableInvocationStore store,
            IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(configuration);

            this.store = store;
            this.options = configuration.GetSection("AiMatrixHarness").Get<AiMatrixHarnessOptions>()
                ?? new AiMatrixHarnessOptions();
            this.controlPlaneId =
                configuration["AiMcpHost:ControlPlaneId"]
                ?? configuration["AiEngine:ControlPlane:ControlPlaneId"]
                ?? "matrix-control";
        }

        public Task<object> RunAsync(
            string acceptanceCase,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(acceptanceCase);

            return acceptanceCase switch
            {
                "accepted-result-replay" => RunAcceptedResultReplayAsync(cancellationToken),
                "duplicate-delivery-convergence" => RunDuplicateDeliveryConvergenceAsync(cancellationToken),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(acceptanceCase),
                    acceptanceCase,
                    "Unsupported matrix journal result-acceptance case.")
            };
        }

        private async Task<object> RunAcceptedResultReplayAsync(
            CancellationToken cancellationToken)
        {
            var definition = CreateDefinition("accepted-result-replay");
            var journal = new AiDurableInvocationJournal(this.store);
            var prepared = await journal
                .PrepareAsync(definition, cancellationToken)
                .ConfigureAwait(false);
            var leased = await journal
                .TryAcquireLeaseAsync(
                    definition.Scope,
                    definition.Identity,
                    "matrix-journal-worker-a",
                    TimeSpan.FromMinutes(1),
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The durable invocation lease was not acquired.");
            var lease = leased.Lease
                ?? throw new InvalidOperationException("The durable invocation lease fence is missing.");
            var result = new AiDurableInvocationResult(true, "{\"value\":42,\"source\":\"matrix\"}");

            var first = await journal
                .CompleteAsync(
                    definition.Scope,
                    definition.Identity,
                    lease,
                    result,
                    cancellationToken)
                .ConfigureAwait(false);

            var replayJournal = new AiDurableInvocationJournal(this.store);
            var replay = await replayJournal
                .CompleteAsync(
                    definition.Scope,
                    definition.Identity,
                    lease,
                    result,
                    cancellationToken)
                .ConfigureAwait(false);
            var terminal = await replayJournal
                .GetAsync(definition.Scope, definition.Identity, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The accepted durable invocation record is missing.");

            if (first != AiDurableInvocationCompletionStatus.Accepted ||
                replay != AiDurableInvocationCompletionStatus.AlreadyAccepted)
            {
                throw new InvalidOperationException(
                    $"Unexpected result acceptance statuses: first='{first}', replay='{replay}'.");
            }

            return new
            {
                acceptanceCase = "accepted-result-replay",
                operationId = terminal.OperationId,
                firstCompletionStatus = first.ToString(),
                replayCompletionStatus = replay.ToString(),
                terminalStatus = terminal.Status.ToString(),
                continuationStatus = terminal.ContinuationStatus.ToString(),
                resultSha256 = terminal.ResultSha256,
                leaseEpoch = terminal.Lease?.Epoch,
                revision = terminal.Revision,
                preparedRevision = prepared.Revision
            };
        }

        private async Task<object> RunDuplicateDeliveryConvergenceAsync(
            CancellationToken cancellationToken)
        {
            var definition = CreateDefinition("duplicate-delivery-convergence");
            var journal = new AiDurableInvocationJournal(this.store);
            await journal.PrepareAsync(definition, cancellationToken).ConfigureAwait(false);
            var leased = await journal
                .TryAcquireLeaseAsync(
                    definition.Scope,
                    definition.Identity,
                    "matrix-journal-worker-b",
                    TimeSpan.FromMinutes(1),
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The durable invocation lease was not acquired.");
            var lease = leased.Lease
                ?? throw new InvalidOperationException("The durable invocation lease fence is missing.");
            var result = new AiDurableInvocationResult(true, "{\"value\":84,\"source\":\"matrix\"}");

            var deliveries = Enumerable.Range(0, 8)
                .Select(_ => new AiDurableInvocationJournal(this.store)
                    .CompleteAsync(
                        definition.Scope,
                        definition.Identity,
                        lease,
                        result,
                        cancellationToken))
                .ToArray();
            var statuses = await Task.WhenAll(deliveries).ConfigureAwait(false);
            var accepted = statuses.Count(status => status == AiDurableInvocationCompletionStatus.Accepted);
            var alreadyAccepted = statuses.Count(status => status == AiDurableInvocationCompletionStatus.AlreadyAccepted);
            var rejected = statuses.Count(status => status == AiDurableInvocationCompletionStatus.LeaseRejected);
            var terminal = await new AiDurableInvocationJournal(this.store)
                .GetAsync(definition.Scope, definition.Identity, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The converged durable invocation record is missing.");

            if (accepted != 1 || alreadyAccepted != 7 || rejected != 0)
            {
                throw new InvalidOperationException(
                    $"Duplicate result delivery did not converge deterministically. Accepted='{accepted}', AlreadyAccepted='{alreadyAccepted}', LeaseRejected='{rejected}'.");
            }

            return new
            {
                acceptanceCase = "duplicate-delivery-convergence",
                operationId = terminal.OperationId,
                deliveryCount = statuses.Length,
                acceptedCount = accepted,
                alreadyAcceptedCount = alreadyAccepted,
                leaseRejectedCount = rejected,
                terminalStatus = terminal.Status.ToString(),
                continuationStatus = terminal.ContinuationStatus.ToString(),
                resultSha256 = terminal.ResultSha256,
                leaseEpoch = terminal.Lease?.Epoch,
                revision = terminal.Revision
            };
        }

        private AiDurableInvocationDefinition CreateDefinition(string acceptanceCase)
        {
            var suffix = Guid.NewGuid().ToString("N");
            var identity = new AiDurableInvocationIdentity(
                this.options.TenantId,
                $"matrix-journal-{acceptanceCase}-{suffix}",
                "work");
            var scope = new AiDurableInvocationScope(
                this.options.TenantId,
                this.options.TenantGroupId,
                this.controlPlaneId);
            var target = new AiDurableInvocationTarget(
                "matrix-journal",
                "1",
                new string('a', 64),
                $"matrix-publication-{suffix}",
                new string('b', 64),
                $"matrix-implementation-{suffix}",
                new string('c', 64),
                "python",
                "matrix-python",
                new string('d', 64));

            return new AiDurableInvocationDefinition(
                identity,
                scope,
                target,
                $"{{\"case\":\"{acceptanceCase}\"}}");
        }
    }
}
