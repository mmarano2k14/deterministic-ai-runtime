import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const here = path.dirname(fileURLToPath(import.meta.url));
const worker = path.resolve(here, '../../workers/hosted_invocation/worker.mjs');
const runtimeSha = 'a'.repeat(64);
const toolchainContract = 'multiplexed-typescript-js-v1|typescript=5.8.3|sha256=dd17428736a07e1db1a138d8a14295ddb2699ba780ee15038acdd2c6da5373a0|target=ES2020|module=ESNext|resolution=Bundler|verbatim=true|rewriteRelativeImportExtensions=true|useDefineForClassFields=true|newLine=LF|sourceMaps=false|helpers=inline';
const loaderHash = crypto.createHash('sha256').update(fs.readFileSync(worker)).digest('hex');
const runtimeRef = 'typescript-node-fixed@typescript-js-v1-' + crypto.createHash('sha256')
  .update(toolchainContract + '|loader-sha256=' + loaderHash).digest('hex');
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
      target: { ...target },
      runtime: { reference: runtimeRef, executionLanguage: 'typescript', runtimeVersion: process.versions.node, runtimeSha256: runtimeSha },
      entryPointPath: 'main.ts', entryPointSymbol: symbol, sources: [file('main.ts', source)], dependencies
    }
  };
}

function dependency(name, source, version = '1.0.0') {
  return { name, version, files: [file('index.ts', source)] };
}

function lockedDependency(name, sources, entryPoint = 'src/index.ts', version = '2.0.1') {
  const sourceFiles = Object.entries(sources).sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0)
    .map(([relative, source]) => file(relative, source));
  const manifest = {
    schemaVersion: 1, packageName: name, version, entryPoint,
    files: sourceFiles.map(item => ({ path: item.path, sha256: item.sha256 }))
  };
  return {
    name, version,
    files: [file('bundle.manifest.json', JSON.stringify(manifest)), ...sourceFiles],
    package: { schemaVersion: 1, kind: 'NodeLockedBundle', manifestPath: 'bundle.manifest.json' }
  };
}

async function invoke(value, timeoutMs = 20000, workerPath = worker) {
  const child = spawn(process.execPath, [
    workerPath,
    `--runtime-reference=${value.code.runtime.reference}`, `--runtime-version=${process.versions.node}`,
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
  let timeout;
  const timer = new Promise((_, reject) => { timeout = setTimeout(() => {
    try { child.kill('SIGKILL'); } catch { }
    reject(new Error('worker timeout'));
  }, timeoutMs); });
  let outcome;
  try { outcome = await Promise.race([exit, timer]); } finally { clearTimeout(timeout); }
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

test('locked Node dependency executes only its manifest-pinned TypeScript closure', async () => {
  const source = `import { scale } from '#rules';
    export function run(inputs: { amount: number }, context: unknown) { return { success: true, payload: scale(inputs.amount) }; }`;
  const dep = lockedDependency('rules', {
    'src/index.ts': `export { scale } from './math.ts';`,
    'src/math.ts': `export function scale(value: number): number { return value * 4; }`
  });
  assert.equal(terminal(await invoke(request(source, { dependencies: [dep] }))).payload, 84);
});

test('locked Node dependency rejects a source digest mismatch before readiness', async () => {
  const dep = lockedDependency('rules', { 'src/index.ts': `export const value = 1;` });
  const manifest = JSON.parse(Buffer.from(dep.files[0].base64Url, 'base64url').toString('utf8'));
  manifest.files[0].sha256 = '0'.repeat(64);
  dep.files[0] = file('bundle.manifest.json', JSON.stringify(manifest));
  const result = await invoke(request(`import { value } from '#rules'; export function run() { return { success: true, payload: value }; }`,
    { dependencies: [dep] }));
  assert.notEqual(result.code, 0);
  assert.equal(result.frames.length, 0);
});

test('locked Node dependency rejects package identity substitution', async () => {
  const dep = lockedDependency('rules', { 'src/index.ts': `export const value = 1;` });
  const manifest = JSON.parse(Buffer.from(dep.files[0].base64Url, 'base64url').toString('utf8'));
  manifest.packageName = 'other';
  dep.files[0] = file('bundle.manifest.json', JSON.stringify(manifest));
  const result = await invoke(request(simple, { dependencies: [dep] }));
  assert.notEqual(result.code, 0);
  assert.equal(result.frames.length, 0);
});

test('locked Node dependency rejects undeclared extra source material', async () => {
  const dep = lockedDependency('rules', { 'src/index.ts': `export const value = 1;` });
  dep.files.push(file('src/extra.ts', 'export const extra = true;'));
  const result = await invoke(request(simple, { dependencies: [dep] }));
  assert.notEqual(result.code, 0);
  assert.equal(result.frames.length, 0);
});

test('locked Node dependency rejects a declaration-only entry point', async () => {
  const dep = lockedDependency('rules', { 'types.d.ts': `export declare const value: number;` }, 'types.d.ts');
  const result = await invoke(request(simple, { dependencies: [dep] }));
  assert.notEqual(result.code, 0);
  assert.equal(result.frames.length, 0);
});

test('TypeScript worker rejects another packaged dependency kind before readiness', async () => {
  const dep = lockedDependency('rules', { 'src/index.ts': `export const value = 1;` });
  dep.package.kind = 'PythonWheelBundle';
  const result = await invoke(request(simple, { dependencies: [dep] }));
  assert.notEqual(result.code, 0);
  assert.equal(result.frames.length, 0);
});

test('locked Node dependency rejects noncanonical manifest ordering', async () => {
  const dep = lockedDependency('rules', {
    'src/a.ts': 'export const a = 1;',
    'src/index.ts': `export { a } from './a.ts';`
  });
  const manifest = JSON.parse(Buffer.from(dep.files[0].base64Url, 'base64url').toString('utf8'));
  manifest.files.reverse();
  dep.files[0] = file('bundle.manifest.json', JSON.stringify(manifest));
  const result = await invoke(request(simple, { dependencies: [dep] }));
  assert.notEqual(result.code, 0);
  assert.equal(result.frames.length, 0);
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


for (const [name, body] of [
  ['numeric enum', 'enum Factor { Twice = 2 }; const factor = Factor.Twice;'],
  ['string enum', "enum Factor { Twice = 'two' }; const factor = Factor.Twice === 'two' ? 2 : 0;"],
  ['namespace', 'namespace MathRules { export const factor = 2; }; const factor = MathRules.factor;'],
  ['parameter property', 'class Rule { constructor(public factor: number) {} }; const factor = new Rule(2).factor;'],
  ['const enum', 'const enum Rule { Twice = 2 }; const factor = Rule.Twice;']
]) {
  test(`portable emit preserves transformed syntax without Node TypeScript flags: ${name}`, async () => {
    const source = `${body} export function run(inputs: { amount: number }, context: unknown) {
      return { success: true, payload: inputs.amount * factor };
    }`;
    assert.equal(terminal(await invoke(request(source))).payload, 42);
  });
}

test('relative .ts imports execute the emitted JavaScript closure', async () => {
  const value = request(`import { scale } from './lib/math.ts'; export function run(inputs: any, context: unknown) {
    return { success: true, payload: scale(inputs.amount) };
  }`);
  value.code.sources.push(file('lib/math.ts', 'export function scale(value: number) { return value * 2; }'));
  assert.equal(terminal(await invoke(value)).payload, 42);
});

test('re-exports preserve relative published module bindings', async () => {
  const value = request(`import { scale } from './exports.ts'; export function run(inputs: any, context: unknown) {
    return { success: true, payload: scale(inputs.amount) };
  }`);
  value.code.sources.push(file('exports.ts', "export { scale } from './math.ts';"));
  value.code.sources.push(file('math.ts', 'export function scale(value: number) { return value * 2; }'));
  assert.equal(terminal(await invoke(value)).payload, 42);
});

for (const expression of ["'./math.ts'", "relativePath"]) {
  test(`dynamic relative import is emitted without native TS: ${expression}`, async () => {
    const value = request(`export async function run(inputs: any, context: unknown) {
      const relativePath = './math.ts'; const rules = await import(${expression});
      return { success: true, payload: rules.scale(inputs.amount) };
    }`);
    value.code.sources.push(file('math.ts', 'export function scale(value: number) { return value * 2; }'));
    assert.equal(terminal(await invoke(value)).payload, 42);
  });
}

test('published alias subpaths with .ts extensions bind to emitted JavaScript', async () => {
  const dep = dependency('rules', "export { scale } from './math.ts';");
  dep.files.push(file('math.ts', 'export function scale(value: number) { return value * 2; }'));
  const value = request(`import { scale } from '#rules/math.ts'; export function run(inputs: any, context: unknown) {
    return { success: true, payload: scale(inputs.amount) };
  }`, { dependencies: [dep] });
  assert.equal(terminal(await invoke(value)).payload, 42);
});

test('declaration-only type imports require no native TS loader', async () => {
  const value = request(`import type { Input } from './types.d.ts';
    export function run(inputs: Input, context: unknown) { return { success: true, payload: inputs.amount * 2 }; }`);
  value.code.sources.push(file('types.d.ts', 'export interface Input { amount: number }'));
  assert.equal(terminal(await invoke(value)).payload, 42);
});

test('emitted modules execute as JavaScript with no inherited Node flags', async () => {
  const value = request(`export function run(inputs: unknown, context: unknown) {
    return { success: true, payload: { url: import.meta.url, args: process.execArgv } };
  }`);
  const result = terminal(await invoke(value));
  assert.match(result.payload.url, /main\.js\?implementation=/u);
  assert.deepEqual(result.payload.args, []);
});

test('runtime property ordering does not alter the exact runtime identity', async () => {
  const value = request(simple);
  value.code.runtime = Object.fromEntries(Object.entries(value.code.runtime).reverse());
  assert.equal(terminal(await invoke(value)).payload.value, 42);
});

test('a legacy unbound environment cannot silently select the new compiler', async () => {
  const value = request(simple);
  value.code.runtime.reference = 'typescript-node-fixed';
  const result = await invoke(value);
  assert.notEqual(result.code, 0);
  assert.equal(result.frames.length, 0);
});

test('runtime version mismatch is rejected even without a major-version allow-list', async () => {
  const value = request(simple);
  value.code.runtime.runtimeVersion = '99.1.1';
  const result = await invoke(value);
  assert.notEqual(result.code, 0);
  assert.equal(result.frames.length, 0);
});

for (const failure of ['missing', 'corrupt']) {
  test(`host compiler ${failure} is refused before readiness, without a registry fallback`, async () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'multiplexed-compiler-test-'));
    try {
      const script = path.join(root, 'worker.mjs');
      fs.copyFileSync(worker, script);
      if (failure === 'corrupt') {
        fs.mkdirSync(path.join(root, 'vendor'));
        fs.writeFileSync(path.join(root, 'vendor', 'typescript-5.8.3.cjs'), 'module.exports = {};');
      }
      const result = await invoke(request(simple), 20000, script);
      assert.notEqual(result.code, 0);
      assert.equal(result.frames.length, 0);
    } finally {
      fs.rmSync(root, { recursive: true, force: true });
    }
  });
}

test('a declaration file cannot be an executable entry point', async () => {
  const value = request('export declare function run(inputs: unknown, context: unknown): unknown;');
  value.code.entryPointPath = 'main.d.ts';
  value.code.sources[0].path = 'main.d.ts';
  const result = await invoke(value);
  assert.notEqual(result.code, 0);
  assert.equal(result.frames.length, 0);
});
