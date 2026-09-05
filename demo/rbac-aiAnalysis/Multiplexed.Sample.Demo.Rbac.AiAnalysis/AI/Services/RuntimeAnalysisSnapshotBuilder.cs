using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Services
{
    public sealed class RuntimeAnalysisSnapshotBuilder : IRuntimeAnalysisSnapshotBuilder
    {
        public RuntimeAnalysisSnapshot Build(
            RuntimeAnalysisSnapshotRequest request)
        {
            ArgumentNullException.ThrowIfNull(
                request);

            ValidateRequest(
                request);

            var evidenceReceivedCount = request.Evidence?.Count ?? 0;
            var evidence = (request.Evidence ?? Array.Empty<RuntimeAnalysisEvidenceInput>())
                .Take(RuntimeAnalysisSnapshotLimits.MaxEvidenceItems)
                .Select(NormalizeEvidence)
                .ToArray();

            return new RuntimeAnalysisSnapshot
            {
                Scope = request.Scope.Trim(),
                CapturedAtUtc = request.CapturedAtUtc,
                Scenario = BuildScenario(
                    request.Scenario),
                Metrics = BuildMetrics(
                    request.Metrics),
                Evidence = evidence,
                EvidenceSummary = BuildEvidenceSummary(
                    evidence),
                EvidenceReceivedCount = evidenceReceivedCount,
                EvidenceTruncated =
                    evidenceReceivedCount > RuntimeAnalysisSnapshotLimits.MaxEvidenceItems
            };
        }

        private static void ValidateRequest(
            RuntimeAnalysisSnapshotRequest request)
        {
            if (!RuntimeAnalysisScopes.IsSupported(request.Scope))
            {
                throw new ArgumentException(
                    $"Unsupported runtime analysis scope '{request.Scope}'.",
                    nameof(request));
            }

            if (request.CapturedAtUtc == default)
            {
                throw new ArgumentException(
                    "CapturedAtUtc is required.",
                    nameof(request));
            }

            ValidateMetrics(
                request.Metrics);

            ValidateScenario(
                request.Scenario);
        }

        private static void ValidateMetrics(
            RuntimeAnalysisMetricsInput metrics)
        {
            ArgumentNullException.ThrowIfNull(
                metrics);

            EnsureNonNegative(
                metrics.Completed,
                nameof(metrics.Completed));
            EnsureNonNegative(
                metrics.InFlight,
                nameof(metrics.InFlight));
            EnsureNonNegative(
                metrics.Ok,
                nameof(metrics.Ok));
            EnsureNonNegative(
                metrics.Unauthorized,
                nameof(metrics.Unauthorized));
            EnsureNonNegative(
                metrics.Forbidden,
                nameof(metrics.Forbidden));
            EnsureNonNegative(
                metrics.TooManyRequests,
                nameof(metrics.TooManyRequests));
            EnsureNonNegative(
                metrics.OtherHttp,
                nameof(metrics.OtherHttp));
            EnsureNonNegative(
                metrics.Errors,
                nameof(metrics.Errors));
            EnsureNonNegative(
                metrics.LiveLogCount,
                nameof(metrics.LiveLogCount));

            EnsureNonNegative(
                metrics.P50Ms,
                nameof(metrics.P50Ms));
            EnsureNonNegative(
                metrics.P95Ms,
                nameof(metrics.P95Ms));
            EnsureNonNegative(
                metrics.ElapsedMs,
                nameof(metrics.ElapsedMs));

            if (metrics.P50Ms.HasValue
                && metrics.P95Ms.HasValue
                && metrics.P95Ms.Value < metrics.P50Ms.Value)
            {
                throw new ArgumentException(
                    "P95Ms cannot be lower than P50Ms.",
                    nameof(metrics));
            }

            var observedOutcomeCount =
                metrics.Ok
                + metrics.Unauthorized
                + metrics.Forbidden
                + metrics.TooManyRequests
                + metrics.OtherHttp
                + metrics.Errors;

            if (observedOutcomeCount > metrics.Completed)
            {
                throw new ArgumentException(
                    "The sum of observed outcomes cannot exceed Completed.",
                    nameof(metrics));
            }
        }

        private static void ValidateScenario(
            RuntimeAnalysisScenarioInput? scenario)
        {
            if (scenario == null)
            {
                return;
            }

            EnsureNonNegative(
                scenario.TotalRequests,
                nameof(scenario.TotalRequests));
            EnsureNonNegative(
                scenario.Concurrency,
                nameof(scenario.Concurrency));
            EnsureNonNegative(
                scenario.BatchSize,
                nameof(scenario.BatchSize));
            EnsureNonNegative(
                scenario.DelayMs,
                nameof(scenario.DelayMs));
            EnsureNonNegative(
                scenario.WavePauseMs,
                nameof(scenario.WavePauseMs));
            EnsureNonNegative(
                scenario.MaxInFlight,
                nameof(scenario.MaxInFlight));
            EnsureNonNegative(
                scenario.RotationOverlapMs,
                nameof(scenario.RotationOverlapMs));
        }

        private static RuntimeAnalysisScenarioSnapshot? BuildScenario(
            RuntimeAnalysisScenarioInput? scenario)
        {
            if (scenario == null)
            {
                return null;
            }

            return new RuntimeAnalysisScenarioSnapshot
            {
                Name = NormalizeOptionalIdentifier(
                    scenario.Name),
                DispatchMode = NormalizeOptionalIdentifier(
                    scenario.DispatchMode),
                PlanKey = NormalizeOptionalIdentifier(
                    scenario.PlanKey),
                TotalRequests = scenario.TotalRequests,
                Concurrency = scenario.Concurrency,
                BatchSize = scenario.BatchSize,
                DelayMs = scenario.DelayMs,
                WavePauseMs = scenario.WavePauseMs,
                MaxInFlight = scenario.MaxInFlight,
                RotationOverlapMs = scenario.RotationOverlapMs
            };
        }

        private static RuntimeAnalysisMetricsSnapshot BuildMetrics(
            RuntimeAnalysisMetricsInput metrics)
        {
            return new RuntimeAnalysisMetricsSnapshot
            {
                Completed = metrics.Completed,
                InFlight = metrics.InFlight,
                Ok = metrics.Ok,
                Unauthorized = metrics.Unauthorized,
                Forbidden = metrics.Forbidden,
                TooManyRequests = metrics.TooManyRequests,
                OtherHttp = metrics.OtherHttp,
                Errors = metrics.Errors,
                P50Ms = metrics.P50Ms,
                P95Ms = metrics.P95Ms,
                ElapsedMs = metrics.ElapsedMs,
                LiveLogCount = metrics.LiveLogCount
            };
        }

        private static RuntimeAnalysisEvidence NormalizeEvidence(
            RuntimeAnalysisEvidenceInput input)
        {
            return new RuntimeAnalysisEvidence
            {
                TimestampUtc = input.TimestampUtc,
                Category = NormalizeRequired(
                    input.Category,
                    RuntimeAnalysisSnapshotLimits.MaxCategoryLength,
                    "unknown"),
                EventType = NormalizeRequired(
                    input.EventType,
                    RuntimeAnalysisSnapshotLimits.MaxEventTypeLength,
                    "unknown"),
                Message = NormalizeOptional(
                    input.Message,
                    RuntimeAnalysisSnapshotLimits.MaxMessageLength),
                StatusCode = input.StatusCode,
                DurationMs = NormalizeDuration(
                    input.DurationMs),
                CorrelationId = NormalizeOptionalIdentifier(
                    input.CorrelationId),
                SharedRunId = NormalizeOptionalIdentifier(
                    input.SharedRunId),
                ExecutionId = NormalizeOptionalIdentifier(
                    input.ExecutionId),
                DagId = NormalizeOptionalIdentifier(
                    input.DagId),
                StepId = NormalizeOptionalIdentifier(
                    input.StepId),
                ChildExecutionId = NormalizeOptionalIdentifier(
                    input.ChildExecutionId),
                PolicyKey = NormalizeOptionalIdentifier(
                    input.PolicyKey),
                Metadata = NormalizeMetadata(
                    input.Metadata)
            };
        }

        private static RuntimeAnalysisEvidenceSummary BuildEvidenceSummary(
            IReadOnlyList<RuntimeAnalysisEvidence> evidence)
        {
            var byCategory = evidence
                .GroupBy(
                    item => item.Category,
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    group => group.Key,
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Sum(EvidenceWeight),
                    StringComparer.OrdinalIgnoreCase);

            var byEventType = evidence
                .GroupBy(
                    item => item.EventType,
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    group => group.Key,
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Sum(EvidenceWeight),
                    StringComparer.OrdinalIgnoreCase);

            return new RuntimeAnalysisEvidenceSummary
            {
                ByCategory = byCategory,
                ByEventType = byEventType,
                HttpErrorCount = evidence
                    .Where(
                        item => item.StatusCode >= 400)
                    .Sum(EvidenceWeight),
                DagRelatedCount = evidence
                    .Where(
                        IsDagRelated)
                    .Sum(EvidenceWeight),
                PolicyRelatedCount = evidence
                    .Where(
                        IsPolicyRelated)
                    .Sum(EvidenceWeight),
                RecoveryRelatedCount = evidence
                    .Where(
                        IsRecoveryRelated)
                    .Sum(EvidenceWeight)
            };
        }

        private static int EvidenceWeight(
            RuntimeAnalysisEvidence evidence)
        {
            if (!evidence.Metadata.TryGetValue(
                    "occurrences",
                    out var occurrenceValue)
                || string.IsNullOrWhiteSpace(
                    occurrenceValue)
                || !int.TryParse(
                    occurrenceValue,
                    out var occurrences)
                || occurrences < 1)
            {
                return 1;
            }

            return occurrences;
        }

        private static bool IsDagRelated(
            RuntimeAnalysisEvidence evidence)
        {
            return !string.IsNullOrWhiteSpace(evidence.DagId)
                || !string.IsNullOrWhiteSpace(evidence.StepId)
                || !string.IsNullOrWhiteSpace(evidence.ChildExecutionId)
                || ContainsToken(
                    evidence.Category,
                    "dag")
                || ContainsToken(
                    evidence.EventType,
                    "dag")
                || ContainsToken(
                    evidence.EventType,
                    "step");
        }

        private static bool IsPolicyRelated(
            RuntimeAnalysisEvidence evidence)
        {
            return !string.IsNullOrWhiteSpace(evidence.PolicyKey)
                || ContainsToken(
                    evidence.Category,
                    "policy")
                || ContainsToken(
                    evidence.EventType,
                    "policy");
        }

        private static bool IsRecoveryRelated(
            RuntimeAnalysisEvidence evidence)
        {
            return ContainsToken(
                    evidence.Category,
                    "recovery")
                || ContainsToken(
                    evidence.EventType,
                    "recovery")
                || ContainsToken(
                    evidence.EventType,
                    "replay");
        }

        private static bool ContainsToken(
            string value,
            string token)
        {
            return value.Contains(
                token,
                StringComparison.OrdinalIgnoreCase);
        }

        private static IReadOnlyDictionary<string, string?> NormalizeMetadata(
            IReadOnlyDictionary<string, string?>? metadata)
        {
            if (metadata == null
                || metadata.Count == 0)
            {
                return new Dictionary<string, string?>();
            }

            return metadata
                .Where(
                    pair => !string.IsNullOrWhiteSpace(pair.Key))
                .Take(RuntimeAnalysisSnapshotLimits.MaxMetadataEntries)
                .ToDictionary(
                    pair => NormalizeRequired(
                        pair.Key,
                        RuntimeAnalysisSnapshotLimits.MaxMetadataKeyLength,
                        "metadata"),
                    pair => NormalizeOptional(
                        pair.Value,
                        RuntimeAnalysisSnapshotLimits.MaxMetadataValueLength),
                    StringComparer.OrdinalIgnoreCase);
        }

        private static string NormalizeRequired(
            string? value,
            int maxLength,
            string fallback)
        {
            var normalized = value?.Trim();

            if (string.IsNullOrWhiteSpace(normalized))
            {
                return fallback;
            }

            return Truncate(
                normalized,
                maxLength);
        }

        private static string? NormalizeOptionalIdentifier(
            string? value)
        {
            return NormalizeOptional(
                value,
                RuntimeAnalysisSnapshotLimits.MaxIdentifierLength);
        }

        private static string? NormalizeOptional(
            string? value,
            int maxLength)
        {
            var normalized = value?.Trim();

            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            return Truncate(
                normalized,
                maxLength);
        }

        private static string Truncate(
            string value,
            int maxLength)
        {
            return value.Length <= maxLength
                ? value
                : value[..maxLength];
        }

        private static double? NormalizeDuration(
            double? durationMs)
        {
            EnsureNonNegative(
                durationMs,
                nameof(durationMs));

            return durationMs;
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

        private static void EnsureNonNegative(
            int? value,
            string name)
        {
            if (value.HasValue
                && value.Value < 0)
            {
                throw new ArgumentException(
                    $"{name} cannot be negative.");
            }
        }

        private static void EnsureNonNegative(
            double? value,
            string name)
        {
            if (value.HasValue
                && value.Value < 0)
            {
                throw new ArgumentException(
                    $"{name} cannot be negative.");
            }
        }
    }
}
