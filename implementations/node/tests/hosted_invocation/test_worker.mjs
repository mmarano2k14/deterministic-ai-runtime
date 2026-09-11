import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const here = path.dirname(fileURLToPath(import.meta.url));
const worker = path.resolve(here, '../../workers/hosted_invocation/worker.mjs');
const runtimeSha = 'a'.repeat(64);
const target = {
  pipelineName: 'pipeline', pipelineVersion: '1', definitionSha256: 'b'.repeat(64),
  publicationRef: 'pub-' + 'c'.repeat(64), publicationSha256: 'c'.repeat(64),
  implementationRef: 'impl-' + 'd'.repeat(64), implementationSha256: 'd'.repeat(64),
  executionLanguage: 'typescript', environmentRef: 'env-' + 'e'.repeat(64), environmentSha256: 'e'.repeat(64)
};

function file(relative, source) {
  const bytes = Buffer.from(source, 'utf8');
  return {
    path: relative,
    sha256: crypto.createHash('sha256').update(bytes).digest('hex'),
    sizeBytes: bytes.length,
    base64Url: bytes.toString('base64url')
  };
}

function request(source, { dependencies = [], inputs = { amount: 21 }, deadlineMs = 15000, symbol = 'run' } = {}) {
  return {
    protocolVersion: 1, type: 'invoke', requestId: 'request-1', operationId: 'operation-1',
    effectIdempotencyKey: 'effect-1', workerId: 'worker-1', epoch: 1, tenantId: 'tenant-1',
    executionId: 'execution-1', stepName: 'step-1', generation: 0,
    deadlineUtc: new Date(Date.now() + deadlineMs).toISOString(), traceParent: null, inputs,
    code: {
      target,
      runtime: { reference: 'typescript-node-fixed', executionLanguage: 'typescript', runtimeVersion: process.versions.node, runtimeSha256: runtimeSha },
      entryPointPath: 'main.ts', entryPointSymbol: symbol, sources: [file('main.ts', source)], dependencies
    }
  };
}

function dependency(name, source, version = '1.0.0') {
  return { name, version, files: [file('index.ts', source)] };
}

async function invoke(value, timeoutMs = 20000) {
  const child = spawn(process.execPath, [
    '--no-warnings', '--experimental-strip-types', '--experimental-transform-types', worker,
    '--runtime-reference=typescript-node-fixed', `--runtime-version=${process.versions.node}`,
    `--runtime-sha256=${runtimeSha}`, '--heartbeat-ms=100'
  ], { stdio: ['pipe', 'pipe', 'pipe'], env: process.platform === 'win32' ? { SystemRoot: process.env.SystemRoot } : {} });
  const stdout = [];
  const stderr = [];
  child.stdout.on('data', chunk => stdout.push(chunk));
  child.stderr.on('data', chunk => stderr.push(chunk));
  child.stdin.end(Buffer.from(JSON.stringify(value) + '\n', 'utf8'));
  const exit = new Promise((resolve, reject) => {
    child.once('error', reject);
    child.once('exit', (code, signal) => resolve({ code, signal }));
  });
  const timer = new Promise((_, reject) => setTimeout(() => {
    try { child.kill('SIGKILL'); } catch { }
    reject(new Error('worker timeout'));
  }, timeoutMs));
  const outcome = await Promise.race([exit, timer]);
  const lines = Buffer.concat(stdout).toString('utf8').trim().split(/\r?\n/u).filter(Boolean).map(x => JSON.parse(x));
  return { ...outcome, frames: lines, stderr: Buffer.concat(stderr).toString('utf8') };
}

function terminal(result) {
  assert.equal(result.code, 0, result.stderr);
  assert.equal(result.frames[0].type, 'ready');
  const frame = result.frames.at(-1);
  assert.equal(frame.type, 'result');
  return frame;
}

const simple = `export function run(inputs: { amount: number }, context: Readonly<Record<string, unknown>>) {
  return { success: true, payload: { value: inputs.amount * 2 } };
}`;

test('synchronous TypeScript executes real typed source', async () => {
  const frame = terminal(await invoke(request(simple)));
  assert.equal(frame.success, true);
  assert.deepEqual(frame.payload, { value: 42 });
});

test('asynchronous TypeScript is awaited', async () => {
  const source = `export async function run(inputs: { amount: number }, context: unknown) {
    await new Promise(resolve => setTimeout(resolve, 20));
    return { success: true, payload: inputs.amount + 1 };
  }`;
  assert.equal(terminal(await invoke(request(source))).payload, 22);
});

test('explicit business failure remains a typed result', async () => {
  const source = `export function run(inputs: unknown, context: unknown) { return { success: false, payload: { reason: 'decision' } }; }`;
  const frame = terminal(await invoke(request(source)));
  assert.equal(frame.success, false);
  assert.deepEqual(frame.payload, { reason: 'decision' });
});

test('vendored dependency is imported through an exact host-generated alias', async () => {
  const source = `import { scale } from '#rules';
    export function run(inputs: { amount: number }, context: unknown) { return { success: true, payload: scale(inputs.amount) }; }`;
  const dep = dependency('rules', `export function scale(value: number): number { return value * 3; }`);
  assert.equal(terminal(await invoke(request(source, { dependencies: [dep] }))).payload, 63);
});

test('console and process stdout cannot forge protocol frames', async () => {
  const source = `export function run(inputs: unknown, context: unknown) {
    console.log('ordinary log'); process.stdout.write('raw-ish stdout\\n');
    return { success: true, payload: 42 };
  }`;
  const result = await invoke(request(source));
  const frame = terminal(result);
  assert.equal(frame.payload, 42);
  assert.equal(result.frames.filter(x => x.type === 'result').length, 1);
});

test('heartbeats keep an asynchronous invocation live', async () => {
  const source = `export async function run(inputs: unknown, context: unknown) {
    await new Promise(resolve => setTimeout(resolve, 650)); return { success: true, payload: 42 };
  }`;
  const result = await invoke(request(source));
  terminal(result);
  assert.ok(result.frames.filter(x => x.type === 'heartbeat').length >= 3);
});

test('portable context excludes lease, permissions and code bundle', async () => {
  const source = `export function run(inputs: unknown, context: any) { return { success: true, payload: context }; }`;
  const frame = terminal(await invoke(request(source)));
  assert.equal(frame.payload.operationId, 'operation-1');
  assert.equal(frame.payload.effectIdempotencyKey, 'effect-1');
  assert.equal(frame.payload.target.publicationRef, target.publicationRef);
  assert.equal('lease' in frame.payload, false);
  assert.equal('permissions' in frame.payload, false);
  assert.equal('code' in frame.payload, false);
});

test('separate assignments do not reuse module globals', async () => {
  const source = `let counter = 0; export function run(inputs: unknown, context: unknown) { counter += 1; return { success: true, payload: counter }; }`;
  assert.equal(terminal(await invoke(request(source))).payload, 1);
  assert.equal(terminal(await invoke(request(source))).payload, 1);
});

for (const [name, source] of [
  ['exception', `export function run(inputs: unknown, context: unknown) { throw new Error('private data'); }`],
  ['wrong shape', `export function run(inputs: unknown, context: unknown) { return true; }`],
  ['wrong success type', `export function run(inputs: unknown, context: unknown) { return { success: 1, payload: 42 }; }`],
  ['nonfinite payload', `export function run(inputs: unknown, context: unknown) { return { success: true, payload: Number.NaN }; }`],
  ['extra result field', `export function run(inputs: unknown, context: unknown) { return { success: true, payload: 42, park: true }; }`],
  ['wrong arity', `export function run() { return { success: true, payload: 42 }; }`],
  ['missing import', `import missing from './missing.ts'; export function run(inputs: unknown, context: unknown) { return { success: true, payload: missing }; }`],
  ['syntax', `export function run(`]
]) {
  test(`source or contract error is not an authoritative result: ${name}`, async () => {
    const result = await invoke(request(source));
    assert.notEqual(result.code, 0);
    assert.equal(result.frames.some(x => x.type === 'result'), false);
  });
}

test('corrupt source is refused before readiness', async () => {
  const value = request(simple);
  value.code.sources[0].sha256 = '0'.repeat(64);
  const result = await invoke(value);
  assert.notEqual(result.code, 0);
  assert.equal(result.frames.length, 0);
});

test('unicode and null round-trip as JSON only', async () => {
  const source = `export function run(inputs: unknown, context: unknown) { return { success: true, payload: inputs }; }`;
  const frame = terminal(await invoke(request(source, { inputs: { name: 'Liège ไทย', optional: null } })));
  assert.deepEqual(frame.payload, { name: 'Liège ไทย', optional: null });
});

test('wrong language is refused before readiness', async () => {
  const value = request(simple);
  value.code.target.executionLanguage = 'python';
  const result = await invoke(value);
  assert.notEqual(result.code, 0);
  assert.equal(result.frames.length, 0);
});

test('expired request is refused before readiness', async () => {
  const value = request(simple);
  value.deadlineUtc = new Date(Date.now() - 1000).toISOString();
  const result = await invoke(value);
  assert.notEqual(result.code, 0);
  assert.equal(result.frames.length, 0);
});

assert.ok(fs.existsSync(worker));
