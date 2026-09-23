# Deterministic AI Runtime

A deterministic, multi-tenant .NET runtime for **durable AI workflow execution** across local workers, real external processes, reusable Runtime Pools, and Kubernetes-hosted runtime instances.

Most AI tooling starts at prompts, agents, and RAG. This runtime starts one layer down, at **execution**:

> **Who owns the work, what survives a crash, what may execute again, and can the result be replayed and audited afterward?**

It provides durable DAG execution, Redis-backed coordination, provider-based dispatch, bounded reusable capacity, crash recovery, deterministic replay, tenant isolation, and canonical event observation behind one shared control plane. The engine does not judge the answer; it guarantees the lifecycle of the execution that produced it — an LLM call, a RAG step, an MCP tool, a database command, a human approval, or any HTTP/gRPC workload.

[![Version](https://img.shields.io/badge/Version-0.0.8.7-blue)](./CHANGELOG.md)
[![Changelog](https://img.shields.io/badge/Changelog-view-lightgrey)](./CHANGELOG.md)
![AI Runtime](https://img.shields.io/badge/AI-Deterministic%20Execution-purple)
![Runtime](https://img.shields.io/badge/Runtime-distributed-brightgreen)
![Child DAG](https://img.shields.io/badge/Child%20DAG-Validated-brightgreen)
![Matrix](https://img.shields.io/badge/Adversarial%20Matrix-36%2F36-brightgreen)
![Observation](https://img.shields.io/badge/Observation-EventDriven-brightgreen)
![Redis](https://img.shields.io/badge/Redis-required-red?logo=redis)
![MongoDB](https://img.shields.io/badge/MongoDB-required-green?logo=mongodb)
![Kubernetes](https://img.shields.io/badge/Kubernetes-supported-326CE5?logo=kubernetes&logoColor=white)
![HTTP](https://img.shields.io/badge/Transport-HTTP-0A66C2)
![gRPC](https://img.shields.io/badge/Transport-gRPC-244C5A)
![Status](https://img.shields.io/badge/Status-active%20development-orange)
![License](https://img.shields.io/badge/License-BSL%201.1-lightgrey)

📄 **Validation methodology and evidence:** [Adversarial Runtime Validation Matrix](docs/ai/adversarial-runtime-validation-matrix.md) — how correctness here is validated as an invariant, not a single green run, with the full 36-row evidence archive.

## Start here

- **Understand the runtime:** [Architecture Quick Start](docs/ai/architecture-quick-start.md) — core components, durable truth, failure boundaries, and trade-offs.
- **External SDKs:** [.NET, TypeScript / JavaScript, and Python](#sdk) — publish user code and manage durable executions through the public MCP boundary.
- **Complete documentation:** [docs/index.md](docs/index.md)
- **Interactive AI Runtime Analysis Demo:** [demo/rbac-aiAnalysis/nextjs/README.md](demo/rbac-aiAnalysis/nextjs/README.md)
- **Installation / local Kubernetes:** [Kubernetes / Minikube installation and recovery guide](docs/ai/kubernetes-local-environment.md)
- **Architecture:** [Architecture overview](docs/ai/architecture-overview.md)
- **Runtime Pools:** [Runtime Pool architecture](docs/ai/runtime-pool-architecture.md)
- **Recovery:** [Runtime Pool failure recovery](docs/ai/runtime-pool-failure-recovery.md)
- **Replay and audit:** [Replay and audit](docs/ai/replay-and-audit.md)

---

## What it proves today

| Area | Evidence |
|---|---|
| Deterministic execution | Dependency-aware DAG execution with durable state, claims, retry, recovery, retention, replay, and deterministic convergence. |
| Durable Child DAGs | `WaitingForExternal` without holding capacity, deterministic child identity, continuation, same-parent recovery, recursive `ChildDepth = 3`. |
| Recursive exactness | Nested child logical-step exactness is verified. Dedicated recursive-child replay remains `NOT_EVALUATED`. |
| Hosting | Local, Process, Kubernetes, ProcessHostPool, KubernetesPool — HTTP and gRPC, exact `RuntimeInstanceId` routing. |
| Runtime Pools | Bounded reusable capacity, warm reuse, child-failure isolation, full-boundary recovery. |
| Deterministic recovery | Same-`ExecutionId` in-flight resume; durable shared-state redispatch for queued work; claim-protected mutation. |
| Durable authority | Failure journal, append-only Lifecycle Journal, Ledger, trace, Recovery Forensics — independent stores correlated by first-class identities. |
| Event-driven lifecycle | Canonical engine facts through one Event Manager and central projection catalog; no second bus. |
| Multi-tenancy | RBAC context survives async dispatch; tenant-scoped admission, capacity, recovery, Ledger, replay, and Forensics. |
| External SDKs | Independent .NET, TypeScript / JavaScript, and Python clients; immutable publication, durable submission, observation and Watch, results, cancellation, pause/resume, human input, and deterministic replay validation. See [SDK execution evidence](#sdk). |

Configuration and policy drive retry, retention, concurrency, admission, isolation, hosting, and recovery — without engine rewrites.

---

## Validation evidence

### 36 / 36 deterministic adversarial matrix

Four provider/transport combinations × nine deterministic failure schedules — all green:

| Runtime model | gRPC | HTTP |
|---|---:|---:|
| KubernetesPool | 9 / 9 | 9 / 9 |
| ProcessHostPool | 9 / 9 | 9 / 9 |

Rows: `Baseline`, `CrashEarly`, `ChildInvocationBoundary`, `ContinuationConsume`, `Depth2RuntimeFailure`, `Depth3RuntimeFailure`, `SeedA`, `SeedB`, `SeedC`.

```text
72 execution cycles          1,296 / 1,296 parent runs completed
66,096 parent logical steps  196,992 recursive child logical steps
0 missing child steps         0 unexpected duplicate child steps
0 ownership-transition violations
1,296 / 1,296 parent replay proofs
288 recovered SharedRuns      72 / 72 process-kill identity-continuity proofs
```

Raw xUnit artifacts — one per row, each with a distinct SHA-256 — are archived at `docs/files/adversarial-runtime-validation-logs.zip`:

```text
SHA-256 = a8e252b2b7277c196d594f0da6963b2e39eab3ad0e2a6415306974d2a8497c03
```

Every number above is **independently recomputable** from that archive: each row's invariants can be extracted directly from its raw log — you do not have to trust this summary. The matrix proves the **selected** deterministic schedules; it does not claim exhaustive exploration of every possible interleaving. See the [evidence index](docs/ai/adversarial-runtime-validation-evidence-index.md).

**Recursive proof scope (per row):**

```text
36 parents · 108 recursive child executions · 5,472 child logical steps · depth 3
Root-parent step exactness      VERIFIED
Nested child-step exactness     VERIFIED
Nested Child DAG terminality    VERIFIED
Parent replay                   VERIFIED
Dedicated recursive-child replay  NOT_EVALUATED
```

**Historical P35 stress campaign:** both HTTP and gRPC completed 35 / 35 — 105 tenants, 315 real DAG executions, 70 real process kills, 210 recovered jobs per transport (HTTP batch: ~4.19M datastore ops, 18.29 GiB). P35 is the experimental edge of the tested machine, not a universal throughput guarantee.

### SDK-to-runtime validation

The SDK execution record is separate from the adversarial matrix above: **37/37 Docker scenarios** with `ProcessHostPool`, plus **3/3 KubernetesPool scenarios** covering live HTTP routing, hierarchical recovery, and external Python SDK execution with a public `Completed` result and the uploaded-function marker verified. This is **40 validated scenarios across two topologies**, not a homogeneous `40/40` matrix. Separate real MCP/HTTP public-SDK E2E validation is also green for `execution.watch()` and for pause/resume/human-input/replay control flows in **.NET, TypeScript, and Python**. These E2E checks are separate validation campaigns and are not added to the 37/37 or 3/3 scenario counts. See the [SDK section](#sdk) for the exact scope and evidence links.

---

## Quick start

**Prerequisites:** a compatible .NET SDK, Redis, MongoDB, Docker, and Kubernetes for K8s-backed scenarios.

```powershell
dotnet build implementations/dotnet/Multiplexed.sln
dotnet test  implementations/dotnet/Tests/Multiplexed.AI.Tests/Multiplexed.AI.Tests.csproj
dotnet test  implementations/dotnet/Tests/Multiplexed.AI.McpServer.Tests.Integration/Multiplexed.AI.McpServer.Tests.Integration.csproj
```

Long-running ProcessHostPool and KubernetesPool proofs run with targeted filters and reachable Redis/MongoDB/Kubernetes. The local Kubernetes image, namespace, and pull-policy contract are defined in `KubernetesSdkScenarioConstants.cs` (the source of truth). Full bootstrap: **[Kubernetes / Minikube guide](docs/ai/kubernetes-local-environment.md)**.

---

## Deep reference

The architecture, identity model, recovery semantics, replay, observability, and full capability matrix are below — collapsed so this page stays scannable. Expand what you need; each section mirrors a dedicated document under [`docs/`](docs/index.md).

<a id="sdk"></a>

<details>

<summary><b>External SDKs (.NET, TypeScript / JavaScript &amp; Python), publication &amp; validated execution</b></summary>

## External SDKs

Independent .NET, TypeScript / JavaScript, and Python SDKs let an external application publish user code and manage durable executions through **MCP Streamable HTTP**. They use portable public contracts, without direct or transitive dependencies on runtime engine assemblies or private control-plane contracts.

**The SDK client language and the hosted function language are independent.** A Python client can publish a .NET function, and a .NET client can publish Python or TypeScript code. The Docker matrix validates all nine client/worker language combinations; the Kubernetes scope is narrower and is stated below.

| Client | Package | Repository source |
|---|---|---|
| .NET | `Multiplexed.AI.Sdk` | [`implementations/dotnet/src/Multiplexed.AI.Sdk/`](implementations/dotnet/src/Multiplexed.AI.Sdk/) |
| TypeScript / JavaScript | `@multiplexed/ai-sdk` | [`implementations/node/sdk/`](implementations/node/sdk/) |
| Python | `multiplexed-ai-sdk` | [`implementations/python/sdk/`](implementations/python/sdk/) |

Local package build/install/import behavior is validated. **Public NuGet, npm, and Python package-index releases are not claimed by this validation**; registry publication and a standalone runtime CLI remain separate deliverables.

### Public operations and execution flow

All three clients expose the same ten public operations:

```text
sdk.publish_pipeline
sdk.execution.submit
sdk.execution.observe
sdk.execution.watch
sdk.execution.result
sdk.execution.cancel
sdk.execution.pause
sdk.execution.resume
sdk.execution.input.submit
sdk.execution.replay
```

```text
External application / SDK client
    -> publish pipeline definition + user functions
    -> immutable publication reference
    -> submit durable execution
    -> existing shared queue, admission, and runtime placement
    -> observe / watch public execution progress
    -> pause / resume / human input / cancel through public control operations
    -> production hosted worker executes the pinned function
    -> durable result acceptance and DAG convergence
    -> terminal result
    -> optional deterministic replay validation
```

The SDK does not create Pods, select execution ownership, or become a scheduler, recovery coordinator, or business-effect retry authority. RBAC and tenant ownership remain server-enforced; transport credentials and access-context headers are not serialized into publication or execution business payloads. Compatible repeated submissions converge through the existing idempotency/run-pin authority; conflicting reuse is rejected.

Cancelling an in-flight client request is not durable execution cancellation. The explicit cancellation operation requests cancellation from the runtime; acceptance does not mean the execution is already terminal. Automatic transport retry is limited to the safe-read operations `observe`, `watch`, and `result`; command-side operations are not automatically retried.

### Watch, execution control, and replay

`observe()` returns an authoritative point-in-time public snapshot. `watch()` exposes an ordered public observation stream with resume/resync behavior; it is an observation surface, not an execution authority.

Pause is cooperative: already-running work may drain, while new claims are gated once the pause state becomes authoritative. Resume and accepted human input make the **existing execution** runnable again and wake/re-enqueue that same execution without changing its `ExecutionId`, immutable run pin, or durable DAG.

`execution.replay` performs deterministic validation/replay of the existing execution. It does not create a new execution, re-run LLM calls, or re-emit external business effects.

### Published code and composition

Published Python and TypeScript sources and precompiled .NET assemblies execute against immutable code, dependency material, and environment bindings pinned for the run. A pipeline language is the default; an explicit custom-step language can override it without changing unrelated steps.

The Docker validation includes nested published Child DAGs, custom `Concurrency`, `Retry`, and `Delegation` policies at their existing checkpoints, and the finite `PythonWheelBundle`, `NodeLockedBundle`, and `DotNetAssemblyClosure` dependency formats. `Retention` remains native-only. The runtime does not resolve packages from registries during execution. See [Hosted Multilanguage Execution](docs/ai/hosted-multilanguage-execution.md) and [Deterministic Dependency Packaging](docs/ai/deterministic-dependency-packaging.md).

Outbound MCP is distinct from the inbound MCP SDK boundary. Its opt-in durable effect evidence supports local replay of confirmed outcomes and fails closed on `Uncertain` effects rather than blindly resending them. This is not a generic exactly-once guarantee for external tools. See [Durable MCP Effect Evidence](docs/ai/durable-mcp-effect-evidence.md).

### Runtime hosting and worker execution

Two provider dimensions describe different responsibilities:

| Dimension | Values | Responsibility |
|---|---|---|
| `runtimeProvider` | `ProcessHostPool`, `KubernetesPool` | Runtime-host lifecycle, membership, and capacity. |
| `workerExecutionProvider` | `TrustedProcess`, `ContainerIsolationProvider` | Physical hosted-function execution boundary. |

`KubernetesPool` is not a third hosted-worker invocation transport. The validated Docker OCI cases use the production Python worker in a sibling container through the host Docker socket, not Docker-in-Docker. They do not imply that all Docker scenarios or all three worker languages were rerun under container isolation.

### Docker SDK matrix: 37/37

The [fixture-free Docker matrix](docs/ai/multilanguage-runtime-matrix-validation.md) validates all nine SDK client/worker combinations, publication pinning and dependency packaging, custom policy families, nested Child DAGs, durable MCP evidence, cancellation, runtime recovery, journal result acceptance, client dependency firewalls, and the selected isolation-provider/artifact cases.

All 37 scenarios use `runtimeProvider=ProcessHostPool`. `TrustedProcess` coverage and the bounded `ContainerIsolationProvider` / `OciImage` cases remain explicitly distinguished in the evidence.

### KubernetesPool SDK execution and closure: 3/3

The external Kubernetes scenario follows the real queue-first path:

```text
External Python SDK
    -> public MCP publication / QueueFirst submission
    -> existing KubernetesPool scale-out and Runtime Pool Pod
    -> RuntimeInstanceOnly child runtime
    -> production Python worker: HostRuntime / TrustedProcess
    -> public result: Completed
    -> uploaded-function marker: VERIFIED
```

The OCI image packages the Runtime Pool host and production workers. **It does not turn this function execution into a Kubernetes sandbox-Pod or `ContainerIsolationProvider` proof.** The control plane owns durable hosted-invocation reconciliation; runtime children own hosted-worker polling. Workers retain the reads and transitions required for their execution path.

| Accepted KubernetesPool evidence | Result |
|---|---|
| Live HTTP routing | Three runtime instances, three routed commands, deletion `ACCEPTED`. |
| Hierarchical recovery | Two runtime-process failures, two Pod failures, eight recovered shared runs, eight ownership transitions, zero violations, parent replay `54/54`, no lost runs or duplicate durable dispatches, warm reuse `PASS`. |
| External Python SDK -> Python worker | Public `Completed` result, uploaded-function marker verified, one ready Pod and one Service. |

The final closure ran the SDK scenario and revalidated existing routing/recovery evidence. It did not rerun every prior workload or establish a shared image build for all three scenarios. The accepted record is **37 Docker + 3 KubernetesPool = 40 validated scenarios across two topologies**, not a homogeneous `40/40` matrix, full Kubernetes client/worker parity, or an addition to the separate 36-row adversarial matrix.

Run commands, runtime-image selection, host-role configuration, RBAC project alignment, snapshot TTL, evidence files, and diagnostic collection are documented in [KubernetesPool Matrix Validation](docs/ai/kubernetes-pool-matrix-validation.md).

### Public SDK Watch and execution-control E2E

Separate real MCP/HTTP E2E validation is green across all three external SDK clients:

| Public SDK E2E | .NET | TypeScript | Python |
|---|---:|---:|---:|
| `execution.watch()` | PASS | PASS | PASS |
| pause / resume / human input / replay | PASS | PASS | PASS |

These checks validate the public SDK boundary and control flow. They are separate from the 37/37 Docker matrix and the 3/3 KubernetesPool closure above.


### SDK documentation and samples

Start with the [External SDK Quickstart](docs/ai/external-sdk-quickstart.md) for publication-to-result examples in all three client languages. [External SDK Libraries](docs/ai/external-sdk-libraries.md) covers local packaging, transport configuration, and client semantics; [Public SDK Boundary](docs/ai/public-sdk-boundary.md) defines the portable contracts and authorization boundary.

Reusable user-code samples are under [`implementations/sdk/samples/published-functions/`](implementations/sdk/samples/published-functions/). They are published function inputs, not substitutes for the production hosted workers.

---

</details>

<details>

<summary><b>Architecture at a glance &amp; identity model</b></summary>

## Architecture at a glance

```text
Client / API / MCP
        ↓
RBAC ExecutionContext
        ↓
Durable ExecutionContextSnapshot
        ↓
Shared Runtime Controller
        ↓
Shared Run Store / Shared Queue
        ↓
Tenant-Aware Admission
        ↓
Registry / Capacity / Reservations
        ↓
Provider Selection
        ↓
Local / HTTP / gRPC Provider
        ↓
Runtime Host Manager
        │
        ├── Local
        ├── Process
        ├── Kubernetes
        ├── ProcessHostPool
        └── KubernetesPool
                ↓
        exact RuntimeInstanceId
                ↓
DAG Execution Engine
        ↓
Redis Hot State + Lua Coordination
        ↓
Step Executors / Plugins
        ↓
MongoDB Payloads / Snapshots / History
        ↓
Canonical Engine Event
        ↓
Existing Event Manager
        ↓
Central Projection Catalog
        ├── Decision Ledger
        ├── Recovery Forensics
        ├── Runtime Lifecycle Journal
        ├── Metrics
        ├── Logging
        └── Realtime
                ↓
Replay / Trace / Deterministic Lifecycle Observation
```

The architecture deliberately separates:

```text
logical execution identity
physical execution attempt
runtime capacity identity
transport route
infrastructure failure boundary
durable recovery authority
```

That separation is what makes deterministic recovery possible.

---

## Identity model

Important identities include:

| Identity | Responsibility |
|---|---|
| `TenantId` | Durable tenant-isolation boundary. |
| `TenantGroupId` | Tenant grouping and shared-isolation context. |
| `SharedRunId` | Durable shared work identity. |
| `LocalRunId` | Runtime-local physical attempt. |
| `ExecutionId` | Durable DAG execution identity. |
| `RuntimeInstanceId` | Independently selectable runtime capacity. |
| `WorkerId` | Worker execution identity. |
| `PoolId` | Logical reusable Runtime Pool. |
| `HostId` | Parent hosting-boundary identity. |
| `KubernetesPodUid` | Kubernetes failure-boundary identity. |
| `RouteId` | Exact transport-route incarnation. |
| `FailureId` | Durable failure observation. |
| `ClaimId` | Deterministic recovery-claim identity. |
| `LeaseId` | Active claim generation. |
| `CorrelationId` | Cross-component correlation. |
| `CausationId` | Causal relationship. |

Critical invariant:

```text
ExecutionId is not RuntimeInstanceId.
RuntimeInstanceId is not HostId.
HostId is not Pod UID.
Pod UID is not a transport route.
LocalRunId is not the durable execution.
```

A physical execution attempt may be replaced while the logical execution remains the same.

---

</details>

<details>

<summary><b>Deterministic DAG execution &amp; durable Child DAG composition</b></summary>

## Deterministic DAG execution

The DAG engine provides:

- dependency resolution;
- durable step state;
- atomic claims;
- retries;
- stale-work recovery;
- deterministic convergence;
- pause;
- resume;
- cancellation;
- human input;
- terminal snapshots;
- payload externalization;
- retention and compaction;
- replay evidence;
- execution-correlated decisions.

A failed physical attempt may start work that is retried later.

Correctness is based on durable logical identities and transitions, not simplistic process-level invocation counts.

See [Architecture overview](docs/ai/architecture-overview.md).

---

## Durable Child DAG composition

A parent DAG can delegate to another durable DAG execution, release runtime capacity while waiting, and resume through a deterministic continuation.

```text
Parent ExecutionId
        ↓
ExecuteChildDag
        ↓
durable child relation
        ↓
Child ExecutionId
        ↓
parent step = WaitingForExternal
        ↓
parent runtime capacity released
        ↓
child executes / retries / recovers
        ↓
child terminal result frozen
        ↓
deterministic continuation scheduled
        ↓
same parent ExecutionId resumes
```

The Child DAG path reuses the existing DAG engine, Shared Run Store, Shared Queue, runtime providers, policy boundaries, recovery ownership, Ledger, tracing, lifecycle, and Forensics.

It does not introduce a second orchestration engine, Child-DAG-specific queue, second event bus, or alternate recovery model.

### Deterministic child identity

```text
ParentExecutionId
        ↓
ParentCallSiteId
        ↓
ChildInvocationKey
        ↓
ChildExecutionId
        ↓
ContinuationId
        ↓
Continuation SharedRunId
```

### Waiting without holding runtime capacity

`WaitingForExternal` is durable.

The parent does not keep a physical runtime slot occupied while the child runs.

### Continuation semantics

Call-site terminality alone does not imply that a continuation has been consumed.

A `Completed` call-site can legitimately coexist with a still-`Scheduled` continuation while the parent remains non-terminal.

That distinction matters during crash recovery and deterministic redrive.

### Recursive validation

Current validation reaches:

```text
ChildDepth = 3
```

The proof evidence is explicitly bounded at depth 3.

The runtime may use the same execution contract for deeper nesting, but deeper-depth validation is not claimed by the current proof matrix.

See [Durable Child DAG composition](docs/ai/child-dag-composition.md).

---

</details>

<details>

<summary><b>Runtime hosting models, ProcessHostPool, KubernetesPool &amp; exact routing</b></summary>

## Runtime hosting models

| Mode | Boundary model | Transport | Reusable capacity | Status |
|---|---|---|---:|---|
| Local | In-process runtime | Local | N/A | Implemented |
| Process | One external runtime process | HTTP / gRPC | No | Implemented / validated |
| Kubernetes | One runtime instance per Pod/Service | HTTP / gRPC | No | Implemented / validated |
| ProcessHostPool | Parent ProcessHost containing multiple runtimes | HTTP / gRPC | Yes | Implemented / validated |
| KubernetesPool | Kubernetes Pod containing multiple runtimes | HTTP / gRPC | Yes | Implemented / validated |

The hosting model changes physical placement.

It does not change the logical execution contract.

---

## ProcessHostPool

ProcessHostPool provides bounded reusable capacity across real external parent processes.

```text
Logical ProcessHostPool
    ├── ProcessHost A
    │      ├── Runtime A1
    │      ├── Runtime A2
    │      └── Runtime A3
    │
    ├── ProcessHost B
    │      ├── Runtime B1
    │      ├── Runtime B2
    │      └── Runtime B3
    │
    └── ProcessHost C
           ├── Runtime C1
           ├── Runtime C2
           └── Runtime C3
```

Each child runtime has its own `RuntimeInstanceId`.

The parent ProcessHost is a hosting and failure boundary, not an execution identity.

### Isolated child-runtime failure

```text
Runtime A2 dies
        ↓
ProcessHost A survives
        ↓
A1 and A3 remain valid
        ↓
exact failed runtime becomes unsafe
        ↓
affected work recovered
        ↓
replacement runtime restores membership
```

Healthy siblings are preserved.

### Full ProcessHost failure

```text
ProcessHost B dies
        ↓
B1 / B2 / B3 disappear
        ↓
exact failed membership identified
        ↓
durable failure recorded
        ↓
recovery candidates claimed
        ↓
replacement ProcessHost created
        ↓
replacement runtimes registered
        ↓
affected SharedRuns recovered
```

This creates a genuinely hierarchical failure model:

```text
child runtime failure
    ≠
full parent-boundary failure
```

Recovery scope follows the failure scope.

See [Runtime Pool architecture](docs/ai/runtime-pool-architecture.md).

---

## KubernetesPool

KubernetesPool applies the same reusable Runtime Pool model inside a real Kubernetes failure boundary.

```text
Kubernetes Node
        ↓
Pod = infrastructure failure boundary
        ↓
in-Pod Runtime Pool
        ├── Runtime A1
        ├── Runtime A2
        ├── Runtime A3
        └── ...
```

The Pod does not become the execution identity.

Each in-Pod runtime remains independently registered and selectable by exact `RuntimeInstanceId`.

Validated behavior includes:

- multiple independent runtimes per Pod;
- bounded Pod count;
- bounded runtimes per Pod;
- HTTP and gRPC;
- exact in-Pod child-runtime failure;
- parent Pod survival during isolated child failure;
- healthy sibling preservation;
- exact child replacement;
- distinct fully busy Pod failure;
- external/manual Pod deletion;
- exact failed-Pod work recovery;
- warm Pod reuse;
- deterministic final cleanup.

For local setup, see the [Kubernetes / Minikube guide](docs/ai/kubernetes-local-environment.md).

---

## Exact routing

The control plane selects one exact `RuntimeInstanceId`.

```text
Control Plane
        ↓
selected RuntimeInstanceId
        ↓
provider
        ↓
exact transport route
        ↓
exact runtime
```

Transport routing is not allowed to silently substitute a sibling runtime.

Route identity and execution identity remain separate.

See:

- [HTTP runtime provider](docs/ai/http-runtime-provider.md)
- [gRPC runtime provider](docs/ai/grpc-runtime-provider.md)
- [Runtime discovery, registry, and capacity](docs/ai/runtime-discovery-registry-capacity.md)

---

</details>

<details>

<summary><b>Deterministic recovery, durable failure authority &amp; warm capacity reuse</b></summary>

## Deterministic recovery

### In-flight work

```text
SharedRunId
LocalRunId
ExecutionId
RuntimeInstanceId
        ↓
physical runtime dies
        ↓
durable failure authority
        ↓
exact candidate inventory
        ↓
recovery claim
        ↓
replacement RuntimeInstanceId
replacement LocalRunId
same ExecutionId
```

Core invariant:

```text
ExecutionIdBefore == ExecutionIdAfter
```

The physical attempt changes.

The durable logical execution does not.

### Durable queued work

A dead local queue is not durable recovery authority.

```text
dead process-local queue
    ≠ durable truth
SharedRunId
    ↓
shared durable state
    ↓
redispatch
```

### Claim-protected recovery mutation

Multiple coordinators may observe the same failure.

Only one recovery mutation authority should win.

```text
FailureId
PoolId
HostId
RuntimeInstanceId
RouteId
InventoryFingerprint
ClaimId
LeaseId
```

Observation can be concurrent.

Mutation is claim-protected.

See [Runtime Pool failure recovery](docs/ai/runtime-pool-failure-recovery.md).

---

## Durable failure authority

Physical failure becomes a durable first-class fact.

```text
physical failure
        ↓
FailureId
        ↓
Runtime Pool Failure Journal
        ↓
exact failed membership
        ↓
candidate inventory
        ↓
recovery claim
        ↓
resume / redispatch
```

Failure state is not reconstructed solely from transient process logs, process exit codes, or registry snapshots.

See [Runtime Pool failure authority](docs/ai/runtime-pool-failure-authority.md).

---

## Warm capacity reuse

Healthy Runtime Pool capacity is reused between execution cycles.

```text
cycle 1
    ↓
create bounded pool
    ↓
execute + recover
    ↓
keep healthy converged capacity
cycle 2
    ↓
reuse warm pool
    ↓
execute + recover
    ↓
final deterministic cleanup
```

Replacement capacity is introduced because of actual failure, not merely because new work arrives.

---

</details>

<details>

<summary><b>Replay, evidence, ledger/forensics &amp; event-driven lifecycle observation</b></summary>

## Replay, audit, and durable evidence

The runtime persists enough evidence to reconstruct and validate execution after completion or recovery.

```text
terminal snapshot
    +
deterministic fingerprint
    +
DAG / step state
    +
payload references
    +
Decision Ledger
    +
Runtime Lifecycle Journal
    +
trace
    +
Recovery Forensics
        ↓
post-execution reconstruction and validation
```

Replay foundations include:

- audit-only replay;
- restore replay;
- deterministic fingerprint validation;
- replay metadata;
- Ledger loading;
- trace loading;
- lifecycle reconstruction;
- post-crash recovery replay proof.

Current proof boundary:

```text
Parent replay                     VERIFIED
Dedicated recursive-child replay NOT_EVALUATED
```

See [Replay and audit](docs/ai/replay-and-audit.md).

---

## Ledger, lifecycle, forensics, metrics, and realtime

Observability is intentionally split by responsibility.

```text
Decision Ledger
    → durable execution and decision evidence
Runtime Lifecycle Journal
    → append-only host / Pod / runtime / placement history
Recovery Forensics
    → work-item-level recovery timeline
Metrics
    → quantitative operational projection
Logging
    → operational diagnostics
Realtime
    → live event delivery
```

These surfaces are correlated through first-class identities but remain independent responsibilities.

See:

- [Runtime Lifecycle Journal](docs/ai/runtime-lifecycle-journal.md)
- [Runtime recovery forensics](docs/ai/runtime-recovery-forensics.md)
- [Recovery replay, Ledger, and trace proof](docs/ai/recovery-replay-ledger-trace-proof.md)

---

## Event-driven lifecycle architecture

Canonical engine facts flow through the existing Event Manager.

No parallel event bus is introduced.

```text
Engine semantic fact
        ↓
Canonical Event Namespace
        ↓
Existing Event Manager
        ↓
Central Projection Catalog
        ├── Decision Ledger
        ├── Recovery Forensics
        ├── Runtime Lifecycle Journal
        ├── Metrics
        ├── Logging
        └── Realtime
```

Architectural rule:

```text
ONE ENGINE FACT
=
ONE CANONICAL EVENT
=
ONE CANONICAL DECLARATION
=
ONE CENTRAL DISPATCH PATH
```

The Event Manager centralizes observation ownership without pretending that every projection shares one transactional boundary.

Projection durability can differ:

```text
RequiredDurable
ReplayableDurable
BestEffort
None
```

See [Engine event observation and lifecycle catalog](docs/ai/engine-event-observation.md).

---

## Deterministic EventDriven testing

Reference synchronization:

```text
durable evidence check
        ↓
subscribe to realtime canonical events
        ↓
durable evidence re-check
        ↓
await canonical event if still needed
        ↓
verify final durable state
```

Events are synchronization.

Durable stores remain correctness authority.

Hard watchdogs remain mandatory.

Historical polling paths remain where useful for compatibility and regression coverage.

See [Testing strategy](docs/ai/testing-strategy.md).

---

</details>

<details>

<summary><b>Configuration, policy, queue-first coordination &amp; RBAC security context</b></summary>

## Configuration-driven runtime

Runtime structure is configurable rather than hard-coded into the execution engine.

Configuration areas include:

- provider;
- transport;
- hosting mode;
- Runtime Pool bounds;
- queue behavior;
- retry;
- retention;
- concurrency;
- isolation;
- observability;
- persistence;
- admission;
- runtime-host settings.

The architectural separation is:

```text
Configuration
    defines runtime structure and operating parameters.
Policy
    decides what should happen for this execution.
Plugin
    performs domain work.
```

See [Configuration-driven runtime](docs/ai/config-driven-runtime.md).

---

## Policy-driven execution

Policies govern decisions such as:

- retry;
- retention;
- concurrency;
- admission;
- isolation;
- runtime selection;
- failure handling;
- recovery;
- execution control;
- resource pressure.

The engine remains responsible for deterministic state transitions.

Policies decide behavior within explicit boundaries.

See [Policy-driven execution](docs/ai/policy-driven-execution.md).

---

## Queue-first admission and distributed coordination

Submissions can enter durable shared state before runtime capacity becomes available.

```text
Client
    ↓
Shared Runtime Controller
    ↓
SharedRun
    ↓
Shared Queue
    ↓
Tenant-aware admission
    ↓
Runtime capacity
    ↓
dispatch
```

This allows the runtime to handle:

- bounded capacity;
- transient backpressure;
- runtime loss;
- redispatch;
- recovery;
- warm-pool reuse;

without losing the durable logical submission.

### Redis responsibilities

Redis provides hot coordination for:

- shared queue state;
- registry and capacity;
- claims;
- leases;
- reservations;
- atomic transitions;
- recovery coordination.

Lua scripts are used where atomic conditional transitions are required.

### MongoDB responsibilities

MongoDB provides durable state and evidence for areas such as:

- execution records;
- payloads;
- snapshots;
- lifecycle history;
- failure facts;
- Recovery Forensics.

Redis and MongoDB intentionally serve different responsibilities.

---

## Security, RBAC, and durable execution context

Authorization context is captured before asynchronous execution leaves the original request scope.

```text
API / MCP
        ↓
RBAC ExecutionContext
        ↓
durable ExecutionContextSnapshot
        ↓
SharedRun
        ↓
Shared Queue
        ↓
runtime dispatch
        ↓
background continuation / recovery
        ↓
RBAC context restored
```

This matters because the original request may no longer exist when:

- queued work starts;
- a Child DAG continuation resumes;
- a background reconciler operates;
- a runtime is replaced;
- recovery moves execution to new physical capacity.

Authorization failure and capacity pressure remain separate concerns.

See [Multi-tenant control-plane isolation](docs/ai/multi-tenant-control-plane-isolation.md).

---

</details>

<details>

<summary><b>Multi-tenant isolation &amp; pluggable execution</b></summary>

## Multi-tenant isolation

The durable tenant boundary is:

```text
ExecutionContextSnapshot.TenantId
```

Tenant-aware behavior is validated across:

- registry;
- capacity;
- admission;
- reservations;
- queueing;
- runtime selection;
- scale-out;
- recovery;
- Ledger;
- replay;
- lifecycle;
- Recovery Forensics.

Supported isolation models include:

```text
Shared
Dedicated
Hybrid
```

Tenant ownership is typed, not inferred from names or diagnostic metadata.

---

## Pluggable execution

The runtime is content-agnostic.

A step can represent:

- LLM execution;
- RAG;
- MCP tools;
- database operations;
- human approval;
- HTTP services;
- gRPC services;
- file processing;
- polyglot processes;
- other domain-specific work.

The engine owns orchestration correctness.

Plugins own domain behavior.

See [Step plugins](docs/ai/step-plugins.md).

### Hosted multilanguage execution

Published Python and TypeScript sources and precompiled .NET assemblies execute in hosted processes through immutable publications, whole-run version pinning, and a durable invocation journal. The existing DAG retains claim, retry, recovery, and continuation authority; hosted function workers do not become runtime instances.

Custom `Concurrency`, `Retry`, and `Delegation` policies use the same language infrastructure at their existing family checkpoints; `Retention` remains native-only. Outbound MCP is a separate invocation mode using server-owned connections and the existing RBAC engine.

**Scope:** this is the opt-in server-side execution layer; the independently consumable clients are described in the [External SDKs section](#sdk). `TrustedProcess` isolation is not a hostile-code sandbox. MCP effect identity alone does not provide durable replay; the separate opt-in [durable MCP effect journal](docs/ai/durable-mcp-effect-evidence.md) adds confirmed-outcome replay and fail-closed uncertainty handling, not generic exactly-once external execution.

See [Hosted Multilanguage Execution](docs/ai/hosted-multilanguage-execution.md) and [Hosted Multilanguage Validation](docs/ai/hosted-multilanguage-validation.md).

---

</details>

<details>

<summary><b>Full capability matrix</b></summary>

## Core capabilities

| Capability | Status |
|---|---:|
| Deterministic DAG execution | Implemented |
| Durable Child DAG composition | Implemented / validated |
| Recursive depth-3 validation | Validated |
| Exact recursive child logical-step accounting | Validated |
| Redis hot state and Lua coordination | Implemented |
| MongoDB durable state / evidence | Implemented |
| Distributed workers and step claims | Implemented |
| Retry and stale-work recovery | Implemented |
| Pause / resume / cancel / human input | Implemented |
| Retention / compaction / payload externalization | Implemented |
| Snapshot foundations | Implemented |
| Audit replay | Implemented |
| Restore replay | Implemented |
| Replay Ledger / trace evidence | Implemented / validated |
| Configuration-driven runtime | Implemented foundation |
| Policy-driven execution | Implemented |
| Pluggable execution | Implemented foundation |
| Decision Ledger | Implemented |
| Runtime Lifecycle Journal | Implemented / validated |
| Runtime Pool Failure Journal | Implemented / validated |
| Recovery Forensics | Implemented / validated |
| Metrics / tracing / realtime foundations | Implemented |
| Canonical engine-event observation | Implemented / validated |
| EventDriven lifecycle observer | Implemented / validated |
| RBAC execution-context propagation | Implemented / validated |
| Shared / Dedicated / Hybrid isolation | Implemented / validated |
| Registry / capacity / reservations | Implemented / validated |
| Shared Runtime Controller / shared queue | Implemented / validated |
| Local runtime provider | Implemented / validated |
| HTTP runtime provider | Implemented / validated |
| gRPC runtime provider | Implemented / validated |
| Kubernetes Runtime Host Provider | Implemented / validated |
| ProcessHostPool | Implemented / validated |
| KubernetesPool | Implemented / validated |
| Exact child failure isolation | Implemented / validated |
| Full-boundary recovery | Implemented / validated |
| External/manual boundary recovery | Implemented / validated |
| Warm Runtime Pool reuse | Implemented / validated |
| Claim-protected recovery | Implemented / validated |
| 36-row adversarial matrix | 36 / 36 VERIFIED |
| Redis Cluster failover validation | Further hardening |
| Multi-control-plane claim arbitration | Further hardening |
| Recovery-of-recovery | Not yet validated |
| Dedicated recursive-child replay | NOT_EVALUATED |
| Portable public publication/execution boundary | Implemented / RBAC-protected |
| External .NET, TypeScript / JavaScript, and Python SDKs | Implemented / validated |
| Public SDK execution Watch | Implemented / validated in .NET, TypeScript, and Python |
| Public SDK pause / resume | Implemented / validated in .NET, TypeScript, and Python |
| Public SDK human input | Implemented / validated in .NET, TypeScript, and Python |
| Public SDK deterministic replay validation | Implemented / validated in .NET, TypeScript, and Python |
| Real MCP/HTTP SDK control E2E | 3 / 3 VERIFIED |
| SDK-to-runtime Docker matrix | 37 / 37 VERIFIED; bounded provider/artifact scope |
| KubernetesPool routing / recovery / Python SDK closure | 3 / 3 VERIFIED; separate evidence |
| Public SDK registry releases and standalone runtime CLI | Separate deliverables |
| Additional public API / SDK polish | Planned |

---

</details>

---

## Where it sits

Compared with Temporal, Dapr, Dagster, Prefect, and LangGraph, this is an **execution-authority layer**, not another agent framework. Its recovery model emphasizes durable execution state and exact ownership rather than relying on workflow-history re-execution as its primary recovery mechanism; it adds first-class multi-tenant isolation and atomic distributed ownership that in-process checkpointing alone does not provide. Durable waiting, sub-workflows, and human-in-the-loop are not claimed as novel — mature engines have them. See [ecosystem positioning](docs/comparison-existing-tools.md).

## Current boundaries

Under active development, not a finished commercial platform. Explicitly outside current proof: dedicated recursive-child replay (`NOT_EVALUATED`), recursion beyond depth 3, and recovery-of-recovery. On ownership: the 36-row matrix proves ownership-**transition** correctness (0 violations). Mutation exclusivity relies on the runtime's atomic claim-token and compare-and-set coordination primitives; continuous ownership-**interval** exclusivity is not independently proven by this matrix. Also further hardening: Redis Cluster failover, durable multi-control-plane claim arbitration, and multi-node Kubernetes scale. See the [roadmap](docs/roadmap.md) and the [full documentation index](docs/index.md).

## Interactive AI Runtime Analysis Demo

A focused interactive demo application is included in this repository to show how the runtime can be **consumed and extended through public extension points without modifying its core**.

> **The demo is not the Deterministic AI Runtime itself.**  
> It is a small application built on top of the runtime and uses its real execution primitives.

The demo exercises:

- RBAC and atomic `ContextKey` rotation under in-flight traffic;
- Redis/Lua-backed atomic coordination;
- realtime metrics, logs, and runtime evidence;
- AI-assisted analysis of bounded execution evidence;
- pluggable steps and deterministic policies;
- AI-generated proposals;
- deterministic policy eligibility decisions;
- explicit human approval or rejection;
- durable Child DAG execution;
- recovery-aware execution and deterministic verification.

Its decision boundary is explicit:

```text
AI analyzes and proposes
        ↓
deterministic policy gates
        ↓
human approves / rejects
        ↓
runtime executes durably
        ↓
evidence verifies
```

After verification, the investigation follows one of two modes:

- **Stop when conclusion is strong** — stop when the available evidence is conclusive.
- **Continue with another useful experiment** — the AI must propose a materially different follow-up that passes policy and explicit human approval again before the next durable Child DAG is created.

The demo therefore demonstrates both application-level extensibility and the runtime boundary:

```text
Demo application
  ├── Next.js UI / traffic scenarios
  ├── AI analysis
  ├── pluggable steps
  ├── pluggable policies
  └── human approval UX
            │
            ▼
Deterministic AI Runtime
  ├── durable execution
  ├── lifecycle
  ├── DAG / Child DAG semantics
  ├── recovery
  ├── Redis coordination
  ├── MongoDB durable persistence
  └── verification
```

### Demo locations

```text
demo/
└── rbac-aiAnalysis/
    ├── Multiplexed.Sample.Demo.Rbac.AiAnalysis.csproj
    └── nextjs/
        ├── package.json
        └── README.md
```

- Demo root: [`demo/rbac-aiAnalysis`](demo/rbac-aiAnalysis)
- Demo API: [`Multiplexed.Sample.Demo.Rbac.AiAnalysis`](/demo/rbac-aiAnalysis/Multiplexed.Sample.Demo.Rbac.AiAnalysis)
- Next.js UI: [`demo/rbac-aiAnalysis/nextjs`](demo/rbac-aiAnalysis/nextjs)
- Demo README: [`demo/rbac-aiAnalysis/nextjs/README.md`](demo/rbac-aiAnalysis/nextjs/README.md)

### Run the demo

From the repository root, start the backend:

```powershell
dotnet run --project .\demo/rbac-aiAnalysis/Multiplexed.Sample.Demo.Rbac.AiAnalysis.csproj.csproj
```

In a second terminal, start the Next.js UI:

```powershell
cd .\demo\rbac-aiAnalysis\nextjs
npm install
npm run dev
```

Redis and MongoDB must be reachable according to the runtime/demo configuration. AI analysis additionally requires the provider configuration expected by the demo API.

---

## License

This project is licensed under the **Business Source License 1.1 (BSL 1.1)**.

The source code may be used for **development, testing, evaluation, research, and other non-production purposes** in accordance with the BSL 1.1 terms.

**Production use is not permitted without a separate commercial license or explicit written authorization from the Licensor.** This includes production deployments, SaaS or managed-service offerings, embedding the runtime into commercial products, white-label or OEM use, and commercial redistribution.

The Licensed Work will transition to the **Apache License 2.0** on the applicable Change Date defined in the repository license terms.

See the repository `LICENSE.md` file for the complete and authoritative licensing terms.


---

> **The runtime does not need to understand the answer. It needs to guarantee what happens to the execution that produced it.**

>

> Models may be probabilistic. Execution identity, ownership, recovery, replay, audit, and failure accounting are not.
