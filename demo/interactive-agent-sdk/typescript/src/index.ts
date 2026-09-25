import {
  AiSdkAccessContextBootstrapper,
  AiSdkClient,
  AiSdkMcpHttpTransport,
  AiSdkStaticCredentialProvider,
  type AiSdkExecutionObservation,
  type AiSdkExecutionResult,
} from "@multiplexed/ai-sdk";
import { loadConfiguration, isSmokeMode, type DemoConfiguration } from "./configuration.js";
import {
  ConsoleInputPump,
  InteractiveExecutionConsole,
} from "./interactive-console.js";
import {
  createPublication,
  createSubmission,
  WAITING_KEY,
  WAITING_STEP_NAME,
} from "./pipeline.js";

async function main(): Promise<number> {
  const config = loadConfiguration();

  if (isSmokeMode()) {
    console.log("Interactive Agent SDK Demo");
    console.log("SDK: TypeScript");
    console.log(`Runtime endpoint: ${config.endpoint.toString()}`);
    console.log(`OpenAI model configured: ${config.openAiModel.trim().length > 0}`);
    console.log("External SDK consumer initialized.");
    console.log("Smoke mode: no authentication/bootstrap/runtime request was sent.");
    return 0;
  }

  printHeader(config);
  const client = await createClient(config);
  const input = new ConsoleInputPump();

  try {
    const userPrompt = (await input.prompt("User request: "))?.trim();
    if (!userPrompt) {
      console.error("A non-empty user request is required.");
      return 2;
    }

    console.log();
    if (config.verbose) {
      console.log("SDK command: sdk.publish_pipeline");
    } else {
      console.log("[>] Publishing immutable pipeline");
    }
    const publication = await client.publishPipeline(createPublication(config.openAiModel));
    if (config.verbose) {
      console.log(`PublicationRef: ${publication.publicationRef}`);
      console.log(`Pipeline: ${publication.pipelineName}@${publication.pipelineVersion}`);
    } else {
      console.log(`[OK] Published ${publication.pipelineName}@${publication.pipelineVersion}`);
    }
    console.log();

    if (config.verbose) {
      console.log("SDK command: sdk.execution.submit");
    } else {
      console.log("[>] Submitting durable execution");
    }
    const submission = await client.submitExecution(
      createSubmission(publication.publicationRef, userPrompt),
    );
    console.log(`ExecutionId: ${submission.executionId}`);
    if (config.verbose) {
      console.log(`Initial status: ${submission.status}`);
    }
    console.log();

    const consoleUi = new InteractiveExecutionConsole(
      client,
      submission.executionId,
      WAITING_KEY,
      WAITING_STEP_NAME,
      input,
      config.verbose,
    );

    const result = await consoleUi.run();
    if (result === null) {
      console.log();
      console.log("Local console detached. The durable execution was not cancelled.");
      return 0;
    }

    printTerminalResult(
      result,
      consoleUi.lastObservation,
      config.openAiModel,
      consoleUi.reviewApproved,
      consoleUi.reviewFeedback,
    );
    await consoleUi.runPostTerminalCommands(result.status);
    return result.status === "Completed" ? 0 : 1;
  } finally {
    input.close();
  }
}

async function createClient(config: DemoConfiguration): Promise<AiSdkClient> {
  if (!config.token) {
    throw new Error(
      "AI_RUNTIME_TOKEN is required for the standalone authenticated TypeScript demo.",
    );
  }

  const credentials = new AiSdkStaticCredentialProvider({
    scheme: "Bearer",
    value: config.token,
  });

  let accessContext = config.accessContext;
  if (!accessContext) {
    if (config.verbose) {
      console.log("Authentication bootstrap:");
      console.log(`  POST ${config.accessContextEndpoint.toString()}`);
      console.log("  Authorization: Bearer <redacted>");
    } else {
      console.log("[>] Creating RBAC access context from JWT claims");
    }

    const bootstrap = await AiSdkAccessContextBootstrapper.create({
      endpoint: config.accessContextEndpoint,
      credentialProvider: credentials,
      accessContextHeaderName: config.accessContextHeader,
    });

    accessContext = bootstrap.accessContext;
    if (config.verbose) {
      console.log(
        `  Access context created via '${bootstrap.headerName}'. Handle not displayed.`,
      );
      console.log("  Subsequent handle rotation is managed by the SDK transport.");
      console.log();
    } else {
      console.log(`[OK] RBAC access context created; ${bootstrap.headerName} rotation enabled`);
      console.log();
    }
  } else if (config.verbose) {
    console.log(
      "Using the pre-provisioned AI_RUNTIME_ACCESS_CONTEXT. " +
        "Subsequent rotation is managed by the SDK transport.",
    );
    console.log();
  } else {
    console.log("[OK] Using pre-provisioned RBAC access context; rotation enabled");
    console.log();
  }

  if (accessContext === undefined) {
    throw new Error("The access-context bootstrap did not produce a usable handle.");
  }

  const initialHeaders: Readonly<Record<string, string>> = {
    [config.accessContextHeader]: accessContext,
  };

  return new AiSdkClient(
    new AiSdkMcpHttpTransport(config.endpoint, {
      credentialProvider: credentials,
      accessContextHeaderName: config.accessContextHeader,
      additionalHeaders: initialHeaders,
    }),
  );
}

function printHeader(config: DemoConfiguration): void {
  console.log("==================================================");
  console.log(" Deterministic AI Runtime - Interactive SDK Agent");
  console.log("==================================================");
  console.log();
  console.log("SDK: TypeScript");
  console.log(`Runtime endpoint: ${config.endpoint.toString()}`);
  console.log(`OpenAI model: ${config.openAiModel}`);
  console.log(`Console mode: ${config.verbose ? "verbose" : "presentation"}`);
  console.log();
  console.log(
    "Runtime authentication uses a Bearer JWT plus a server-created RBAC access context.",
  );
  console.log(
    "OpenAI authentication stays on the runtime host. " +
      "The external SDK does not send OPENAI_API_KEY.",
  );
  console.log();
}

function printTerminalResult(
  result: AiSdkExecutionResult,
  observation: AiSdkExecutionObservation | undefined,
  configuredModel: string,
  reviewApproved: boolean | undefined,
  reviewFeedback: string | undefined,
): void {
  console.log();
  console.log("==================================================");
  console.log(" Agent result");
  console.log("==================================================");

  const published = publishedResult(result.output);
  const answer = readString(published, "value")
    ?? readString(published, "rawText")
    ?? (typeof published === "string" ? published : undefined);

  console.log();
  console.log("OpenAI response:");
  console.log();
  console.log(answer?.trim() ? answer : formatJson(published ?? "(none)"));

  if (isRecord(published)) {
    const provider = readString(published, "providerKey") ?? "openai";
    const model = readString(published, "model") ?? configuredModel;
    const inputTokens = readNumber(published, "inputTokens");
    const outputTokens = readNumber(published, "outputTokens");
    const totalTokens = readNumber(published, "totalTokens");

    console.log();
    console.log("Model response metadata:");
    console.log(`  Provider: ${provider}`);
    console.log(`  Model:    ${model}`);
    if (inputTokens !== undefined || outputTokens !== undefined || totalTokens !== undefined) {
      console.log(
        `  Tokens:   input=${inputTokens ?? "-"}, output=${outputTokens ?? "-"}, total=${totalTokens ?? "-"}`,
      );
    }
  }

  console.log();
  console.log("Execution:");
  console.log(`  ID:        ${result.executionId}`);
  console.log(`  Status:    ${result.status}`);
  console.log(`  Completed: ${result.completedAtUtc ?? "-"}`);

  if (observation !== undefined) {
    console.log();
    console.log("Pipeline:");
    for (const step of observation.steps) {
      console.log(`  ${step.name.padEnd(18)} ${step.status}`);
    }
  }

  if (reviewApproved !== undefined) {
    console.log();
    console.log("Human review:");
    console.log(`  Approved: ${reviewApproved}`);
    if (reviewFeedback?.trim()) {
      console.log(`  Feedback: ${reviewFeedback}`);
    }
  }

  if (result.failure !== undefined) {
    console.log();
    console.log(`Failure code: ${result.failure.code}`);
    console.log(`Failure message: ${result.failure.message}`);
  }
}

function publishedResult(output: unknown): unknown {
  if (isRecord(output) && "result" in output) {
    return output.result;
  }
  return output;
}

function isRecord(value: unknown): value is Readonly<Record<string, unknown>> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function readString(value: unknown, name: string): string | undefined {
  if (!isRecord(value)) {
    return undefined;
  }
  return typeof value[name] === "string" ? value[name] : undefined;
}

function readNumber(value: unknown, name: string): number | undefined {
  if (!isRecord(value)) {
    return undefined;
  }
  return typeof value[name] === "number" ? value[name] : undefined;
}

function formatJson(value: unknown): string {
  if (typeof value === "string") {
    return value;
  }
  if (value === undefined) {
    return "(none)";
  }
  return JSON.stringify(value, null, 2);
}

try {
  process.exitCode = await main();
} catch (error) {
  console.error();
  console.error(`Demo failed: ${error instanceof Error ? error.message : String(error)}`);
  process.exitCode = 1;
}
