using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Results;
using System;
using System.Collections.Generic;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Assertions
{
    /// <summary>
    /// Provides reusable assertions for production runtime recovery forensics scenarios.
    /// </summary>
    public static class ProductionRecoveryForensicsAssertions
    {
        /// <summary>
        /// Asserts that the recovery forensics read model is visible through MCP with the expected complete timeline.
        /// </summary>
        public static async Task AssertRecoveryForensicsTimelineViaMcpAsync(
            McpTestClient mcp,
            string expectedForensicsId,
            string executionId,
            string sharedRunId,
            string failedRuntimeInstanceId,
            string failedLocalRunId,
            string replacementRuntimeInstanceId,
            string replacementLocalRunId,
            string tenantId,
            string tenantGroupId,
            string controlPlaneId,
            string pipelineName,
            ITestOutputHelper output,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(mcp);
            ArgumentException.ThrowIfNullOrWhiteSpace(expectedForensicsId);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(failedRuntimeInstanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(failedLocalRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(replacementRuntimeInstanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(replacementLocalRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantGroupId);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentNullException.ThrowIfNull(output);

            var expectedTimeline =
                new[]
                {
                    "execution.recovery.candidate.detected",
                    "shared.run.requeued.for.resume",
                    "failed.local.run.marked.requeued.for.recovery",
                    "replacement.runtime.selected",
                    "replacement.local.run.registered",
                    "resume.context.seeded",
                    "dag.resume.started",
                    "dag.resume.completed",
                    "execution.recovery.completed"
                };

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            AiRuntimeRecoveryForensicsReadModel? model = null;
            AiRuntimeRecoveryForensicsQueryResult? lastResult = null;
            var searchOutputWritten = false;

            while (DateTimeOffset.UtcNow < deadline)
            {
                lastResult =
                    await mcp
                        .SearchRuntimeRecoveryForensicsAsync(
                            new AiRuntimeRecoveryForensicsQuery
                            {
                                ExecutionId = executionId,
                                SharedRunId = sharedRunId,
                                TenantId = tenantId,
                                Limit = 50
                            })
                        .ConfigureAwait(false);

                model =
                    lastResult.Items
                        .Where(item =>
                            string.Equals(item.ForensicsId, expectedForensicsId, StringComparison.Ordinal) &&
                            string.Equals(item.ExecutionId, executionId, StringComparison.Ordinal) &&
                            string.Equals(item.SharedRunId, sharedRunId, StringComparison.Ordinal) &&
                            string.Equals(item.TenantId, tenantId, StringComparison.Ordinal))
                        .OrderByDescending(item => item.UpdatedAtUtc)
                        .FirstOrDefault();

                if (model is not null)
                {
                    if (!searchOutputWritten)
                    {
                        ProductionRecoveryForensicsOutputWriter.WriteSearchResult(
                            output,
                            lastResult);

                        searchOutputWritten = true;
                    }

                    if (expectedTimeline.All(expected => model.Timeline.Any(item =>
                        string.Equals(item.EventType, expected, StringComparison.Ordinal))))
                    {
                        break;
                    }
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            if (model is null)
            {
                var observed =
                    lastResult?.Items
                        .Select(item =>
                            $"ForensicsId='{item.ForensicsId}', ExecutionId='{item.ExecutionId}', SharedRunId='{item.SharedRunId}', TenantId='{item.TenantId}', ControlPlaneId='{item.ControlPlaneId}', Timeline='{string.Join(" -> ", item.Timeline.Select(t => t.EventType))}'")
                        .ToArray() ?? Array.Empty<string>();

                Assert.Fail(
                    "Recovery forensics read model was not visible through MCP tenant/execution/shared query within the timeout. " +
                    $"ExpectedForensicsId='{expectedForensicsId}', ExecutionId='{executionId}', SharedRunId='{sharedRunId}', TenantId='{tenantId}', " +
                    $"DiagnosticControlPlaneId='{controlPlaneId}', ObservedCount='{lastResult?.Items.Count ?? 0}', Observed='{string.Join(" || ", observed)}'.");
            }

            Assert.NotNull(model);
            Assert.Equal(expectedForensicsId, model!.ForensicsId);
            Assert.Equal(executionId, model.ExecutionId);
            Assert.Equal(sharedRunId, model.SharedRunId);
            Assert.Equal(tenantId, model.TenantId);

            Assert.True(
                string.IsNullOrWhiteSpace(model.ControlPlaneId) ||
                string.Equals(controlPlaneId, model.ControlPlaneId, StringComparison.Ordinal),
                $"ControlPlaneId is optional for recovery forensics, but when present it must match. Expected='{controlPlaneId}', Actual='{model.ControlPlaneId}'.");

            var fetched =
                await mcp
                    .GetRuntimeRecoveryForensicsAsync(model.ForensicsId)
                    .ConfigureAwait(false);

            Assert.NotNull(fetched);
            Assert.Equal(expectedForensicsId, fetched!.ForensicsId);
            Assert.Equal(executionId, fetched.ExecutionId);
            Assert.Equal(sharedRunId, fetched.SharedRunId);
            Assert.Equal(tenantId, fetched.TenantId);

            Assert.True(
                string.IsNullOrWhiteSpace(fetched.ControlPlaneId) ||
                string.Equals(controlPlaneId, fetched.ControlPlaneId, StringComparison.Ordinal),
                $"ControlPlaneId is optional for recovery forensics, but when present it must match. Expected='{controlPlaneId}', Actual='{fetched.ControlPlaneId}'.");

            ProductionRecoveryForensicsOutputWriter.WriteSummary(
                output,
                fetched);

            var timeline =
                await mcp
                    .GetRuntimeRecoveryForensicsTimelineAsync(model.ForensicsId)
                    .ConfigureAwait(false);

            ProductionRecoveryForensicsOutputWriter.WriteTimeline(
                output,
                timeline);

            Assert.Equal(
                expectedTimeline,
                timeline.Select(item => item.EventType).ToArray());

            Assert.Contains(
                timeline,
                item =>
                    string.Equals(item.EventType, "replacement.runtime.selected", StringComparison.Ordinal) &&
                    string.Equals(item.RuntimeInstanceId, replacementRuntimeInstanceId, StringComparison.Ordinal));

            Assert.Contains(
                timeline,
                item =>
                    string.Equals(item.EventType, "replacement.local.run.registered", StringComparison.Ordinal) &&
                    string.Equals(item.RuntimeInstanceId, replacementRuntimeInstanceId, StringComparison.Ordinal) &&
                    string.Equals(item.LocalRunId, replacementLocalRunId, StringComparison.Ordinal));

            Assert.Contains(
                timeline,
                item =>
                    string.Equals(item.EventType, "execution.recovery.completed", StringComparison.Ordinal) &&
                    string.Equals(item.RuntimeInstanceId, replacementRuntimeInstanceId, StringComparison.Ordinal) &&
                    string.Equals(item.LocalRunId, replacementLocalRunId, StringComparison.Ordinal));

            Assert.Equal(
                failedRuntimeInstanceId,
                ResolveFirstTimelineMetadataValue(timeline, "failed.runtimeInstanceId"));

            Assert.Equal(
                failedLocalRunId,
                ResolveFirstTimelineMetadataValue(timeline, "failed.localRunId"));

            Assert.Equal(
                replacementRuntimeInstanceId,
                ResolveFirstTimelineMetadataValue(timeline, "replacement.runtimeInstanceId"));

            Assert.Equal(
                replacementLocalRunId,
                ResolveFirstTimelineMetadataValue(timeline, "replacement.localRunId"));

            Assert.False(
                string.IsNullOrWhiteSpace(ResolveFirstTimelineMetadataValue(timeline, "resume.contextKey")),
                "The recovery forensics timeline must expose the restored RBAC context key.");

            ProductionRecoveryForensicsOutputWriter.WriteProof(
                output,
                fetched,
                timeline,
                tenantGroupId,
                controlPlaneId,
                pipelineName);
        }

        /// <summary>
        /// Resolves the first metadata value matching a key across an ordered forensics timeline.
        /// </summary>
        private static string? ResolveFirstTimelineMetadataValue(
            IReadOnlyList<AiRuntimeRecoveryForensicsTimelineItem> timeline,
            string key)
        {
            ArgumentNullException.ThrowIfNull(timeline);
            ArgumentException.ThrowIfNullOrWhiteSpace(key);

            foreach (var item in timeline)
            {
                if (item.Metadata is null)
                {
                    continue;
                }

                foreach (var pair in item.Metadata)
                {
                    if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                    {
                        return pair.Value;
                    }
                }
            }

            return null;
        }
    }
}