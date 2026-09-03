using System.Text.Json;
using StackExchange.Redis;
using MultiplexedRbac.Sample.Crm.Api.AI.Models;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Runtime
{
    public sealed class RedisRuntimeAnalysisHumanApprovalStore :
        IRuntimeAnalysisHumanApprovalStore
    {
        private const string KeyPrefix =
            "ai-demo:runtime-analysis:human-approval:v1:";

        private static readonly TimeSpan Expiration =
            TimeSpan.FromHours(24);

        private static readonly JsonSerializerOptions SerializerOptions =
            new()
            {
                PropertyNameCaseInsensitive = true
            };

        private readonly IDatabase _database;

        public RedisRuntimeAnalysisHumanApprovalStore(
            IConnectionMultiplexer connectionMultiplexer)
        {
            ArgumentNullException.ThrowIfNull(
                connectionMultiplexer);

            _database = connectionMultiplexer.GetDatabase();
        }

        public async Task<RuntimeAnalysisHumanApprovalRecord?> GetAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                executionId);

            cancellationToken.ThrowIfCancellationRequested();

            var value = await _database.StringGetAsync(
                    CreateKey(executionId))
                .ConfigureAwait(false);

            return value.IsNullOrEmpty
                ? null
                : Deserialize(value!);
        }

        public async Task<RuntimeAnalysisHumanApprovalRecord> CreatePendingAsync(
            RuntimeAnalysisHumanApprovalRecord record,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(
                record);

            cancellationToken.ThrowIfCancellationRequested();

            var key = CreateKey(record.ExecutionId);
            var json = JsonSerializer.Serialize(
                record,
                SerializerOptions);

            var created = await _database.StringSetAsync(
                    key,
                    json,
                    Expiration,
                    When.NotExists)
                .ConfigureAwait(false);

            if (created)
            {
                return record;
            }

            var existing = await GetAsync(
                    record.ExecutionId,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Approval record '{record.ExecutionId}' disappeared after Redis create collision.");

            if (!string.Equals(
                    existing.StepName,
                    record.StepName,
                    StringComparison.Ordinal)
                || !string.Equals(
                    existing.ContinuationId,
                    record.ContinuationId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Approval record collision for execution '{record.ExecutionId}'.");
            }

            return existing;
        }

        public Task<RuntimeAnalysisHumanApprovalRecord> AttachInitialRunIdAsync(
            string executionId,
            string runId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                runId);

            return UpdateAsync(
                executionId,
                current =>
                {
                    if (!string.IsNullOrWhiteSpace(
                            current.InitialRunId))
                    {
                        if (!string.Equals(
                                current.InitialRunId,
                                runId,
                                StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                $"Approval record for execution '{executionId}' is already bound to initial run '{current.InitialRunId}'.");
                        }

                        return current;
                    }

                    return Clone(
                        current,
                        initialRunId: runId);
                },
                cancellationToken);
        }

        public Task<RuntimeAnalysisHumanApprovalRecord> DecideAsync(
            string executionId,
            string status,
            string decidedBy,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                status);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                decidedBy);

            if (!string.Equals(
                    status,
                    RuntimeAnalysisHumanApprovalStatuses.Approved,
                    StringComparison.Ordinal)
                && !string.Equals(
                    status,
                    RuntimeAnalysisHumanApprovalStatuses.Rejected,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Unsupported approval status '{status}'.",
                    nameof(status));
            }

            return UpdateAsync(
                executionId,
                current =>
                {
                    if (string.Equals(
                            current.Status,
                            status,
                            StringComparison.Ordinal))
                    {
                        return current;
                    }

                    if (!string.Equals(
                            current.Status,
                            RuntimeAnalysisHumanApprovalStatuses.Pending,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Approval for execution '{executionId}' is already '{current.Status}' and cannot become '{status}'.");
                    }

                    return Clone(
                        current,
                        status: status,
                        decidedAtUtc: DateTimeOffset.UtcNow,
                        decidedBy: decidedBy);
                },
                cancellationToken);
        }

        private async Task<RuntimeAnalysisHumanApprovalRecord> UpdateAsync(
            string executionId,
            Func<RuntimeAnalysisHumanApprovalRecord,
                RuntimeAnalysisHumanApprovalRecord> update,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                executionId);
            ArgumentNullException.ThrowIfNull(
                update);

            var key = CreateKey(executionId);

            for (var attempt = 0; attempt < 5; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var currentValue = await _database.StringGetAsync(
                        key)
                    .ConfigureAwait(false);

                if (currentValue.IsNullOrEmpty)
                {
                    throw new InvalidOperationException(
                        $"Approval record for execution '{executionId}' does not exist.");
                }

                var current = Deserialize(
                    currentValue!);
                var updated = update(
                    current);

                if (ReferenceEquals(
                        current,
                        updated))
                {
                    return current;
                }

                var transaction = _database.CreateTransaction();
                transaction.AddCondition(
                    Condition.StringEqual(
                        key,
                        currentValue));

                _ = transaction.StringSetAsync(
                    key,
                    JsonSerializer.Serialize(
                        updated,
                        SerializerOptions),
                    Expiration);

                if (await transaction.ExecuteAsync()
                        .ConfigureAwait(false))
                {
                    return updated;
                }
            }

            throw new InvalidOperationException(
                $"Approval record for execution '{executionId}' could not be updated after concurrent changes.");
        }

        private static RuntimeAnalysisHumanApprovalRecord Clone(
            RuntimeAnalysisHumanApprovalRecord source,
            string? initialRunId = null,
            string? status = null,
            DateTimeOffset? decidedAtUtc = null,
            string? decidedBy = null)
        {
            return new RuntimeAnalysisHumanApprovalRecord
            {
                ExecutionId = source.ExecutionId,
                StepName = source.StepName,
                ContinuationId = source.ContinuationId,
                InitialRunId = initialRunId ?? source.InitialRunId,
                Status = status ?? source.Status,
                PolicyValidation = source.PolicyValidation,
                ExecutionContextSnapshot = source.ExecutionContextSnapshot,
                RequestedAtUtc = source.RequestedAtUtc,
                DecidedAtUtc = decidedAtUtc ?? source.DecidedAtUtc,
                DecidedBy = decidedBy ?? source.DecidedBy
            };
        }

        private static RedisKey CreateKey(
            string executionId)
        {
            return $"{KeyPrefix}{executionId}";
        }

        private static RuntimeAnalysisHumanApprovalRecord Deserialize(
            RedisValue value)
        {
            return JsonSerializer.Deserialize<RuntimeAnalysisHumanApprovalRecord>(
                       value.ToString(),
                       SerializerOptions)
                   ?? throw new InvalidOperationException(
                       "Redis human approval record deserialized to null.");
        }
    }
}
