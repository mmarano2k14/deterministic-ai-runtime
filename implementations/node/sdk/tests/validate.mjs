import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import { fileURLToPath } from "node:url";
import ts from "../../workers/hosted_invocation/vendor/typescript-5.8.3.cjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const sdkRoot = path.resolve(here, "..");
const sourceRoot = path.join(sdkRoot, "src");
const manifestPath = path.resolve(sdkRoot, "../../sdk/protocol/ai-sdk-protocol-v1.json");

const sourceFiles = [];
const collect = (directory) => {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) collect(fullPath);
    else if (entry.isFile() && entry.name.endsWith(".ts")) sourceFiles.push(fullPath);
  }
};
collect(sourceRoot);

for (const file of sourceFiles) {
  const source = fs.readFileSync(file, "utf8");
  const result = ts.transpileModule(source, {
    fileName: file,
    reportDiagnostics: true,
    compilerOptions: {
      target: ts.ScriptTarget.ES2022,
      module: ts.ModuleKind.ESNext,
      strict: true,
    },
  });
  const errors = (result.diagnostics ?? []).filter((diagnostic) => diagnostic.category === ts.DiagnosticCategory.Error);
  if (errors.length > 0) {
    for (const diagnostic of errors) {
      console.error(ts.flattenDiagnosticMessageText(diagnostic.messageText, "\n"));
    }
    process.exit(1);
  }
}

const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
const operationSource = fs.readFileSync(path.join(sourceRoot, "protocol/operations.ts"), "utf8");
for (const operation of manifest.operations) {
  if (!operationSource.includes(`"${operation.name}"`)) {
    throw new Error(`TypeScript protocol is missing operation '${operation.name}'.`);
  }
}
if (!operationSource.includes(`AI_SDK_PROTOCOL_VERSION = ${manifest.protocolVersion}`)) {
  throw new Error("TypeScript protocol version does not match the canonical manifest.");
}

console.log(`TypeScript SDK foundation validated: ${sourceFiles.length} source files, ${manifest.operations.length} operations.`);
