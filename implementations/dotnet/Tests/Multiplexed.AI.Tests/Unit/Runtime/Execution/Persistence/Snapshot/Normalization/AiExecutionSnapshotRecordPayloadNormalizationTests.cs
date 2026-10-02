using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Execution.Persistence.Snapshot;
using Multiplexed.AI.Runtime.Execution.Persistence.Snapshot.Normalization;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.Runtime.Execution.Persistence.Snapshot.Normalization
{
    /// <summary>
    /// Verifies persistence normalization for immutable execution-record payloads.
    /// </summary>
    public sealed class AiExecutionSnapshotRecordPayloadNormalizationTests
    {
        [Fact]
        public void Normalize_Should_Convert_Record_PipelineDefinition_JsonElement_To_String()
        {
            const string definitionJson = "{\"Name\":\"interactive-agent\",\"Version\":\"1\"}";
            const string contentHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

            var inlineElement = JsonSerializer.SerializeToElement(definitionJson);
            var payload = AiStoredPayload.Inline(
                inlineElement,
                sizeBytes: definitionJson.Length,
                contentType: "application/json",
                contentHash: contentHash);

            var snapshot = CreateSnapshot(payload);

            AiExecutionSnapshotNormalizer.Normalize(snapshot);

            Assert.NotNull(snapshot.Record.PipelineDefinitionSnapshot);
            var normalized = snapshot.Record.PipelineDefinitionSnapshot!;
            Assert.True(normalized.IsInline);
            Assert.Equal(definitionJson, Assert.IsType<string>(normalized.InlineValue));
            Assert.Equal(contentHash, normalized.ContentHash);
            Assert.Equal("application/json", normalized.ContentType);
            Assert.Equal(definitionJson.Length, normalized.SizeBytes);
        }

        [Fact]
        public void Remap_Should_Preserve_Normalized_Record_PipelineDefinition_String()
        {
            const string definitionJson = "{\"Name\":\"interactive-agent\",\"Version\":\"1\"}";

            var snapshot = CreateSnapshot(
                AiStoredPayload.Inline(
                    JsonSerializer.SerializeToElement(definitionJson),
                    contentType: "application/json"));

            AiExecutionSnapshotNormalizer.Normalize(snapshot);
            AiExecutionSnapshotRemapper.Remap(snapshot);

            Assert.NotNull(snapshot.Record.PipelineDefinitionSnapshot);
            var remapped = snapshot.Record.PipelineDefinitionSnapshot!;
            Assert.Equal(definitionJson, Assert.IsType<string>(remapped.InlineValue));
        }

        private static AiExecutionSnapshotDocument<object?> CreateSnapshot(AiStoredPayload pipelineDefinitionSnapshot)
        {
            const string executionId = "snapshot-record-payload-test";

            return new AiExecutionSnapshotDocument<object?>
            {
                ExecutionId = executionId,
                PipelineName = "interactive-agent",
                Status = AiExecutionStatus.Completed.ToString(),
                Record = new AiExecutionRecord
                {
                    ExecutionId = executionId,
                    PipelineName = "interactive-agent",
                    Status = AiExecutionStatus.Completed,
                    PipelineDefinitionSnapshot = pipelineDefinitionSnapshot
                },
                State = new AiExecutionState
                {
                    ExecutionId = executionId,
                    PipelineName = "interactive-agent"
                }
            };
        }
    }
}
