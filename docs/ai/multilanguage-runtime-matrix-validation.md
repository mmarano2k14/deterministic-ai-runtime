# Multilanguage Runtime Matrix Validation

**Status:** GREEN - 37/37 Docker scenarios passed with `runtimeProvider=ProcessHostPool` and the documented `TrustedProcess` / `ContainerIsolationProvider` worker-execution boundaries, with the matrix fixture tree removed from the execution path.

A separate **3/3 KubernetesPool** closure adds live HTTP routing, hierarchical recovery, and Python SDK-to-Python `TrustedProcess` execution with `Completed` and an uploaded-function marker verified. This is not full Kubernetes client/worker parity or Kubernetes sandbox-Pod validation. See [KubernetesPool Matrix Validation](kubernetes-pool-matrix-validation.md).

**Docker evidence date:** 2026-09-19. **KubernetesPool documentation update:** 2026-09-21.

## Purpose

This document records the live end-to-end validation of the external .NET, TypeScript/JavaScript, and Python SDK clients through the public MCP boundary into the deterministic runtime and production hosted workers.

This is a separate proof domain from the earlier SDK parity/package-smoke tests and from the older hosted-language TRX closure artifacts. It validates the combined client/server/runtime path rather than inferring live behavior from unit or package tests.

The current closure extends the previously validated 33-scenario `ProcessHostPool` baseline with four explicit provider/artifact-selection scenarios. The added scenarios validate trusted-process versus sandboxed-container selection and `HostRuntime` versus `OciImage` artifact selection without reclassifying the original 33 scenarios as container-isolation coverage.

## Validated path

```text
External application / SDK client
        -> .NET / TypeScript / Python SDK
        -> MCP Streamable HTTP
        -> public SDK server boundary
        -> immutable publication
        -> runtime admission / runtimeProvider=ProcessHostPool
        -> pinned hosted-worker environment and artifact selection
             HostRuntime
               -> workerExecutionProvider=TrustedProcess
               -> production hosted worker
             OciImage
               -> workerExecutionProvider=ContainerIsolationProvider
               -> SandboxedContainer
               -> sibling worker container through host Docker socket
               -> production Python hosted worker
        -> durable invocation journal
        -> external wait / continuation
        -> DAG resume / convergence
        -> public observation / result
```

The matrix preserves server ownership of scheduling, runtime placement, RBAC, durable invocation leases/epochs, immutable publication pinning, recovery, result acceptance, DAG transitions, and finalization.

The OCI closure uses sibling containers through the host Docker socket for the local/CI profile. It does not use Docker-in-Docker and it is not a Kubernetes claim.

## Fixture-free closure

The final matrix run does not depend on `implementations/matrix/fixtures`.

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
| Core client x worker matrix | 9/9 | .NET, TypeScript, and Python SDK clients across .NET, TypeScript, and Python hosted workers through `ProcessHostPool` |
| Publication pinning + deterministic dependency packaging | 6/6 | Immutable run pinning plus `DotNetAssemblyClosure`, `NodeLockedBundle`, and `PythonWheelBundle` through `ProcessHostPool` |
| Hosted custom policy families | 3/3 | Custom `Concurrency`, `Retry`, and `Delegation` through their existing runtime checkpoints |
| Nested published Child DAGs | 3/3 | Published root -> child -> grandchild -> hosted worker for all three worker languages |
| Durable MCP effect evidence | 2/2 | Confirmed-result local replay and `Uncertain` fail-closed no-blind-resend behavior |
| Durable execution cancellation | 3/3 | .NET, TypeScript, and Python SDK cancellation against active hosted execution |
| Runtime recovery | 2/2 | In-flight resume with the same `ExecutionId` and local-queued redispatch with a new execution identity |
| Durable journal result acceptance | 2/2 | Accepted replay and duplicate-delivery convergence under lease/epoch/CAS authority |
| Worker-isolation-provider selection | 2/2 | `TrustedProcess` and `SandboxedContainer` selection |
| Isolation-artifact selection | 2/2 | `HostRuntime` and `OciImage` selection |
| External-client dependency firewall | 3/3 | .NET, TypeScript, and Python clients remain outside engine/runtime implementation dependencies |
| **Total** | **37/37** | **Docker; `runtimeProvider=ProcessHostPool`; `workerExecutionProvider=TrustedProcess` or bounded `ContainerIsolationProvider`** |

The verifier also confirmed:

Historical console excerpts below retain the legacy `providers` label. Current evidence records `runtimeProvider` separately from `workerExecutionProvider`; that legacy label must not be interpreted as two alternative runtime-hosting topologies.

```text
9/9 production-like Docker ProcessHostPool scenarios passed.
6/6 publication-pinning/dependency-package ProcessHostPool feature scenarios passed.
3/3 hosted custom-policy-family ProcessHostPool scenarios passed.
3/3 nested Child DAG ProcessHostPool scenarios passed.
2/2 durable MCP effect evidence ProcessHostPool scenarios passed.
3/3 durable cancellation SDK-client scenarios passed.
2/2 runtime recovery ProcessHostPool scenarios passed.
2/2 durable journal result-acceptance scenarios passed.
3/3 external client dependency-firewall scenarios passed.
2/2 worker-isolation-provider scenarios passed (trusted process + sandboxed container).
2/2 isolation-artifact-selection scenarios passed (HostRuntime + OciImage).
Exact executed-coverage closure: 37/37 scenarios; topology=docker; providers=ProcessHostPool+ContainerIsolationProvider.
Fixture-free closure: public SDK samples + production hosted workers; matrix fixture tree not required.
OCI closure profile: sibling worker containers through the host Docker socket; no Docker-in-Docker and no Kubernetes claim.
```

## Recovery evidence boundary

The matrix validates both runtime recovery modes used by the `ProcessHostPool` closure:

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

The four isolation/artifact-selection scenarios do not claim a second recovery implementation. `ContainerIsolationProvider` remains a physical execution provider behind the same durable journal, lease/epoch, result-acceptance, DAG, and recovery authorities.

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

## Isolation-provider evidence boundary

The added closure proves that the runtime distinguishes:

```text
runtimeProvider=ProcessHostPool
    -> HostRuntime -> workerExecutionProvider=TrustedProcess
    -> OciImage -> SandboxedContainer
                -> workerExecutionProvider=ContainerIsolationProvider
```

The `OciImage` path is digest-pinned and is executed as a sibling container through the host Docker socket. The live OCI scenarios in this matrix use the production Python hosted worker.

This matrix does not infer that every previously validated scenario was rerun under the container-isolation provider.

## Exact non-claims

The 37/37 result must not be cited as proof of:

- all 33 pre-existing runtime scenarios under `ContainerIsolationProvider`;
- .NET or TypeScript production hosted-worker execution inside OCI isolation unless separately evidenced;
- Kubernetes sandbox-Pod materialization for hosted custom code;
- `KubernetesPool` parity for this 37-scenario public-SDK matrix;
- Docker-in-Docker deployment;
- every Docker-compatible engine, operating system, kernel, cgroup mode, or OCI implementation;
- every deployment topology or failure schedule.

Kubernetes runtime-pool capabilities documented elsewhere remain separate evidence domains. A Kubernetes hosted-code sandbox provider must be validated independently rather than inferred from the local/CI Docker closure.

## KubernetesPool closure and cross-topology accounting

The separate KubernetesPool closure is **3/3**: live HTTP routing, hierarchical runtime/Pod failure recovery, and external Python SDK publication/execution with a public `Completed` result and the uploaded-function marker verified. The combined record is **40 validated scenarios across two topologies (37 Docker + 3 Kubernetes)**, not a homogeneous `40/40` matrix. The final SDK invocation revalidated retained routing/recovery evidence; it did not rerun those campaigns. See [KubernetesPool Matrix Validation](kubernetes-pool-matrix-validation.md).

| Evidence domain | Accepted scenarios | Scope |
|---|---:|---|
| Preserved fixture-free Docker matrix | 37/37 | `ProcessHostPool`; the existing trusted-process baseline and bounded OCI worker-selection scenarios. |
| KubernetesPool matrix | 3/3 | Routing; hierarchical recovery; external Python SDK -> production Python worker via `TrustedProcess`. |
| Cross-topology coverage record | 40 | The two scoped evidence sets, not every scenario on every topology. |

The Kubernetes SDK result uses the `HostRuntime` publication artifact inside a Runtime Pool OCI image. It does not establish Kubernetes `ContainerIsolationProvider` execution. The canonical Kubernetes guide records image provenance, host-role settings, commands, diagnostic bundles, and retained-evidence limits.

## Canonical Docker command

From the repository root on Windows PowerShell:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force; & (Join-Path (git rev-parse --show-toplevel) "implementations\matrix\runtime\docker\run-matrix.ps1")
```

A successful closure requires all client containers and the verifier to exit `0` and the verifier to report:

```text
Exact executed-coverage closure: 37/37 scenarios; topology=docker; providers=ProcessHostPool+ContainerIsolationProvider.
GREEN - MATRIX PASSED
```

## Related documentation

- [External SDK Quickstart](external-sdk-quickstart.md)
- [External SDK Libraries](external-sdk-libraries.md)
- [External SDK Libraries Validation](external-sdk-libraries-validation.md)
- [Public SDK Boundary](public-sdk-boundary.md)
- [Public SDK Boundary Validation](public-sdk-boundary-validation.md)
- [Hosted Multilanguage Execution](hosted-multilanguage-execution.md)
- [Hosted Multilanguage Validation](hosted-multilanguage-validation.md)
- [Hosted Worker Isolation](hosted-worker-isolation.md)
- [Hosted Worker Isolation Validation](hosted-worker-isolation-validation.md)
- [Deterministic Dependency Packaging](deterministic-dependency-packaging.md)
- [Durable MCP Effect Evidence Validation](durable-mcp-effect-evidence-validation.md)
