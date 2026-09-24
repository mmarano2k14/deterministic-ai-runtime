import { randomUUID } from "node:crypto";
import type {
  AiSdkExecutionSubmissionRequest,
  AiSdkJsonValue,
  AiSdkPipelinePublicationRequest,
  AiSdkPipelineStepDefinition,
} from "@multiplexed/ai-sdk";

export const WAITING_KEY = "interactive-agent-review";
export const WAITING_STEP_NAME = "await-review";

const ROOT_PIPELINE_NAME = "interactive-agent-sdk-typescript";
const ROOT_PIPELINE_VERSION = "1";
const CHILD_PIPELINE_NAME = "interactive-agent-sdk-typescript-analysis";
const CHILD_PIPELINE_VERSION = "1";

export function createPublication(openAiModel: string): AiSdkPipelinePublicationRequest {
  if (openAiModel.trim().length === 0) {
    throw new Error("OPENAI_MODEL must not be empty.");
  }

  return {
    definition: {
      name: ROOT_PIPELINE_NAME,
      version: ROOT_PIPELINE_VERSION,
      executionMode: "Dag",
      steps: [
        promptStep(
          "plan",
          0,
          [],
          openAiModel,
          `You are the root planning agent in a deterministic AI runtime.

The public execution request is supplied as JSON:
{{requestJson}}

Read the "userPrompt" field from that JSON.

Produce a concise planning note containing:
1. the user's actual objective,
2. a short plan,
3. one bounded analysis task to delegate to a child agent,
4. important assumptions or risks.

Do not answer the user's request yet.`,
          { requestJson: "state.input" },
        ),
        {
          name: "delegate-analysis",
          stepKey: "execution.child-dag",
          order: 1,
          dependsOn: ["plan"],
          input: {
            requestJson: "state.input",
            rootPlan: "steps.plan.result.data.value",
          },
          config: {
            childDagId: CHILD_PIPELINE_NAME,
            childDagVersion: CHILD_PIPELINE_VERSION,
            logicalInvocationKey: "interactive-agent-analysis",
            childDagDefinition: createEmbeddedChildDefinition(openAiModel),
          },
        },
        {
          name: WAITING_STEP_NAME,
          stepKey: "execution.await-input",
          order: 2,
          dependsOn: ["delegate-analysis"],
          config: {
            waitingKey: WAITING_KEY,
            reason:
              "Review the root plan and delegated child analysis before final response generation.",
          },
        },
        promptStep(
          "final-answer",
          3,
          [WAITING_STEP_NAME],
          openAiModel,
          `You are the root agent producing the final response.

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
the independent child analysis, and the human feedback. Keep the response focused.`,
          {
            requestJson: "state.input",
            rootPlan: "steps.plan.result.data.value",
            childAnalysis: "steps.delegate-analysis.result.payload.data.result",
            approved: `steps.${WAITING_STEP_NAME}.result.value.approved`,
            feedback: `steps.${WAITING_STEP_NAME}.result.value.feedback`,
          },
        ),
        {
          name: "publish-result",
          stepKey: "execution.publish-result",
          order: 4,
          dependsOn: ["final-answer"],
          config: {
            source: "steps.final-answer.result.data.value",
          },
        },
      ],
    },
    functions: [],
  };
}

export function createSubmission(
  publicationRef: string,
  userPrompt: string,
): AiSdkExecutionSubmissionRequest {
  if (publicationRef.trim().length === 0) {
    throw new Error("publicationRef must not be empty.");
  }
  if (userPrompt.trim().length === 0) {
    throw new Error("userPrompt must not be empty.");
  }

  const requestId = randomUUID().replaceAll("-", "");

  return {
    publicationRef,
    idempotencyKey: `interactive-agent-typescript-${requestId}`,
    correlationId: `interactive-agent-typescript-${requestId}`,
    input: {
      userPrompt,
      requestId,
    },
    metadata: {
      demo: "interactive-agent-sdk",
      "sdk.language": "typescript",
    },
  };
}

function promptStep(
  name: string,
  order: number,
  dependsOn: readonly string[],
  model: string,
  template: string,
  input: Readonly<Record<string, AiSdkJsonValue>>,
): AiSdkPipelineStepDefinition {
  return {
    name,
    stepKey: "ai.prompt",
    order,
    dependsOn,
    input,
    config: {
      provider: "openai",
      model,
      template,
      promptVersion: "interactive-agent-sdk-v1",
    },
  };
}

function createEmbeddedChildDefinition(openAiModel: string): AiSdkJsonValue {
  return {
    Name: CHILD_PIPELINE_NAME,
    Version: CHILD_PIPELINE_VERSION,
    ExecutionMode: "Dag",
    Steps: [
      {
        Name: "child-analysis",
        StepKey: "ai.prompt",
        Order: 0,
        DependsOn: [],
        Input: {
          requestJson: "state.requestJson",
          rootPlan: "state.rootPlan",
        },
        Config: {
          provider: "openai",
          model: openAiModel,
          template: `You are a bounded delegated child agent.

The original public execution request is JSON:
{{requestJson}}

Root planning note:
{{rootPlan}}

Read the "userPrompt" field from the request JSON.

Independently analyze the bounded delegated task implied by the root plan.
Check assumptions, identify a useful correction or confirmation, and return
concise evidence or reasoning that the parent should consider.

Do not delegate again and do not ask for human input.`,
          promptVersion: "interactive-agent-sdk-child-v1",
        },
      },
      {
        Name: "publish-child-result",
        StepKey: "execution.publish-result",
        Order: 1,
        DependsOn: ["child-analysis"],
        Input: {},
        Config: {
          source: "steps.child-analysis.result.data.value",
        },
      },
    ],
    Config: {},
  };
}
