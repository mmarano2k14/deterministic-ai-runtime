import { randomUUID } from "node:crypto";
import { mkdir, writeFile } from "node:fs/promises";
import { dirname } from "node:path";
import type { AiSdkClient, AiSdkExecutionObservation } from "@multiplexed/ai-sdk";
import type { DemoConfiguration } from "./configuration.js";
import { createPublication, createSubmission, WAITING_KEY, WAITING_STEP_NAME } from "./pipeline.js";

const POLL_MS = 400;
const DEFAULT_TIMEOUT_SECONDS = 20 * 60;
const DEFAULT_PROMPT = "Validate the interactive agent topology through the public SDK boundary.";
const DEFAULT_FEEDBACK = "topology-validation";
const TERMINAL = new Set(["Completed", "Failed", "Cancelled"]);
const ROOT_STEPS = ["plan", "delegate-analysis", "await-review", "final-answer", "publish-result"] as const;

export function isAutomatedValidationMode(): boolean {
  return ["1", "true", "yes", "on"].includes(
    (process.env.AI_DEMO_AUTOMATED_VALIDATION ?? "").trim().toLowerCase(),
  );
}

export async function runAutomatedValidation(
  client: AiSdkClient,
  config: DemoConfiguration,
): Promise<number> {
  const startedAtUtc = new Date().toISOString();
  const runtimeProvider = required("AI_VALIDATION_RUNTIME_PROVIDER");
  const runtimeTransport = required("AI_VALIDATION_RUNTIME_TRANSPORT");
  const evidencePath = required("AI_DEMO_EVIDENCE_PATH");
  const prompt = process.env.AI_DEMO_VALIDATION_PROMPT?.trim() || DEFAULT_PROMPT;
  const feedback = process.env.AI_DEMO_VALIDATION_FEEDBACK ?? DEFAULT_FEEDBACK;
  const timeoutSeconds = Number.parseInt(
    process.env.AI_DEMO_VALIDATION_TIMEOUT_SECONDS ?? String(DEFAULT_TIMEOUT_SECONDS),
    10,
  );

  const publication = await client.publishPipeline(createPublication(config.openAiModel));
  const submission = await client.submitExecution(
    createSubmission(publication.publicationRef, prompt),
  );
  const executionId = submission.executionId;
  const deadline = Date.now() + Math.max(timeoutSeconds, 1) * 1000;

  let waitingBoundaryReached = false;
  let inputResponse: { accepted: boolean; executionId: string } | undefined;
  let finalObservation: AiSdkExecutionObservation | undefined;

  while (true) {
    if (Date.now() >= deadline) {
      throw new Error(`Automated validation timed out after ${timeoutSeconds}s for ${executionId}.`);
    }

    const observation = await client.observeExecution(executionId);
    finalObservation = observation;
    const waitingStep = observation.steps.find((step) => step.name === WAITING_STEP_NAME);

    if (!inputResponse && waitingStep?.status === "WaitingForExternal") {
      waitingBoundaryReached = true;
      inputResponse = await client.submitExecutionInput(executionId, {
        waitingKey: WAITING_KEY,
        waitingStepName: WAITING_STEP_NAME,
        reason: "interactive-agent-topology-validation",
        correlationId: `interactive-agent-validation-input-${randomUUID().replaceAll("-", "")}`,
        input: { approved: true, feedback },
      });
      if (!inputResponse.accepted) {
        throw new Error("Automated human input was not accepted.");
      }
      if (inputResponse.executionId !== executionId) {
        throw new Error("Human input changed the durable ExecutionId.");
      }
    }

    if (TERMINAL.has(observation.status)) {
      break;
    }
    await delay(POLL_MS);
  }

  if (!finalObservation || !waitingBoundaryReached || !inputResponse) {
    throw new Error("Human-review waiting boundary was not reached.");
  }

  const result = await client.getExecutionResult(executionId);
  const replay = await client.replayExecution(executionId, {
    strictDeterminism: true,
    includeDiagnostics: true,
    reason: "interactive-agent-topology-validation",
    correlationId: `interactive-agent-validation-replay-${randomUUID().replaceAll("-", "")}`,
  });

  const published = publishedResult(result.output);
  const provider = readString(published, "providerKey") ?? "openai";
  const model = readString(published, "model") ?? config.openAiModel;
  const rootSteps = Object.fromEntries(
    finalObservation.steps
      .filter((step) => ROOT_STEPS.includes(step.name as typeof ROOT_STEPS[number]))
      .map((step) => [step.name, step.status]),
  );

  const evidence = {
    contractVersion: 1,
    scenarioId: "interactive-agent-sdk-canonical-v1",
    sdkClient: "typescript",
    runtimeProvider,
    runtimeTransport,
    publication: {
      accepted: true,
      publicationRef: publication.publicationRef,
      pipelineName: publication.pipelineName,
      pipelineVersion: publication.pipelineVersion,
    },
    execution: {
      executionId,
      executionIdAfterHumanInput: inputResponse.executionId,
      sameExecutionIdAfterHumanInput: inputResponse.executionId === executionId,
      status: result.status,
    },
    humanReview: {
      waitingBoundaryReached,
      waitingStepName: WAITING_STEP_NAME,
      waitingKey: WAITING_KEY,
      accepted: inputResponse.accepted,
      approved: true,
      feedback,
    },
    provider: {
      name: provider,
      model,
      responsePayloadPresent: published !== undefined && published !== null,
    },
    rootSteps,
    result: {
      available: result.output !== undefined && result.output !== null,
      requestId: findRequestId(published) ?? null,
    },
    replay: {
      succeeded: replay.succeeded,
      deterministic: replay.deterministic === true,
      executionIdUnchanged: result.executionId === executionId,
    },
    timing: {
      startedAtUtc,
      completedAtUtc: new Date().toISOString(),
    },
    diagnostics: {
      replayMessage: replay.message ?? null,
      replayFailureReason: replay.failureReason ?? null,
      terminalCompletedAtUtc: result.completedAtUtc ?? null,
    },
  };

  await mkdir(dirname(evidencePath), { recursive: true });
  await writeFile(evidencePath, `${JSON.stringify(evidence, null, 2)}\n`, "utf8");
  return evidencePasses(evidence) ? 0 : 1;
}

function evidencePasses(evidence: any): boolean {
  return evidence.execution.status === "Completed"
    && evidence.execution.sameExecutionIdAfterHumanInput === true
    && evidence.humanReview.accepted === true
    && ROOT_STEPS.every((name) => evidence.rootSteps[name] === "Completed")
    && evidence.provider.name === "openai"
    && Boolean(evidence.provider.model)
    && evidence.provider.responsePayloadPresent === true
    && evidence.result.available === true
    && evidence.replay.succeeded === true
    && evidence.replay.deterministic === true
    && evidence.replay.executionIdUnchanged === true;
}

function publishedResult(output: unknown): unknown {
  return isRecord(output) && "result" in output ? output.result : output;
}

function readString(value: unknown, name: string): string | undefined {
  return isRecord(value) && typeof value[name] === "string" ? value[name] : undefined;
}

function findRequestId(value: unknown): string | undefined {
  if (isRecord(value)) {
    if (typeof value.requestId === "string") return value.requestId;
    for (const child of Object.values(value)) {
      const found = findRequestId(child);
      if (found) return found;
    }
  } else if (Array.isArray(value)) {
    for (const child of value) {
      const found = findRequestId(child);
      if (found) return found;
    }
  }
  return undefined;
}

function isRecord(value: unknown): value is Record<string, any> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function required(name: string): string {
  const value = process.env[name]?.trim();
  if (!value) throw new Error(`Missing required automated-validation configuration '${name}'.`);
  return value;
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
