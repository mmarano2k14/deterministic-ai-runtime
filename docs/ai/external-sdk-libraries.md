# External SDK Libraries

**Status:** Implemented and validated for .NET, TypeScript/JavaScript, and Python client packages. The live fixture-free Docker ProcessHostPool matrix is GREEN at 33/33 scenarios across the documented execution/recovery boundaries. Public registry publication and the standalone runtime CLI remain separate productization work.

## Purpose

The external SDK libraries provide independently consumable clients over the existing portable public SDK boundary. They do not reference runtime engine assemblies or become authorities for scheduling, recovery, queue ownership, leases, epochs, publication pinning, or result acceptance.

The architecture is:

```text
External application
      |
      +--> .NET SDK
      +--> TypeScript SDK
      `--> Python SDK
               |
               v
        portable SDK protocol
               |
               v
       MCP Streamable HTTP
               |
               v
       existing public tools
               |
               v
      public server boundary
               |
               v
    existing runtime authorities
```

The five public operations are shared across all three languages:

```text
sdk.publish_pipeline
sdk.execution.submit
sdk.execution.observe
sdk.execution.result
sdk.execution.cancel
```

The canonical language-neutral protocol manifest is:

```text
implementations/sdk/protocol/ai-sdk-protocol-v1.json
```

Cross-language fixtures are stored under:

```text
implementations/sdk/parity/
```

## Stable package roots

```text
implementations/
|-- dotnet/src/Multiplexed.AI.Sdk/
|-- node/sdk/
|-- python/sdk/
|-- sdk/protocol/
`-- sdk/parity/
```

The public .NET wire contracts remain in:

```text
implementations/dotnet/src/Multiplexed.AI.Sdk.Contracts/
```

## Public SDK samples

Reusable sample user code lives outside the matrix harness:

```text
implementations/sdk/samples/published-functions/
  dotnet/
  typescript/
  python/

implementations/sdk/samples/mcp-effect-server/
```

The published-function samples represent code an external SDK consumer supplies for publication. They are not hosted-worker implementations. The fixture-free runtime matrix executes those samples through the real public SDK boundary and the production .NET, TypeScript, and Python hosted workers.

The standalone MCP effect server is a controlled external service sample used to validate durable outbound-effect evidence without turning the matrix harness into another runtime authority.

See [Multilanguage Runtime Matrix Validation](multilanguage-runtime-matrix-validation.md) for the 33/33 live closure.

## Shared client semantics

All three SDKs preserve the same client/runtime boundary:

- JSON uses camel-case wire names;
- public enum values are stable strings;
- public documents carry explicit `schemaVersion` values;
- unsupported protocol/schema versions fail closed;
- absent optional fields are omitted;
- explicit JSON payload objects may contain JSON `null` values;
- a top-level optional execution input of `null` is treated as absent consistently;
- credentials are injected by the transport and are never serialized into publication or execution business payloads;
- client cancellation cancels only the in-flight SDK request;
- durable execution cancellation is explicit through the cancellation operation;
- automatic transport retry is limited to read-only observation and result retrieval.

The retry boundary is:

```text
publish  -> no automatic transport retry
submit   -> no automatic transport retry
observe  -> bounded safe-read transport retry
result   -> bounded safe-read transport retry
cancel   -> no automatic transport retry
```

This restriction is intentional. SDK convenience code does not become a second idempotency or business-effect retry authority.

## .NET SDK

Package/project:

```text
Multiplexed.AI.Sdk
```

Current repository target:

```text
.NET 10 / net10.0
```

The SDK depends on the public `Multiplexed.AI.Sdk.Contracts` package/project and the client-side MCP transport library. A project firewall forbids references to other repository runtime/infrastructure projects.

### .NET command line

From the repository root, build the SDK:

```cmd
dotnet build .\implementations\dotnet\src\Multiplexed.AI.Sdk\Multiplexed.AI.Sdk.csproj
```

Run the SDK tests:

```cmd
dotnet test .\implementations\dotnet\Tests\Multiplexed.AI.Sdk.Tests\Multiplexed.AI.Sdk.Tests.csproj
```

Run the public-contract firewall tests:

```cmd
dotnet test .\implementations\dotnet\Tests\Multiplexed.AI.Sdk.Contracts.Tests\Multiplexed.AI.Sdk.Contracts.Tests.csproj
```

Build local NuGet packages for the contracts and SDK:

```cmd
dotnet pack .\implementations\dotnet\src\Multiplexed.AI.Sdk.Contracts\Multiplexed.AI.Sdk.Contracts.csproj -c Release -o .\artifacts\sdk
dotnet pack .\implementations\dotnet\src\Multiplexed.AI.Sdk\Multiplexed.AI.Sdk.csproj -c Release -o .\artifacts\sdk
```

A local consumer can then add the package without restoring immediately, and restore from both the local feed and NuGet.org so the client-side MCP dependency can be resolved:

```cmd
dotnet add package Multiplexed.AI.Sdk --source .\artifacts\sdk --no-restore
dotnet restore --source .\artifacts\sdk --source https://api.nuget.org/v3/index.json
```

The normal registry-style command, once a package is intentionally published to a configured NuGet feed, is:

```cmd
dotnet add package Multiplexed.AI.Sdk
```

That last command documents the package-consumption shape; branch validation does not claim that a public NuGet release has already been published.

### .NET client construction

```csharp
using Multiplexed.AI.Sdk;
using Multiplexed.AI.Sdk.Authentication;
using Multiplexed.AI.Sdk.Transport;

var transport = new AiSdkMcpHttpTransport(
    new Uri("https://runtime.example/mcp"),
    new AiSdkTransportOptions
    {
        CredentialProvider = new AiSdkStaticCredentialProvider(
            new AiSdkCredential("Bearer", token))
    });

IAiSdkClient client = new AiSdkClient(transport);
```

The typed client exposes:

```text
PublishPipelineAsync
SubmitExecutionAsync
ObserveExecutionAsync
GetExecutionResultAsync
CancelExecutionAsync
```

## TypeScript / JavaScript SDK

Package:

```text
@multiplexed/ai-sdk
```

Runtime compatibility:

```text
Node.js >= 20
ES2022
```

The Node floor comes from the selected MCP client dependency. The package does not require Node 22/24 and does not rely on experimental Node TypeScript execution flags.

### TypeScript command line

From the repository root:

```cmd
cd implementations\node\sdk
npm install
npm run build
npm test
npm run validate:foundation
npm pack
cd ..\..\..
```

For a local package-consumption test, install the tarball produced by `npm pack` into a consumer project:

```cmd
npm install <path-to-multiplexed-ai-sdk-tarball.tgz>
```

The normal registry-style command, once the package is intentionally published to an npm registry, is:

```cmd
npm install @multiplexed/ai-sdk
```

Branch validation proves local package build/install/import behavior; it does not claim that a public npm release has already been published.

### TypeScript client construction

```ts
import {
  AiSdkClient,
  AiSdkMcpHttpTransport,
  AiSdkStaticCredentialProvider,
} from "@multiplexed/ai-sdk";

const transport = new AiSdkMcpHttpTransport(
  new URL("https://runtime.example/mcp"),
  {
    credentialProvider: new AiSdkStaticCredentialProvider({
      scheme: "Bearer",
      value: process.env.MULTIPLEXED_TOKEN!,
    }),
  },
);

const client = new AiSdkClient(transport);
```

The typed client exposes:

```text
publishPipeline
submitExecution
observeExecution
getExecutionResult
cancelExecution
```

## Python SDK

Package:

```text
multiplexed-ai-sdk
```

Runtime compatibility:

```text
Python >= 3.10
```

### Python command line

From the repository root:

```cmd
cd implementations\python\sdk
python -m pip install -e .
python -m unittest discover -s tests -p "test_*.py" -v
python tests\validate.py
python -m pip wheel . --no-deps -w dist
cd ..\..\..
```

A local consumer can install the generated wheel directly:

```cmd
python -m pip install <path-to-multiplexed_ai_sdk-wheel.whl>
```

The normal registry-style command, once the package is intentionally published to a Python package index, is:

```cmd
python -m pip install multiplexed-ai-sdk
```

Branch validation proves local wheel build/install/import behavior; it does not claim that a public package-index release has already been published.

### Python client construction

```python
from multiplexed_ai_sdk import AiSdkClient, AiSdkMcpHttpTransport

transport = AiSdkMcpHttpTransport("https://runtime.example/mcp")
client = AiSdkClient(transport)
```

The asynchronous typed client exposes:

```text
publish_pipeline
submit_execution
observe_execution
get_execution_result
cancel_execution
```

## Cross-language command line

The canonical parity suites can be run separately as described above. The local package build/install/import smoke can also validate all three SDKs in one command from the repository root:

```cmd
python .\implementations\sdk\parity\package_smoke.py --language all
```

Language-specific package smoke scopes are available:

```cmd
python .\implementations\sdk\parity\package_smoke.py --language dotnet
python .\implementations\sdk\parity\package_smoke.py --language typescript
python .\implementations\sdk\parity\package_smoke.py --language python
```

The package smoke builds local artifacts, consumes them from temporary applications, verifies import/use of the public SDK surface, and removes the temporary consumers. It does not publish packages to external registries.

## Authentication boundary

Authentication is transport-owned. Static credential providers exist for straightforward client scenarios, but credentials remain outside the portable publication/execution documents.

Tenant authority is still resolved by the authorized server context. SDKs do not accept private runtime-placement identities as client-selected authority.

## Error boundary

Each SDK normalizes client-side transport/remote failures into a portable SDK error taxonomy. That client error model is not the durable execution-result failure model and does not replace server/runtime evidence.

## Current limits

The external SDK branch does not claim:

- public NuGet/npm/Python-registry publication;
- a standalone `ai-runtime` CLI implementation;
- replay, ledger, forensics, diagnostics, queue, or runtime-instance APIs beyond the current public publication/execution surface;
- an additional REST gateway;
- live client-language x hosted-worker-language x provider coverage from the package parity suite alone; the separate runtime matrix provides the documented Docker ProcessHostPool closure;
- any change to scheduler, queue, recovery, journal, lease/epoch, publication-pinning, or result-acceptance authority.

Live client-to-server evidence is recorded separately in [Multilanguage Runtime Matrix Validation](multilanguage-runtime-matrix-validation.md); it supplements rather than changes the package-parity boundary described here.

## Implementation references

- `implementations/dotnet/src/Multiplexed.AI.Sdk/`
- `implementations/dotnet/src/Multiplexed.AI.Sdk.Contracts/`
- `implementations/node/sdk/`
- `implementations/python/sdk/`
- `implementations/sdk/protocol/`
- `implementations/sdk/parity/`

## Related documentation

- [External SDK Quickstart](external-sdk-quickstart.md)
- [External SDK Libraries Validation](external-sdk-libraries-validation.md)
- [Public SDK Boundary](public-sdk-boundary.md)
- [Public SDK Boundary Validation](public-sdk-boundary-validation.md)
- [MCP Server Control Plane](mcp-server-control-plane.md)
- [Hosted Multilanguage Execution](hosted-multilanguage-execution.md)
