using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Execution;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.SharedTests
{
    /// <summary>
    /// Verifies the opt-in child DAG extension of the shared production-test pipeline factory.
    /// </summary>
    public sealed class ProductionChildDagPipelineFactoryTests
    {
        /// <summary>
        /// Verifies that the historical default depth keeps the exact pipeline shape without a child call-site.
        /// </summary>
        [Fact]
        public void CreatePipelineDefinition_Should_Preserve_Historical_Shape_When_ChildDepth_Is_Zero()
        {
            const int stepCount = 5;

            var historicalDefault = McpTestPipelineFactory.CreatePipelineDefinition(
                "production-child-depth-zero",
                stepCount);
            var explicitZero = McpTestPipelineFactory.CreatePipelineDefinition(
                "production-child-depth-zero",
                stepCount,
                childDepth: 0);

            Assert.Equal(
                System.Text.Json.JsonSerializer.Serialize(historicalDefault),
                System.Text.Json.JsonSerializer.Serialize(explicitZero));
            Assert.Equal(stepCount, historicalDefault.Steps.Count);
            Assert.DoesNotContain(
                historicalDefault.Steps,
                step => string.Equals(step.StepKey, ExecuteChildDagStep.StepKey, StringComparison.Ordinal));
        }

        /// <summary>
        /// Verifies that depth one adds exactly one child call-site whose embedded definition has no nested child.
        /// </summary>
        [Fact]
        public void CreatePipelineDefinition_Should_Add_One_Child_When_Depth_Is_One()
        {
            const int stepCount = 5;
            const string pipelineName = "production-child-depth-one";

            var definition = McpTestPipelineFactory.CreatePipelineDefinition(
                pipelineName,
                stepCount,
                childDepth: 1);

            var childStep = Assert.Single(
                definition.Steps.Where(step =>
                    string.Equals(step.StepKey, ExecuteChildDagStep.StepKey, StringComparison.Ordinal)));

            Assert.Equal(stepCount + 1, definition.Steps.Count);
            Assert.Equal(McpTestPipelineFactory.ChildDagStepName, childStep.Name);
            Assert.Equal(stepCount, childStep.DependsOn.Count);

            var childDefinition = Assert.IsType<Multiplexed.Abstractions.AI.Pipeline.AiPipelineDefinition>(
                childStep.Config[ExecuteChildDagStep.ChildDagDefinitionConfigKey]);

            Assert.Equal(
                McpTestPipelineFactory.CreateChildPipelineName(pipelineName, 1),
                childDefinition.Name);
            Assert.Equal(McpTestPipelineFactory.PipelineVersion, childDefinition.Version);
            Assert.Equal(stepCount, childDefinition.Steps.Count);
            Assert.DoesNotContain(
                childDefinition.Steps,
                step => string.Equals(step.StepKey, ExecuteChildDagStep.StepKey, StringComparison.Ordinal));
        }

        /// <summary>
        /// Verifies that depth two recursively embeds one child call-site at each requested nesting level.
        /// </summary>
        [Fact]
        public void CreatePipelineDefinition_Should_Embed_One_Child_Per_Requested_Depth()
        {
            const int stepCount = 3;
            const string pipelineName = "production-child-depth-two";

            var parent = McpTestPipelineFactory.CreatePipelineDefinition(
                pipelineName,
                stepCount,
                childDepth: 2);

            var levelOneStep = Assert.Single(
                parent.Steps.Where(step =>
                    string.Equals(step.StepKey, ExecuteChildDagStep.StepKey, StringComparison.Ordinal)));
            var levelOne = Assert.IsType<Multiplexed.Abstractions.AI.Pipeline.AiPipelineDefinition>(
                levelOneStep.Config[ExecuteChildDagStep.ChildDagDefinitionConfigKey]);

            var levelTwoStep = Assert.Single(
                levelOne.Steps.Where(step =>
                    string.Equals(step.StepKey, ExecuteChildDagStep.StepKey, StringComparison.Ordinal)));
            var levelTwo = Assert.IsType<Multiplexed.Abstractions.AI.Pipeline.AiPipelineDefinition>(
                levelTwoStep.Config[ExecuteChildDagStep.ChildDagDefinitionConfigKey]);

            Assert.DoesNotContain(
                levelTwo.Steps,
                step => string.Equals(step.StepKey, ExecuteChildDagStep.StepKey, StringComparison.Ordinal));
        }
    }
}
