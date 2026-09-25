# External SDK Libraries Validation

**Status:** Branch-closure validation completed for the independent .NET, TypeScript/JavaScript, and Python SDK libraries and their shared wire-contract parity. The separate live fixture-free Docker runtime matrix is GREEN at 37/37 scenarios with `runtimeProvider=ProcessHostPool` and bounded `ContainerIsolationProvider` worker/artifact-selection coverage. The packaged interactive-agent Docker presentation is additionally GREEN for all three external SDK consumers with terminal `Completed` and deterministic replay. Public package-registry publication and the standalone CLI remain separate productization scopes.

A separate **3/3 KubernetesPool** closure adds live HTTP routing, hierarchical recovery, and Python SDK-to-Python `TrustedProcess` execution with `Completed` and an uploaded-function marker verified. This is not full Kubernetes client/worker parity or Kubernetes sandbox-Pod validation. See [KubernetesPool Matrix Validation](kubernetes-pool-matrix-validation.md).

## Validation scope

The external SDK closure validates four distinct concerns:

1. each language client remains outside runtime/control-plane implementation assemblies and packages;
2. .NET, TypeScript, and Python serialize the same portable public protocol semantics;
3. language package artifacts can be built and consumed independently;
4. the SDK transport does not acquire runtime business-operation authority.

The canonical protocol and parity material is stored under:

```text
implementations/sdk/protocol/
implementations/sdk/parity/
```

## Shared parity fixture

`implementations/sdk/parity/fixtures/sdk-parity-v1.json` is consumed by the three language validation suites.

The fixture locks:

- protocol version;
- the five public operation names;
- request and response schema versions;
- string enum literals;
- full request wire shapes;
- minimal request defaults;
- omission of absent optional fields;
- explicit JSON `null` preservation inside payload objects;
- top-level optional execution input `null` normalization;
- normalized error semantics;
- absence of private runtime/control-plane identity from external SDK source surfaces.

JSON property ordering is not part of the contract. Validation compares structural JSON meaning rather than incidental object-property order.

## .NET validation commands

From the repository root:

```cmd
dotnet build .\implementations\dotnet\src\Multiplexed.AI.Sdk\Multiplexed.AI.Sdk.csproj
dotnet test .\implementations\dotnet\Tests\Multiplexed.AI.Sdk.Tests\Multiplexed.AI.Sdk.Tests.csproj
dotnet test .\implementations\dotnet\Tests\Multiplexed.AI.Sdk.Contracts.Tests\Multiplexed.AI.Sdk.Contracts.Tests.csproj
```

The SDK dependency firewall permits only the public SDK contracts project from the repository. Runtime, control-plane, storage, worker, queue, recovery, lease, epoch, and journal projects remain forbidden.

## TypeScript validation commands

From the repository root:

```cmd
cd implementations\node\sdk
npm install
npm run build
npm test
npm run validate:foundation
npm pack --dry-run
cd ..\..\..
```

The validation covers the typed client, MCP HTTP transport, protocol foundation, cross-language parity, fail-closed schema handling, safe-read retry classification, and package contents.

## Python validation commands

From the repository root:

```cmd
cd implementations\python\sdk
python -m pip install -e .
python -m unittest discover -s tests -p "test_*.py" -v
python tests\validate.py
python -m pip wheel . --no-deps -w dist
cd ..\..\..
```

The validation covers the asynchronous typed client, MCP HTTP transport, protocol foundation, cross-language parity, fail-closed schema handling, safe-read retry classification, wheel construction, and importable package metadata.

## Cross-language package smoke

The closure package smoke builds local artifacts and consumes them from temporary applications:

```cmd
python .\implementations\sdk\parity\package_smoke.py --language all
```

Individual language scopes are:

```cmd
python .\implementations\sdk\parity\package_smoke.py --language dotnet
python .\implementations\sdk\parity\package_smoke.py --language typescript
python .\implementations\sdk\parity\package_smoke.py --language python
```

The .NET smoke packs both `Multiplexed.AI.Sdk.Contracts` and `Multiplexed.AI.Sdk`, restores a temporary `net10.0` consumer from the local package feed, and imports `AiSdkClient`.

The TypeScript smoke builds and packs `@multiplexed/ai-sdk`, installs the generated tarball into a temporary ESM consumer, and imports the public SDK surface.

The Python smoke builds a wheel, installs it into an isolated target directory, and imports `multiplexed_ai_sdk`.

Package publication is not performed by the smoke runner.

## Live fixture-free runtime matrix closure

The package/parity closure above is supplemented by a separate live Docker runtime matrix. The final verifier reported **37/37** executed scenarios with all three external SDK clients and the production hosted workers.

The live matrix covers:

```text
9  core client x worker combinations
6  publication-pinning / dependency-package scenarios
3  hosted custom-policy-family scenarios
3  nested published Child DAG scenarios
2  durable MCP effect-evidence scenarios
3  durable cancellation SDK-client scenarios
2  runtime recovery scenarios
2  durable journal result-acceptance scenarios
2  worker-isolation-provider scenarios
2  isolation-artifact-selection scenarios
3  external-client dependency-firewall scenarios
-----------------------------------------------
37 total
```

The final closure is fixture-free at the matrix execution layer: `implementations/matrix/fixtures` is not required. Published user code comes from reusable SDK samples and executes through the production .NET, TypeScript, and Python hosted workers.

The executed provider boundary is intentionally mixed rather than a full provider cross-product. The original 33 scenarios remain the `ProcessHostPool` baseline. Four additional scenarios close `TrustedProcess` versus `SandboxedContainer` and `HostRuntime` versus `OciImage` selection under `runtimeProvider=ProcessHostPool` with explicit worker-execution provider selection. The live OCI path uses the production Python hosted worker in a sibling container through the host Docker socket.

See [Multilanguage Runtime Matrix Validation](multilanguage-runtime-matrix-validation.md) for the scenario-level evidence boundary.

## Interactive agent Docker presentation closure

A separate presentation-oriented Docker scenario validates the same durable agent workflow through each external SDK consumer:

```text
.NET        -> Completed + deterministic replay GREEN
TypeScript -> Completed + deterministic replay GREEN
Python     -> Completed + deterministic replay GREEN
```

Each run exercises the authenticated public MCP boundary, immutable publication, durable execution submission, runtime-native OpenAI execution, Child DAG delegation, durable parent continuation, human review/input on the same execution, business-result publication, terminal result retrieval, and deterministic replay.

This is a focused 3-client presentation closure, not an extension of the 37-scenario runtime matrix count. It must not be added to the 37/37 or KubernetesPool 3/3 totals because it validates a different scenario shape and reuses capabilities already covered by those broader matrices.

The demo container consumes independently packaged external SDK artifacts. The .NET consumer is restored from the exact local `0.0.0-local` SDK/contracts packages before publish. OpenAI credentials remain on the runtime service.

One Python run observed a transient business-result publication failure followed by successful publication, terminal `Completed`, and successful deterministic replay. The evidence supports durable convergence for that run; it does not establish first-attempt publication success as an invariant.

See [Interactive Agent SDK Demo](interactive-agent-sdk-demo.md).

## KubernetesPool external SDK closure

The separate KubernetesPool closure is **3/3**: live HTTP routing, hierarchical runtime/Pod failure recovery, and external Python SDK publication/execution with a public `Completed` result and the uploaded-function marker verified. The combined record is **40 validated scenarios across two topologies (37 Docker + 3 Kubernetes)**, not a homogeneous `40/40` matrix. The final SDK invocation revalidated retained routing/recovery evidence; it did not rerun those campaigns. See [KubernetesPool Matrix Validation](kubernetes-pool-matrix-validation.md).

The external SDK proof uses Python on both sides, `HostRuntime` and `TrustedProcess`, one ready runtime Pod, and one Service. Prior package/parity totals remain unchanged. A profile-generation test summary must not be counted as execution of every added C# regression case.

## Retry and cancellation validation boundary

Automatic transport retry is limited to:

```text
sdk.execution.observe
sdk.execution.result
```

The following operations are not automatically retried by SDK transports:

```text
sdk.publish_pipeline
sdk.execution.submit
sdk.execution.cancel
```

This proves an SDK retry policy boundary, not generic exactly-once behavior.

Client-call cancellation also remains distinct from durable execution cancellation. A cancelled `.NET CancellationToken`, JavaScript `AbortSignal`, or Python task cancels the in-flight client operation; durable cancellation requires the explicit execution-cancellation SDK operation.

## What the evidence proves

The branch closure establishes that:

- .NET, TypeScript, and Python external SDK package roots exist independently;
- the three clients share one public operation/protocol model;
- cross-language request semantics are locked by common fixtures;
- local packages can be built, installed/consumed, and imported;
- language clients remain independent from runtime/control-plane implementation contracts;
- authentication remains a transport concern;
- schema/protocol incompatibility fails closed;
- safe-read retry does not become business-operation retry authority;
- the existing MCP public boundary remains the physical client/server transport surface for this branch;
- the separate live matrix executes 37/37 Docker scenarios through all three external clients, including the 33-scenario `ProcessHostPool` baseline and four bounded isolation-provider/artifact-selection scenarios;
- the final live closure no longer depends on the matrix fixture tree and uses public SDK samples plus production hosted workers.

The additional KubernetesPool SDK scenario proves publication/execution through the same public boundary, with `Completed` and the uploaded-function marker verified. Routing and recovery remain separately scoped evidence sets.

## What the evidence does not prove

This validation does **not** prove:

- public NuGet/npm/Python package-registry publication;
- a standalone CLI implementation;
- a new REST/gateway surface;
- every external-client x hosted-worker-language x process/container/provider combination beyond the explicitly executed 37-scenario coverage;
- full .NET/TypeScript/Python client-by-worker `KubernetesPool` parity, Kubernetes sandbox-Pod materialization, or all 33 baseline scenarios rerun under `ContainerIsolationProvider`; the separate Kubernetes SDK proof is Python-to-Python `TrustedProcess` execution;
- replay/ledger/forensics/diagnostic APIs that are not part of the current public SDK operation set;
- any new scheduler, queue, recovery, journal, lease/epoch, publication-pinning, or result-acceptance authority.

The executed live combinations and their exact non-claims are recorded in [Multilanguage Runtime Matrix Validation](multilanguage-runtime-matrix-validation.md).

## Related documentation

- [External SDK Quickstart](external-sdk-quickstart.md)
- [Interactive Agent SDK Demo](interactive-agent-sdk-demo.md)
- [External SDK Libraries](external-sdk-libraries.md)
- [Public SDK Boundary](public-sdk-boundary.md)
- [Public SDK Boundary Validation](public-sdk-boundary-validation.md)
- [Multilanguage Runtime Matrix Validation](multilanguage-runtime-matrix-validation.md)
- [Testing Strategy](testing-strategy.md)
