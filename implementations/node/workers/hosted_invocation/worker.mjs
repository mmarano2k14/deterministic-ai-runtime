/**
 * One published TypeScript function per private-pipe assignment.
 *
 * This loader executes trusted, host-approved TypeScript source. It is not an OS sandbox.
 * Published source/dependency bytes are hash-checked and materialized into a private
 * temporary workspace; no package manager, network fetch, mutable registry lookup, or
 * runtime DLL is involved.
 */
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { fork } from 'node:child_process';
import { createRequire } from 'node:module';
import vm from 'node:vm';

const MAX_REQUEST_BYTES = 50_331_648;
const MAX_INLINE_BYTES = 262_144;
const MAX_FILE_BYTES = 4_194_304;
const MAX_BUNDLE_BYTES = 33_554_432;
const MAX_FILES = 512;
const MAX_DEPENDENCIES = 64;
const HASH = /^[0-9a-f]{64}$/u;
const IDENTIFIER = /^[A-Za-z_$][A-Za-z0-9_$]*$/u;
const EXACT_VERSION = /^[0-9][A-Za-z0-9_.+\-]{0,127}$/u;
const DEPENDENCY_NAME = /^[A-Za-z][A-Za-z0-9_.\-]{0,63}$/u;
const IDENTITY_FIELDS = ['requestId', 'operationId', 'effectIdempotencyKey', 'workerId', 'tenantId', 'executionId', 'stepName'];
const TARGET_FIELDS = ['pipelineName', 'pipelineVersion', 'definitionSha256', 'publicationRef', 'publicationSha256',
  'implementationRef', 'implementationSha256', 'executionLanguage', 'environmentRef', 'environmentSha256'];
const RUNTIME_FIELDS = ['reference', 'executionLanguage', 'runtimeVersion', 'runtimeSha256'];

const COMPILER_SHA256 = 'dd17428736a07e1db1a138d8a14295ddb2699ba780ee15038acdd2c6da5373a0';
const TOOLCHAIN_CONTRACT = 'multiplexed-typescript-js-v1|typescript=5.8.3|sha256=dd17428736a07e1db1a138d8a14295ddb2699ba780ee15038acdd2c6da5373a0|target=ES2020|module=ESNext|resolution=Bundler|verbatim=true|rewriteRelativeImportExtensions=true|useDefineForClassFields=true|newLine=LF|sourceMaps=false|helpers=inline';
const REFERENCE_SUFFIX = '@typescript-js-v1-';
const COMPILER_PATH = fileURLToPath(new URL('./vendor/typescript-5.8.3.cjs', import.meta.url));
const MAX_EMITTED_BYTES = 67_108_864;

class ContractError extends Error {}

function sha256(bytes) { return crypto.createHash('sha256').update(bytes).digest('hex'); }

function requireCapabilities() {
  // Test actual APIs instead of an allow-list that excludes newer Node releases.
  requireCondition(typeof fs.rmSync === 'function' && typeof fs.mkdtempSync === 'function' &&
    typeof createRequire === 'function' && typeof vm.Script === 'function' && typeof fork === 'function',
    'The installed Node.js runtime lacks required hosted-loader APIs.');
}

function toolchainSuffix() {
  const loaderHash = sha256(fs.readFileSync(fileURLToPath(import.meta.url)));
  return REFERENCE_SUFFIX + sha256(Buffer.from(TOOLCHAIN_CONTRACT + '|loader-sha256=' + loaderHash, 'utf8'));
}

function readCompiler() {
  const bytes = fs.readFileSync(COMPILER_PATH);
  requireCondition(sha256(bytes) === COMPILER_SHA256, 'The host TypeScript compiler digest differs from the pinned toolchain.');
  return bytes;
}

function compilerFromVerifiedBytes(bytes) {
  // Execute exactly the verified bytes; do not resolve a global/local npm installation.
  const module = { exports: {} };
  const wrapper = new vm.Script('(function(exports, require, module, __filename, __dirname) {' +
    bytes.toString('utf8') + '\n})', { filename: COMPILER_PATH }).runInThisContext();
  wrapper(module.exports, createRequire(pathToFileURL(COMPILER_PATH)), module, COMPILER_PATH, path.dirname(COMPILER_PATH));
  requireCondition(module.exports.version === '5.8.3', 'Unexpected host TypeScript compiler version.');
  return module.exports;
}

function compileClosure(materialized) {
  const ts = compilerFromVerifiedBytes(readCompiler());
  let emittedBytes = 0;
  for (const file of materialized.files) {
    const raw = fs.readFileSync(file.path);
    requireCondition(sha256(raw) === file.sha256, 'Staged TypeScript source integrity mismatch.');
    // Declaration-only material can participate in type imports but is never executed.
    if (file.path.endsWith('.d.ts')) continue;
    const result = ts.transpileModule(raw.toString('utf8'), {
      fileName: file.path, reportDiagnostics: true,
      compilerOptions: {
        target: ts.ScriptTarget.ES2020, module: ts.ModuleKind.ESNext,
        moduleResolution: ts.ModuleResolutionKind.Bundler,
        verbatimModuleSyntax: true, rewriteRelativeImportExtensions: true,
        useDefineForClassFields: true, newLine: ts.NewLineKind.LineFeed,
        isolatedModules: true, sourceMap: false, inlineSourceMap: false,
        declaration: false, importHelpers: false, noEmitHelpers: false
      }
    });
    requireCondition(!(result.diagnostics ?? []).some(item => item.category === ts.DiagnosticCategory.Error),
      'Published TypeScript has invalid source syntax or compiler options.');
    const output = Buffer.from(result.outputText, 'utf8');
    emittedBytes += output.length;
    requireCondition(emittedBytes <= MAX_EMITTED_BYTES, 'Emitted JavaScript exceeds its closure bound.');
    fs.writeFileSync(file.path.slice(0, -3) + '.js', output, { mode: 0o600, flag: 'wx' });
  }
}


function requireCondition(condition, reason) {
  if (!condition) throw new ContractError(reason);
}

function exactObject(value, keys) {
  requireCondition(value !== null && typeof value === 'object' && !Array.isArray(value), 'Expected object.');
  const actual = Object.keys(value).sort();
  const expected = [...keys].sort();
  requireCondition(actual.length === expected.length && actual.every((x, i) => x === expected[i]), 'Unexpected object fields.');
  return value;
}

function text(value) {
  requireCondition(typeof value === 'string' && value.length > 0 && value.length <= 512 && value.trim() !== '' &&
    ![...value].some(c => { const n = c.charCodeAt(0); return n < 32 || (n >= 127 && n <= 159); }), 'Invalid identifier.');
  return value;
}

function digest(value) {
  requireCondition(typeof value === 'string' && HASH.test(value), 'Invalid digest.');
  return value;
}

function integer(value, low, high) {
  requireCondition(Number.isSafeInteger(value) && value >= low && value <= high, 'Invalid integer.');
  return value;
}

function plainJson(value, depth = 0, seen = new Set()) {
  requireCondition(depth <= 32, 'JSON nesting exceeds the inline contract.');
  if (value === null || typeof value === 'string' || typeof value === 'boolean') return;
  if (typeof value === 'number') {
    requireCondition(Number.isFinite(value), 'JSON numbers must be finite.');
    return;
  }
  requireCondition(typeof value === 'object', 'Only plain JSON values are supported.');
  requireCondition(!seen.has(value), 'JSON cycles are not supported.');
  seen.add(value);
  if (Array.isArray(value)) {
    requireCondition(depth < 32, 'JSON nesting exceeds the inline contract.');
    for (const item of value) plainJson(item, depth + 1, seen);
  } else {
    const prototype = Object.getPrototypeOf(value);
    requireCondition(prototype === Object.prototype || prototype === null, 'Only plain JSON objects are supported.');
    requireCondition(depth < 32, 'JSON nesting exceeds the inline contract.');
    for (const [key, item] of Object.entries(value)) {
      requireCondition(typeof key === 'string', 'JSON keys must be strings.');
      plainJson(item, depth + 1, seen);
    }
  }
  seen.delete(value);
}

function encode(value) {
  plainJson(value);
  const json = JSON.stringify(value);
  requireCondition(typeof json === 'string', 'Value is not JSON serializable.');
  return Buffer.from(json, 'utf8');
}

function parseDeadline(value) {
  text(value);
  requireCondition(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/u.test(value),
    'Deadline must be an explicit UTC timestamp.');
  const milliseconds = Date.parse(value);
  requireCondition(Number.isFinite(milliseconds), 'Invalid deadline.');
  return milliseconds;
}

function portablePath(value) {
  requireCondition(typeof value === 'string' && value.length > 0 && value.length <= 240, 'Invalid source path.');
  const parts = value.split('/');
  for (const part of parts) {
    requireCondition(part !== '' && part !== '.' && part !== '..' && !part.endsWith('.') && /^[A-Za-z0-9_.\-]+$/u.test(part),
      'Source paths must be portable relative paths.');
    const stem = part.split('.')[0].toUpperCase();
    requireCondition(!['CON', 'PRN', 'AUX', 'NUL'].includes(stem) && !/^(?:COM|LPT)[1-9]$/u.test(stem), 'Device path is forbidden.');
  }
  requireCondition(parts[parts.length - 1].endsWith('.ts'), 'Only .ts TypeScript source files are supported.');
  return value;
}

function decodeFile(value) {
  const item = exactObject(value, ['path', 'sha256', 'sizeBytes', 'base64Url']);
  const sourcePath = portablePath(item.path);
  const sha256 = digest(item.sha256);
  const size = integer(item.sizeBytes, 0, MAX_FILE_BYTES);
  requireCondition(typeof item.base64Url === 'string' && /^[A-Za-z0-9_\-]*$/u.test(item.base64Url) && item.base64Url.length % 4 !== 1,
    'Invalid base64url content.');
  const raw = Buffer.from(item.base64Url.replace(/-/gu, '+').replace(/_/gu, '/'), 'base64');
  const canonical = raw.toString('base64').replace(/=+$/u, '').replace(/\+/gu, '-').replace(/\//gu, '_');
  requireCondition(canonical === item.base64Url && raw.length === size &&
    crypto.createHash('sha256').update(raw).digest('hex') === sha256, 'Source content integrity mismatch.');
  const decoded = raw.toString('utf8');
  requireCondition(Buffer.from(decoded, 'utf8').equals(raw), 'Source must be canonical UTF-8.');
  return { path: sourcePath, raw };
}

function ensureDirectory(root, relative) {
  const target = path.join(root, ...relative.split('/'));
  fs.mkdirSync(path.dirname(target), { recursive: true, mode: 0o700 });
  return target;
}

function materialize(bundle) {
  const dependencies = bundle.dependencies;
  requireCondition(Array.isArray(dependencies) && dependencies.length <= MAX_DEPENDENCIES, 'Invalid dependency list.');
  const sourceFiles = new Map();
  const dependencyFiles = new Map();
  const seenPaths = new Set();
  let total = 0;
  let count = 0;

  function collect(files, owner, targetMap) {
    requireCondition(Array.isArray(files) && files.length > 0 && files.length <= MAX_FILES, 'An explicit file list is required.');
    for (const file of files) {
      const decoded = decodeFile(file);
      const key = `${owner}:${decoded.path.toLowerCase()}`;
      requireCondition(!seenPaths.has(key), 'Duplicate or case-ambiguous source path.');
      seenPaths.add(key);
      total += decoded.raw.length;
      count += 1;
      requireCondition(total <= MAX_BUNDLE_BYTES && count <= MAX_FILES, 'Source closure exceeds its bound.');
      targetMap.set(decoded.path, decoded.raw);
    }
  }

  collect(bundle.sources, 'source', sourceFiles);
  const aliases = {};
  const seenDependencies = new Set();
  for (const dependency of dependencies) {
    const item = exactObject(dependency, ['name', 'version', 'files']);
    const name = text(item.name);
    requireCondition(DEPENDENCY_NAME.test(name) && !seenDependencies.has(name.toLowerCase()), 'Ambiguous dependency name.');
    requireCondition(typeof item.version === 'string' && EXACT_VERSION.test(item.version), 'An exact dependency version is required.');
    seenDependencies.add(name.toLowerCase());
    const files = new Map();
    collect(item.files, `dependency:${name.toLowerCase()}`, files);
    requireCondition(files.has('index.ts'), 'A TypeScript dependency must publish index.ts as its explicit entry point.');
    dependencyFiles.set(name, files);
    aliases[`#${name}`] = `./.dependencies/${name}/index.js`;
    aliases[`#${name}/*.ts`] = `./.dependencies/${name}/*.js`;
    aliases[`#${name}/*`] = `./.dependencies/${name}/*`;
  }

  const entryPointPath = portablePath(bundle.entryPointPath);
  requireCondition(sourceFiles.has(entryPointPath), 'Entry point must be in the published source files.');
  requireCondition(typeof bundle.entryPointSymbol === 'string' && IDENTIFIER.test(bundle.entryPointSymbol),
    'A simple TypeScript entry-point symbol is required.');

  requireCondition(!entryPointPath.endsWith('.d.ts'), 'A declaration file cannot be an executable entry point.');
  const compiledFiles = [];
  const workspace = fs.mkdtempSync(path.join(os.tmpdir(), 'multiplexed-ai-ts-'));
  try {
    for (const [relative, raw] of sourceFiles) {
      const target = ensureDirectory(workspace, relative);
      fs.writeFileSync(target, raw, { mode: 0o600, flag: 'wx' });
      compiledFiles.push({ path: target, sha256: sha256(raw) });
    }
    for (const [name, files] of dependencyFiles) {
      for (const [relative, raw] of files) {
        const target = ensureDirectory(workspace, `.dependencies/${name}/${relative}`);
        fs.writeFileSync(target, raw, { mode: 0o600, flag: 'wx' });
        compiledFiles.push({ path: target, sha256: sha256(raw) });
      }
    }
    const packageJson = JSON.stringify({ type: 'module', imports: aliases });
    fs.writeFileSync(path.join(workspace, 'package.json'), packageJson, { mode: 0o600, flag: 'wx' });
    return { workspace, entryPath: path.join(workspace, ...entryPointPath.split('/')), entryPointSymbol: bundle.entryPointSymbol, files: compiledFiles };
  } catch (error) {
    fs.rmSync(workspace, { recursive: true, force: true, maxRetries: 3, retryDelay: 50 });
    throw error;
  }
}

function parseArgs() {
  const values = {};
  for (const arg of process.argv.slice(2)) {
    const match = /^--([a-z0-9-]+)=(.*)$/u.exec(arg);
    requireCondition(match !== null && !(match[1] in values), 'Invalid worker argument.');
    values[match[1]] = match[2];
  }
  const expected = ['runtime-reference', 'runtime-version', 'runtime-sha256', 'heartbeat-ms'];
  requireCondition(Object.keys(values).length === expected.length && expected.every(x => x in values), 'Missing worker argument.');
  const reference = text(values['runtime-reference']);
  requireCondition(reference.toLowerCase() !== 'latest' && !reference.includes('://'), 'An exact runtime reference is required.');
  const version = text(values['runtime-version']);
  const match = /^(\d+)\.(\d+)\.(\d+)$/u.exec(version);
  requireCondition(match !== null && version === process.versions.node, 'The installed Node.js version differs from the profile.');
  requireCondition(Number(match[1]) > 0, 'An exact stable Node.js runtime version is required.');
  requireCapabilities();
  requireCondition(reference.endsWith(toolchainSuffix()), 'The exact TypeScript loader/compiler contract is not pinned.');
  readCompiler();
  const heartbeatMs = Number(values['heartbeat-ms']);
  integer(heartbeatMs, 50, 5000);
  return {
    runtime: { reference, executionLanguage: 'typescript', runtimeVersion: version, runtimeSha256: digest(values['runtime-sha256']) },
    heartbeatMs
  };
}

function readRequest() {
  const raw = fs.readFileSync(0);
  requireCondition(raw.length <= MAX_REQUEST_BYTES + 1 && raw[raw.length - 1] === 10, 'A bounded newline-terminated request is required.');
  const body = raw.subarray(0, raw.length - 1);
  requireCondition(!body.includes(10), 'Only one request is allowed per process.');
  const value = JSON.parse(body.toString('utf8'));
  requireCondition(value !== null && typeof value === 'object' && !Array.isArray(value), 'The request must be a JSON object.');
  return value;
}

function validateRequest(request, runtime) {
  exactObject(request, [...IDENTITY_FIELDS, 'protocolVersion', 'type', 'epoch', 'generation', 'deadlineUtc', 'traceParent', 'inputs', 'code']);
  requireCondition(request.protocolVersion === 1 && request.type === 'invoke', 'Unsupported protocol.');
  for (const name of IDENTITY_FIELDS) text(request[name]);
  integer(request.epoch, 1, Number.MAX_SAFE_INTEGER);
  integer(request.generation, 0, 2_147_483_647);
  if (request.traceParent !== null) text(request.traceParent);
  const deadline = parseDeadline(request.deadlineUtc);
  requireCondition(deadline > Date.now(), 'Invocation deadline has elapsed.');
  requireCondition(request.inputs !== null && typeof request.inputs === 'object' && !Array.isArray(request.inputs), 'Inputs must be a JSON object.');
  requireCondition(encode(request.inputs).length <= MAX_INLINE_BYTES, 'Inputs exceed their inline limit.');

  const bundle = exactObject(request.code, ['target', 'runtime', 'entryPointPath', 'entryPointSymbol', 'sources', 'dependencies']);
  const target = exactObject(bundle.target, TARGET_FIELDS);
  for (const name of TARGET_FIELDS) name.endsWith('Sha256') ? digest(target[name]) : text(target[name]);
  requireCondition(target.executionLanguage === 'typescript', 'This worker executes TypeScript only.');
  exactObject(bundle.runtime, RUNTIME_FIELDS);
  requireCondition(RUNTIME_FIELDS.every(key => bundle.runtime[key] === runtime[key]), 'The exact configured Node.js runtime is required.');
  return { deadline, materialized: materialize(bundle) };
}

function deepFreeze(value) {
  if (value !== null && typeof value === 'object' && !Object.isFrozen(value)) {
    Object.freeze(value);
    for (const child of Object.values(value)) deepFreeze(child);
  }
  return value;
}

function context(request) {
  const values = {};
  for (const key of [...IDENTITY_FIELDS, 'epoch', 'generation', 'deadlineUtc', 'traceParent']) values[key] = request[key];
  values.target = { ...request.code.target };
  return deepFreeze(values);
}

function validateResult(value) {
  const result = exactObject(value, ['success', 'payload']);
  requireCondition(typeof result.success === 'boolean', 'Success must be an explicit boolean.');
  requireCondition(encode(result.payload).length <= MAX_INLINE_BYTES, 'Result exceeds the inline payload limit.');
  return result;
}

function emitter(request) {
  let terminal = false;
  const identity = {
    protocolVersion: request.protocolVersion,
    requestId: request.requestId,
    operationId: request.operationId,
    workerId: request.workerId,
    epoch: request.epoch
  };
  return (type, result = null) => {
    if (terminal) return;
    const frame = { ...identity, type };
    if (type === 'result') {
      requireCondition(result !== null, 'Missing terminal result.');
      Object.assign(frame, result);
      terminal = true;
    }
    const bytes = Buffer.concat([encode(frame), Buffer.from('\n')]);
    requireCondition(bytes.length <= 1_048_576, 'Protocol frame exceeds its bound.');
    process.stdout.write(bytes);
  };
}

async function executeInChild(materialized, request) {
  const environment = { ...process.env };
  for (const key of Object.keys(environment)) {
    if (['NODE_OPTIONS', 'NODE_PATH'].includes(key.toUpperCase())) delete environment[key];
  }
  return await new Promise((resolve, reject) => {
    const child = fork(fileURLToPath(import.meta.url), ['--published-child'], {
      stdio: ['ignore', 'pipe', 'pipe', 'ipc'],
      env: environment,
      execArgv: [] // Native TypeScript flags and parent loader hooks must not reach the child.
    });
    let settled = false;
    let diagnosticBytes = 0;
    const fail = error => {
      if (settled) return;
      settled = true;
      try { child.kill('SIGKILL'); } catch { }
      reject(error);
    };
    const drain = stream => stream.on('data', chunk => {
      diagnosticBytes += chunk.length;
      if (diagnosticBytes > 65_536) fail(new Error('Published TypeScript diagnostics exceeded their bound.'));
    });
    drain(child.stdout);
    drain(child.stderr);
    child.once('error', fail);
    child.once('exit', code => {
      if (!settled) fail(new Error(`Published TypeScript child exited before a result (${code}).`));
    });
    child.once('message', message => {
      if (settled) return;
      if (message?.type === 'published-result') {
        try {
          const result = validateResult(message.result);
          settled = true;
          try { child.disconnect(); } catch { }
          try { child.kill(); } catch { }
          resolve(result);
        } catch (error) {
          fail(error);
        }
      } else {
        fail(message?.type === 'published-contract-error' ? new ContractError('Published TypeScript contract failed.') :
          new Error('Published TypeScript execution failed.'));
      }
    });
    child.send({
      type: 'execute-published',
      entryPath: materialized.entryPath,
      entryPointSymbol: materialized.entryPointSymbol,
      implementationSha256: request.code.target.implementationSha256,
      inputs: request.inputs,
      context: context(request),
      files: materialized.files
    });
  });
}

async function childMain() {
  const send = typeof process.send === 'function' ? process.send.bind(process) : null;
  if (send === null) return 70;
  try {
    delete process.send;
    process.stdout.write = process.stderr.write.bind(process.stderr);
    console.log = (...args) => console.error(...args);
    console.info = (...args) => console.error(...args);
    const message = await new Promise((resolve, reject) => {
      process.once('message', resolve);
      process.once('disconnect', () => reject(new Error('Parent disconnected.')));
    });
    exactObject(message, ['type', 'entryPath', 'entryPointSymbol', 'implementationSha256', 'inputs', 'context', 'files']);
    requireCondition(message.type === 'execute-published' && path.isAbsolute(message.entryPath) &&
      IDENTIFIER.test(message.entryPointSymbol) && HASH.test(message.implementationSha256), 'Invalid child execution envelope.');
    requireCondition(Array.isArray(message.files) && message.files.length > 0 && message.files.length <= MAX_FILES,
      'Invalid private compiler closure.');
    compileClosure(message);
    const moduleUrl = `${pathToFileURL(message.entryPath.slice(0, -3) + '.js').href}?implementation=${message.implementationSha256}`;
    const module = await import(moduleUrl);
    const fn = module[message.entryPointSymbol];
    requireCondition(typeof fn === 'function' && fn.length === 2, 'The entry point must be an exported two-argument function.');
    const value = await fn(message.inputs, deepFreeze(message.context));
    const result = validateResult(value);
    await new Promise(resolve => send({ type: 'published-result', result }, resolve));
    try { process.disconnect(); } catch { }
    return 0;
  } catch (error) {
    const type = error instanceof ContractError || error instanceof SyntaxError || error instanceof URIError ?
      'published-contract-error' : 'published-technical-error';
    try { await new Promise(resolve => send({ type }, resolve)); } catch { }
    try { process.disconnect(); } catch { }
    return type === 'published-contract-error' ? 65 : 70;
  }
}

async function main() {
  let workspace = null;
  let heartbeat = null;
  let deadlineTimer = null;
  try {
    const configuration = parseArgs();
    const request = readRequest();
    const validated = validateRequest(request, configuration.runtime);
    workspace = validated.materialized.workspace;
    const remaining = validated.deadline - Date.now();
    requireCondition(remaining > 0, 'Invocation deadline has elapsed.');

    const emit = emitter(request);
    emit('ready');
    heartbeat = setInterval(() => emit('heartbeat'), configuration.heartbeatMs);
    deadlineTimer = setTimeout(() => process.exit(124), remaining);

    const result = await executeInChild(validated.materialized, request);
    clearInterval(heartbeat); heartbeat = null;
    clearTimeout(deadlineTimer); deadlineTimer = null;
    requireCondition(Date.now() < validated.deadline, 'Invocation deadline elapsed before result acceptance.');
    fs.rmSync(workspace, { recursive: true, force: true, maxRetries: 3, retryDelay: 50 });
    workspace = null;
    emit('result', result);
    return 0;
  } catch (error) {
    if (error instanceof ContractError || error instanceof SyntaxError || error instanceof URIError) {
      process.stderr.write('typescript-worker: invalid_contract\n');
      return 65;
    }
    process.stderr.write('typescript-worker: technical_failure\n');
    return 70;
  } finally {
    if (heartbeat !== null) clearInterval(heartbeat);
    if (deadlineTimer !== null) clearTimeout(deadlineTimer);
    if (workspace !== null) {
      try { fs.rmSync(workspace, { recursive: true, force: true, maxRetries: 3, retryDelay: 50 }); } catch { }
    }
  }
}

process.exitCode = process.argv.includes('--published-child') ? await childMain() : await main();
