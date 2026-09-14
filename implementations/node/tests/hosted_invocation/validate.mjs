/** Run the installed Node executable without a major-version allow-list; refuse empty/skipped .NET validation. */
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '../../../..');
const args = process.argv.slice(2);
if (args.some(value => value !== '--node-only') || args.length > 1) {
  throw new Error('Usage: node validate.mjs [--node-only]');
}
const nodeOnly = args.includes('--node-only');

async function run(command, argv, options = {}) {
  return await new Promise((resolve, reject) => {
    const child = spawn(command, argv, { stdio: ['inherit', 'pipe', 'pipe'], ...options });
    const chunks = [];
    child.stdout.on('data', value => { chunks.push(value); process.stdout.write(value); });
    child.stderr.on('data', value => process.stderr.write(value));
    child.once('error', reject);
    child.once('close', (code, signal) => code === 0 ? resolve(Buffer.concat(chunks).toString('utf8')) :
      reject(new Error(`${command} exited with ${code ?? signal}`)));
  });
}

console.log(`Hosted TypeScript compatibility: Node.js ${process.versions.node}`);
const output = await run(process.execPath, ['--test', '--test-reporter=tap', path.join(here, 'test_worker.mjs')]);
function count(name) {
  const match = new RegExp(`^# ${name} (\\d+)\\s*$`, 'm').exec(output);
  if (!match) throw new Error(`Node test summary is missing ${name}.`);
  return Number(match[1]);
}
const nodeResults = { total: count('tests'), passed: count('pass'), failed: count('fail'), skipped: count('skipped'), cancelled: count('cancelled') };
if (nodeResults.total < 38 || nodeResults.passed !== nodeResults.total || nodeResults.failed || nodeResults.skipped || nodeResults.cancelled) {
  throw new Error('Node compatibility validation is empty, incomplete or unsuccessful.');
}
if (nodeOnly) {
  console.log('Node-only validation completed. .NET profile, publication/DAG and policy tests were NOT executed.');
} else {
  const dotnet = process.platform === 'win32' ? 'dotnet.exe' : 'dotnet';
  const testProject = path.join(root, 'implementations', 'dotnet', 'Tests', 'Multiplexed.AI.Tests', 'Multiplexed.AI.Tests.csproj');
  if (!fs.existsSync(testProject)) throw new Error('Run this validator inside the complete repository.');
  const runDirectory = path.join(root, 'implementations', 'dotnet', 'TestResults', 'sdk-closure', 'node-' + crypto.randomUUID());
  fs.mkdirSync(runDirectory, { recursive: true });
  const trxPath = path.join(runDirectory, 'sdk-closure-node-compatibility.trx');
  const filter = 'FullyQualifiedName~Multiplexed.AI.Tests.Runtime.Invocation.Workers.TypeScript|' +
    'FullyQualifiedName=Multiplexed.AI.Tests.Runtime.Invocation.Workers.Policies.AiHostedConcurrencyPolicyRealProcessTests.TypeScript_Process_Executes_A_Custom_Concurrency_Policy';
  const environment = { ...process.env, MULTIPLEXED_NODE_EXECUTABLE: process.execPath };
  console.log(`TRX output: ${trxPath}`);
  await run(dotnet, ['test', testProject, '--filter', filter, '--logger',
    'trx;LogFileName=sdk-closure-node-compatibility.trx', '--results-directory', runDirectory], { env: environment });
  const trx = fs.readFileSync(trxPath, 'utf8');
  // Inspect individual outcomes: global notExecuted counters can hide dynamically skipped theories.
  const rows = [...trx.matchAll(/<(?:\w+:)?UnitTestResult\b[^>]*>/gu)].map(match => match[0]);
  if (rows.length < 76 || rows.some(row => !/\boutcome="Passed"/u.test(row))) {
    throw new Error('The .NET TypeScript suite is incomplete, skipped or failing. Inspect its fresh TRX.');
  }
  for (const required of [
    'Published_Synchronous_Function_Returns_Real_Computed_Data',
    'Unstarted_TypeScript_Function_Executes_Original_Code_After_Republication',
    'Corrupt_Host_Compiler_Fails_Before_Process_Readiness',
    'TypeScript_Process_Executes_A_Custom_Concurrency_Policy'
  ]) {
    if (!rows.some(row => row.includes(required))) throw new Error(`Required real-process proof is missing: ${required}`);
  }
  console.log(`Complete Node/.NET validation passed: ${nodeResults.passed} Node tests; ${rows.length} .NET results. TRX: ${trxPath}`);
}
