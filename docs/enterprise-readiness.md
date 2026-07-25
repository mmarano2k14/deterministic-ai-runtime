# Enterprise Readiness

This document maps core enterprise AI execution questions to the current runtime design.

The goal is to be clear and honest about what is implemented, what is available as a foundation, and what remains planned.

The runtime should not be positioned as a finished commercial platform yet. It is better described as an advanced, test-driven execution infrastructure project that is increasingly proving production-style runtime guarantees across Redis, MCP, HTTP process-host runtimes, replay, ledger, trace, and multi-tenant isolation boundaries.

---

## Status Legend

| Status | Meaning |
|---|---|
| Implemented | The runtime has current implementation and integration-test coverage or validated behavior. |
| Foundation available | The runtime has the core building blocks, but the public API, documentation, or production hardening is still evolving. |
| Planned | The capability is identified as roadmap work. |

---

## Enterprise Readiness Matrix

| Enterprise Question | Runtime Answer | Implementation Mechanism | Evidence / Tests | Status |
|---|---|---|---|---|
| What happens if a worker crashes? | The runtime can recover stale `Running` steps and make them eligible again without consuming retry budget as a normal step failure. | Redis-backed DAG state, claim ownership, claimed timestamps, stale running-step recovery, recovery count. | Integration coverage around worker recovery, retry/recovery separation, distributed execution scenarios. | Implemented |
| What happens if a runtime process dies while work is assigned to it? | The runtime separates runtime health from execution recovery. Unsafe capacity is suppressed, assigned work is reconciled, in-flight executions can resume from the same durable `ExecutionId`, and local queued work can be redispatched through the durable `SharedRunId`. | `RuntimeInstanceHealthReconciler`, `ExecutionRecoveryReconciler`, shared run store, shared queue, runtime run execution index, DAG store, registry/capacity, replay/ledger/trace evidence, recovery forensics. | Real HTTP `RuntimeInstanceOnly` process-kill scenarios validate recovery after external OS process death, including in-flight DAG resume and local queued redispatch. | Implemented / validated |
| How do you prevent duplicate executions? | Only one worker can own a step at a time. Stale or competing workers cannot complete or fail a step they do not own. | Redis Lua atomic claim scripts, claim tokens, ownership validation on complete/fail transitions. | Multi-worker and distributed claim tests validate single ownership and convergence. | Implemented |
| How do you replay a workflow? | Completed executions can be snapshotted and restored from terminal snapshot foundations. Replay can detect existing live state or restore deleted live state. MCP process-boundary scenarios also validate replay report, replay ledger, and replay trace after recovery. | MongoDB snapshots, replay service/foundations, `ExecutionId`-based snapshot restoration, deterministic replay fingerprint validation, replay report/ledger/trace retrieval through MCP tools. | Tests validate `AlreadyExists`, restore after live deletion, fingerprint equality, and process-boundary replay proof after real runtime recovery. | Implemented foundation / validated |
| How do you audit an AI decision? | Execution state, step outputs, retry metadata, retention metadata, snapshots, ledger events, trace records, and recovery forensics provide audit foundations. | Execution records, step states, persisted payloads, terminal snapshots, execution-correlated ledger, control-plane causal-chain ledger, runtime tracing, recovery forensics. | Current evidence exists through state/snapshot/observability tests and process-boundary replay/ledger/trace validation. Public audit APIs and dashboards remain roadmap work. | Foundation available / validated in scenarios |
| How do you limit concurrency? | Concurrency can be limited locally and across distributed workers/runtime instances. Provider, model, operation, execution, pipeline, step, and instance scopes are supported. | Policy-driven concurrency engine, `config.concurrency`, Redis ZSET lease gate, Lua-style atomic admission, lease expiration. | Tests validate Redis lease semantics, provider/model/operation throttling, admission denial, and release on failed claim. | Implemented |
| How do you resolve execution context safely? | The runtime resolves input bindings, previous step outputs, payload references, provider/model/operation metadata, and policy contexts through helper layers instead of scattering context-building logic across the engine. | Context resolution helpers, input resolver, step context builder, payload resolver, provider context helper, policy context builders, RAG context resolver. | Runtime usage and tests validate input binding resolution, provider/model/operation propagation, payload rehydration, RAG context resolution, and replay-safe comparison foundations. | Foundation available |
| How do you pause/resume/cancel safely? | Execution control state blocks new claims and coordinates state transitions without corrupting DAG state. Cancellation can override natural completion during finalization. | `IAiExecutionControlService`, Redis control state, control gate, claim blocking, cancellation finalization override. | Integration tests cover pause, resume, cancel, claim blocking, `Pausing -> Paused`, `Resuming -> Running`, cancellation override. | Implemented |
| How do you control human-in-the-loop? | Executions can be moved to `WaitingForInput`, new claims are blocked, and external input can be submitted to resume execution. | Durable execution control state, waiting key, waiting step name, submitted input payload, `SubmitHumanInputAsync`. | Integration tests cover waiting for input and human input submission. | Implemented |
| How do you keep memory/state bounded? | The runtime separates hot state from cold payloads and can compact or evict completed data while preserving resolver access. | Retention engine, retention triggers, compaction, eviction, payload externalization, MongoDB payload store, rehydration resolver. | Tests validate retention safety, archived payload resolution, and resolver consistency after eviction. | Implemented |
| How do you coordinate multiple runtime instances? | Runtime instances coordinate through Redis-backed state instead of direct communication. Claims, leases, concurrency admission, shared queue ownership, registry visibility, capacity descriptors, and dispatch ownership are coordinated through distributed state. | Redis DAG store, Lua atomic step claiming, Redis shared run store, Redis shared queue, Redis runtime registry, Redis runtime capacity store, Redis admission reservation store, runtime instance identity foundations. | Distributed multi-runtime-instance, shared queue, MCP, HTTP pooled runtime, and aggressive distributed scenario tests validate safe convergence and dispatch ownership. | Implemented |
| How do you prove race-condition safety under real process loss? | The runtime uses adversarial parallel scenarios with exact pre-crash inventories, durable crash gates, real operating-system process kills, safe tenants, replay, ledger, trace, forensics, and independent HTTP/gRPC validation. The harness separates infrastructure saturation, lifecycle defects, recovery convergence defects, and harness races. | Real external process hosts, stable scale-out deduplication, readiness lifecycle, shared queue claims, runtime execution index, durable DAG state, recovery reconcilers, tenant-scoped evidence, and server-side Redis/MongoDB counters. | P10–P30 form the repeatable validation range. HTTP and gRPC P35 completed 35/35 with 105 tenants, 315 DAG executions, 70 real process kills, and 210 affected jobs recovered per transport. | Implemented / validated |
| Does the runtime depend on the language or semantic content of a step? | No. The runtime owns execution semantics rather than model semantics. Steps may be RAG, LLM calls, MCP tools, network services, database operations, human approval, or code implemented in any language behind a supported adapter. | Step plugins and provider adapters return results into the same admission, policy, ownership, retry, retention, eviction, recovery, replay, and observability lifecycle. | Architecture and process-host tests validate the execution protocol independently of step business meaning. Workload-specific latency, side effects, streaming, and provider limits remain policy and adapter concerns. | Implemented architecture boundary |
| How do you prove deterministic convergence? | Final execution status and completed outputs are derived from state, not execution order. Replay fingerprints validate restored terminal state. | DAG dependency rules, explicit state transitions, atomic claims, retry state, terminal finalization, deterministic fingerprint checks. | Tests validate large DAG completion, multi-worker convergence, retry convergence, replay fingerprint equality, and recovery convergence after real process death. | Implemented |
| How do you submit work when no runtime capacity exists? | Admission can return `RequestScaleOut` instead of rejecting or queueing blindly. The shared run is persisted as `ScaleOutRequested`, a Redis scale-out request is created, and capacity can be created by a provider-backed scale-out flow. | Direct-dispatch submit mode, `IAiRunAdmissionController`, `StoreBackedAiRuntimeScaleOutRequestPublisher`, `RedisAiRuntimeScaleOutRequestStore`, `AiRuntimeScaleOutRequestWatcherHostedService`, `AiRuntimeScaleOutProviderSelector`. | MCP Redis local scale-out tests validate zero initial runtime capacity, `ScaleOutRequested`, persisted scale-out request, watcher processing, and fulfilled request. | Implemented |
| How do you scale local runtime capacity without bypassing the architecture? | Scale-out is a provider capability, not a separate scheduler. The local provider can create a new isolated local runtime instance through the local runtime scaler while preserving the local queue ownership boundary. | `IAiRuntimeScaleOutProvider` extends `IAiRuntimeInstanceProvider`; `LocalAiRuntimeInstanceProvider`; `IAiLocalRuntimeInstanceScaler`; `AiLocalRuntimeInstanceScaler`; local runtime instance host/factory; runtime registration and capacity publication. | Local provider scale-out tests, local scaler tests, provider selector tests, and MCP Redis local scale-out tests validate dynamic runtime creation and registration. | Implemented |
| How do you launch real runtime capacity instead of fake test capacity? | The HTTP provider delegates host lifecycle to the Runtime Host Manager. In process mode, a real `RuntimeInstanceOnly` process starts, self-registers, publishes capacity, becomes visible, and is then used through the normal dispatch path. | `IAiRuntimeHostManager`, `AiRuntimeHostCreationManager`, `ProcessAiRuntimeHostCreationStrategy`, HTTP scale-out provider, Redis discovery/registry/capacity, readiness wait. | MCP production runtime scenario tests validate real child process launch, registration, capacity visibility, dispatch, DAG completion, retention, ledger, trace, and replay across process boundaries. | Implemented / validated |
| How do you ensure a scale-out-created run is actually executed? | A fulfilled scale-out request requeues the original shared run. The normal shared queue pump claims it, performs dispatch-time admission using newly visible capacity, dispatches to the created runtime instance, and the local runtime executes the DAG. | `IAiScaleOutFulfilledRunRequeueService`, `AiScaleOutFulfilledRunRequeueService`, `IAiSharedQueue`, `IAiSharedQueuePump`, dispatch-time admission, provider dispatch, local runtime queue, local background controller. | The final MCP test validates `ScaleOutRequestStatus=Fulfilled`, `SharedRunStatus=Dispatched`, assigned runtime instance, `LocalRunId`, `ExecutionId`, and `RuntimeRunStatus=completed`. | Implemented |
| How do you prove one tenant's runtime crash does not contaminate another tenant? | Tenant context is durable execution input. Crash recovery queries, replay, ledger, trace, and forensics remain tenant-scoped. A safe tenant should continue normally and should not receive recovery records for another tenant's failure. | Durable `ExecutionContextSnapshot`, tenant-aware registry/capacity filtering, tenant-aware admission, tenant-scoped recovery forensics, ledger isolation checks, replay/trace queries scoped by tenant/run/execution identity. | Multi-tenant process-kill validation proves impacted tenants recover while the safe tenant completes normally with recovered work count `0`, recovery forensics count `0`, and no cross-tenant ledger leakage. | Implemented / validated |
| How do you inspect the recovery path after a crash? | Recovery is treated as evidence, not only as a successful final status. Forensics records show which work was detected, why it was recoverable, how it was redispatched or resumed, and which replacement runtime received it. | Runtime recovery forensics records, control-plane ledger causal-chain entries, execution trace timeline, replay report, replay ledger, replay trace. | Process-host crash scenarios validate readable forensics, causal-chain counts, replay proof, ledger proof, trace proof, and safe tenant absence from recovery evidence. | Implemented / validated |
| How do you prepare this for Kubernetes scaling? | The control loop is validated locally and through real process-host creation before Kubernetes pod creation exists. Kubernetes can later replace process/local host creation behind the same provider/host-manager boundary. | Provider-based scale-out model, Runtime Host Manager, Redis scale-out request store, runtime registry, capacity descriptors, shared queue pump, provider routing, future Kubernetes host creation strategy. | Local scale-out and process-host scale-out validate the lifecycle that Kubernetes will reuse; actual Kubernetes pod/deployment scaling remains planned. | Foundation available |

---

## Current Strengths

The current runtime is strongest in these areas:

- deterministic DAG execution
- Redis-backed distributed coordination
- Redis Lua atomic state transitions
- context resolution and helper foundations
- retry and recovery safety
- runtime process crash recovery across real `RuntimeInstanceOnly` processes
- stale step recovery versus runtime crash recovery separation
- bounded hot state through retention and compaction
- provider/model/operation throttling
- execution control state
- background queue control
- shared runtime controller and shared queue coordination
- Redis-backed shared run and shared queue persistence
- runtime instance registry and capacity visibility
- Redis-backed admission reservations
- provider-based local and HTTP pooled runtime dispatch
- HTTP runtime provider hardening
- Runtime Host Manager process-host provisioning
- Redis-backed scale-out request lifecycle
- local runtime scale-out from zero executable capacity
- HTTP process-host scale-out from zero executable capacity
- fulfilled scale-out shared run requeue
- MCP-validated scale-out dispatch and execution completion
- tenant-aware Shared/Dedicated/Hybrid isolation
- multi-tenant crash isolation with safe tenant non-impact
- replay and snapshot foundations
- ledger, trace, replay report, replay ledger, and replay trace validation across process boundaries
- runtime recovery forensics
- control-plane causal-chain proof
- integration-test-driven validation
- adversarial HTTP/gRPC process-host concurrency validation through P35
- content-agnostic step execution boundary
- stable single-flight recovery scale-out identity

---

## Validated Scale-Out Evidence

The Redis/local scale-out flow is validated end-to-end through MCP integration tests.

Validated evidence:

```text
Initial ActiveLocalInstances = 0
Admission = RequestScaleOut
SharedRun.Status = ScaleOutRequested
ScaleOutRequest.Status = Fulfilled
ScaleOutRuntimeInstanceId = host-...:mcp-scaleout-runtime-1
ActiveLocalInstances = 1
SharedRun.Status = Dispatched
AssignedRuntimeInstanceId = host-...:mcp-scaleout-runtime-1
QueueStatus = Dispatched
LocalRunId = available
ExecutionId = available
RuntimeRunStatus = completed
```

This proves:

- a run can be submitted when no executable runtime capacity exists
- admission can request scale-out
- the scale-out request is persisted in Redis
- the watcher can observe and process the request
- the provider selector can resolve the local scale-out-capable provider
- the local scaler can create and start a new runtime instance
- the new runtime instance registers and publishes capacity
- the scale-out request is marked fulfilled
- the original shared run is requeued
- the normal shared queue pump dispatches the requeued run
- the created runtime instance executes the run to completion

---

## Validated Process-Host Crash Recovery Evidence

The runtime also validates recovery when the failed participant is not an in-memory fixture, but a real external runtime host process.

Validated scenario shape:

```text
Shared control plane
    ↓
HTTP provider
    ↓
Runtime Host Manager
    ↓
real RuntimeInstanceOnly child processes
    ↓
tenant runtime processes killed
    ↓
health reconciliation suppresses unsafe capacity
    ↓
execution recovery reconciles assigned work
    ↓
replacement runtime capacity becomes visible
    ↓
in-flight work resumes
    ↓
local queued work is redispatched
    ↓
replay / ledger / trace / forensics proof is readable
```

The most complete validation uses three tenants:

```text
Tenant A
    runtime process killed
    1 in-flight execution recovered by DAG resume
    2 local queued runs recovered by redispatch

Tenant B
    runtime process killed
    1 in-flight execution recovered by DAG resume
    2 local queued runs recovered by redispatch

Tenant C
    runtime process not killed
    3 runs complete normally
    recovered work = 0
    recovery forensics = 0
```

This matters because it proves something more precise than “the run eventually completed”.

It proves that:

- in-flight DAG execution identity is durable
- `ExecutionId` is preserved for in-flight recovery
- local queued work can be recovered even when no `ExecutionId` existed yet
- `SharedRunId` remains the durable submission identity for local queued redispatch
- `LocalRunId` remains attempt-local and may change after recovery
- unsafe runtime capacity is not selected for new work
- the HTTP provider reports transport failure but does not own recovery
- recovery is performed by the control-plane recovery components
- unrelated tenant work is not marked as recovered
- replay, ledger, trace, and forensics remain readable after recovery

Representative proof:

```text
Total submitted runs = 9
Total replay proofs = 9 / 9
Impacted tenants recovered work = 6
Safe tenant recovered work = 0
Safe tenant recovery forensics = 0
CrossTenantLedgerLeakDetected = false
SafeTenantRecoveryLeakDetected = false
SafeTenantNonImpactValidated = true
```

This is not presented as a benchmark.

It is better understood as a contract test: if a runtime claims durable execution, it should be able to show not only that failed work recovered, but also that unrelated tenants remained untouched.

---

## Recovery Boundary

The recovery design intentionally separates responsibilities.

```text
RuntimeInstanceHealthReconciler
    detects stale / unsafe / draining runtime capacity
    prevents unsafe capacity from being selected for new work

ExecutionRecoveryReconciler
    enumerates work already assigned to unsafe runtime capacity
    recovers in-flight DAG executions
    redispatches local queued shared runs

HTTP provider
    reports transport / endpoint failure signals
    dispatches over HTTP when capacity is safe
    does not own runtime recovery
    does not kill, restart, or replace runtimes

Local runtime queue
    is volatile
    is not the source of truth

Durable truth
    shared run store
    shared queue
    runtime run execution index
    DAG store
    registry / capacity
    ledger / trace / forensics / replay evidence
```

This boundary keeps recovery from becoming an accidental side effect of the transport layer.

The provider can observe that a runtime endpoint is unreachable.

The control plane decides whether the runtime is unsafe.

The recovery reconciler decides what assigned work must be recovered.

---


## Adversarial Concurrency Validation Evidence

The local concurrency campaign intentionally concentrates more lifecycle pressure than a production topology should.

P35 represents:

```text
35 scenarios
105 tenants
315 real 50-step DAG executions
70 real process kills
210 affected jobs recovered
15,750 logical DAG step completions
```

The final HTTP run also measured 4,191,448 server-side Redis and MongoDB operations and 18.29 GiB of datastore traffic.

The correct interpretation is not that one machine defines production capacity.

The result is that ownership, execution identity, safe-tenant isolation, recovery, replay, ledger, trace, and forensics remained correct while local capacity degraded.

Production should distribute the same protocol across warm runtime pools, tenant-aware cells, bounded scale-out, multiple nodes, and managed or clustered datastores.

See:

- [Concurrency Hardening and Adversarial Validation](ai/concurrency-hardening-and-adversarial-validation.md)


## Honest Boundaries

The project should not be presented as a finished commercial platform yet.

The following areas are still evolving:

- production-grade public replay API
- production dashboard and operator UI
- OpenTelemetry exporters
- Prometheus/Grafana integration
- Kubernetes deployment package
- Kubernetes pod/deployment scale-out adapter
- Redis command queue runtime provider
- gRPC runtime provider
- production multi-control-plane leader election
- full provider capability negotiation
- database-backed tenant runtime settings provider
- public SDK/API polish
- enterprise sample applications
- continued documentation refinement beyond Phase 0 V1

The following should be described as validated foundations rather than future work:

- HTTP provider hardening
- Runtime Host Manager process-host provisioning
- real `RuntimeInstanceOnly` process launch
- process-boundary replay / ledger / trace validation
- runtime health to execution recovery boundary
- real runtime process crash recovery
- safe tenant non-impact during crash recovery
- runtime recovery forensics

---

## Ecosystem Positioning

Deterministic AI Runtime is not intended to replace agent frameworks, workflow orchestrators, data pipeline tools, observability platforms, or distributed infrastructure.

Existing tools are strong in their own domains.

This runtime focuses on a specific architectural problem:

```text
deterministic, distributed, state-driven AI execution
```

That means the project is focused on runtime guarantees such as:

- distributed step ownership
- Redis Lua coordination
- retry and recovery separation
- runtime process crash recovery
- bounded hot state
- context resolution
- provider/model/operation throttling
- execution control state
- human-in-the-loop control
- shared queue ownership
- provider-based runtime dispatch
- provider-based scale-out lifecycle
- replay foundations
- ledger and trace evidence
- deterministic convergence
- tenant-aware runtime isolation

For a detailed comparison with existing tools and categories, see:

- [Comparison with Existing Tools](comparison-existing-tools.md)

---

## Enterprise Positioning

The project is best positioned as:

> A deterministic AI execution runtime for production-grade AI workloads.

It is especially relevant for teams exploring how to move from prompt-level or agent-demo AI systems toward reliable execution infrastructure.

The key architectural message is:

> AI orchestration becomes a distributed systems problem once it reaches production.

A more recent way to describe the runtime is:

> When a runtime process dies, the execution should not become guesswork. The system should know what was running, what was only queued, what can resume, what must be redispatched, and which tenants were never affected.

The repository should be presented as an advanced reference implementation and evolving infrastructure project.

It should be positioned seriously, without overstating its maturity.

---

## Related Documents

- [Architecture Overview](ai/architecture-overview.md)
- [Retry and Recovery](ai/retry-and-recovery.md)
- [Runtime Process Crash Recovery](ai/runtime-process-crash-recovery.md)
- [Runtime Recovery Forensics](ai/runtime-recovery-forensics.md)
- [Multi-Tenant Runtime Crash Isolation](ai/multi-tenant-runtime-crash-isolation.md)
- [Control-Plane Ledger Causal Chain](ai/control-plane-ledger-causal-chain.md)
- [Recovery Replay Ledger Trace Proof](ai/recovery-replay-ledger-trace-proof.md)
- [Runtime Control Plane](ai/runtime-control-plane.md)
- [HTTP Runtime Provider](ai/http-runtime-provider.md)
- [MCP Production Runtime Scenario Framework](ai/mcp-production-runtime-scenario-framework.md)
- [Execution-Correlated Ledger](ai/execution-correlated-ledger.md)
- [Observability](ai/observability.md)
- [Testing Strategy](ai/testing-strategy.md)
- [Concurrency Hardening and Adversarial Validation](ai/concurrency-hardening-and-adversarial-validation.md)
- [Comparison with Existing Tools](comparison-existing-tools.md)
