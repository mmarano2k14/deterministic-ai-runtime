import fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  AiSdkClient,
  AiSdkMcpHttpTransport,
  AiSdkStaticCredentialProvider,
} from "../../../node/sdk/dist/index.js";

const args = await parseArgs(process.argv.slice(2));
if (args.feature === "dependency-firewall") {
  await runDependencyFirewall(args);
  process.exit(0);
}
const root = process.env.MATRIX_SAMPLE_ROOT ?? path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../../..");
const credentialProvider = args.token
  ? new AiSdkStaticCredentialProvider({ scheme: "Bearer", value: args.token })
  : undefined;
const client = new AiSdkClient(
  new AiSdkMcpHttpTransport(new URL(args.endpoint), {
    credentialProvider,
    additionalHeaders: args.accessContext
      ? { [args.accessContextHeader]: args.accessContext }
      : undefined,
  }),
);
if (args.feature === "control") {
  await runControl(client, args, root);
  process.exit(0);
}
const source = ["cancellation", "watch"].includes(args.feature)
  ? await cancellationWorkerSource(root, args.worker)
  : await workerSource(root, args.worker);
const marker = `${args.scenarioId}-marker`;

const publication = await client.publishPipeline({
  definition: {
    name: `matrix-${args.scenarioId}`,
    version: "1",
    executionLanguage: args.worker,
    executionMode: "Dag",
    steps: [{
      name: "work",
      stepKey: "custom",
      order: 0,
      executionLanguage: args.worker,
      invocation: { kind: "Custom" },
      dependsOn: [],
      input: { marker },
      config: {},
    }],
    config: {},
  },
  functions: [{
    site: { kind: "Step", stepName: "work" },
    environmentRef: args.environmentRef,
    entryPointPath: source.entryPointPath,
    entryPointSymbol: source.entryPointSymbol,
    sources: [{ path: source.entryPointPath, contentBase64: source.bytes.toString("base64") }],
    dependencies: [],
  }],
});

const submitted = await client.submitExecution({
  publicationRef: publication.publicationRef,
  idempotencyKey: `${args.scenarioId}-${crypto.randomUUID().replaceAll("-", "")}`,
  input: { marker },
  metadata: {
    "matrix.scenario": args.scenarioId,
    "matrix.client": "typescript",
    "matrix.worker": args.worker,
  },
});

if (args.feature === "cancellation") {
  const active = await waitForActiveStep(client, submitted.executionId, "work", 30_000);
  const correlationId = `cancel-${crypto.randomUUID().replaceAll("-", "")}`;
  const cancellation = await client.cancelExecution(submitted.executionId, {
    reason: "matrix-running-cancellation",
    correlationId,
  });
  if (!cancellation.cancellationRequested || cancellation.executionId !== submitted.executionId) {
    throw new Error("Public cancellation operation did not acknowledge the submitted execution.");
  }
  if (!cancellation.requestedAtUtc || cancellation.correlationId !== correlationId) {
    throw new Error("Public cancellation acknowledgement did not preserve durable request metadata.");
  }

  const terminal = await waitForTerminal(client, submitted.executionId, 45_000);
  const result = await client.getExecutionResult(submitted.executionId);
  if (terminal.status !== "Cancelled" || result.status !== "Cancelled") {
    throw new Error(`Cancellation scenario ended as observation='${terminal.status}', result='${result.status}', expected 'Cancelled'.`);
  }
  if (result.output != null || result.failure != null) {
    throw new Error("Cancelled public result unexpectedly exposed completed output or failure payload.");
  }
  const activeStep = active.steps.find((step) => step.name === "work");
  const terminalStep = terminal.steps.find((step) => step.name === "work");
  await writeEvidence(args.evidence, {
    schemaVersion: 1,
    scenarioId: args.scenarioId,
    status: "passed",
    coverageTarget: "cancellation",
    coverageValues: [],
    cancellationMode: "running-cooperative",
    clientLanguage: "typescript",
    workerLanguage: args.worker,
    endpoint: args.endpoint,
    topology: args.topology,
    provider: args.provider,
    runtimeProvider: args.runtimeProvider,
    workerExecutionProvider: args.workerExecutionProvider,
    publicationRef: publication.publicationRef,
    executionId: submitted.executionId,
    activeStatusBeforeCancel: active.status,
    activeStepStatusBeforeCancel: activeStep?.status,
    cancellationRequested: cancellation.cancellationRequested,
    cancellationRequestedAtUtc: cancellation.requestedAtUtc,
    cancellationCorrelationId: cancellation.correlationId,
    terminalStatus: result.status,
    terminalStepStatus: terminalStep?.status,
    evidence: [
      "publish", "submit", "active-execution-observed", "sdk-execution-cancel",
      "durable-cancellation-acknowledged", "terminal-cancelled-observed", "terminal-result",
    ],
    recordedAtUtc: terminal.updatedAtUtc,
  });
} else if (args.feature === "watch") {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 45_000);
  const publicSequences = [];
  const publicEventTypes = [];
  let sawSnapshot = false;
  let sawEvent = false;
  let sawResync = false;

  try {
    for await (const item of client.watchExecution({
      executionId: submitted.executionId,
      includeInitialSnapshot: true,
    }, controller.signal)) {
      if (item.executionId !== submitted.executionId) {
        throw new Error("Watch returned an event for a different execution.");
      }
      if (item.sequence != null) publicSequences.push(item.sequence);
      if (item.kind === "Snapshot") sawSnapshot = true;
      else if (item.kind === "Event") {
        sawEvent = true;
        if (item.eventType) publicEventTypes.push(item.eventType);
      } else if (item.kind === "ResyncRequired") {
        sawResync = true;
      }
    }
  } finally {
    clearTimeout(timeout);
  }

  if (!sawSnapshot) throw new Error("Watch E2E did not receive the authoritative initial snapshot.");
  if (!sawEvent) throw new Error("Watch E2E did not receive any incremental public event after the snapshot.");
  if (sawResync) throw new Error("Nominal Watch E2E unexpectedly required resynchronization.");
  if (publicSequences.length < 2 || publicSequences.slice(1).some((value, index) => value <= publicSequences[index])) {
    throw new Error("Watch E2E did not observe a strictly increasing public sequence.");
  }

  const result = await client.getExecutionResult(submitted.executionId);
  if (result.status !== "Completed") {
    throw new Error(`Watch E2E execution '${submitted.executionId}' ended as '${result.status}', expected 'Completed'.`);
  }

  await writeEvidence(args.evidence, {
    schemaVersion: 1,
    scenarioId: args.scenarioId,
    status: "passed",
    coverageTarget: "execution-watch-e2e",
    coverageValues: [],
    clientLanguage: "typescript",
    workerLanguage: args.worker,
    endpoint: args.endpoint,
    topology: args.topology,
    provider: args.provider,
    runtimeProvider: args.runtimeProvider,
    workerExecutionProvider: args.workerExecutionProvider,
    publicationRef: publication.publicationRef,
    executionId: submitted.executionId,
    initialSnapshotObserved: sawSnapshot,
    incrementalEventObserved: sawEvent,
    resyncObserved: sawResync,
    publicSequences,
    publicEventTypes,
    terminalStatus: result.status,
    evidence: [
      "publish", "submit", "sdk-execution-watch", "mcp-http-public-boundary",
      "initial-snapshot", "ordered-incremental-event", "terminal-watch-convergence",
      "terminal-result", "public-execution-id",
    ],
    recordedAtUtc: new Date().toISOString(),
  });
} else {
  if (args.feature) throw new Error(`Unsupported --feature '${args.feature}'.`);
  await waitForTerminal(client, submitted.executionId, 90_000);
  const result = await client.getExecutionResult(submitted.executionId);
  if (result.status !== "Completed") {
    throw new Error(`Execution '${submitted.executionId}' ended as '${result.status}'.`);
  }

  await writeEvidence(args.evidence, {
    schemaVersion: 1,
    scenarioId: args.scenarioId,
    status: "passed",
    clientLanguage: "typescript",
    workerLanguage: args.worker,
    endpoint: args.endpoint,
    topology: args.topology,
    provider: args.provider,
    runtimeProvider: args.runtimeProvider,
    workerExecutionProvider: args.workerExecutionProvider,
    publicationRef: publication.publicationRef,
    executionId: submitted.executionId,
    terminalStatus: result.status,
    evidence: ["publish", "submit", "observe", "terminal-result", "public-execution-id"],
    recordedAtUtc: new Date().toISOString(),
  });
}


async function runControl(client, args, root) {
  console.log(`[matrix-typescript-client] CONTROL START scenario=${args.scenarioId} worker=${args.worker}`);

  const pausePublication = await publishControlPipeline(client, args, root, "pause");
  const pauseExecution = await submitControlExecution(client, args, pausePublication.publicationRef, "pause");
  const pauseWatch = startControlWatch(client, pauseExecution.executionId, 75_000);
  await waitForWatchActiveStep(pauseWatch.firstStepActive, pauseExecution.executionId, "first", 30_000);

  const pause = await client.pauseExecution(pauseExecution.executionId, { reason: "matrix-control-e2e-pause" });
  if (!pause.accepted || pause.executionId !== pauseExecution.executionId) {
    throw new Error("Public pause operation was not accepted for the submitted execution.");
  }
  const pauseGateVerified = await waitForPauseGate(client, pauseExecution.executionId, 20_000);
  if (!pauseGateVerified) throw new Error("Pause did not gate the dependent second step.");

  const resume = await client.resumeExecution(pauseExecution.executionId, { reason: "matrix-control-e2e-resume" });
  if (!resume.accepted || resume.executionId !== pauseExecution.executionId) {
    throw new Error("Public resume operation was not accepted for the paused execution.");
  }
  const pauseResult = await waitForCompletedResult(client, pauseExecution.executionId, 45_000);
  const pauseWatchEvidence = await pauseWatch.completion;
  if (pauseWatchEvidence.resyncObserved) throw new Error("Pause/resume unexpectedly forced Watch resynchronization.");

  const inputPublication = await publishControlPipeline(client, args, root, "input");
  const inputExecution = await submitControlExecution(client, args, inputPublication.publicationRef, "input");
  const inputWatch = startControlWatch(client, inputExecution.executionId, 75_000);
  await waitForWatchActiveStep(inputWatch.firstStepActive, inputExecution.executionId, "first", 30_000);
  const waitingKey = `approval:${args.scenarioId}:${crypto.randomUUID().replaceAll("-", "")}`;
  await seedWaitingForInput(args.endpoint, inputExecution.executionId, waitingKey, "second");
  const inputGateVerified = await waitForPauseGate(client, inputExecution.executionId, 20_000);
  if (!inputGateVerified) throw new Error("Waiting-for-input did not gate the dependent second step.");

  const input = await client.submitExecutionInput(inputExecution.executionId, {
    waitingKey,
    waitingStepName: "second",
    reason: "matrix-control-e2e-approval",
    input: { approved: true, source: "matrix" },
  });
  if (!input.accepted || input.executionId !== inputExecution.executionId || !input.state?.inputReceivedAtUtc) {
    throw new Error("Public human-input submission was not durably acknowledged.");
  }
  const inputResult = await waitForCompletedResult(client, inputExecution.executionId, 45_000);
  const inputWatchEvidence = await inputWatch.completion;
  if (inputWatchEvidence.resyncObserved) throw new Error("Human-input flow unexpectedly forced Watch resynchronization.");

  const replayPublication = await publishSingleReplayPipeline(client, args, root);
  const replayExecution = await submitControlExecution(client, args, replayPublication.publicationRef, "replay");
  await waitForCompletedResult(client, replayExecution.executionId, 45_000);
  const replay = await client.replayExecution(replayExecution.executionId, {
    includeDiagnostics: true,
    reason: "matrix-control-e2e-replay",
  });
  if (!replay.succeeded || replay.deterministic === false) {
    throw new Error(`Public replay validation failed: ${replay.message ?? replay.failureReason ?? "unknown"}`);
  }

  await writeEvidence(args.evidence, {
    schemaVersion: 1,
    scenarioId: args.scenarioId,
    status: "passed",
    coverageTarget: "execution-control-replay-e2e",
    clientLanguage: "typescript",
    workerLanguage: args.worker,
    endpoint: args.endpoint,
    topology: args.topology,
    provider: args.provider,
    runtimeProvider: args.runtimeProvider,
    workerExecutionProvider: args.workerExecutionProvider,
    pauseExecutionId: pauseExecution.executionId,
    pauseAccepted: pause.accepted,
    pauseGateVerified,
    resumeAccepted: resume.accepted,
    pauseResumeTerminalStatus: pauseResult.status,
    inputExecutionId: inputExecution.executionId,
    inputWaitSeeded: true,
    inputAccepted: input.accepted,
    inputGateVerified,
    inputTerminalStatus: inputResult.status,
    replayExecutionId: replayExecution.executionId,
    replaySucceeded: replay.succeeded,
    replayDeterministic: replay.deterministic,
    watchResyncObserved: pauseWatchEvidence.resyncObserved || inputWatchEvidence.resyncObserved,
    watchSnapshotsObserved: pauseWatchEvidence.snapshotObserved && inputWatchEvidence.snapshotObserved,
    watchIncrementalEventsObserved: pauseWatchEvidence.eventObserved && inputWatchEvidence.eventObserved,
    evidence: [
      "publish", "submit", "sdk-execution-watch", "sdk-execution-pause", "pause-gated-next-step",
      "sdk-execution-resume", "resume-terminal-convergence", "matrix-wait-input-production-authority",
      "sdk-execution-input-submit", "input-terminal-convergence", "sdk-execution-replay",
      "replay-validation-succeeded", "mcp-http-public-boundary",
    ],
    recordedAtUtc: new Date().toISOString(),
  });
}

async function publishControlPipeline(client, args, root, suffix) {
  const { slow, fast } = await controlWorkerSources(root, args.worker);
  return client.publishPipeline({
    definition: {
      name: `matrix-${args.scenarioId}-${suffix}`,
      version: "1",
      executionLanguage: args.worker,
      executionMode: "Dag",
      steps: [
        { name: "first", stepKey: "custom", order: 0, executionLanguage: args.worker, invocation: { kind: "Custom" }, dependsOn: [], input: { marker: `${args.scenarioId}-first` }, config: {} },
        { name: "second", stepKey: "custom", order: 1, executionLanguage: args.worker, invocation: { kind: "Custom" }, dependsOn: ["first"], input: { marker: `${args.scenarioId}-second` }, config: {} },
      ],
      config: {},
    },
    functions: [uploadForStep(slow, args.environmentRef, "first"), uploadForStep(fast, args.environmentRef, "second")],
  });
}

async function publishSingleReplayPipeline(client, args, root) {
  const source = await workerSource(root, args.worker);
  return client.publishPipeline({
    definition: {
      name: `matrix-${args.scenarioId}-replay`, version: "1", executionLanguage: args.worker, executionMode: "Dag",
      steps: [{ name: "work", stepKey: "custom", order: 0, executionLanguage: args.worker, invocation: { kind: "Custom" }, dependsOn: [], input: { marker: `${args.scenarioId}-replay` }, config: {} }],
      config: {},
    },
    functions: [uploadForStep(source, args.environmentRef, "work")],
  });
}

function uploadForStep(source, environmentRef, stepName) {
  return {
    site: { kind: "Step", stepName }, environmentRef,
    entryPointPath: source.entryPointPath, entryPointSymbol: source.entryPointSymbol,
    sources: [{ path: source.entryPointPath, contentBase64: source.bytes.toString("base64") }], dependencies: [],
  };
}

async function submitControlExecution(client, args, publicationRef, suffix) {
  return client.submitExecution({
    publicationRef,
    idempotencyKey: `${args.scenarioId}-${suffix}-${crypto.randomUUID().replaceAll("-", "")}`,
    input: { scenario: args.scenarioId, phase: suffix },
    metadata: { "matrix.scenario": args.scenarioId, "matrix.client": "typescript", "matrix.worker": args.worker, "matrix.feature": "control", "matrix.phase": suffix },
  });
}

async function waitForPauseGate(client, executionId, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  let lastExecutionStatus;
  let lastFirstStatus;
  let lastSecondStatus;

  while (Date.now() < deadline) {
    const observation = await client.observeExecution(executionId);
    const first = observation.steps.find((step) => step.name === "first");
    const second = observation.steps.find((step) => step.name === "second");

    lastExecutionStatus = observation.status;
    lastFirstStatus = first?.status;
    lastSecondStatus = second?.status;

    if (["Running", "Completed", "Failed"].includes(second?.status)) {
      console.log(
        `[matrix-typescript-client] PAUSE GATE FAILED second advanced. ExecutionStatus='${observation.status}' FirstStatus='${first?.status ?? "missing"}' SecondStatus='${second.status}'.`,
      );
      return false;
    }

    if (["Completed", "Failed", "Cancelled"].includes(observation.status)) {
      console.log(
        `[matrix-typescript-client] PAUSE GATE FAILED execution became terminal. ExecutionStatus='${observation.status}' FirstStatus='${first?.status ?? "missing"}' SecondStatus='${second?.status ?? "missing"}'.`,
      );
      return false;
    }

    // Published custom functions use the durable invocation adapter. The DAG step starts,
    // parks as WaitingForExternal, and the completed invocation continuation later makes
    // the same step Ready again. While paused, that Ready continuation must NOT be claimed.
    if (first?.status === "Ready") {
      await new Promise((resolve) => setTimeout(resolve, 750));
      const confirm = await client.observeExecution(executionId);
      const confirmFirst = confirm.steps.find((step) => step.name === "first");
      const confirmSecond = confirm.steps.find((step) => step.name === "second");
      const gated = !["Completed", "Failed", "Cancelled"].includes(confirm.status)
        && confirmFirst?.status === "Ready"
        && !["Running", "Completed", "Failed"].includes(confirmSecond?.status);

      console.log(
        `[matrix-typescript-client] PAUSE GATE PROOF executionStatus='${confirm.status}' firstStatus='${confirmFirst?.status ?? "missing"}' secondStatus='${confirmSecond?.status ?? "missing"}' gated='${gated}'.`,
      );
      return gated;
    }

    if (["Completed", "Failed"].includes(first?.status)) {
      console.log(
        `[matrix-typescript-client] PAUSE GATE FAILED first continuation advanced while paused. ExecutionStatus='${observation.status}' FirstStatus='${first.status}' SecondStatus='${second?.status ?? "missing"}'.`,
      );
      return false;
    }

    await new Promise((resolve) => setTimeout(resolve, 100));
  }

  console.log(
    `[matrix-typescript-client] PAUSE GATE TIMEOUT executionId='${executionId}' ExecutionStatus='${lastExecutionStatus ?? "unknown"}' FirstStatus='${lastFirstStatus ?? "missing"}' SecondStatus='${lastSecondStatus ?? "missing"}'.`,
  );
  return false;
}

async function waitForCompletedResult(client, executionId, timeoutMs) {
  const terminal = await waitForTerminal(client, executionId, timeoutMs);
  if (terminal.status !== "Completed") throw new Error(`Execution '${executionId}' ended as '${terminal.status}'.`);
  const result = await client.getExecutionResult(executionId);
  if (result.status !== "Completed") throw new Error(`Execution '${executionId}' result ended as '${result.status}'.`);
  return result;
}

function startControlWatch(client, executionId, timeoutMs) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  let activeSettled = false;
  let resolveActive;
  let rejectActive;
  const firstStepActive = new Promise((resolve, reject) => {
    resolveActive = resolve;
    rejectActive = reject;
  });

  const completion = (async () => {
    let snapshotObserved = false;
    let eventObserved = false;
    let resyncObserved = false;
    try {
      for await (const item of client.watchExecution({ executionId, includeInitialSnapshot: true }, controller.signal)) {
        snapshotObserved ||= item.kind === "Snapshot";
        eventObserved ||= item.kind === "Event";
        resyncObserved ||= item.kind === "ResyncRequired";
        if (!activeSettled && watchItemShowsActiveStep(item, "first")) {
          activeSettled = true;
          resolveActive(true);
        }
      }
      return { snapshotObserved, eventObserved, resyncObserved };
    } catch (error) {
      if (!activeSettled) {
        activeSettled = true;
        rejectActive(error);
      }
      throw error;
    } finally {
      clearTimeout(timer);
      if (!activeSettled) {
        activeSettled = true;
        rejectActive(new Error(`Execution '${executionId}' Watch ended before step 'first' became active.`));
      }
    }
  })();

  return { completion, firstStepActive };
}

function watchItemShowsActiveStep(item, stepName) {
  if (item.snapshot?.steps?.some((step) =>
    step.name === stepName && ["Running", "WaitingForExternal"].includes(step.status))) {
    return true;
  }

  if (item.kind !== "Event" || item.channel !== "Steps" ||
      item.payload === null || typeof item.payload !== "object" || Array.isArray(item.payload)) {
    return false;
  }

  return item.payload.name === stepName &&
    ["Running", "WaitingForExternal"].includes(item.payload.status);
}

async function waitForWatchActiveStep(activeStep, executionId, stepName, timeoutMs) {
  let timer;
  try {
    await Promise.race([
      activeStep,
      new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error(
          `Execution '${executionId}' Watch did not expose active step '${stepName}' within ${timeoutMs} ms.`)), timeoutMs);
      }),
    ]);
  } finally {
    if (timer) clearTimeout(timer);
  }
}

async function seedWaitingForInput(publicEndpoint, executionId, waitingKey, waitingStepName) {
  const endpoint = new URL(`/matrix/execution-control/${encodeURIComponent(executionId)}/wait-for-input`, publicEndpoint);
  const response = await fetch(endpoint, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ waitingKey, waitingStepName, reason: "matrix-control-e2e-await-approval" }),
  });
  if (!response.ok) throw new Error(`Matrix wait-for-input setup failed: HTTP ${response.status} ${await response.text()}`);
}

async function controlWorkerSources(root, worker) {
  if (worker === "dotnet") {
    const bytes = await fs.readFile(path.join(root, "dotnet", "Multiplexed.AI.Samples.PublishedFunctions.dll"));
    return {
      slow: { entryPointPath: "control-slow.dll", entryPointSymbol: "Multiplexed.AI.Samples.PublishedFunctions.Functions::PinStable", bytes },
      fast: { entryPointPath: "control-fast.dll", entryPointSymbol: "Multiplexed.AI.Samples.PublishedFunctions.Functions::Run", bytes },
    };
  }
  if (worker === "typescript") {
    return {
      slow: { entryPointPath: "control-slow.ts", entryPointSymbol: "run", bytes: Buffer.from("export async function run(inputs, context) { await new Promise(resolve => setTimeout(resolve, 8000)); return { success: true, payload: { marker: inputs?.marker ?? null, phase: 'slow' } }; }\n", "utf8") },
      fast: { entryPointPath: "control-fast.ts", entryPointSymbol: "run", bytes: Buffer.from("export function run(inputs, context) { return { success: true, payload: { marker: inputs?.marker ?? null, phase: 'fast' } }; }\n", "utf8") },
    };
  }
  if (worker === "python") {
    return {
      slow: { entryPointPath: "control_slow.py", entryPointSymbol: "run", bytes: Buffer.from("import time\ndef run(inputs, context):\n    time.sleep(8)\n    return {'success': True, 'payload': {'marker': inputs.get('marker'), 'phase': 'slow'}}\n", "utf8") },
      fast: { entryPointPath: "control_fast.py", entryPointSymbol: "run", bytes: Buffer.from("def run(inputs, context):\n    return {'success': True, 'payload': {'marker': inputs.get('marker'), 'phase': 'fast'}}\n", "utf8") },
    };
  }
  throw new Error(`Unsupported worker language '${worker}'.`);
}


async function runDependencyFirewall(args) {
  const sdkRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../../node/sdk");
  const packageDocument = JSON.parse(await fs.readFile(path.join(sdkRoot, "package.json"), "utf8"));
  const declaredDependencies = Object.keys(packageDocument.dependencies ?? {}).sort();
  const declaredDevDependencies = Object.keys(packageDocument.devDependencies ?? {}).sort();
  const forbiddenDeclaredDependencies = [...declaredDependencies, ...declaredDevDependencies]
    .filter((name) => name.startsWith("@multiplexed/") || name.startsWith("multiplexed-"))
    .sort();

  const distRoot = path.join(sdkRoot, "dist");
  const files = await walkFiles(distRoot);
  const importSpecifiers = new Set();
  const importPattern = /(?:\bfrom\s+|\bimport\s*\(\s*|\brequire\s*\(\s*)["']([^"']+)["']/g;
  for (const file of files.filter((item) => item.endsWith(".js") || item.endsWith(".d.ts"))) {
    const text = await fs.readFile(file, "utf8");
    for (const match of text.matchAll(importPattern)) importSpecifiers.add(match[1]);
  }
  const forbiddenDistImports = [...importSpecifiers]
    .filter((name) => name.startsWith("@multiplexed/") || name.startsWith("multiplexed-"))
    .sort();

  if (forbiddenDeclaredDependencies.length || forbiddenDistImports.length) {
    throw new Error(`External TypeScript SDK dependency firewall detected repository dependencies: ${[...forbiddenDeclaredDependencies, ...forbiddenDistImports].join(", ")}`);
  }

  await writeEvidence(args.evidence, {
    schemaVersion: 1,
    scenarioId: args.scenarioId,
    status: "passed",
    coverageTarget: "external-client-dependency-firewall",
    coverageValues: [],
    clientLanguage: "typescript",
    workerLanguage: null,
    topology: args.topology,
    provider: args.provider,
    runtimeProvider: args.runtimeProvider,
    workerExecutionProvider: args.workerExecutionProvider,
    artifactKind: "compiled-typescript-sdk",
    declaredDependencies,
    declaredDevDependencies,
    distImportSpecifiers: [...importSpecifiers].sort(),
    forbiddenDeclaredDependencies,
    forbiddenDistImports,
    evidence: [
      "package-dependencies-inspected",
      "compiled-dist-imports-inspected",
      "no-engine-runtime-dependency",
    ],
    recordedAtUtc: new Date().toISOString(),
  });
}

async function walkFiles(root) {
  const result = [];
  for (const entry of await fs.readdir(root, { withFileTypes: true })) {
    const full = path.join(root, entry.name);
    if (entry.isDirectory()) result.push(...await walkFiles(full));
    else if (entry.isFile()) result.push(full);
  }
  return result;
}

async function waitForTerminal(sdk, executionId, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const observation = await sdk.observeExecution(executionId);
    if (["Completed", "Failed", "Cancelled"].includes(observation.status)) return observation;
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
  throw new Error(`Execution '${executionId}' did not become terminal within ${timeoutMs} ms.`);
}

async function waitForActiveStep(sdk, executionId, stepName, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const observation = await sdk.observeExecution(executionId);
    if (["Completed", "Failed", "Cancelled"].includes(observation.status)) {
      throw new Error(`Execution '${executionId}' became terminal as '${observation.status}' before cancellation could be requested.`);
    }
    const step = observation.steps.find((item) => item.name === stepName);
    if (step && ["Running", "WaitingForExternal"].includes(step.status)) return observation;
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error(`Execution '${executionId}' did not expose active step '${stepName}' within ${timeoutMs} ms.`);
}

function samplePath(repoRoot, relative) {
  return process.env.MATRIX_SAMPLE_ROOT
    ? path.join(repoRoot, relative)
    : path.join(repoRoot, "implementations", "sdk", "samples", "published-functions", relative);
}

async function cancellationWorkerSource(repoRoot, worker) {
  if (worker === "dotnet") {
    const bytes = await fs.readFile(samplePath(repoRoot, "dotnet/Multiplexed.AI.Samples.PublishedFunctions.dll"));
    return { entryPointPath: "functions.dll", entryPointSymbol: "Multiplexed.AI.Samples.PublishedFunctions.Functions::PinStable", bytes };
  }
  if (worker === "typescript") {
    return {
      entryPointPath: "main.ts",
      entryPointSymbol: "run",
      bytes: Buffer.from("export async function run(inputs: unknown, context: unknown) { await new Promise(resolve => setTimeout(resolve, 8000)); return { success: true, payload: { cancellationSample: true } }; }\n", "utf8"),
    };
  }
  if (worker === "python") {
    return {
      entryPointPath: "main.py",
      entryPointSymbol: "run",
      bytes: Buffer.from("import time\ndef run(inputs, context):\n    time.sleep(8)\n    return {'success': True, 'payload': {'cancellationSample': True}}\n", "utf8"),
    };
  }
  throw new Error(`Unsupported worker language '${worker}'.`);
}

async function workerSource(repoRoot, worker) {
  if (worker === "python") return fromTextAbsolute(samplePath(repoRoot, "python/functions.py"), "functions.py", "run");
  if (worker === "typescript") return fromTextAbsolute(samplePath(repoRoot, "typescript/functions.ts"), "functions.ts", "run");
  if (worker === "dotnet") {
    const bytes = await fs.readFile(samplePath(repoRoot, "dotnet/Multiplexed.AI.Samples.PublishedFunctions.dll"));
    return { entryPointPath: "functions.dll", entryPointSymbol: "Multiplexed.AI.Samples.PublishedFunctions.Functions::Run", bytes };
  }
  throw new Error(`Unsupported worker language '${worker}'.`);
}

async function fromTextAbsolute(file, entryPointPath, entryPointSymbol) {
  return { entryPointPath, entryPointSymbol, bytes: Buffer.from(await fs.readFile(file, "utf8"), "utf8") };
}

async function parseArgs(values) {
  const read = (name) => {
    const index = values.indexOf(name);
    return index >= 0 && index + 1 < values.length ? values[index + 1] : undefined;
  };
  const required = (name) => read(name) ?? (() => { throw new Error(`Missing ${name}.`); })();
  const worker = required("--worker");
  const scenarioId = required("--scenario-id");
  const evidence = required("--evidence");
  const feature = read("--feature");
  const manifestPath = read("--manifest");
  if (manifestPath) {
    const manifest = JSON.parse(await fs.readFile(path.resolve(manifestPath), "utf8"));
    const environmentRef = read("--environment-ref") ?? manifest.environmentRefs?.[worker];
    if (!environmentRef) throw new Error(`Runtime manifest has no environment reference for '${worker}'.`);
    return {
      endpoint: read("--endpoint") ?? manifest.endpoint,
      worker,
      environmentRef,
      scenarioId,
      evidence,
      feature,
      token: read("--token") ?? manifest.bearerToken,
      accessContext: read("--access-context") ?? manifest.accessContext,
      accessContextHeader: manifest.accessContextHeader ?? "X-Access-Context",
      topology: manifest.topology ?? "local",
      provider: manifest.provider ?? "ProcessHostPool",
      runtimeProvider: manifest.runtimeProvider ?? manifest.provider ?? "ProcessHostPool",
      workerExecutionProvider: manifest.workerExecutionProvider ?? "TrustedProcess",
    };
  }
  return {
    endpoint: required("--endpoint"), worker,
    environmentRef: required("--environment-ref"), scenarioId, evidence, feature,
    token: read("--token"), accessContext: read("--access-context"),
    accessContextHeader: read("--access-context-header") ?? "X-Access-Context",
    topology: read("--topology") ?? "local", provider: read("--provider") ?? "ProcessHostPool",
    runtimeProvider: read("--runtime-provider") ?? "ProcessHostPool",
    workerExecutionProvider: read("--worker-execution-provider") ?? "TrustedProcess",
  };
}

async function writeEvidence(file, document) {
  await fs.mkdir(path.dirname(path.resolve(file)), { recursive: true });
  await fs.writeFile(path.resolve(file), JSON.stringify(document, null, 2) + "\n", "utf8");
}
