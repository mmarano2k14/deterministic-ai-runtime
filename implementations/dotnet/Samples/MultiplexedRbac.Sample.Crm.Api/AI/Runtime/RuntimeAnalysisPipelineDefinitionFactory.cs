using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Pipeline;
using MultiplexedRbac.Sample.Crm.Api.AI.Models;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Runtime
{
    public sealed class RuntimeAnalysisPipelineDefinitionFactory
    {
        public const string PipelineName = "runtime-analysis";
        public const string AnalyzeStepName = "analyze-runtime-with-openai";

        public AiPipelineDefinition Create(
            RuntimeAnalysisProviderRequest request)
        {
            ArgumentNullException.ThrowIfNull(
                request);

            var requestJson = JsonSerializer.Serialize(
                request);

            return new AiPipelineDefinition
            {
                Name = PipelineName,
                Version = "1",
                ExecutionMode = AiExecutionMode.Dag,
                Steps =
                [
                    new AiPipelineStepDefinition
                    {
                        Name = AnalyzeStepName,
                        StepKey = RuntimeAnalysisStepKeys.AnalyzeWithOpenAi,
                        Order = 1,
                        Config = new Dictionary<string, object?>(
                            StringComparer.Ordinal)
                        {
                            [RuntimeAnalysisStepConfigKeys.ProviderRequestJson] =
                                requestJson
                        },
                        Execution = new AiPipelineStepExecutionDefinition
                        {
                            MaxRetries = 0,
                            RetryDelayMs = 0
                        }
                    }
                ]
            };
        }
    }
}
