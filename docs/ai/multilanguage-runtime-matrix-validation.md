# Multilanguage Runtime Matrix Validation

**Status:** GREEN - 33/33 Docker ProcessHostPool scenarios passed with the matrix fixture tree removed from the execution path.

## Purpose

This document records the live end-to-end validation of the external .NET, TypeScript/JavaScript, and Python SDK clients through the public MCP boundary into the deterministic runtime and production hosted workers.

This is a separate proof domain from the earlier SDK parity/package-smoke tests and from the older hosted-language TRX closure artifacts. It validates the combined client/server/runtime path rather than inferring live behavior from unit or package tests.

## Validated path

```text
External application / SDK client
        -> .NET / TypeScript / Python SDK
        -> MCP Streamable HTTP
        -> public SDK server boundary
        -> immutable publication
        -> runtime admission
        -> Docker ProcessHostPool
        -> production hosted worker
             .NET / TypeScript / Python
        -> durable invocation journal
        -> external wait / continuation
        -> DAG resume / convergence
        -> public observation / result
```

The matrix preserves server ownership of scheduling, runtime placement, RBAC, durable invocation leases/epochs, immutable publication pinning, recovery, result acceptance, DAG transitions, and finalization.

## Fixture-free closure

The final matrix run no longer depends on `implementations/matrix/fixtures`.

Published user code is supplied through reusable public SDK samples under:

```text
implementations/sdk/samples/published-functions/
  dotnet/
  typescript/
  python/
```

The durable MCP effect scenarios use the standalone sample MCP server under:

```text
implementations/sdk/samples/mcp-effect-server/
```

These samples represent code or services an SDK consumer can publish or call. They are not runtime-worker substitutes.

The actual hosted workers used by the matrix remain the production worker implementations:

```text
implementations/dotnet/workers/Multiplexed.AI.HostedInvocation.DotNetWorker/
implementations/node/workers/hosted_invocation/
implementations/python/workers/hosted_invocation/
```

## Executed coverage

The final Docker verifier reported:

| Group | Passed | Executed boundary |
|---|---:|---|
| Core client x worker matrix | 9/9 | .NET, TypeScript, and Python SDK clients across .NET, TypeScript, and Python hosted workers |
| Publication pinning + deterministic dependency packaging | 6/6 | Immutable run pinning plus `DotNetAssemblyClosure`, `NodeLockedBundle`, and `PythonWheelBundle` |
| Hosted custom policy families | 3/3 | Custom `Concurrency`, `Retry`, and `Delegation` through their existing runtime checkpoints |
| Nested published Child DAGs | 3/3 | Published root -> child -> grandchild -> hosted worker for all three worker languages |
| Durable MCP effect evidence | 2/2 | Confirmed-result local replay and `Uncertain` fail-closed no-blind-resend behavior |
| Durable execution cancellation | 3/3 | .NET, TypeScript, and Python SDK cancellation against active hosted execution |
| Runtime recovery | 2/2 | In-flight resume with the same `ExecutionId` and local-queued redispatch with a new execution identity |
| Durable journal result acceptance | 2/2 | Accepted replay and duplicate-delivery convergence under lease/epoch/CAS authority |
| External-client dependency firewall | 3/3 | .NET, TypeScript, and Python clients remain outside engine/runtime implementation dependencies |
| **Total** | **33/33** | **Docker + ProcessHostPool** |

The verifier also confirmed:

```text
Fixture-free closure: public SDK samples + production hosted workers; matrix fixture tree not required.
```

## Recovery evidence boundary

The matrix validates both runtime recovery modes used by this closure:

```text
in-flight resume
    -> failed runtime ownership
    -> replacement capacity
    -> same durable ExecutionId
    -> resume and convergence

local-queued redispatch
    -> queued ownership without ExecutionId
    -> failed runtime ownership
    -> recovery requeue
    -> replacement LocalRunId
    -> new ExecutionId
    -> convergence
```

The local-queued proof follows the actual authority chain:

```text
SharedRun -> replacement RuntimeInstanceId + LocalRunId
RuntimeRunExecutionIndex(LocalRunId) -> ExecutionId
DAG store(ExecutionId) -> terminal state
```

## Durable MCP evidence boundary

The two MCP effect cases intentionally do not claim generic exactly-once execution:

```text
Completed evidence
    -> replay locally
    -> no second physical tools/call

Uncertain evidence
    -> fail closed
    -> no blind resend
```

Provider-specific reconciliation/idempotency remains necessary where external truth is ambiguous.

## Exact non-claims

The final verifier explicitly leaves these dimensions outside this matrix:

```text
worker-isolation-provider
isolation-artifact-selection
```

They are deferred to the dedicated isolation/container validation scope. This 33/33 matrix therefore must not be cited as proof of:

- OCI `SandboxedContainer` execution selection;
- `OciImage` artifact selection;
- Kubernetes sandbox-Pod materialization;
- Kubernetes runtime-provider parity for these 33 scenarios;
- every deployment topology or failure schedule.

Those capabilities have their own documentation and evidence where implemented.

## Canonical Docker command

From the repository root on Windows PowerShell:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force; & (Join-Path (git rev-parse --show-toplevel) "implementations\matrix\runtime\docker\run-matrix.ps1")
```

A successful closure requires all client containers and the verifier to exit `0` and the verifier to report `33/33`.

## Related documentation

- [External SDK Quickstart](external-sdk-quickstart.md)
- [External SDK Libraries](external-sdk-libraries.md)
- [External SDK Libraries Validation](external-sdk-libraries-validation.md)
- [Public SDK Boundary](public-sdk-boundary.md)
- [Public SDK Boundary Validation](public-sdk-boundary-validation.md)
- [Hosted Multilanguage Execution](hosted-multilanguage-execution.md)
- [Hosted Multilanguage Validation](hosted-multilanguage-validation.md)
- [Deterministic Dependency Packaging](deterministic-dependency-packaging.md)
- [Durable MCP Effect Evidence Validation](durable-mcp-effect-evidence-validation.md)
