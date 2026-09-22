# Recovery Replay Ledger Trace Proof

Status: Implemented and validated for HTTP and gRPC process-host runtime crash recovery scenarios, including real external runtime host processes, in-flight DAG resume, local-queued redispatch, tenant-scoped ledger queries, replay reports, replay ledger, replay trace, runtime recovery forensics, and safe-tenant non-impact validation.

This document describes the proof model used by the Deterministic AI Runtime to validate that recovered work is not only completed, but also replayable, traceable, ledger-backed, tenant-scoped, and auditable after recovery.

It complements:

- [Runtime Process Crash Recovery](runtime-process-crash-recovery.md)
- [HTTP Runtime Provider](http-runtime-provider.md)
- [gRPC Runtime Provider](grpc-runtime-provider.md)
- [Runtime Recovery Forensics](runtime-recovery-forensics.md)
- [Multi-Tenant Runtime Crash Isolation](multi-tenant-runtime-crash-isolation.md)
- [Control-Plane Ledger Causal Chain](control-plane-ledger-causal-chain.md)
- [Execution-Correlated Decision Ledger](execution-correlated-ledger.md)
- [Observability](observability.md)
- [Observability, Metrics, and Tracing](observability-tracing.md)
- [Replay and Audit](replay-and-audit.md)
- [Testing Strategy](testing-strategy.md)

---

## Purpose

Runtime recovery is not considered proven only because a DAG eventually reaches `Completed`.

A runtime can appear operationally recovered while still losing auditability, traceability, replayability, or tenant isolation evidence.

The purpose of this proof model is to validate that after a runtime process crash:

- impacted in-flight executions resume and complete;
- impacted local-queued work is redispatched and completes;
- safe tenants complete normally without recovery contamination;
- every completed execution remains replayable;
- every execution exposes ledger evidence;
- every execution exposes trace evidence;
- recovery forensics remain queryable after convergence;
- control-plane causal chain entries explain how recovery happened;
- tenant-scoped queries do not leak records across tenants.

The core rule is:

```text
Recovery completion is not the end of the proof.
Recovery completion is the start of the audit proof.
```

---

## Provider Coverage

The proof model is provider-agnostic.

It is now validated for both HTTP and gRPC process-host runtime providers.

Validated provider paths:

```text
HTTP process-host recovery
    ControlPlaneWithHttpRuntimeInstances
    provider.name = http
    transport.name = http
    real RuntimeInstanceOnly process
    HTTP dispatch
    process kill
    replacement runtime
    strict DAG resume / redispatch
    replay / ledger / trace / forensics proof

gRPC process-host recovery
    ControlPlaneWithGrpcRuntimeInstances
    provider.name = grpc
    transport.name = grpc
    real RuntimeInstanceOnly process
    gRPC dispatch
    process kill
    replacement runtime
    strict DAG resume / redispatch
    replay / ledger / trace / forensics proof
```

The provider transport is not the recovery owner.

HTTP and gRPC providers deliver work to selected runtime capacity and report transport failures. Runtime health reconciliation, assigned-work recovery, replay validation, ledger proof, trace proof, and forensics proof remain control-plane responsibilities.

---

## Recovery Proof Principle

The runtime validates recovery across multiple independent evidence surfaces.

A recovered execution must satisfy all of the following:

```text
DAG completed
+ step completion evidence exists
+ execution ledger evidence exists
+ trace evidence exists
+ replay report is readable
+ replay ledger is readable
+ replay trace is readable
+ strict replay validation passes
+ recovery forensics timeline exists when the work was impacted
+ tenant-scoped queries remain isolated
```

For a safe tenant, the invariant is different:

```text
DAG completed
+ replay evidence exists
+ ledger evidence exists
+ trace evidence exists
+ recovery work count = 0
+ recovery forensics count = 0
+ runtime process killed = false
+ crash impacted = false
```

This distinction is important.

Impacted tenants must show recovery evidence.

Safe tenants must show normal execution evidence and absence of recovery contamination.

---

## Evidence Surfaces

The proof is built from five evidence surfaces.

| Evidence Surface | Purpose |
|---|---|
| DAG state | Proves the execution reached terminal completion with expected step count. |
| Replay | Proves the execution remains reconstructable and comparable after recovery. |
| Ledger | Proves decisions and lifecycle facts are durable, queryable, and tenant-scoped. |
| Trace | Proves runtime flow and execution timeline remain inspectable. |
| Forensics | Proves the recovery timeline for impacted work items. |

No single surface is enough.

A DAG can complete without enough audit evidence.

A replay report can exist without recovery forensics.

A ledger can contain execution events while missing the control-plane recovery causal chain.

For production recovery, all surfaces must agree.

---

## Recovery Work Types

The crash recovery proof distinguishes two kinds of impacted work.

### InFlightExecution

An `InFlightExecution` is work that had already created a durable DAG execution before the runtime process died.

It has:

```text
ExecutionId
SharedRunId
LocalRunId
AssignedRuntimeInstanceId
DAG state
completed step count
```

Recovery must resume the same durable `ExecutionId` on replacement capacity.

The proof requires:

```text
ExecutionIdBefore == ExecutionIdAfter
CompletedSteps == ExpectedStepCount
RecoveryMode == resume-existing-execution
ReplayValid == true
Forensics timeline includes dag.resume.started and dag.resume.completed
```

### LocalQueued

A `LocalQueued` item is work that had been dispatched to a runtime-local queue but had not yet started DAG execution when the process died.

It has:

```text
SharedRunId
LocalRunId
AssignedRuntimeInstanceId
```

It does not yet have a durable `ExecutionId`.

Recovery must not pretend to resume a DAG that never existed.

Instead, the shared run is redispatched through durable shared-run state.

The proof requires:

```text
SharedRunId is preserved
failed LocalRunId is marked requeued for recovery
replacement LocalRunId is registered
new ExecutionId is created only after replacement runtime starts execution
ReplayValid == true after completion
Forensics timeline is local-queued recovery, not DAG resume
```

---

## Identity Semantics

The proof depends on strict identity separation.

```text
SharedRunId
= durable shared/control-plane submission identity

LocalRunId
= runtime-local attempt identity

ExecutionId
= durable DAG execution identity
```

These identities must not be collapsed.

For in-flight recovery:

```text
SharedRunId remains the same
failed LocalRunId is preserved as failed/requeued history
replacement LocalRunId is new
ExecutionId remains the same
```

For local-queued recovery:

```text
SharedRunId remains the same
failed LocalRunId is preserved as failed/requeued history
replacement LocalRunId is new
ExecutionId is created only when replacement runtime starts DAG execution
```

This is what allows the system to distinguish:

```text
resume an existing durable execution
```

from:

```text
redispatch a durable shared run that had not started executing
```

---

## Strict Replay Validation

After recovery and completion, every execution must be submitted to replay validation.

Replay validation must prove:

```text
replay report readable
replay ledger readable
replay trace readable
strict replay validation passes
synthetic = false
scope = replay-ready
```

`Synthetic = false` is important.

It means the replay proof is based on real recovered or completed executions, not synthetic reconstruction records created only for tests.

The replay proof should cover:

- impacted in-flight recovered executions;
- impacted local-queued redispatched executions;
- safe tenant normal executions.

Representative proof shape:

```text
Strict replay validation: 9/9
Replay reports readable: 9/9
Replay ledger readable: 9/9
Replay trace readable: 9/9
Synthetic = False for all validated executions
```

---

## Ledger Proof

The ledger proof validates both execution-level and control-plane-level evidence.

### Execution Ledger Evidence

Execution ledger evidence proves that an execution's lifecycle and step activity were recorded.

Useful evidence includes:

```text
execution.created
execution.started
step.started
step.completed
run.started
run.completed
execution.completed
finalization.completed
snapshot.created
replay.* entries where applicable
```

For recovered in-flight executions, the ledger should show a continuous causal history for the same `ExecutionId` across multiple runtime components.

Representative components may include:

```text
original runtime instance
replacement runtime instance
mcp-control-plane
pipeline-background-controller
policy-engine
replay-service
snapshot-service
```

The key invariant:

```text
A recovered in-flight execution keeps one durable ExecutionId across crash, recovery, replay, and audit queries.
```

### Control-Plane Ledger Evidence

Control-plane ledger evidence proves how the runtime infrastructure reacted to the crash.

Validated domains include:

```text
scale-out request persisted
scale-out watcher observed request
provider selected
HTTP or gRPC provider selected according to runtime metadata
runtime host manager created host
process runtime host started
runtime capacity became visible
runtime registry/capacity lookup succeeded
execution recovery reconciled assigned work
recovered work redispatched
```

This is not only execution observability.

It is infrastructure decision evidence.

---

## Trace Proof

Trace proof validates that runtime activity remains inspectable through the tracing surface after recovery.

Trace evidence may include:

```text
execution traces
step traces
dag-store traces
claim traces
concurrency lease traces
recovery traces
runtime provider traces
replay traces
```

A recovered execution should expose enough trace information to answer:

- which runtime instance executed the original work?
- where did the crash happen relative to step progress?
- which runtime instance completed the recovered work?
- which steps completed before and after recovery?
- can replay trace be loaded through MCP observability?

Trace proof is not a replacement for ledger proof.

Trace shows flow.

Ledger records structured decisions.

Forensics records recovery-specific incident timelines.

---

## Forensics Proof

Forensics proof validates the recovery timeline per impacted work item.

A recovered in-flight execution should have a forensics id shaped like:

```text
runtime-recovery:{ExecutionId}:{SharedRunId}:{LocalRunId}
```

A recovered local-queued run should have a forensics id shaped like:

```text
runtime-recovery:local-queued:{SharedRunId}:{LocalRunId}
```

For in-flight work, the expected timeline is:

```text
execution.recovery.candidate.detected
→ shared.run.requeued.for.resume
→ failed.local.run.marked.requeued.for.recovery
→ replacement.runtime.selected
→ replacement.local.run.registered
→ resume.context.seeded
→ dag.resume.started
→ dag.resume.completed
→ execution.recovery.completed
```

For local-queued work, the expected timeline is:

```text
SharedRunRequeuedForLocalQueuedRecovery
→ failed.local.run.marked.requeued.for.recovery
→ replacement.runtime.selected
→ replacement.local.run.registered
→ resume.context.seeded
```

The forensics proof is intentionally per work item.

A global recovery summary is not enough.

Each impacted work item must have its own causal timeline.

---

## Safe Tenant Proof

The safe tenant proof validates tenant isolation.

It is not enough to prove that impacted tenants recovered.

The system must also prove that tenants whose runtimes were not killed remained outside the recovery path.

The safe tenant proof requires:

```text
SubmittedRuns = expected safe tenant run count
CompletedRuns = expected safe tenant run count
ReplayProof = expected safe tenant run count
Recovered = 0
Forensics = 0
RuntimeProcessKilled = false
CrashImpacted = false
SafeTenantNonImpactValidated = true
SafeTenantRecoveryLeakDetected = false
CrossTenantLedgerLeakDetected = false
```

The safe tenant should still expose normal execution proof:

```text
execution ledger evidence
trace evidence
replay report
replay ledger
replay trace
strict replay validation
step completion evidence
```

But it must not expose recovery proof because it was not impacted.

This distinction prevents a dangerous false positive:

```text
all tenants received recovery handling
```

The correct behavior is:

```text
only impacted tenants received recovery handling
safe tenant completed normally
```

---

## Tenant-Scoped Query Proof

Tenant isolation must be proven through direct scoped queries.

The proof should validate:

```text
TenantBEntriesVisibleFromTenantA = 0
TenantAEntriesVisibleFromTenantB = 0
SafeTenantRecoveryEntriesVisibleFromImpactedQueries = 0
CrossTenantLedgerLeakDetected = false
```

These are not inferred from naming conventions.

They must be measured by querying through the same MCP ledger / observability surface used by external consumers.

The query model must respect:

```text
ExecutionContextSnapshot.TenantId
TenantGroupId
RBAC / TRN capability scope
MCP tenant-scoped access context
```

The tenant boundary is enforced at the observability API surface, not only at dispatch time.

---

## Proof Matrix

A complete recovery proof should satisfy this matrix.

| Proof Area | Impacted InFlightExecution | Impacted LocalQueued | Safe Tenant |
|---|---:|---:|---:|
| DAG completed | Required | Required | Required |
| Expected step count reached | Required | Required | Required |
| Same ExecutionId preserved | Required | Not applicable before start | Normal execution only |
| SharedRunId preserved | Required | Required | Required |
| New replacement LocalRunId | Required | Required | Not recovery-related |
| Recovery forensics exists | Required | Required | Must be zero |
| Replay report readable | Required | Required | Required |
| Replay ledger readable | Required | Required | Required |
| Replay trace readable | Required | Required | Required |
| Strict replay validation | Required | Required | Required |
| Execution ledger evidence | Required | Required | Required |
| Trace evidence | Required | Required | Required |
| Tenant-scoped query isolation | Required | Required | Required |
| Recovery contamination absent | Required | Required | Required |

---

## Validated Scenario Shape

The strongest validated scenario uses:

```text
one shared control plane
three tenants
real external RuntimeInstanceOnly host processes
HTTP and gRPC provider variants validated through the same recovery proof model over HTTP or gRPC
one tenant-scoped process-host runtime per tenant in the scenario
Tenant A process killed
Tenant B process killed
Tenant C process not killed
3 runs per tenant
50-step DAG per run
kill after 25 completed steps on the in-flight execution
```

At crash time, each impacted tenant has:

```text
1 InFlightExecution
2 LocalQueued
```

Total work:

```text
9 submitted runs
6 impacted work items
3 safe tenant runs
6 expected recovered items
0 expected safe tenant recovered items
```

Expected global proof:

```text
all 9 executions complete
all 9 executions expose ledger evidence
all 9 executions expose trace evidence
all 9 executions expose completion evidence
all 9 executions expose step completion evidence
all 9 executions pass strict replay validation
all 9 replay reports are readable
all 9 replay ledgers are readable
all 9 replay traces are readable
```

Expected impacted proof:

```text
Tenant A recovered work = 3
Tenant B recovered work = 3
in-flight resume keeps same ExecutionId
local queued work redispatched through SharedRunId
recovery forensics exists for each impacted work item
```

Expected safe tenant proof:

```text
Safe tenant submitted runs = 3
Safe tenant completed runs = 3
Safe tenant replay proofs = 3
Safe tenant recovered work = 0
Safe tenant recovery forensics = 0
Safe tenant runtime process killed = false
Safe tenant crash impacted = false
```

---

## Validated Provider-Specific Test Names

HTTP recovery proof scenarios include:

```text
Http_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_With_Strict_Dag_Resume_Replay_Ledger_And_Trace

Http_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace
```

gRPC recovery proof scenarios include:

```text
Grpc_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_With_Strict_Dag_Resume_Replay_Ledger_And_Trace

Grpc_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace
```

The gRPC provider also validates the single-tenant strict resume proof:

```text
Grpc_ProcessHost_Should_Requeue_Real_InFlight_Dag_After_Runtime_Process_Kill
```

The important invariant is provider-independent:

```text
OriginalExecutionId == RecoveredExecutionId
```

The transport may change from HTTP to gRPC.

The durable execution identity must not change.


---

## What This Proof Does Not Claim

This proof does not claim that every possible infrastructure failure is solved.

It does not validate:

- corrupted durable stores;
- network partitions between control plane and Redis/Mongo;
- Byzantine runtime instances;
- poisoned payloads;
- Kubernetes pod eviction behavior;
- multi-control-plane leader election;
- global performance or throughput limits.

It validates a specific contract:

```text
When real tenant-scoped runtime processes die, the control plane can recover only the impacted assigned work, preserve durable execution identity where applicable, redispatch volatile local-queued work from durable shared state, and prove the result through replay, ledger, trace, forensics, and tenant-scoped observability queries.
```

---

## Design Rules

### Do

```text
Validate replay after recovery.
Validate ledger after recovery.
Validate trace after recovery.
Validate forensics after recovery.
Validate safe tenant absence from recovery.
Validate tenant-scoped query isolation directly.
Preserve ExecutionId for in-flight resume.
Preserve SharedRunId for local-queued redispatch.
Treat LocalRunId as attempt identity.
Keep recovery proof queryable after convergence.
```

### Do Not

```text
Do not treat DAG completion alone as recovery proof.
Do not count replacement runtime creation as recovery completion.
Do not use log lines as the only audit surface.
Do not create recovery evidence for safe tenants.
Do not collapse SharedRunId, LocalRunId, and ExecutionId.
Do not infer tenant isolation only from runtime ids or prefixes.
Do not let HTTP or gRPC providers own recovery.
Do not treat local queue state as durable truth.
```

---

## Current Status

| Capability | Status |
|---|---|
| Replay report after recovery | Implemented / validated |
| Replay ledger after recovery | Implemented / validated |
| Replay trace after recovery | Implemented / validated |
| Strict replay validation for recovered executions | Implemented / validated |
| Execution ledger evidence after recovery | Implemented / validated |
| Trace evidence after recovery | Implemented / validated |
| Runtime recovery forensics | Implemented / validated |
| In-flight recovery timeline | Implemented / validated |
| Local-queued recovery timeline | Implemented / validated |
| Control-plane causal chain ledger | Implemented / validated |
| Tenant-scoped ledger isolation proof | Implemented / validated |
| Safe tenant non-impact proof | Implemented / validated |
| MCP observability query surface | Implemented / validated |
| HTTP process-host replay / ledger / trace recovery proof | Implemented / validated |
| gRPC process-host replay / ledger / trace recovery proof | Implemented / validated |
| Production dashboard view | Planned |
| OpenTelemetry exporter mapping | Planned |
| Kubernetes crash recovery proof | Implemented / validated, including KubernetesPool hierarchical recovery |

---

## Responsibilities by Component

| Component | Responsibility |
|---|---|
| DAG store / execution engine | Preserve execution state and step completion evidence. |
| Shared run store | Preserve durable shared submission identity and redispatch state. |
| Shared queue | Own claim/requeue/dispatch lifecycle for shared work. |
| Runtime run execution index | Track assigned work by runtime instance for recovery reconciliation. |
| Runtime health reconciler | Detect unsafe runtime capacity and prevent unsafe routing. |
| Execution recovery reconciler | Recover work already assigned to unsafe runtime instances. |
| Runtime provider | Deliver work to selected runtime capacity and report transport failures. |
| HTTP provider | Report HTTP transport failures; it does not own recovery. |
| gRPC provider | Report gRPC transport failures; it does not own recovery. |
| Runtime Host Manager / lifecycle owner | Create or attach replacement runtime capacity when needed. |
| Replay service / MCP replay API | Validate replay report, replay ledger, and replay trace. |
| Decision ledger | Record execution and control-plane decisions as structured evidence. |
| Trace layer | Record runtime flow and timeline diagnostics. |
| Runtime recovery forensics | Record per-work-item recovery causal timelines. |
| MCP observability tools | Expose tenant-scoped ledger, trace, replay, and forensics queries. |

---

## Related Documents

- [Runtime Process Crash Recovery](runtime-process-crash-recovery.md)
- [HTTP Runtime Provider](http-runtime-provider.md)
- [gRPC Runtime Provider](grpc-runtime-provider.md)
- [Runtime Recovery Forensics](runtime-recovery-forensics.md)
- [Multi-Tenant Runtime Crash Isolation](multi-tenant-runtime-crash-isolation.md)
- [Control-Plane Ledger Causal Chain](control-plane-ledger-causal-chain.md)
- [Execution-Correlated Decision Ledger](execution-correlated-ledger.md)
- [Observability](observability.md)
- [Observability, Metrics, and Tracing](observability-tracing.md)
- [Runtime Control Plane](runtime-control-plane.md)
- [HTTP Runtime Provider](http-runtime-provider.md)
- [gRPC Runtime Provider](grpc-runtime-provider.md)
- [MCP Production Runtime Scenario Framework](mcp-production-runtime-scenario-framework.md)
- [Testing Strategy](testing-strategy.md)

---

## Documentation Rule

Do not document recovery as complete only because a replacement runtime was created or because the DAG eventually completed.

A production recovery claim must include replay, ledger, trace, and forensics evidence after convergence.

A multi-tenant recovery claim must additionally prove that unrelated tenants remained untouched.

When documenting provider-specific proof, keep the transport detail explicit: HTTP proof belongs in the HTTP provider document, gRPC proof belongs in the gRPC provider document, and this document remains the provider-agnostic audit proof model shared by both.
