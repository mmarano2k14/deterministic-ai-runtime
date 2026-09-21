# Architecture Quick Start

**Core components, durable truth, failure boundaries, and trade-offs.**

The Deterministic AI Runtime runs workflows across replaceable worker and runtime processes. A workflow is a DAG: operations connected by dependencies, such as calling an LLM, executing code, invoking a tool, or waiting for human approval.

**The core idea: physical capacity is replaceable; durable execution identity and recorded progress are not tied to that capacity.**

Here, *deterministic* describes execution identity, ownership, state transitions, and replay validation. It does not make model answers deterministic or guarantee exactly-once effects in an external system.

## 1. The system in one picture

```text
Client / API / inbound MCP
           |
      RBAC + durable tenant context
           |
      CONTROL PLANE
      shared work -> admission -> capacity reservation
           |
      Local / HTTP / gRPC provider
           |
      selected RUNTIME INSTANCE
      DAG engine -> claim -> execute -> persist transition
           |
      Native plugin / hosted function / outbound MCP tool

Shared state: Redis coordination + MongoDB persistence
Recovery:     observe failure -> claim recovery -> resume / redispatch
Observation:  canonical events -> Ledger / lifecycle / forensics
```

These are responsibility boundaries, not a requirement for a separate service per box.

| Component | Owns | Does not own |
|---|---|---|
| **Control plane** | Shared submissions, tenant-aware admission, runtime selection, capacity, and recovery coordination. | Executing the body of each DAG operation. |
| **Provider and host manager** | Transport to the selected runtime; placement in local, process, or Kubernetes hosting. | Work recovery or silent routing to a different runtime. |
| **Runtime instance and DAG engine** | Dependency readiness, claims, policy checkpoints, retries, durable waits, and finalization. | The business meaning of a plugin's result. |
| **Plugin or hosted function** | Domain work and its result. Hosted functions run Python, TypeScript, or .NET code. | DAG progression; hosted functions receive no runtime-store authority. |

A hosted function process is **not** a trusted runtime instance. A supervisor manages its lifetime; the invocation journal controls its assignment and result. See [hosted execution](hosted-multilanguage-execution.md).

## 2. Where truth lives

The distributed Redis/MongoDB composition separates these authorities:

| Question | Authoritative state |
|---|---|
| What work was accepted and still needs dispatch? | **Shared Run Store and Shared Queue** in Redis, not a process-local queue. |
| Who may advance a live DAG operation? | **Redis DAG state, claims, tokens, and Lua transitions.** Redis is active state, not a disposable cache. |
| What data and definition are retained? | **MongoDB records, payloads, and snapshots.** Published functions additionally use an immutable publication and run pin. |
| Which hosted-function result may be applied? | **Durable Invocation Journal** in MongoDB: frozen inputs, lease/epoch, result, and continuation obligation. |
| What failed, and who may recover it? | **Runtime Pool Failure Journal** records the incident; **Recovery Claim Store** controls mutation. The registry describes current runtime/capacity state. |
| What happened, and why? | **Decision Ledger, Runtime Lifecycle Journal, and Recovery Forensics** retain evidence, not replacement execution authority. |

There is no single cross-store transaction. Atomic transitions protect individual boundaries; continuation and reconciliation bring their states into agreement. **Logs and realtime notifications are not proof of durable application.**

Details: [distributed execution](distributed-execution.md), [failure authority](runtime-pool-failure-authority.md), [event observation](engine-event-observation.md).

## 3. Five invariants to keep in mind

**Identity survives placement.** `SharedRunId` identifies shared work, `ExecutionId` the durable DAG, and `RuntimeInstanceId` replaceable capacity. In-flight recovery preserves `ExecutionId`, not the old physical attempt.

**Tenant ownership survives the request.** The execution-context snapshot carries `TenantId` across queueing, dispatch, and recovery. Background execution restores it rather than depending on the original request.

**Only current authority may mutate.** DAG claims protect transitions; hosted-function leases and epochs protect result acceptance. An obsolete function worker cannot gain DAG authority by returning a result.

**Hosted results: recorded, scheduled, and applied are different.** A result arriving before `Park` remains journaled. Queue acceptance is not application; acknowledgement requires the exact result receipt and terminal parent state.

**A pinned run does not follow `latest`.** Republishing code does not change an unstarted function in an existing pinned run. Missing required artifacts fail explicitly rather than selecting another version.

See [tenant isolation](multi-tenant-control-plane-isolation.md) and [hosted execution contracts](hosted-multilanguage-execution.md).

## 4. What happens when something fails?

Consider one runtime disappearing midway through a DAG:

```text
Execution E on runtime R1
    -> failure recorded; R1 excluded from capacity
    -> exact affected work enumerated; recovery claimed
    -> replacement capacity R2 selected
    -> same execution E resumes from recorded state
```

Work lost from R1's local queue is redispatched from `SharedRunId`; it may not have an `ExecutionId` yet.

| Failure boundary | Recovery scope or limit |
|---|---|
| **One runtime inside a pool** | Recover its affected work; preserve the parent host and healthy siblings. |
| **Entire ProcessHost or Kubernetes Pod** | Recover the exact failed membership; runtimes in other host boundaries remain selectable. |
| **Hosted function process** | Reject stale assignment results. An expired lease alone does not justify repeating a possibly completed external effect. |
| **Interruption after a hosted result is journaled** | Reconciliation can apply the recorded result without launching the function again. Duplicate continuation is not a new logical invocation. |
| **External MCP tool timeout or lost response** | With durable MCP effect evidence configured, the runtime preserves `Dispatching`/`Uncertain` fail-closed evidence and does not blindly re-emit. A classified pre-`tools/call` failure may become `NotSent`; explicit provider/tool reconciliation may later establish `Completed`, `NotSent`, or continued uncertainty. None of these states grants automatic retry authority. |

Process recovery does not make datastore outages transparent. Redis Cluster failover and distributed multi-control-plane recovery claims remain hardening boundaries, not established guarantees.

Details: [pool failure recovery](runtime-pool-failure-recovery.md). Durable Child DAG waiting uses the same orchestration and releases parent capacity while waiting; see [Child DAG composition](child-dag-composition.md).

## 5. The main trade-offs

| Architectural choice | Benefit | Cost or constraint |
|---|---|---|
| **Redis coordination plus durable MongoDB records** | Conditional ownership changes and retained execution/evidence data have distinct responsibilities. | More than one persistence boundary to operate and reconcile; no global transaction is implied. |
| **Explicit identities and exact routing** | Recovery targets failed work without silently substituting a healthy sibling. | Identity and tenant context must be propagated and validated across every asynchronous boundary. |
| **Bounded reusable runtime pools** | Healthy capacity survives execution cycles and can be reused. | Admission must queue or apply backpressure when available capacity is exhausted. |
| **Immutable publication and deterministic dependency bundles** | A run retains its original implementation, dependency closure, and environment selection. Supported bundles include pure-Python wheels, locked Node source bundles, and managed .NET assembly closures. | Required artifacts must remain available. Changes require publication; no package-manager or registry resolution occurs during execution. |
| **Policies decide; the engine transitions** | Behavior is extensible without moving lifecycle authority into plugins. | Hosted custom support is family-specific: `Concurrency`, `Retry`, and `Delegation` use distinct contracts at their existing checkpoints. `Retention` remains native-only, and policy taxonomy values without an independent checkpoint are not advertised as hosted capabilities. |

## 6. What this does not claim

Hosted execution has a separate public SDK contract/server boundary for publication, submission, observation, result retrieval, and cancellation. See [Public SDK Boundary](public-sdk-boundary.md) for the portable contract/server-adapter split. The portable contracts remain independent of engine DLLs, and independent .NET, TypeScript/JavaScript, and Python clients consume the same boundary; see [External SDK Libraries](external-sdk-libraries.md) and the [External SDK Quickstart](external-sdk-quickstart.md). The live fixture-free 37/37 Docker closure is recorded separately in [Multilanguage Runtime Matrix Validation](multilanguage-runtime-matrix-validation.md); it preserves the 33-scenario `ProcessHostPool` baseline and adds bounded `ContainerIsolationProvider` provider/artifact-selection scenarios. Exact inline Child DAG definitions can contain published custom Python, TypeScript, or .NET functions: the child keeps the original immutable publication through a `ChildExecutionId` binding and resumes its parent through the existing Child DAG continuation path. This does not create a second scheduler or worker authority.

A separate **3/3 KubernetesPool** closure adds live HTTP routing, hierarchical recovery, and Python SDK-to-Python `TrustedProcess` execution with `Completed` and an uploaded-function marker verified. This is not full Kubernetes client/worker parity or Kubernetes sandbox-Pod validation. See [KubernetesPool Matrix Validation](kubernetes-pool-matrix-validation.md).

The trusted-process provider is **not** a hostile-code sandbox and still rejects stronger isolation requirements rather than downgrading them. A separate `SandboxedContainer` provider is implemented for the selected Linux/amd64 OCI path: it requires an exact image-manifest digest, verifies applied isolation before releasing tenant material, denies network egress, uses a read-only root filesystem with bounded `/tmp`, runs non-root with dropped capabilities and `no-new-privileges`, and enforces bounded CPU, memory, and PID resources. This is a bounded provider/platform guarantee, not a universal hostile-code claim and not yet a Kubernetes sandbox-Pod implementation. See [Hosted Worker Isolation](hosted-worker-isolation.md) and [Hosted Worker Isolation Validation](hosted-worker-isolation-validation.md).

Outbound MCP now has an optional durable effect-evidence boundary: stable logical identity, immutable intent, a pre-call dispatch fence, local replay of confirmed `Completed` responses, conservative `NotSent`/`Uncertain` classification, and explicit reconciliation without reissuing the original business call. This still does **not** guarantee generic exactly-once external actions, and `NotSent`, `Dispatching`, or `Uncertain` do not authorize automatic redelivery. Hosted policy workers likewise do not own lifecycle transitions: `Retry` budget/backoff/state transitions and Child DAG delegation allocation/CAS remain server-owned. See [Durable MCP Effect Evidence](durable-mcp-effect-evidence.md).

Evidence is bounded: existing adversarial tests cover selected failure schedules, not all possible interleavings. Native recursive Child DAG validation reaches Depth3, while published custom Child DAG closure explicitly exercises two nested Child DAG levels. Parent replay is covered; dedicated recursive-child replay remains `NOT_EVALUATED`. See the [runtime validation matrix](adversarial-runtime-validation-matrix.md) and [hosted validation](hosted-multilanguage-validation.md).

---

**Continue reading:** [Architecture reference](architecture-overview.md) · [Replay and audit](replay-and-audit.md) · [Complete documentation](../index.md).

**Run it:** [Existing Quick start](../../README.md#quick-start) · [Local Kubernetes setup](kubernetes-local-environment.md).
