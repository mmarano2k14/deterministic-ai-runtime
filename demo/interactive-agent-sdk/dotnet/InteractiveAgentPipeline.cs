using System.Text.Json;
using Multiplexed.AI.Sdk.Contracts.Executions;
using Multiplexed.AI.Sdk.Contracts.Pipelines;
using Multiplexed.AI.Sdk.Contracts.Publication;

namespace Multiplexed.AI.Demo.InteractiveAgent.DotNet;

internal static class InteractiveAgentPipeline
{
    internal const string WaitingKey = "interactive-agent-review";
    internal const string WaitingStepName = "await-review";

    private const string RootPipelineName = "interactive-agent-sdk-dotnet";
    private const string RootPipelineVersion = "1";
    private const string ChildPipelineName = "interactive-agent-sdk-dotnet-analysis";
    private const string ChildPipelineVersion = "1";

    internal static AiSdkPipelinePublicationRequest CreatePublication(
        string openAiModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(openAiModel);

        return new AiSdkPipelinePublicationRequest
        {
            Definition = new AiSdkPipelineDefinition
            {
                Name = RootPipelineName,
                Version = RootPipelineVersion,
                ExecutionMode = AiSdkExecutionMode.Dag,
                Steps = new AiSdkPipelineStepDefinition[]
                {
                    PromptStep(
                        name: "plan",
                        order: 0,
                        dependsOn: Array.Empty<string>(),
                        model: openAiModel,
                        template:
                            """
                            You are the root planning agent in a deterministic AI runtime.

                            The public execution request is supplied as JSON:
                            {{requestJson}}

                            Read the "userPrompt" field from that JSON.

                            Produce a concise planning note containing:
                            1. the user's actual objective,
                            2. a short plan,
                            3. one bounded analysis task to delegate to a child agent,
                            4. important assumptions or risks.

                            Do not answer the user's request yet.
                            """,
                        inputs: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                        {
                            ["requestJson"] = JsonSerializer.SerializeToElement("state.input")
                        }),

                    new AiSdkPipelineStepDefinition
                    {
                        Name = "delegate-analysis",
                        StepKey = "execution.child-dag",
                        Order = 1,
                        DependsOn = new[] { "plan" },
                        Input = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                        {
                            ["requestJson"] = JsonSerializer.SerializeToElement("state.input"),
                            ["rootPlan"] = JsonSerializer.SerializeToElement(
                                "steps.plan.result.data.value")
                        },
                        Config = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                        {
                            ["childDagId"] = JsonSerializer.SerializeToElement(ChildPipelineName),
                            ["childDagVersion"] = JsonSerializer.SerializeToElement(ChildPipelineVersion),
                            ["logicalInvocationKey"] = JsonSerializer.SerializeToElement(
                                "interactive-agent-analysis"),
                            ["childDagDefinition"] = CreateEmbeddedChildDefinition(openAiModel)
                        }
                    },

                    new AiSdkPipelineStepDefinition
                    {
                        Name = WaitingStepName,
                        StepKey = "execution.await-input",
                        Order = 2,
                        DependsOn = new[] { "delegate-analysis" },
                        Config = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                        {
                            ["waitingKey"] = JsonSerializer.SerializeToElement(WaitingKey),
                            ["reason"] = JsonSerializer.SerializeToElement(
                                "Review the root plan and delegated child analysis before final response generation.")
                        }
                    },

                    PromptStep(
                        name: "final-answer",
                        order: 3,
                        dependsOn: new[] { WaitingStepName },
                        model: openAiModel,
                        template:
                            """
                            You are the root agent producing the final response.

                            The original public execution request is JSON:
                            {{requestJson}}

                            Root planning note:
                            {{rootPlan}}

                            Delegated child analysis:
                            {{childAnalysis}}

                            Human approval:
                            {{approved}}

                            Human feedback:
                            {{feedback}}

                            Read the "userPrompt" field from the request JSON.

                            If approval is false, do not present the proposed work as approved. Briefly explain
                            that the proposal was rejected and incorporate the feedback.

                            If approval is true, answer the user's original request directly. Use the root plan,
                            the independent child analysis, and the human feedback. Keep the response focused.
                            """,
                        inputs: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                        {
                            ["requestJson"] = JsonSerializer.SerializeToElement("state.input"),
                            ["rootPlan"] = JsonSerializer.SerializeToElement(
                                "steps.plan.result.data.value"),
                            ["childAnalysis"] = JsonSerializer.SerializeToElement(
                                "steps.delegate-analysis.result.payload.data.result"),
                            ["approved"] = JsonSerializer.SerializeToElement(
                                $"steps.{WaitingStepName}.result.value.approved"),
                            ["feedback"] = JsonSerializer.SerializeToElement(
                                $"steps.{WaitingStepName}.result.value.feedback")
                        }),

                    new AiSdkPipelineStepDefinition
                    {
                        Name = "publish-result",
                        StepKey = "execution.publish-result",
                        Order = 4,
                        DependsOn = new[] { "final-answer" },
                        Config = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                        {
                            ["source"] = JsonSerializer.SerializeToElement(
                                "steps.final-answer.result.data")
                        }
                    }
                }
            },
            Functions = Array.Empty<AiSdkPublicationFunctionUpload>()
        };
    }

    internal static AiSdkExecutionSubmissionRequest CreateSubmission(
        string publicationRef,
        string userPrompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicationRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(userPrompt);

        var requestId = Guid.NewGuid().ToString("N");

        return new AiSdkExecutionSubmissionRequest
        {
            PublicationRef = publicationRef,
            IdempotencyKey = $"interactive-agent-dotnet-{requestId}",
            CorrelationId = $"interactive-agent-dotnet-{requestId}",
            Input = JsonSerializer.SerializeToElement(
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["userPrompt"] = userPrompt,
                    ["requestId"] = requestId
                }),
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["demo"] = "interactive-agent-sdk",
                ["sdk.language"] = "dotnet"
            }
        };
    }

    private static AiSdkPipelineStepDefinition PromptStep(
        string name,
        int order,
        IReadOnlyList<string> dependsOn,
        string model,
        string template,
        IReadOnlyDictionary<string, JsonElement> inputs)
    {
        return new AiSdkPipelineStepDefinition
        {
            Name = name,
            StepKey = "ai.prompt",
            Order = order,
            DependsOn = dependsOn,
            Input = inputs,
            Config = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["provider"] = JsonSerializer.SerializeToElement("openai"),
                ["model"] = JsonSerializer.SerializeToElement(model),
                ["template"] = JsonSerializer.SerializeToElement(template),
                ["promptVersion"] = JsonSerializer.SerializeToElement(
                    "interactive-agent-sdk-v1")
            }
        };
    }

    /// <summary>
    /// Builds the exact inline Child DAG shape expected by the existing immutable
    /// publication compiler. Property casing intentionally matches the already
    /// validated nested Child DAG public-SDK matrix contract.
    /// </summary>
    private static JsonElement CreateEmbeddedChildDefinition(string openAiModel)
    {
        var child = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Name"] = ChildPipelineName,
            ["Version"] = ChildPipelineVersion,
            ["ExecutionMode"] = "Dag",
            ["Steps"] = new object?[]
            {
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Name"] = "child-analysis",
                    ["StepKey"] = "ai.prompt",
                    ["Order"] = 0,
                    ["DependsOn"] = Array.Empty<string>(),
                    ["Input"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["requestJson"] = "state.requestJson",
                        ["rootPlan"] = "state.rootPlan"
                    },
                    ["Config"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["provider"] = "openai",
                        ["model"] = openAiModel,
                        ["template"] =
                            """
                            You are a bounded delegated child agent.

                            The original public execution request is JSON:
                            {{requestJson}}

                            Root planning note:
                            {{rootPlan}}

                            Read the "userPrompt" field from the request JSON.

                            Independently analyze the bounded delegated task implied by the root plan.
                            Check assumptions, identify a useful correction or confirmation, and return
                            concise evidence or reasoning that the parent should consider.

                            Do not delegate again and do not ask for human input.
                            """,
                        ["promptVersion"] = "interactive-agent-sdk-child-v1"
                    }
                },
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Name"] = "publish-child-result",
                    ["StepKey"] = "execution.publish-result",
                    ["Order"] = 1,
                    ["DependsOn"] = new[] { "child-analysis" },
                    ["Input"] = new Dictionary<string, object?>(StringComparer.Ordinal),
                    ["Config"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["source"] = "steps.child-analysis.result.data.value"
                    }
                }
            },
            ["Config"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        };

        return JsonSerializer.SerializeToElement(child);
    }
}
