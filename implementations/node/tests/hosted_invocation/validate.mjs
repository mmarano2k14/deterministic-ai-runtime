import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '../../../..');

function run(command, args, options = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { stdio: 'inherit', ...options });
    child.once('error', reject);
    child.once('exit', code => code === 0 ? resolve() : reject(new Error(`${command} exited with ${code}`)));
  });
}

await run(process.execPath, ['--test', path.join(here, 'test_worker.mjs')]);

const dotnet = process.platform === 'win32' ? 'dotnet.exe' : 'dotnet';
const testProject = path.join(root, 'implementations', 'dotnet', 'Tests', 'Multiplexed.AI.Tests', 'Multiplexed.AI.Tests.csproj');
if (!fs.existsSync(testProject)) throw new Error('Run this validator from a repository containing implementations/dotnet.');
const env = { ...process.env, MULTIPLEXED_NODE_EXECUTABLE: process.execPath };
await run(dotnet, ['test', testProject, '--filter', 'FullyQualifiedName~Multiplexed.AI.Tests.Runtime.Invocation.Workers.TypeScript'], { env });
