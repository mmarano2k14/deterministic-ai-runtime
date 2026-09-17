import fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  AiSdkClient,
  AiSdkMcpHttpTransport,
  AiSdkStaticCredentialProvider,
} from "../../../node/sdk/dist/index.js";

const args = await parseArgs(process.argv.slice(2));
const root = process.env.MATRIX_FIXTURE_ROOT ?? path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../../..");
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
const source = await workerSource(root, args.worker);
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
  publicationRef: publication.publicationRef,
  executionId: submitted.executionId,
  terminalStatus: result.status,
  evidence: ["publish", "submit", "observe", "terminal-result", "public-execution-id"],
  recordedAtUtc: new Date().toISOString(),
});

async function waitForTerminal(sdk, executionId, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const observation = await sdk.observeExecution(executionId);
    if (["Completed", "Failed", "Cancelled"].includes(observation.status)) return observation;
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
  throw new Error(`Execution '${executionId}' did not become terminal within ${timeoutMs} ms.`);
}

async function workerSource(repoRoot, worker) {
  const fixture = (relative) => process.env.MATRIX_FIXTURE_ROOT
    ? path.join(repoRoot, relative)
    : path.join(repoRoot, "implementations", "matrix", "fixtures", relative);
  if (worker === "python") {
    return fromTextAbsolute(fixture("python-worker/main.py"), "main.py", "run");
  }
  if (worker === "typescript") {
    return fromTextAbsolute(fixture("typescript-worker/main.ts"), "main.ts", "run");
  }
  if (worker === "dotnet") {
    const bytes = await fs.readFile(fixture("dotnet-worker/Multiplexed.AI.Matrix.Worker.dll"));
    return { entryPointPath: "functions.dll", entryPointSymbol: "Multiplexed.AI.Matrix.Worker.Functions::Run", bytes };
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
      token: read("--token") ?? manifest.bearerToken,
      accessContext: read("--access-context") ?? manifest.accessContext,
      accessContextHeader: manifest.accessContextHeader ?? "X-Access-Context",
      topology: manifest.topology ?? "local",
      provider: manifest.provider ?? "ProcessHostPool",
    };
  }
  return {
    endpoint: required("--endpoint"), worker,
    environmentRef: required("--environment-ref"), scenarioId, evidence,
    token: read("--token"), accessContext: read("--access-context"),
    accessContextHeader: read("--access-context-header") ?? "X-Access-Context",
    topology: read("--topology") ?? "local", provider: read("--provider") ?? "ProcessHostPool",
  };
}

async function writeEvidence(file, document) {
  await fs.mkdir(path.dirname(path.resolve(file)), { recursive: true });
  await fs.writeFile(path.resolve(file), JSON.stringify(document, null, 2) + "\n", "utf8");
}
