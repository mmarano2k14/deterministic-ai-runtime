# Interactive Agent SDK Topology Validation

**Status:** GREEN - the same interactive agent workflow completed through the public SDK boundary for .NET, TypeScript/JavaScript, and Python clients on both live `ProcessHostPool/HTTP` and live `KubernetesPool/HTTP`.

**Recorded closure:** 2026-10-01.

## Purpose

This document records the live topology validation for `demo/interactive-agent-sdk/`. It is intentionally separate from:

- the fixture-free 37/37 Docker SDK-to-runtime matrix;
- the historical KubernetesPool 3/3 routing/recovery/external-Python closure;
- the 36-row deterministic adversarial runtime matrix;
- the packaged Docker presentation demo.

No counts from those campaigns are changed by this validation.

## Validated public flow

```text
external .NET / TypeScript / Python SDK
    -> real Bearer JWT
    -> POST /auth/access-context
    -> X-Access-Context
    -> public MCP Streamable HTTP boundary
    -> immutable pipeline publication
    -> QueueFirst durable submission
    -> runtime placement
    -> root ai.prompt / OpenAI
    -> Child DAG delegation
    -> durable child continuation
    -> execution.await-input
    -> accepted human input
    -> same ExecutionId resumes
    -> final ai.prompt / OpenAI
    -> public business result
    -> terminal Completed
    -> deterministic replay validation
```

The external consumers do not receive `OPENAI_API_KEY`, runtime placement identities, MongoDB/Redis access, scheduler authority, recovery authority, or internal DAG-store contracts.

## Executed topology matrix

| Runtime topology | .NET client | TypeScript client | Python client |
|---|---:|---:|---:|
| `ProcessHostPool/HTTP` | PASS | PASS | PASS |
| `KubernetesPool/HTTP` | PASS | PASS | PASS |

This is a six-path interactive integration record, not a replacement for the 37-scenario Docker matrix and not a new aggregate scenario count.

## Required validation gates

Every accepted client path must pass all eleven gates:

```text
execution.completed
execution.sameExecutionIdAfterHumanInput
humanReview.accepted
rootSteps.completed
provider.openai
provider.model
provider.responsePayloadPresent
result.available
replay.succeeded
replay.deterministic
replay.executionIdUnchanged
```

The gate set proves that the workflow did more than reach a terminal status. It requires the runtime-produced OpenAI result, accepted human continuation on the same execution identity, public result availability, and deterministic replay.

## ProcessHostPool/HTTP proof

The ProcessHostPool validation runs the external consumers against the production ProcessHostPool integration path with three ProcessHosts and three child RuntimeInstances per host. The interactive validation bridge is opt-in and leaves the historical test authentication path unchanged when the interactive flag is absent.

The final accepted run reported:

```text
Test Run Successful.
Total tests: 1
Passed: 1
[processhost-launcher] PASS: ProcessHostPool/HTTP 3/3
```

The public replay request can race terminal snapshot persistence after execution becomes observable as `Completed`. The final validation therefore retains the same bounded replay-readiness rule described below.

## KubernetesPool/HTTP proof

The Kubernetes validation reuses the existing Kubernetes Runtime Pool lifecycle and scale-out authority rather than creating a matrix-owned Pod lifecycle. The external SDK contract is unchanged.

The accepted run observed:

```text
scale-out request: Observed
current Runtime Pool Pod: Pending
scale-out request: Fulfilled
runtime capacity selected
```

The topology attestation is exact-pool and fail-closed. It requires the current PoolId to resolve to a running/ready Runtime Pool Pod, the matching Service, the declared runtime-instance count, and the actual runtime image. Stale unrelated Pods do not satisfy the proof.

The final accepted client results were:

```text
[kubernetes-interactive] dotnet: PASS
[kubernetes-interactive] typescript: PASS
[kubernetes-interactive] python: PASS
[kubernetes-sdk-matrix] PASS: interactive KubernetesPool/HTTP 3/3
[kubernetes-launcher] PASS: KubernetesPool/HTTP 3/3
```

The Kubernetes control plane keeps durable DAG reconciliation while RuntimeInstanceOnly children own hosted-worker polling. Child DAG composition is enabled on both sides so the existing durable child-relation store and continuation authority remain available. No second Child DAG engine or relation store is introduced.

## Authentication and secret boundary

Both topology campaigns use the real public authentication flow:

```text
Bearer JWT
    -> /auth/access-context
    -> server-created access-context handle
    -> SDK transport headers
```

The Kubernetes matrix harness uses an opt-in standalone-authentication mode for this scenario so its historical `MatrixBearer` behavior remains unchanged for existing matrix tests.

The OpenAI credential is runtime-owned. The public clients select the model but do not receive, serialize, or forward `OPENAI_API_KEY`.

## Replay readiness

Execution completion and terminal snapshot visibility are separate observable milestones. A public execution may become `Completed` while the terminal MongoDB snapshot is still finishing persistence.

The validated consumer behavior is:

```text
Completed observed
    -> replay immediately
    -> if and only if result is exactly "Snapshot not found."
       retry the same public replay operation
    -> maximum readiness window: 30 seconds
    -> delay between eligible retries: 250 ms
```

The first replay attempt remains immediate. The retry does not resubmit the execution and does not construct a synthetic replay result. Authorization failures, transport errors, determinism failures, identity mismatches, and any other replay failure are not retried by this readiness rule.

The final Kubernetes run demonstrated the expected transient behavior in all three clients: an initial `SnapshotPending` attempt followed by a real `Ready` replay response, after which all replay gates passed.

## Commands

ProcessHostPool/HTTP:

```powershell
python .\demo\interactive-agent-sdk\validation\launch_processhostpool_matrix.py --runtime-transport HTTP
```

KubernetesPool/HTTP:

```powershell
python .\demo\interactive-agent-sdk\validation\verify_kubernetespool_interactive_launcher.py
python .\demo\interactive-agent-sdk\validation\test_kubernetespool_attestation.py
python .\demo\interactive-agent-sdk\validation\test_replay_readiness.py
python .\demo\interactive-agent-sdk\validation\launch_kubernetespool_matrix.py
```

Do not use the image-skip option after host or worker code baked into the Runtime Pool image changes.

## Preserved authorities

This validation does not move or duplicate authority for:

- DAG scheduling or transitions;
- Child DAG identity, relation persistence, continuation, or finalization;
- shared queue or admission;
- Runtime Pool membership or Pod/ProcessHost lifecycle;
- durable invocation leases/epochs and result acceptance;
- RBAC and execution ownership;
- recovery;
- deterministic replay.

## Exact non-claims

This interactive topology campaign does not prove:

- `ProcessHostPool/gRPC` or `KubernetesPool/gRPC` parity for the interactive scenario;
- the full external-client x hosted-worker language cross-product on Kubernetes;
- Kubernetes `ContainerIsolationProvider` or sandbox-Pod execution;
- control-plane crash recovery or multi-control-plane leader/claim arbitration;
- Redis/MongoDB failover;
- every failure schedule from the adversarial or recovery matrices;
- public package-registry publication;
- generic exactly-once external effects.

## Related documentation

- [Interactive Agent SDK Demo](interactive-agent-sdk-demo.md)
- [External SDK Quickstart](external-sdk-quickstart.md)
- [External SDK Libraries Validation](external-sdk-libraries-validation.md)
- [Multilanguage Runtime Matrix Validation](multilanguage-runtime-matrix-validation.md)
- [KubernetesPool Matrix Validation](kubernetes-pool-matrix-validation.md)
- [Replay and Audit](replay-and-audit.md)
