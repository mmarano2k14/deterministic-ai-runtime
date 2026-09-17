# External SDK Libraries Validation

**Status:** Branch-closure validation completed for the independent .NET, TypeScript/JavaScript, and Python SDK libraries and their shared wire-contract parity. Public package-registry publication and the live multi-language runtime matrix remain separate validation scopes.

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
- the existing MCP public boundary remains the physical client/server transport surface for this branch.

## What the evidence does not prove

This validation does **not** prove:

- public NuGet/npm/Python package-registry publication;
- a standalone CLI implementation;
- a new REST/gateway surface;
- live end-to-end execution for every external client language;
- every external-client x hosted-worker-language x process/container/provider combination;
- replay/ledger/forensics/diagnostic APIs that are not part of the current public SDK operation set;
- any new scheduler, queue, recovery, journal, lease/epoch, publication-pinning, or result-acceptance authority.

Those live combinations belong to the final multi-language runtime matrix.

## Related documentation

- [External SDK Libraries](external-sdk-libraries.md)
- [Public SDK Boundary](public-sdk-boundary.md)
- [Public SDK Boundary Validation](public-sdk-boundary-validation.md)
- [Testing Strategy](testing-strategy.md)
