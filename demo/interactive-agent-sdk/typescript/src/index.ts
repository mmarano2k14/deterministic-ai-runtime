import {
  AiSdkAccessContextBootstrapper,
  AiSdkClient,
  AiSdkMcpHttpTransport,
  AiSdkStaticCredentialProvider,
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
    console.log("SDK command: sdk.publish_pipeline");
    const publication = await client.publishPipeline(createPublication(config.openAiModel));
    console.log(`PublicationRef: ${publication.publicationRef}`);
    console.log(`Pipeline: ${publication.pipelineName}@${publication.pipelineVersion}`);
    console.log();

    console.log("SDK command: sdk.execution.submit");
    const submission = await client.submitExecution(
      createSubmission(publication.publicationRef, userPrompt),
    );
    console.log(`ExecutionId: ${submission.executionId}`);
    console.log(`Initial status: ${submission.status}`);
    console.log();

    const consoleUi = new InteractiveExecutionConsole(
      client,
      submission.executionId,
      WAITING_KEY,
      WAITING_STEP_NAME,
      input,
    );

    const result = await consoleUi.run();
    if (result === null) {
      console.log();
      console.log("Local console detached. The durable execution was not cancelled.");
      return 0;
    }

    printTerminalResult(result);
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
    console.log("Authentication bootstrap:");
    console.log(`  POST ${config.accessContextEndpoint.toString()}`);
    console.log("  Authorization: Bearer <redacted>");

    const bootstrap = await AiSdkAccessContextBootstrapper.create({
      endpoint: config.accessContextEndpoint,
      credentialProvider: credentials,
      accessContextHeaderName: config.accessContextHeader,
    });

    accessContext = bootstrap.accessContext;
    console.log(
      `  Access context created via '${bootstrap.headerName}'. Handle not displayed.`,
    );
    console.log("  Subsequent handle rotation is managed by the SDK transport.");
    console.log();
  } else {
    console.log(
      "Using the pre-provisioned AI_RUNTIME_ACCESS_CONTEXT. " +
        "Subsequent rotation is managed by the SDK transport.",
    );
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

function printTerminalResult(result: AiSdkExecutionResult): void {
  console.log();
  console.log("==================================================");
  console.log(" Terminal execution result");
  console.log("==================================================");
  console.log(`ExecutionId: ${result.executionId}`);
  console.log(`Status: ${result.status}`);
  console.log(`CompletedAtUtc: ${result.completedAtUtc}`);

  if (result.output !== undefined) {
    console.log();
    console.log("Agent response:");
    if (
      result.output !== null &&
      typeof result.output === "object" &&
      !Array.isArray(result.output) &&
      "result" in result.output
    ) {
      console.log(formatJson(result.output.result));
    } else {
      console.log(formatJson(result.output));
    }
  }

  if (result.failure !== undefined) {
    console.log();
    console.log(`Failure code: ${result.failure.code}`);
    console.log(`Failure message: ${result.failure.message}`);
  }
}

function formatJson(value: unknown): string {
  return typeof value === "string" ? value : JSON.stringify(value, null, 2);
}

try {
  process.exitCode = await main();
} catch (error) {
  console.error();
  console.error(`Demo failed: ${error instanceof Error ? error.message : String(error)}`);
  process.exitCode = 1;
}
