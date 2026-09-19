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
const source = args.feature === "cancellation"
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
