using System.Text.Json;
using StackExchange.Redis;
using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Runtime
{
    public sealed class RedisRuntimeAnalysisScenarioExecutionStore :
        IRuntimeAnalysisScenarioExecutionStore
    {
        private const string KeyPrefix =
            "ai-demo:runtime-analysis:scenario-execution:v1:";

        private static readonly TimeSpan Expiration =
            TimeSpan.FromHours(24);

        private static readonly JsonSerializerOptions SerializerOptions =
            new()
            {
                PropertyNameCaseInsensitive = true
            };

        private readonly IDatabase _database;

        public RedisRuntimeAnalysisScenarioExecutionStore(
            IConnectionMultiplexer connectionMultiplexer)
        {
            ArgumentNullException.ThrowIfNull(
                connectionMultiplexer);

            _database = connectionMultiplexer.GetDatabase();
        }

        public async Task<RuntimeAnalysisScenarioExecutionRecord?> GetAsync(
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

        public async Task<RuntimeAnalysisScenarioExecutionRecord>
            CreatePendingAsync(
                RuntimeAnalysisScenarioExecutionRecord record,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(
                record);

            cancellationToken.ThrowIfCancellationRequested();

            var key = CreateKey(
                record.ExecutionId);

            var created = await _database.StringSetAsync(
                    key,
                    JsonSerializer.Serialize(
                        record,
                        SerializerOptions),
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
                    $"Scenario execution record '{record.ExecutionId}' disappeared after Redis create collision.");

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
                    $"Scenario execution record collision for execution '{record.ExecutionId}'.");
            }

            return existing;
        }

        public Task<RuntimeAnalysisScenarioExecutionRecord> CompleteAsync(
            string executionId,
            RuntimeAnalysisScenarioExecutionObservation observation,
            string completedBy,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(
                observation);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                completedBy);

            ValidateObservation(
                observation);

            return UpdateAsync(
                executionId,
                current =>
                {
                    if (string.Equals(
                            current.Status,
                            RuntimeAnalysisScenarioExecutionStatuses.Completed,
                            StringComparison.Ordinal))
                    {
                        return current;
                    }

                    if (!string.Equals(
                            current.Status,
                            RuntimeAnalysisScenarioExecutionStatuses.Pending,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Scenario execution '{executionId}' is already '{current.Status}' and cannot be completed.");
                    }

                    return CloneCompleted(
                        current,
                        observation,
                        completedBy);
                },
                cancellationToken);
        }

        private async Task<RuntimeAnalysisScenarioExecutionRecord> UpdateAsync(
            string executionId,
            Func<RuntimeAnalysisScenarioExecutionRecord,
                RuntimeAnalysisScenarioExecutionRecord> update,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                executionId);
            ArgumentNullException.ThrowIfNull(
                update);

            var key = CreateKey(
                executionId);

            for (var attempt = 0; attempt < 5; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var currentValue = await _database.StringGetAsync(
                        key)
                    .ConfigureAwait(false);

                if (currentValue.IsNullOrEmpty)
                {
                    throw new InvalidOperationException(
                        $"Scenario execution record for execution '{executionId}' does not exist.");
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
                $"Scenario execution record for execution '{executionId}' could not be updated after concurrent changes.");
        }

        private static RuntimeAnalysisScenarioExecutionRecord CloneCompleted(
            RuntimeAnalysisScenarioExecutionRecord source,
            RuntimeAnalysisScenarioExecutionObservation observation,
            string completedBy)
        {
            return new RuntimeAnalysisScenarioExecutionRecord
            {
                ExecutionId = source.ExecutionId,
                StepName = source.StepName,
                ContinuationId = source.ContinuationId,
                InitialRunId = source.InitialRunId,
                Status = RuntimeAnalysisScenarioExecutionStatuses.Completed,
                Scenario = source.Scenario,
                PlanKey = source.PlanKey,
                ExecutionContextSnapshot = source.ExecutionContextSnapshot,
                RequestedAtUtc = source.RequestedAtUtc,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Observation = observation,
                CompletedBy = completedBy,
                Error = observation.Error
            };
        }

        private static void ValidateObservation(
            RuntimeAnalysisScenarioExecutionObservation observation)
        {
            if (observation.StartedAtUtc == default)
            {
                throw new ArgumentException(
                    "Scenario execution StartedAtUtc is required.");
            }

            if (observation.FinishedAtUtc == default
                || observation.FinishedAtUtc < observation.StartedAtUtc)
            {
                throw new ArgumentException(
                    "Scenario execution FinishedAtUtc must be on or after StartedAtUtc.");
            }

            EnsureNonNegative(
                observation.Completed,
                nameof(observation.Completed));
            EnsureNonNegative(
                observation.InFlight,
                nameof(observation.InFlight));
            EnsureNonNegative(
                observation.Ok,
                nameof(observation.Ok));
            EnsureNonNegative(
                observation.Unauthorized,
                nameof(observation.Unauthorized));
            EnsureNonNegative(
                observation.Forbidden,
                nameof(observation.Forbidden));
            EnsureNonNegative(
                observation.TooManyRequests,
                nameof(observation.TooManyRequests));
            EnsureNonNegative(
                observation.OtherHttp,
                nameof(observation.OtherHttp));
            EnsureNonNegative(
                observation.Errors,
                nameof(observation.Errors));

            var outcomes =
                observation.Ok
                + observation.Unauthorized
                + observation.Forbidden
                + observation.TooManyRequests
                + observation.OtherHttp
                + observation.Errors;

            if (outcomes > observation.Completed)
            {
                throw new ArgumentException(
                    "Scenario execution outcome count cannot exceed Completed.");
            }
        }

        private static void EnsureNonNegative(
            int value,
            string name)
        {
            if (value < 0)
            {
                throw new ArgumentException(
                    $"{name} cannot be negative.");
            }
        }

        private static RedisKey CreateKey(
            string executionId)
        {
            return $"{KeyPrefix}{executionId}";
        }

        private static RuntimeAnalysisScenarioExecutionRecord Deserialize(
            RedisValue value)
        {
            return JsonSerializer.Deserialize<RuntimeAnalysisScenarioExecutionRecord>(
                       value.ToString(),
                       SerializerOptions)
                   ?? throw new InvalidOperationException(
                       "Redis scenario execution record deserialized to null.");
        }
    }
}
