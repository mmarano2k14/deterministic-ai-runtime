# Control-Plane Ledger Causal Chain

Status: Implemented / validated for HTTP process-host scale-out, runtime host manager provisioning, real runtime process crash recovery, tenant-scoped recovery proof, replay, ledger, trace, and recovery forensics validation.

This document describes the control-plane ledger causal chain used by the Deterministic AI Runtime to prove infrastructure-level runtime decisions across admission, scale-out, runtime host creation, capacity visibility, recovery reconciliation, redispatch, replay, and tenant-scoped observability.

The control-plane causal chain is different from the execution ledger. The execution ledger explains what happened inside a durable `ExecutionId`. The control-plane causal chain explains how the shared control plane moved work, created or selected capacity, reacted to runtime failure, and proved that only impacted tenant work was recovered.

---

## Purpose

A distributed runtime cannot rely only on execution-level logs to prove reliability.

When a runtime process dies, the important questions are not only:

```text
Did the DAG eventually complete?
```

The control plane must also prove:

```text
Which scale-out request was persisted?
Which watcher observed it?
Which provider was selected?
Which runtime host was created?
When did capacity become visible?
Which runtime was marked unsafe?
Which assigned work was reconciled?
Which work was redispatched or resumed?
Which tenant did each decision belong to?
Was any unrelated tenant touched?
```

The control-plane ledger causal chain exists to make these infrastructure decisions queryable, ordered, correlated, and auditable.

It is the proof layer for runtime orchestration outside the DAG engine.

---

## Scope

The control-plane causal chain covers decisions and lifecycle events around:

- shared run submission;
- tenant-aware admission;
- scale-out request persistence;
- scale-out watcher observation;
- provider selection;
- HTTP provider scale-out delegation;
- Runtime Host Manager host creation;
- real `RuntimeInstanceOnly` process startup;
- runtime registration and heartbeat;
- runtime capacity publication;
- readiness visibility;
- shared run requeue after scale-out fulfillment;
- dispatch-time admission;
- provider dispatch;
- runtime process health transition;
- runtime unsafe capacity suppression;
- execution recovery reconciliation;
- in-flight execution resume;
- local queued work redispatch;
- safe tenant non-impact proof;
- replay / ledger / trace proof after recovery;
- recovery forensics correlation.

The chain is intentionally broader than the DAG execution ledger.

It explains how the control plane reached the point where DAG execution could continue safely.

---

## Execution Ledger vs Control-Plane Ledger

The runtime uses both execution-correlated ledger evidence and control-plane causal-chain evidence.

They answer different questions.

| Layer | Main Identity | Question Answered |
|---|---|---|
| Execution ledger | `ExecutionId` | What happened inside this durable DAG execution? |
| Run / shared-run ledger | `RunId`, `SharedRunId` | What happened to this submitted run before and after dispatch? |
| Control-plane causal chain | `ControlPlaneId`, `TenantId`, `RuntimeInstanceId`, `SharedRunId`, `ForensicsId` | How did the control plane route, scale, recover, and prove tenant isolation? |
| Recovery forensics | `ForensicsId`, `RuntimeFailureIncidentId` | What happened to this recovered work item during runtime failure recovery? |

A recovered in-flight DAG execution should expose all layers:

```text
Control-plane causal chain
    proves replacement capacity and recovery orchestration.

Recovery forensics
    proves the per-work-item recovery timeline.

Execution ledger
    proves the same ExecutionId continued and completed.

Replay / trace
    prove the recovered execution remains auditable after convergence.
```

---

## Core Identities

The causal chain relies on strict identity separation.

```text
ControlPlaneId
    Logical shared control-plane scope used by Redis stores and runtime hosts.

TenantId
    Durable tenant boundary from ExecutionContextSnapshot.

TenantGroupId
    Optional enterprise/group boundary used for visibility and runtime ownership.

RuntimeInstanceId
    Dispatchable runtime identity. In process-host scenarios, this identifies the runtime process/capacity that owns the local queue.

SharedRunId
    Durable shared run submission identity. Exists before local runtime dispatch and before ExecutionId for local-queued work.

LocalRunId
    Runtime-local attempt identity. Changes when work is redispatched or recovered to a replacement runtime.

ExecutionId
    Durable DAG execution identity. Must remain stable for in-flight resume recovery.

ForensicsId
    Durable recovery work item identity.

RuntimeFailureIncidentId
    Durable incident identity for a failed runtime process / unsafe runtime instance.
```

These identities must not be collapsed.

The most important recovery invariant is:

```text
ExecutionId survives runtime process death.
LocalRunId may change.
SharedRunId remains the durable submission identity.
```

---

## Validated Causal Chain Phases

The real process-host crash recovery scenario validates the control-plane causal chain across these phases:

```text
1. Scale-out request persisted
2. Scale-out watcher observed request
3. Provider selected
4. Runtime host manager created host
5. Process runtime host started
6. Runtime capacity became visible
7. Runtime instance visible through registry/capacity lookup
8. Runtime failure / unsafe capacity handled by health boundary
9. Execution recovery reconciled assigned work
10. Recovered work redispatched or resumed
```

The exact counts may vary by scenario timing and by whether multiple tenants are active, but the phase contract must hold.

A representative validation output shape is:

```text
[PASS] 1. Scale-out request persisted records='14'
[PASS] 2. Scale-out watcher observed request records='4'
[PASS] 3. Provider selected records='6'
[PASS] 4. Runtime host manager created host records='4'
[PASS] 5. Process runtime host started records='4'
[PASS] 6. Runtime capacity became visible records='4'
[PASS] 7. Runtime instance visible through registry/capacity lookup records='8'
[PASS] 8. Failed runtime marked unhealthy records='0'
[PASS] 9. Execution recovery reconciled assigned work records='6'
[PASS] 10. Recovered work redispatched records='6'
```

Phase 8 requires careful interpretation.

`Failed runtime marked unhealthy records='0'` does not mean runtime health was ignored. It means the health transition belongs to the runtime health reconciliation boundary, while the causal-chain query shown here tracks the positive recovery/control-plane flow. Health reconciliation and execution recovery reconciliation are intentionally separate responsibilities.

---

## Health vs Recovery Boundary

The control-plane causal chain must preserve this boundary:

```text
RuntimeInstanceHealthReconciler
    detects stale / unsafe runtime capacity
    marks or suppresses unsafe runtime instances
    prevents new work from being routed to unsafe capacity

Execution Recovery Reconciler
    enumerates work already assigned to unsafe runtime instances
    recovers in-flight executions
    redispatches local queued work
    writes recovery forensics and recovery ledger evidence

HTTP Provider
    reports transport / endpoint / circuit failure signals
    dispatches commands over HTTP
    participates in scale-out through provider capability
    does not own runtime crash recovery
    does not kill, restart, or recover runtime instances directly

Runtime Host Manager / lifecycle owner
    creates, attaches, or supervises runtime hosts
```

This separation is not optional.

It prevents the HTTP transport provider from becoming a hidden lifecycle manager and keeps recovery policy centralized in the control plane.

---

## Validated Transport-Health-to-Recovery Boundary

The validated boundary is:

```text
HTTP dispatch / endpoint failure signal
    ↓
structured provider failure reason such as http-circuit-open or http-provider-unavailable
    ↓
runtime endpoint health signal emitted or observed
    ↓
health reconciler may mark runtime capacity unsafe / unhealthy / draining
    ↓
admission stops selecting unsafe runtime capacity
    ↓
execution recovery reconciler recovers work already assigned to the unsafe runtime
    ↓
replacement capacity is selected or requested if required
    ↓
lifecycle owner creates or attaches replacement runtime capacity
```

This is not a future conceptual path. The separation has been validated by the recovery workstream.

The provider signal is not recovery completion.

Runtime replacement is not recovery completion.

Recovery is complete only when assigned work has been reconciled, redispatched or resumed, completed, and proven through replay / ledger / trace / forensics.

---

## Scale-Out Causal Chain

Normal scale-out produces infrastructure-level ledger evidence before any DAG execution exists.

The validated process-host scale-out chain is:

```text
MCP submit
    ↓
tenant-aware admission
    ↓
no tenant-visible capacity
    ↓
SharedRun.Status = ScaleOutRequested
    ↓
Redis scale-out request persisted
    ↓
scale-out watcher observes pending request
    ↓
provider selector resolves HTTP provider
    ↓
HTTP provider delegates to provisioner
    ↓
provisioner resolves tenant runtime settings
    ↓
Runtime Host Manager receives start request
    ↓
ProcessAiRuntimeHostCreationStrategy launches RuntimeInstanceOnly process
    ↓
runtime self-registers
    ↓
runtime publishes heartbeat and capacity
    ↓
readiness is observed
    ↓
scale-out request marked Fulfilled
    ↓
shared run requeued
    ↓
shared queue pump dispatches through normal admission
```

The watcher does not dispatch the run directly.

That rule is important:

```text
Scale-out fulfillment creates capacity.
Shared queue pump owns dispatch.
```

This keeps dispatch ownership, queue item lifecycle, and admission checks consistent.

---

## Runtime Process Crash Recovery Causal Chain

When a runtime process dies, the control-plane causal chain shifts from capacity creation to recovery orchestration.

The validated recovery chain is:

```text
Runtime process stops heartbeating
    ↓
health reconciliation marks or treats runtime capacity as unsafe
    ↓
admission no longer selects unsafe runtime capacity
    ↓
execution recovery reconciliation enumerates assigned work
    ↓
assigned work is classified
        - InFlightExecution
        - LocalQueued
    ↓
forensics record is opened per work item
    ↓
replacement runtime is selected or created
    ↓
work is recovered according to type
        - in-flight execution resumes same ExecutionId
        - local queued work redispatches through SharedRunId
    ↓
new LocalRunId is registered on replacement runtime
    ↓
execution completes or new execution is created when appropriate
    ↓
recovery forensics complete
    ↓
ledger / trace / replay evidence is validated
```

This chain proves that the local runtime queue is not the source of truth.

The local queue is allowed to die with the process.

Durable recovery truth comes from:

```text
SharedRunStore
SharedQueue
RuntimeRunExecutionIndex
DAG execution store
Runtime registry / capacity state
Decision ledger
Trace store
Recovery forensics store
Replay artifacts
```

---

## In-Flight Execution Recovery Evidence

An in-flight execution already has a durable `ExecutionId`.

The control-plane chain must prove that recovery continues the same durable execution, not a new one.

Required evidence:

```text
Original runtime instance became unsafe
Assigned in-flight execution was discovered
SharedRun was requeued for resume
Failed LocalRun was marked requeued for recovery
Replacement runtime was selected
Replacement LocalRun was registered
Resume context was seeded
DAG resume started
DAG resume completed
ExecutionIdBefore == ExecutionIdAfter
CompletedSteps == StepCount
```

The proof line looks like:

```text
ExecutionIdBefore='...'
ExecutionIdAfter='...'
```

The values must match.

That is the core durable execution identity proof.

---

## Local-Queued Recovery Evidence

Local queued work was dispatched to the dead runtime but had not started a durable DAG execution yet.

It may not have an `ExecutionId`.

Therefore recovery must not pretend to resume a DAG.

It must redispatch the durable shared run.

Required evidence:

```text
SharedRunId exists
Original LocalRunId exists
ExecutionId may be absent before recovery
Failed LocalRun marked requeued for recovery
SharedRun requeued for local-queued recovery
Replacement runtime selected
Replacement LocalRun registered
Execution begins on replacement runtime
ExecutionId becomes available after DAG creation
Run completes normally
```

The invariant is:

```text
LocalQueued recovery is SharedRunId-based redispatch.
InFlightExecution recovery is ExecutionId-based resume.
```

---

## Tenant-Scoped Causal Chain

Every causal-chain query must preserve tenant scope.

Tenant scope is not a display filter.

It is enforced through the execution context and the MCP observability surface.

Required tenant fields:

```text
TenantId
TenantGroupId
ExecutionContextSnapshot
RuntimeInstanceIdPrefix
IsolationMode
PreferDedicatedCapacity
AllowSharedFallback
```

Required isolation proof:

```text
Tenant A cannot see Tenant B recovery ledger entries.
Tenant B cannot see Tenant A recovery ledger entries.
Impacted tenant queries cannot see safe tenant recovery contamination.
Safe tenant recovery work count remains zero.
```

Representative proof lines:

```text
TenantBEntriesVisibleFromTenantA='0'
TenantAEntriesVisibleFromTenantB='0'
SafeTenantRecoveryEntriesVisibleFromImpactedQueries='0'
CrossTenantLedgerLeakDetected='false'
```

These are direct query results, not assumptions.

---

## Safe Tenant Causal Chain

The safe tenant is the most important isolation proof.

In the validated three-tenant crash scenario:

```text
Tenant A runtime process is killed.
Tenant B runtime process is killed.
Tenant C runtime process is not killed.
```

The safe tenant must continue normally.

Required safe tenant evidence:

```text
SubmittedRuns = 3
CompletedRuns = 3
ReplayProofs = 3
RecoveredWork = 0
RecoveryForensics = 0
RuntimeProcessKilled = false
CrashImpacted = false
SafeTenantNonImpactValidated = true
SafeTenantRecoveryLeakDetected = false
```

The safe tenant may have normal execution ledger, trace, and replay evidence.

It must not have recovery evidence.

The distinction is critical:

```text
Normal observability evidence is expected.
Recovery observability evidence must be absent.
```

---

## Control-Plane Event Domains

Control-plane causal-chain entries should be grouped by domain.

Recommended domains:

| Domain | Examples |
|---|---|
| Admission | no capacity, assign, request scale-out, reject |
| SharedRun | created, scale-out requested, requeued, dispatched, completed |
| SharedQueue | enqueued, claimed, requeued, dispatched |
| ScaleOut | request persisted, observed, fulfilled, rejected, expired |
| ProviderSelection | provider selected, no provider, provider rejected |
| HttpProvider | provider request received, provisioner delegated, transport metadata emitted |
| HostManager | host start requested, host created, duplicate denied |
| ProcessHost | process started, process exited, runtime started |
| Registry | runtime registered, heartbeat observed, status changed |
| Capacity | capacity published, capacity visible, readiness observed |
| Health | runtime stale, runtime unsafe, runtime draining/unhealthy |
| Recovery | assigned work discovered, recovery started, recovery completed |
| Replay | replay requested, report readable, replay ledger readable, replay trace readable |
| Forensics | forensics opened, timeline advanced, forensics completed |
| Isolation | tenant-scoped query validated, cross-tenant leak denied |

Not every domain belongs in a single store implementation, but the observable causal chain should make these domains queryable.

---

## Duplicate Host Creation and Idempotence

The causal chain should show duplicate scale-out and host creation attempts as safe, explicit outcomes.

Example:

```text
EventType='control.scaling.runtime-process-host-creation.denied'
Outcome='Denied'
Reason='process-runtime-instance-already-started:...:tenant-real-crash-a-runtime-1'
```

This is not a failure.

It proves idempotence.

Concurrent recovery or scale-out attempts can race, but only one replacement runtime should be created for the same tenant/runtime scope.

The duplicate attempt should be denied cleanly and recorded.

---

## Replay / Ledger / Trace Proof After Recovery

Recovery is not considered fully proven when the runtime run completes.

A recovered execution must remain observable and replayable after convergence.

Required post-recovery proof:

```text
execution ledger readable
execution trace readable
completion evidence present
step completion evidence present
replay report readable
replay ledger readable
replay trace readable
strict replay validation succeeds
Synthetic = false
```

For the validated three-tenant crash scenario:

```text
Replay validated executions = 9/9
Execution ledger evidence = 9/9
Execution trace evidence = 9/9
Completion evidence = 9/9
Step completion evidence = 9/9
Replay report readable = 9/9
Replay ledger readable = 9/9
Replay trace readable = 9/9
```

This is the difference between operational recovery and audit-grade recovery.

---

## Query Model

A control-plane causal-chain query should support filtering by:

```text
ControlPlaneId
TenantId
TenantGroupId
RuntimeInstanceId
RuntimeFailureIncidentId
SharedRunId
LocalRunId
ExecutionId
ForensicsId
ProviderName
PipelineKey
EventType
Category / domain
Time range
```

Common queries:

```text
Show all control-plane events for a runtime failure incident.
Show all recovery decisions for a tenant.
Show the full causal chain for a recovered ExecutionId.
Show all scale-out events for a SharedRunId.
Show all replacement runtime creation events for a tenant.
Show cross-tenant visibility count from tenant A to tenant B.
Show whether safe tenant recovery entries exist.
```

The query model must preserve tenant authorization.

A caller should only see entries allowed by its `ExecutionContextSnapshot` and TRN capabilities.

---

## MCP Observability Boundary

The MCP observability surface exposes ledger, trace, replay, and forensics as external audit APIs.

Relevant capabilities include:

```text
trn:{project}:observability:ledger:read
trn:{project}:observability:ledger:query
trn:{project}:observability:trace:read
trn:{project}:observability:trace:query
trn:{project}:replay:execution:run
trn:{project}:runtime-recovery:forensics:read
trn:{project}:runtime-recovery:forensics:query
```

The control-plane causal chain should be accessible only through a tenant-scoped and capability-enforced context.

The MCP layer is not just a UI adapter.

It is part of the audit boundary.

---

## Validated Scenario

The most important validated scenario is:

```text
Http_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace
```

Scenario shape:

```text
One shared control plane
Three tenants
Three real external RuntimeInstanceOnly process-host runtimes
Tenant A runtime process killed
Tenant B runtime process killed
Tenant C runtime process not killed
3 runs per tenant
50 DAG steps per run
Kill after 25 completed steps on the in-flight execution
6 recovered work items
0 safe tenant recovered work items
9 replay-validated executions
```

The test validates:

- real OS process kill;
- no fixture runtime;
- automatic health/recovery reconciliation;
- in-flight DAG resume with same `ExecutionId`;
- local queued redispatch through `SharedRunId`;
- replacement runtime host creation;
- tenant-scoped registry/capacity visibility;
- recovery forensics per work item;
- control-plane causal chain proof;
- no cross-tenant ledger leak;
- safe tenant non-impact;
- replay / ledger / trace evidence for all executions.

---

## Validated Evidence Shape

Representative final evidence:

```text
TenantAEntries='4201'
TenantBEntries='4158'
CombinedScenarioEntries='8359'
ScenarioCausalChainEntries='6156'
TenantBEntriesVisibleFromTenantA='0'
TenantAEntriesVisibleFromTenantB='0'
SafeTenantRecoveryEntriesVisibleFromImpactedQueries='0'
CrossTenantLedgerLeakDetected='false'
```

Safe tenant proof:

```text
TenantId='tenant-real-crash-safe'
SubmittedRuns='3'
CompletedRuns='3'
ReplayProofs='3'
RecoveredWork='0'
RecoveryForensics='0'
RuntimeProcessKilled='false'
CrashImpacted='false'
SafeTenantNonImpactValidated='true'
SafeTenantRecoveryLeakDetected='false'
```

Global replay proof:

```text
StrictReplayValidation='9/9'
ReplayReportsReadable='9/9'
ReplayLedgerReadable='9/9'
ReplayTraceReadable='9/9'
Synthetic='False'
```

---

## What the Causal Chain Does Not Claim

The control-plane causal chain is not a global correctness proof for every possible infrastructure failure.

It does not by itself prove:

- network partition safety between Redis/Mongo/control plane;
- corrupted durable storage recovery;
- Byzantine runtime behavior;
- Kubernetes pod eviction handling;
- multi-control-plane leader election;
- global autoscaling correctness;
- cost governance;
- security hardening of every external API.

It proves a narrower but important contract:

```text
Given a shared control plane, tenant-scoped runtime capacity, real process-host runtimes, durable Redis/Mongo-backed state, and tenant-scoped MCP observability, the system can recover work assigned to failed runtime processes and prove through ledger, trace, replay, and forensics that unrelated tenants were not touched.
```

---

## Design Rules

### Do

```text
Record control-plane infrastructure decisions as structured evidence.
Preserve TenantId and TenantGroupId on every causal-chain entry.
Keep ExecutionId, SharedRunId, and LocalRunId separate.
Record scale-out fulfillment separately from dispatch completion.
Record runtime replacement separately from recovery completion.
Record per-work-item recovery forensics.
Query ledger/trace/replay/forensics after recovery convergence.
Assert safe tenant recovery evidence is zero.
Assert cross-tenant visibility is zero.
```

### Do Not

```text
Do not treat HTTP provider failures as recovery completion.
Do not let the HTTP provider own runtime recovery.
Do not mark scale-out fulfilled before registry/capacity readiness is visible.
Do not dispatch directly from the scale-out watcher.
Do not use LocalRunId as durable execution identity.
Do not create a new ExecutionId for in-flight resume recovery.
Do not recover local queued work from the dead local queue.
Do not write safe tenant recovery forensics when its runtime was not impacted.
Do not rely on logs as the only audit proof.
```

---

## Current Status

| Capability | Status |
|---|---|
| Execution ledger | Implemented / validated |
| Replay ledger through MCP | Implemented / validated |
| Replay trace through MCP | Implemented / validated |
| Control-plane scale-out causal chain | Implemented / validated |
| Runtime Host Manager causal evidence | Implemented / validated |
| Process-host creation causal evidence | Implemented / validated |
| Runtime capacity readiness evidence | Implemented / validated |
| Runtime crash recovery causal evidence | Implemented / validated |
| Assigned work recovery reconciliation evidence | Implemented / validated |
| Recovered work redispatch evidence | Implemented / validated |
| Tenant-scoped ledger queries | Implemented / validated |
| Cross-tenant leak assertion | Implemented / validated |
| Safe tenant recovery contamination assertion | Implemented / validated |
| Recovery forensics correlation | Implemented / validated |
| Full UI/dashboard causal-chain view | Planned |
| OpenTelemetry exporter | Planned |
| Kubernetes causal-chain proof | Planned |
| Multi-control-plane leader-election proof | Planned |

---

## Related Documents

- [Architecture Overview](architecture-overview.md)
- [Runtime Control Plane](runtime-control-plane.md)
- [Runtime Discovery, Registry, and Capacity](runtime-discovery-registry-capacity.md)
- [HTTP Runtime Provider](http-runtime-provider.md)
- [MCP Production Runtime Scenario Framework](mcp-production-runtime-scenario-framework.md)
- [Runtime Process Crash Recovery](runtime-process-crash-recovery.md)
- [Runtime Recovery Forensics](runtime-recovery-forensics.md)
- [Multi-Tenant Runtime Crash Isolation](multi-tenant-runtime-crash-isolation.md)
- [Recovery Replay Ledger Trace Proof](recovery-replay-ledger-trace-proof.md)
- [Execution-Correlated Decision Ledger](execution-correlated-ledger.md)
- [Observability](observability.md)
- [Observability, Metrics, and Tracing](observability-tracing.md)
- [Replay and Audit](replay-and-audit.md)
- [Testing Strategy](testing-strategy.md)

---

## Documentation Rule

Do not describe the control-plane causal chain as a future observability idea.

The process-host scale-out chain, runtime host manager chain, runtime crash recovery chain, tenant-scoped ledger proof, replay proof, trace proof, and recovery forensics proof are validated capabilities.

Future work should be limited to UI/dashboard/exporter/Kubernetes/multi-control-plane extensions unless the capability is not yet implemented or not yet validated.
