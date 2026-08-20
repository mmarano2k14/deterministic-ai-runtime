using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;

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
                    Identity = MergeIdentity(existing.Identity, record.Identity),
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
            var eventWithForensicsId = string.Equals(evt.ForensicsId, forensicsId, StringComparison.OrdinalIgnoreCase)
                ? evt
                : evt with { ForensicsId = forensicsId };

            _records.AddOrUpdate(
                forensicsId,
                _ => new AiRuntimeRecoveryForensicsRecord
                {
                    Identity = CreateIdentityFromEvent(forensicsId, eventWithForensicsId),
                    Events = [eventWithForensicsId],
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                },
                (_, existing) => existing with
                {
                    Identity = MergeIdentity(existing.Identity, CreateIdentityFromEvent(forensicsId, eventWithForensicsId)),
                    Events = MergeEvents(existing.Events, [eventWithForensicsId]),
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

        private static AiRuntimeRecoveryForensicsIdentity CreateIdentityFromEvent(
            string forensicsId,
            AiRuntimeRecoveryForensicsEvent evt)
        {
            return new AiRuntimeRecoveryForensicsIdentity
            {
                ForensicsId = forensicsId,
                ExecutionId = evt.ExecutionId ?? string.Empty,
                SharedRunId = evt.SharedRunId,
                PipelineName = ResolveMetadataValue(evt.Metadata, AiPipelineMetadataKeys.CamelCasePipelineName, AiPipelineMetadataKeys.Name, AiPipelineMetadataKeys.CamelCasePipelineKey, AiPipelineMetadataKeys.Key),
                TenantId = ResolveMetadataValue(evt.Metadata, AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantId, AiRuntimeInstanceIsolationMetadataKeys.TenantId),
                TenantGroupId = ResolveMetadataValue(evt.Metadata, AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantGroupId, AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId),
                ControlPlaneId = ResolveMetadataValue(evt.Metadata, AiControlPlaneMetadataKeys.ControlPlaneId, AiControlPlaneMetadataKeys.LegacyDottedControlPlaneId)
            };
        }

        private static AiRuntimeRecoveryForensicsIdentity MergeIdentity(
            AiRuntimeRecoveryForensicsIdentity existing,
            AiRuntimeRecoveryForensicsIdentity incoming)
        {
            return new AiRuntimeRecoveryForensicsIdentity
            {
                Id = FirstNonEmpty(existing.Id, incoming.Id),
                ForensicsId = FirstNonEmpty(existing.ForensicsId, incoming.ForensicsId) ?? string.Empty,
                ExecutionId = FirstNonEmpty(existing.ExecutionId, incoming.ExecutionId) ?? string.Empty,
                SharedRunId = FirstNonEmpty(existing.SharedRunId, incoming.SharedRunId),
                PipelineName = FirstNonEmpty(existing.PipelineName, incoming.PipelineName),
                TenantId = FirstNonEmpty(existing.TenantId, incoming.TenantId),
                TenantGroupId = FirstNonEmpty(existing.TenantGroupId, incoming.TenantGroupId),
                ControlPlaneId = FirstNonEmpty(existing.ControlPlaneId, incoming.ControlPlaneId)
            };
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

        private static string? ResolveMetadataValue(
            IReadOnlyDictionary<string, string>? metadata,
            params string[] keys)
        {
            if (metadata is null || metadata.Count == 0)
            {
                return null;
            }

            foreach (var key in keys)
            {
                if (metadata.TryGetValue(key, out var value) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            foreach (var key in keys)
            {
                var match = metadata.FirstOrDefault(pair =>
                    string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(match.Value))
                {
                    return match.Value;
                }
            }

            return null;
        }

        private static string? FirstNonEmpty(
            string? first,
            string? second)
        {
            return !string.IsNullOrWhiteSpace(first)
                ? first
                : second;
        }
    }
}