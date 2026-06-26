using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Stores runtime recovery forensics records in memory.
    /// </summary>
    public sealed class InMemoryAiRuntimeRecoveryForensicsStore : IAiRuntimeRecoveryForensicsStore
    {
        private readonly ConcurrentDictionary<string, AiRuntimeRecoveryForensicsRecord> _records = new(StringComparer.OrdinalIgnoreCase);

        /// <inheritdoc />
        public Task UpsertAsync(AiRuntimeRecoveryForensicsRecord record, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(record);

            if (string.IsNullOrWhiteSpace(record.Identity.ForensicsId))
            {
                throw new ArgumentException("ForensicsId is required.", nameof(record));
            }

            var now = DateTimeOffset.UtcNow;

            _records.AddOrUpdate(
                record.Identity.ForensicsId,
                _ => record with
                {
                    CreatedAtUtc = record.CreatedAtUtc == default ? now : record.CreatedAtUtc,
                    UpdatedAtUtc = record.UpdatedAtUtc == default ? now : record.UpdatedAtUtc
                },
                (_, existing) => record with
                {
                    CreatedAtUtc = existing.CreatedAtUtc == default ? now : existing.CreatedAtUtc,
                    UpdatedAtUtc = record.UpdatedAtUtc == default ? now : record.UpdatedAtUtc,
                    Events = MergeEvents(existing.Events, record.Events)
                });

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task AppendEventAsync(string forensicsId, AiRuntimeRecoveryForensicsEvent evt, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(forensicsId);
            ArgumentNullException.ThrowIfNull(evt);

            var now = DateTimeOffset.UtcNow;

            _records.AddOrUpdate(
                forensicsId,
                _ => new AiRuntimeRecoveryForensicsRecord
                {
                    Identity = new AiRuntimeRecoveryForensicsIdentity
                    {
                        ForensicsId = forensicsId,
                        ExecutionId = evt.ExecutionId ?? string.Empty,
                        SharedRunId = evt.SharedRunId
                    },
                    Events = [evt],
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                },
                (_, existing) => existing with
                {
                    Events = MergeEvents(existing.Events, [evt]),
                    UpdatedAtUtc = now
                });

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<AiRuntimeRecoveryForensicsRecord?> GetByForensicsIdAsync(string forensicsId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(forensicsId);

            _records.TryGetValue(forensicsId, out var record);

            return Task.FromResult(record);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>> ListByExecutionIdAsync(string executionId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            var records = _records.Values
                .Where(x => string.Equals(x.Identity.ExecutionId, executionId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.CreatedAtUtc)
                .ToList();

            return Task.FromResult<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>>(records);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>> ListBySharedRunIdAsync(string sharedRunId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);

            var records = _records.Values
                .Where(x => string.Equals(x.Identity.SharedRunId, sharedRunId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.CreatedAtUtc)
                .ToList();

            return Task.FromResult<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>>(records);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>> ListByRuntimeInstanceIdAsync(string runtimeInstanceId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            var records = _records.Values
                .Where(x =>
                    string.Equals(x.Failure?.FailedRuntimeInstanceId, runtimeInstanceId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.Replacement?.ReplacementRuntimeInstanceId, runtimeInstanceId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.CreatedAtUtc)
                .ToList();

            return Task.FromResult<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>>(records);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>> ListByRuntimeFailureIncidentIdAsync(string runtimeFailureIncidentId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeFailureIncidentId);

            var records = _records.Values
                .Where(x => string.Equals(x.Failure?.RuntimeFailureIncidentId, runtimeFailureIncidentId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.CreatedAtUtc)
                .ToList();

            return Task.FromResult<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>>(records);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>> ListRecentAsync(int limit, CancellationToken cancellationToken = default)
        {
            var safeLimit = Math.Max(1, limit);

            var records = _records.Values
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(safeLimit)
                .ToList();

            return Task.FromResult<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>>(records);
        }

        private static IReadOnlyList<AiRuntimeRecoveryForensicsEvent> MergeEvents(
            IReadOnlyList<AiRuntimeRecoveryForensicsEvent> existing,
            IReadOnlyList<AiRuntimeRecoveryForensicsEvent> incoming)
        {
            return existing
                .Concat(incoming)
                .GroupBy(x => x.EventId, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .OrderBy(x => x.TimestampUtc)
                .ToList();
        }
    }
}