# Runtime Pool Production Validation

**Status:** Completed for HTTP and gRPC across ProcessHostPool and KubernetesPool, including both harness-triggered full-boundary failure and operator-triggered external full-boundary failure.

This document is the public proof contract for the reusable Runtime Pool architecture. It focuses on what was actually executed and validated rather than on implementation sequencing.

---

## Validation Matrix

Automatic full-boundary failure:

```text
gRPC + ProcessHostPool   PASS
HTTP + ProcessHostPool   PASS
gRPC + KubernetesPool    PASS
HTTP + KubernetesPool    PASS
```

Operator-triggered external full-boundary failure:

```text
gRPC + ProcessHostPool   PASS
HTTP + ProcessHostPool   PASS
gRPC + KubernetesPool    PASS
HTTP + KubernetesPool    PASS
```

The same hierarchical correctness contract is exercised independently across both transport providers, both failure-boundary models, and both failure-trigger modes.

The manual/external variants do not duplicate the recovery scenario. They reuse the same core workload, child-failure, convergence, recovery, replay, ledger, lifecycle, and forensics path. Only the full parent-boundary trigger changes: the test arms one exact fully busy boundary and waits until an operator destroys that exact ProcessHost or Pod from outside the test.

---

## Current Closure Profiles

The correctness contract is the same, but the current closure runs intentionally use different scale profiles.

| Scenario | Parent boundaries | Runtimes per boundary | Submission iterations per cycle | Cycles | DAGs per cycle | Total DAGs | Logical steps |
|---|---:|---:|---:|---:|---:|---:|---:|
| gRPC + ProcessHostPool | 7 | 5 | 20 | 2 | 700 | 1400 | 70000 |
| HTTP + ProcessHostPool | 3 | 5 | 5 | 2 | 75 | 150 | 7500 |
| gRPC + KubernetesPool | 5 | 5 | 5 | 2 | 125 | 250 | 12500 |
| HTTP + KubernetesPool | 3 | 5 | 5 | 2 | 75 | 150 | 7500 |

Each DAG executes 50 logical steps. Every cycle includes one exact child-runtime failure after durable progress and one later distinct full parent-boundary failure.

### gRPC ProcessHostPool scale spotlight — `7 × 5 × 20 × 2`

The largest current ProcessHostPool closure profile is intentionally worth reading as a workload, not only as four parameters:

```text
7 parent ProcessHosts
× 5 independent runtime instances per ProcessHost
= 35 reusable runtime slots

35 runtime slots
× 20 submission iterations per cycle
= 700 DAGs per cycle

700 DAGs
× 2 warm-reuse cycles
= 1400 DAGs per scenario

1400 DAGs
× 50 logical steps
= 70000 logical steps per scenario
```

Each `7 × 5 × 20 × 2` scenario also performs two exact child-runtime failures and two later failures of distinct fully busy parent ProcessHosts. The recovery contract therefore proves 12 exact recovered runs per scenario: one child-affected run plus five parent-boundary-affected runs in each of the two cycles.

Both trigger variants are green at this same profile:

```text
Automatic parent failure       1400 DAGs   70000 steps   2 child failures   2 parent failures   12 exact recoveries
External/manual parent kill    1400 DAGs   70000 steps   2 child failures   2 parent failures   12 exact recoveries
---------------------------------------------------------------------------------------------------------------
Combined evidence              2800 DAGs  140000 steps   4 child failures   4 parent failures   24 exact recoveries
```

The combined row is an evidence-volume aggregate across two independent production scenarios; it is not a claim that 2800 DAGs execute in one test invocation. The important architectural point is that the same 35-slot warm ProcessHostPool survives the same hierarchical recovery contract whether the parent failure is injected by the harness or caused externally by an operator.

Earlier 3 × 5 validation remains valid historical evidence. The larger gRPC closure profiles extend that proof rather than redefining the architectural contract.

For ProcessHostPool, a failure boundary is one external parent ProcessHost.

For KubernetesPool, a failure boundary is one Kubernetes Pod incarnation identified by exact Pod UID.

---

## Hierarchical Failure Sequence

Each cycle performs the same production contract:

```text
submit the initial full-capacity iterations
    ↓
select one busy child runtime
    ↓
wait until >= 25 / 50 steps complete
    ↓
kill that exact child process
    ↓
recover exactly one affected run
    ↓
verify parent boundary survives
    ↓
verify healthy siblings keep identity
    ↓
verify child membership replacement
    ↓
drain initial workload
    ↓
wait exact warm topology and capacity
    ↓
submit the deferred final full-capacity iteration
    ↓
select one distinct fully busy boundary with 5 / 5 active runtimes
    ↓
trigger full-boundary failure
    ↓
recover exactly five affected runs
    ↓
drain all configured DAGs
    ↓
replay / ledger / trace / lifecycle / forensics proof
```

The full-boundary trigger has two validated modes:

```text
automatic
    -> the scenario itself kills the selected ProcessHost or force-deletes the selected Pod

external-manual
    -> the scenario does not kill the parent boundary
    -> it publishes the exact target and command
    -> an operator kills the exact boundary from another shell
    -> the scenario observes disappearance and continues through the same recovery path
```

The deferred failure iteration remains part of the configured workload. No synthetic extra runs are added merely to make the failure easier to hit.

---

## Exact Child Failure Contract

The child failure proof requires:

```text
CompletedStepsBeforeKill >= 25
TotalSteps               = 50
RecoveredWorkCount       = 1
ExecutionIdBefore        = ExecutionIdAfter
ParentBoundarySurvived   = true
PreservedSiblingCount    = 4
```

The replacement child receives a fresh runtime identity while unaffected siblings retain theirs.

For KubernetesPool, a dynamically created in-Pod replacement may reuse a safe same-Pod Gateway ingress route alias while retaining its fresh `RuntimeInstanceId`. The Gateway routing value is an ingress detail; the runtime command body still carries the exact logical target identity and the in-Pod router forwards to that exact child.

---

## Exact Full-Boundary Failure Contract

The parent boundary is selected only after the child-failure workload has converged.

With five runtimes per parent boundary:

```text
RuntimeCount       = 5
ActiveRunCount     = 5
FailedRuntimeCount = 5
CandidateCount     = 5
AcceptedCount      = 5
RejectedCount      = 0
RecoveredRunCount  = 5
```

This proves recovery of the exact failed membership rather than broad pool-wide replay.

For operator-triggered tests, the scenario also proves that the exact armed boundary disappears without the test invoking the full-boundary kill itself.

---

## Queue-First Placement and Recovery Safety

The final fully busy boundary proof depends on placement surviving the shared queue handoff.

When a queue-first shared run carries required placement, that placement is persisted with the durable shared-run record and restored by dispatch-time admission. This allows the deferred failure iteration to pin exactly one run to each selected runtime in the target boundary.

Recovery redispatch intentionally does not keep a dead runtime placement. A recovered shared run is allowed to select valid replacement capacity instead of being repinned to the failed runtime or failed parent boundary.

This separation is required for both exact failure targeting and safe recovery.

---

## Warm Reuse Contract

There is no cleanup between the two cycles.

Cycle two starts from the exact converged capacity produced by cycle one:

```text
ColdStart = false
ReusedBoundaryIdentitySet = exact converged set from previous cycle
ReusedRuntimeIdentitySet  = exact converged set from previous cycle
IntermediateCleanupExecuted = false
```

The concrete boundary/runtime counts depend on the closure profile shown above. This is a reuse proof, not two independent cold-start tests.

---

## Manual External Failure Operator Signal

Manual/external tests publish a small signal file outside the xUnit output stream so an operator does not need to search the large production log.

KubernetesPool watcher:

```powershell
Get-Content "$env:TEMP\multiplexed-ai-manual-kubernetes-kill.txt" -Wait
```

ProcessHostPool watcher:

```powershell
Get-Content "$env:TEMP\multiplexed-ai-manual-processhost-kill.txt" -Wait
```

The signal file is append-only during the two-cycle test and exposes clear state transitions such as:

```text
Status=READY
Status=WAITING
Status=OBSERVED
```

When `Status=WAITING` appears, the file includes the exact target and a ready-to-run command. Kubernetes emits an exact `kubectl delete pod ... --grace-period=0 --force` command. ProcessHostPool emits an exact `taskkill /PID ... /T /F` command on Windows.

With `ExecutionCycleCount = 2`, the operator performs one external full-boundary kill per cycle.

---

## ProcessHostPool Results

### gRPC closure profile

The current gRPC ProcessHostPool closure run passed with:

```text
7 ProcessHosts
5 runtimes per ProcessHost
35 active runtime slots
20 submission iterations per cycle
700 DAGs per cycle
2 warm cycles
1400 DAGs total
70000 logical steps
2 child runtime failures
2 distinct parent ProcessHost failures
12 exact recovered runs
```

Both the automatic parent-failure variant and the operator-triggered external parent-failure variant are green.

### HTTP closure profile

The HTTP ProcessHostPool closure profile remains:

```text
3 ProcessHosts
5 runtimes per ProcessHost
15 active runtime slots
5 submission iterations per cycle
75 DAGs per cycle
2 warm cycles
150 DAGs total
7500 logical steps
2 child runtime failures
2 distinct parent ProcessHost failures
12 exact recovered runs
```

Both the automatic and operator-triggered external variants are green.

---

## KubernetesPool Results

### gRPC closure profile

The current gRPC KubernetesPool closure profile is:

```text
5 Pods
5 runtimes per Pod
25 active runtime slots
5 submission iterations per cycle
125 DAGs per cycle
2 warm cycles
250 DAGs total
12500 logical steps
2 child runtime failures
2 distinct Pod failures
12 exact recovered runs
```

Both automatic Pod deletion and operator-triggered external Pod deletion are green.

A representative operator-triggered closure run recorded:

```text
PodFailureTrigger              = external-manual
ForcedPodDeletionCount         = 0
ExternalPodDeletionCount       = 2
TotalSubmittedRunCount         = 250
TotalCompletedRunCount         = 250
TotalLogicalStepCount          = 12500
RecoveredSharedRunCount        = 12
RecoveryForensicsProofCount    = 12
WarmPoolReusedBetweenCycles    = true
IntermediateCleanupExecuted    = false
DuplicateDispatchDetected      = false
LostRunDetected                = false
PodCapacityExceeded            = false
RuntimeCapacityExceeded        = false
RemainingPodCount              = 0
```

The same run completed in approximately 42.2 minutes on the local validation cluster. Its initial, filler, target, and consolidated admission phases reported zero transient `429 Too Many Requests` retries.

### HTTP closure profile

The HTTP KubernetesPool closure profile remains:

```text
3 Pods
5 runtimes per Pod
15 active runtime slots
5 submission iterations per cycle
75 DAGs per cycle
2 warm cycles
150 DAGs total
7500 logical steps
2 child runtime failures
2 distinct Pod failures
12 exact recovered runs
```

Both the automatic and operator-triggered external variants are green.

---

## Aggregate Closure Evidence

One complete automatic matrix currently represents:

```text
1950 completed DAGs
97500 logical steps
8 child runtime failures
8 full-boundary failures
48 exact recovered runs
1950 replay proofs
```

The operator-triggered external matrix repeats the same workload profiles and correctness contract:

```text
1950 completed DAGs
97500 logical steps
8 child runtime failures
8 externally triggered full-boundary failures
48 exact recovered runs
1950 replay proofs
```

Across both trigger modes:

```text
3900 completed DAGs
195000 logical steps
16 child runtime failures
16 full-boundary failures
96 exact recovered runs
3900 replay proofs
```

Of the 16 full-boundary failures, eight are operator-triggered external failures. The other eight are triggered directly by the scenario harness.

---

## What This Proves

The validation demonstrates that the same deterministic runtime contract survives both runtime-local and host-wide failure across two transports, two physical hosting models, and two full-boundary trigger modes.

It specifically proves:

- runtime process identity is independent from ProcessHost and Pod identity;
- a child can fail without invalidating healthy siblings;
- a complete parent boundary can fail without contaminating other boundaries;
- an external operator can destroy an armed real ProcessHost or Pod and the runtime detects the failure without cooperation from the test kill path;
- in-flight recovery preserves durable `ExecutionId`;
- recovery is scoped to exact assigned work;
- queue-first required placement survives the durable shared-queue handoff before failure injection;
- recovery redispatch does not repin work to dead placement;
- dynamically created KubernetesPool replacement runtimes remain control-plane routable without reusing execution identity;
- warm capacity can be repaired and reused;
- replay and audit evidence survives failure;
- correctness remains intact under bounded concurrent load and real process/Pod termination.

---

## What This Does Not Claim

The proof is intentionally precise. It does not claim:

- unlimited throughput;
- multi-region failover;
- fully distributed multi-control-plane claim ownership;
- automatic cluster-node autoscaling correctness;
- Redis Cluster failover correctness;
- commercial SaaS operational maturity.

Those require separate evidence and should not be inferred from this matrix.

---

## EventDriven Recursive Validation Baseline

The Runtime Pool production validation now also includes recursive Child DAG execution using deterministic EventDriven post-failure synchronization.

Reference progression:

```text
3×3×3×2×Depth3       GREEN — recursive Depth3 validation
5×5×5×2×Depth3       GREEN — high-scale validation
```

Validated high-scale variants include gRPC KubernetesPool and both HTTP/gRPC ProcessHostPool transports. HTTP KubernetesPool transport parity is closed through the shared KubernetesPool production path and existing HTTP coverage without requiring another long-running high-scale permutation solely for transport.

The current EventDriven full-failure contract validates:

```text
real child-runtime process kill after durable progress
same ExecutionId resume
replacement runtime membership
distinct busy parent ProcessHost or Pod failure
exact affected-work recovery
warm topology reuse across two cycles
canonical RuntimeLifecycleJournal evidence
MCP replay
Ledger / trace / Recovery Forensics
no lost run
no duplicate durable dispatch
no configured capacity violation
```

The `5×5×5×2×Depth3` profile completes 250 parent DAGs and 12,750 exact **root parent** logical steps. Recursive child terminality is verified through authoritative durable DAG execution records. Separately, the bounded recursive Depth3 production proof now validates exact child-level logical-step accounting per depth through durable `step.completed` Ledger evidence, with zero missing and zero unexpected duplicate child logical steps. The high-scale root-step count and the bounded recursive child-step proof remain distinct evidence scopes.

See [Engine Event Observation and Lifecycle Catalog](engine-event-observation.md) and [Testing Strategy](testing-strategy.md).

---

## Related Documents

- [Runtime Pool Architecture](runtime-pool-architecture.md)
- [Runtime Pool Failure Recovery](runtime-pool-failure-recovery.md)
- [Runtime Pool Failure Authority](runtime-pool-failure-authority.md)
- [Durable Runtime Lifecycle Journal](runtime-lifecycle-journal.md)
- [Runtime Queue Control](runtime-queue-control.md)
- [Testing Strategy](testing-strategy.md)
- [Enterprise Readiness](../enterprise-readiness.md)
