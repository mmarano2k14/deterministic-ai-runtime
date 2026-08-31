# Adversarial Runtime Validation Matrix

**Status:** Implemented / validated  
**Status date:** 2026-08-29  
**Scope:** deterministic semantic failure-boundary validation across HTTP/gRPC × ProcessHostPool/KubernetesPool.

This document is the canonical reference for the current adversarial Runtime Pool validation matrix. It complements the larger throughput and full-boundary production closure campaigns; it does not replace or invalidate those historical proofs.

---

## Validation Objective

The matrix is designed to prove that the same logical execution contract survives deliberately selected failure boundaries across both transports and both Runtime Pool hosting models.

The test target is not merely "the run eventually completed". Each adversarial row requires agreement between the relevant durable execution, ownership, recovery, lifecycle, Ledger, replay, and forensic surfaces.

The shared rule is:

```text
physical runtime or host failure
        ≠
logical execution identity loss
```

---

## Provider / Transport Matrix

The current semantic matrix is green across all four combinations:

| Hosting model | gRPC | HTTP |
|---|---|---|
| ProcessHostPool | VERIFIED | VERIFIED |
| KubernetesPool | VERIFIED | VERIFIED |

Each combination executes the same nine high-information adversarial rows.

```text
4 provider/transport combinations
× 9 semantic adversarial rows
= 36 validated matrix rows
```

---

## Canonical Rows

| Row | Purpose |
|---|---|
| `Baseline` | Control run using the same production topology and proof surfaces without the targeted adversarial boundary. |
| `CrashEarly` | Kill a physical runtime early enough to exercise recovery before substantial DAG progress. |
| `ChildInvocationBoundary` | Attack the durable transition between the parent execution and deterministic Child DAG invocation identity. |
| `ContinuationConsume` | Kill the exact physical runtime consuming the durable parent continuation after Child DAG completion. |
| `Depth2RuntimeFailure` | Inject a real runtime failure inside recursive Child DAG depth 2. |
| `Depth3RuntimeFailure` | Inject a real runtime failure inside recursive Child DAG depth 3. |
| `SeedA` | Deterministic schedule variation using the first adversarial ordering seed. |
| `SeedB` | Deterministic schedule variation using the second adversarial ordering seed. |
| `SeedC` | Deterministic schedule variation using the third adversarial ordering seed. |

The seed rows vary ordering deterministically. They are not random fuzz tests and they do not claim exhaustive exploration of every possible distributed-system interleaving.

---

## Shared Scenario Contract

The matrix reuses the real production Runtime Pool scenario contract:

- durable QueueFirst admission;
- bounded ProcessHostPool or KubernetesPool capacity;
- independently identified child runtime instances;
- recursive Child DAG composition through `ChildDepth = 3`;
- deterministic parent suspension through `WaitingForExternal`;
- deterministic continuation identity;
- real physical child-runtime failure;
- distinct later full ProcessHost or Pod failure where the row requires the hierarchical recovery path;
- same-`ExecutionId` in-flight recovery;
- warm topology reuse across execution cycles;
- exact ownership transition validation;
- Runtime Lifecycle Journal evidence;
- durable Ledger evidence;
- parent replay proof;
- Recovery Forensics evidence;
- final bounded-topology convergence.

HTTP and gRPC preserve the same logical correctness contract. Transport timing and framing may differ, but logical identity, durable ownership, recovery semantics, and proof requirements do not.

---

## Continuation-Consume Boundary

`ContinuationConsume` is the most timing-sensitive semantic row in the ProcessHostPool matrix.

The production continuation path remains unchanged. The validation does not add a synthetic production lifecycle event or an artificial post-child execution gate.

The deterministic targeting sequence is:

```text
historical preparation checkpoint at parent step 50
        ↓
derive exact ParentExecutionId
        ↓
derive exact ChildInvocationKey
        ↓
derive exact continuation SharedRunId
        ↓
pre-arm current physical RuntimeInstanceId → PID handles
        ↓
release normal production execution
        ↓
relation = Completed
continuation = Scheduled
        ↓
tight durable watch of the exact continuation SharedRun
        ↓
first exact durable ownership commit:
    SharedRun.Status = Dispatched
    ExecutionId = parent ExecutionId
    LocalRunId present
    RuntimeInstanceId present
        ↓
suspend the exact already-pre-armed physical process immediately
        ↓
prove non-terminal parent + exact running attempt
        ↓
kill the same suspended PID
        ↓
normal in-flight recovery
```

`SharedRunDispatched` remains a best-effort diagnostic signal. It is not the ownership authority.

The durable authority is the exact `SharedRun` binding:

```text
SharedRunId
+ LocalRunId
+ ExecutionId
+ RuntimeInstanceId
```

A valid physical boundary proves the exact continuation attempt is still active when the runtime is frozen and killed.

Representative proof markers include:

```text
SharedRunStatus='Dispatched'
CompletedStepsAtKill='50'
TotalStepsAtKill='51'
DagStatus='Waiting'
IndexStatus='running'
PhysicalKillProof='PASS'
Kind='InFlightExecution'
ExecutionIdBefore == ExecutionIdAfter
TransitionViolationCount='0'
```

---

## Recursive Failure Boundaries

The depth-specific rows prove that runtime recovery is not limited to the root parent or first child.

```text
Depth2RuntimeFailure → exact RecursiveChildRuntime target at depth 2
Depth3RuntimeFailure → exact RecursiveChildRuntime target at depth 3
```

The recursive production proof validates exact durable child logical-step accounting per depth using `step.completed` Ledger evidence.

The bounded reference proof includes:

```text
Child executions = 54
Child logical steps = 2736

Depth 1 = 918 / 918
Depth 2 = 918 / 918
Depth 3 = 900 / 900
```

The proof requires zero missing and zero unexpected duplicate child logical steps after accounting for legitimate recovery attempts.

---

## Ownership and Recovery Proof

The scenario distinguishes logical identity from physical attempt identity.

For in-flight recovery:

```text
ExecutionIdBefore
=
ExecutionIdAfter
```

while the physical attempt changes:

```text
Failed RuntimeInstanceId
        ↓
Replacement RuntimeInstanceId

Failed LocalRunId
        ↓
Replacement LocalRunId
```

Runtime-ownership handoff correctness is proven by exact recovery outcomes and final durable ownership convergence. Transient exclusivity itself remains grounded in the Redis shared-queue claim-token and exact-owner CAS contracts rather than being inferred from periodic test snapshots.

---

## Event and Durable-State Discipline

The matrix follows the runtime's event-driven observation model:

```text
event = synchronization / wake-up
durable store = authority
hard timeout = watchdog
```

Canonical lifecycle events are used when they exist. A new production event must not be invented solely to make a test easier.

Likewise, a passing result must not be manufactured by increasing timeouts or adding sleeps around a missing semantic boundary.

---

## Evidence Surfaces

Depending on the row, a complete validation may correlate:

- `SharedRun` durable ownership;
- `LocalRunId`;
- `ExecutionId`;
- `RuntimeInstanceId`;
- `RuntimeRunExecutionIndex`;
- DAG durable state;
- Child DAG relation state;
- shared failure journal;
- Runtime Lifecycle Journal;
- recovery inventory;
- Recovery Forensics;
- execution Ledger;
- parent replay proof;
- final runtime membership and capacity.

A signal or console line alone is not the authority where a durable store exists.

---

## Verified Evidence Archive

The full 36-row matrix is backed by archived raw xUnit output rather than by projection from a smaller subset.

```text
docs/files/adversarial-runtime-validation-logs.zip
SHA-256 = a8e252b2b7277c196d594f0da6963b2e39eab3ad0e2a6415306974d2a8497c03
```

The archive contains 36 distinct artifacts and has been checked for exact provider, transport, and scenario alignment. Every frozen `RECURSIVE_CHILD_DAG_PROOF_RESULT` reports `Status='PASS'`, `Cycles='2'`, `ParentRunsTotal='36'`, `ParentReplay='36/36'`, zero missing recursive child steps, zero unexpected duplicate recursive child steps, and zero ownership transition violations.

Across the complete matrix this represents 1,296 submitted and completed parent runs, 5,184 total executions, and 263,088 logical steps. Dedicated recursive-child replay remains explicitly `NOT_EVALUATED` in every row.

For row-level test names, durations, artifact hashes, and provenance notes, see [Adversarial Runtime Validation Evidence Index](adversarial-runtime-validation-evidence-index.md).

---

## Claim Boundary

The current matrix proves the selected deterministic adversarial schedules across the four provider/transport combinations.

It does **not** claim exhaustive exploration of the distributed state space.

The following remain distinct proof domains:

- recovery-of-recovery / repeated failure during an active recovery chain;
- recursive-child replay as a dedicated replay proof for every nested child execution;
- multi-node Kubernetes fault-domain validation;
- multi-control-plane recovery ownership;
- Redis Cluster compatibility.

Parent execution replay remains part of the validated production proof. Dedicated recursive-child replay should not be implied by that parent replay result unless separately demonstrated.

---

## Relationship to Existing Production Closure Proofs

The semantic adversarial matrix complements two older and still-valid validation families:

1. **P10–P35 concurrency campaigns** — concentrated local pressure, process loss, ownership races, and datastore saturation.
2. **Hierarchical Runtime Pool production closure** — larger HTTP/gRPC × ProcessHostPool/KubernetesPool profiles with child-runtime failure, full parent-boundary failure, warm reuse, bounded capacity, replay, Ledger, lifecycle, and Forensics.

Those proofs remain valid and should not be rewritten as if the semantic matrix replaced them.

The new matrix adds deterministic coverage of *where* failure is injected in the recursive execution lifecycle.

---

## Validation Discipline

A matrix row is considered valid only when:

1. the intended semantic boundary is reached;
2. the exact physical failure target is identified;
3. the failure is physically injected at that boundary;
4. recovery is attributed to the exact durable work item;
5. logical identity invariants remain valid;
6. recursive work accounting remains exact where applicable;
7. final ownership converges without transition violations;
8. required replay, lifecycle, Ledger, and forensic proofs agree;
9. the topology returns to its bounded healthy state.

Debugging output from an earlier failed attempt is historical diagnostic evidence, not a substitute for the final successful proof artifact.

---

## Related Documents

- [Adversarial Runtime Validation Evidence Index](adversarial-runtime-validation-evidence-index.md)
- [Durable Child DAG Composition](child-dag-composition.md)
- [Runtime Pool Production Validation](runtime-pool-production-validation.md)
- [Concurrency Hardening and Adversarial Validation](concurrency-hardening-and-adversarial-validation.md)
- [Testing Strategy](testing-strategy.md)
- [Engine Event Observation and Lifecycle Catalog](engine-event-observation.md)
- [Runtime Pool Failure Recovery](runtime-pool-failure-recovery.md)
- [Runtime Pool Failure Authority](runtime-pool-failure-authority.md)
- [Runtime Lifecycle Journal](runtime-lifecycle-journal.md)
- [Runtime Recovery Forensics](runtime-recovery-forensics.md)
- [Recovery Replay Ledger Trace Proof](recovery-replay-ledger-trace-proof.md)
