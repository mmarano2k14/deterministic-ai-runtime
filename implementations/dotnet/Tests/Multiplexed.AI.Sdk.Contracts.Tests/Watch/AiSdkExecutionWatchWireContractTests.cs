using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Multiplexed.AI.Sdk.Contracts.Executions;
using Multiplexed.AI.Sdk.Contracts.Observation;
using Multiplexed.AI.Sdk.Contracts.Watch;

namespace Multiplexed.AI.Sdk.Contracts.Tests.Watch
{
    public sealed class AiSdkExecutionWatchWireContractTests
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            PropertyNameCaseInsensitive = false,
            WriteIndented = false
        };

        [Fact]
        public void Watch_Request_Uses_Public_Sequence_Channels_And_Snapshot_Preference()
        {
            var request = new AiSdkExecutionWatchRequest
            {
                ExecutionId = "exec-1",
                Channels =
                [
                    AiSdkExecutionWatchChannel.Lifecycle,
                    AiSdkExecutionWatchChannel.Steps,
                    AiSdkExecutionWatchChannel.Recovery
                ],
                AfterSequence = 42,
                IncludeInitialSnapshot = false
            };

            var json = JsonSerializer.Serialize(request, JsonOptions);
            var restored = JsonSerializer.Deserialize<AiSdkExecutionWatchRequest>(json, JsonOptions)!;

            Assert.Contains("\"executionId\":\"exec-1\"", json, StringComparison.Ordinal);
            Assert.Contains("\"afterSequence\":42", json, StringComparison.Ordinal);
            Assert.Contains("\"Lifecycle\"", json, StringComparison.Ordinal);
            Assert.False(restored.IncludeInitialSnapshot);
            Assert.Equal(42, restored.AfterSequence);
            Assert.Equal(request.Channels, restored.Channels);
            Assert.DoesNotContain("tenant", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("lease", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("epoch", json, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Watch_Event_Roundtrip_Preserves_Snapshot_Event_And_Resync_Shapes()
        {
            using var payloadDocument = JsonDocument.Parse("{\"status\":\"Running\"}");
            var values = new[]
            {
                new AiSdkExecutionWatchEvent
                {
                    ExecutionId = "exec-1",
                    Sequence = 10,
                    Kind = AiSdkExecutionWatchEventKind.Snapshot,
                    OccurredAtUtc = DateTimeOffset.Parse("2026-09-17T00:00:01+00:00"),
                    Snapshot = new AiSdkExecutionObservation
                    {
                        ExecutionId = "exec-1",
                        PublicationRef = "pub-1",
                        PipelineName = "demo",
                        PipelineVersion = "v1",
                        Status = AiSdkExecutionStatus.Running,
                        CreatedAtUtc = DateTimeOffset.Parse("2026-09-17T00:00:00+00:00"),
                        UpdatedAtUtc = DateTimeOffset.Parse("2026-09-17T00:00:01+00:00")
                    }
                },
                new AiSdkExecutionWatchEvent
                {
                    ExecutionId = "exec-1",
                    Sequence = 11,
                    Kind = AiSdkExecutionWatchEventKind.Event,
                    OccurredAtUtc = DateTimeOffset.Parse("2026-09-17T00:00:02+00:00"),
                    Channel = AiSdkExecutionWatchChannel.Lifecycle,
                    EventType = "execution.running",
                    PayloadSchemaVersion = 1,
                    Payload = payloadDocument.RootElement.Clone()
                },
                new AiSdkExecutionWatchEvent
                {
                    ExecutionId = "exec-1",
                    Kind = AiSdkExecutionWatchEventKind.ResyncRequired,
                    OccurredAtUtc = DateTimeOffset.Parse("2026-09-17T00:00:03+00:00"),
                    ResyncRequired = new AiSdkExecutionWatchResyncRequired
                    {
                        Reason = AiSdkExecutionWatchResyncReason.HistoryUnavailable,
                        RequestedAfterSequence = 1,
                        EarliestAvailableSequence = 5,
                        LatestSequence = 12,
                        Message = "History expired."
                    }
                }
            };

            foreach (var value in values)
            {
                var json = JsonSerializer.SerializeToElement(value, JsonOptions);
                var restored = json.Deserialize<AiSdkExecutionWatchEvent>(JsonOptions)!;
                var roundtrip = JsonSerializer.SerializeToElement(restored, JsonOptions);
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json.GetRawText()), JsonNode.Parse(roundtrip.GetRawText())));
            }
        }
    }
}
