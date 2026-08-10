# Runtime Pool Production Validation

**Status:** Completed for HTTP and gRPC across ProcessHostPool and KubernetesPool.

This document is the public proof contract for the reusable Runtime Pool architecture. It focuses on what was actually executed and validated rather than on implementation sequencing.

---

## Validation Matrix

```text
gRPC + ProcessHostPool   PASS
HTTP + ProcessHostPool   PASS
gRPC + KubernetesPool    PASS
HTTP + KubernetesPool    PASS
```

The same hierarchical correctness contract is exercised independently across both transport providers and both failure-boundary models.

---

## Scenario Topology

Every final scenario uses:

```text
FailureBoundaryCount          = 3
RuntimeCountPerBoundary       = 5
MaximumRuntimeCapacity        = 15
SubmissionWavesPerCycle       = 5
RunsPerWave                   = 15
SubmittedRunsPerCycle         = 75
ExecutionCycleCount           = 2
TotalSubmittedRuns            = 150
LogicalStepsPerDAG            = 50
TotalLogicalSteps             = 7500
ChildKillProgressThreshold    = 25 completed steps
CleanupPolicy                 = after final cycle only
```

For ProcessHostPool, a failure boundary is one external parent ProcessHost.

For KubernetesPool, a failure boundary is one Kubernetes Pod.

---

## Hierarchical Failure Sequence

Each cycle performs the following production workload:

```text
submit 60 DAGs across four full-capacity waves
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
submit final 15-DAG warm wave
    ↓
select one distinct fully busy boundary with 5 / 5 active runtimes
    ↓
kill full ProcessHost or Pod
    ↓
recover exactly five affected runs
    ↓
drain all 75 DAGs
    ↓
replay / ledger / trace / lifecycle / forensics proof
```

The final failure wave is part of the configured workload. No synthetic extra runs are added.

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

The replacement child receives a fresh runtime identity while the unaffected siblings retain theirs.

---

## Exact Full-Boundary Failure Contract

The parent boundary is selected from the deferred warm wave only after the child-failure workload has converged.

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

---

## Warm Reuse Contract

There is no cleanup between the two cycles.

The second cycle starts from the exact converged capacity produced by the first cycle:

```text
ColdStart = false
BoundaryCount = 3
RuntimeCount  = 15
IntermediateCleanupExecuted = false
```

This is a reuse proof, not two independent cold-start tests.

---

## ProcessHostPool Results

### gRPC

Validated:

```text
3 ProcessHosts
15 active runtimes
150 completed DAGs
7500 logical steps
2 child runtime crashes
2 ProcessHost crashes
12 recovered runs
150 replay proofs
0 lost runs
0 failed runs
0 duplicate dispatch
0 capacity violations
```

Observed full scenario duration was approximately 10.5 minutes on the local validation machine.

### HTTP

Validated final result:

```text
ExecutionCycleCount       = 2
ProcessHostCount          = 3
RuntimeCountPerHost       = 5
TotalRuntimeCount         = 15
TotalSubmittedRunCount    = 150
TotalCompletedRunCount    = 150
TotalLogicalStepCount     = 7500
TotalReplayProofCount     = 150
ChildRuntimeCrashCount    = 2
ParentHostCrashCount      = 2
RecoveredRunCount         = 12
FinalParentProcessAlive   = 3
```

Observed duration before cleanup:

```text
00:10:43.9735672
```

Final safety state:

```text
QueueDrained                           = true
ExactDispatchValidated                 = true
DagCompletionValidated                 = true
ReplayValidated                        = true
LedgerValidated                        = true
LogicalStepLedgerIdentityValidated     = true
TraceValidated                         = true
ExactChildRuntimeRecoveryValidated     = true
ChildRuntimeParentBoundarySurvived     = true
ChildRuntimeSiblingIdentityPreserved   = true
ExactParentHostRecoveryValidated       = true
RecoveryForensicsValidated             = true
DuplicateDispatchDetected              = false
LostRunDetected                        = false
FailedRunDetected                      = false
ProcessHostCapacityExceeded            = false
RuntimeCapacityExceeded                = false
```

---

## KubernetesPool Results

### gRPC

Validated:

```text
3 Pods
15 active runtimes
150 completed DAGs
7500 logical steps
2 child runtime crashes
2 Pod crashes
12 recovered runs
0 lost runs
0 duplicate dispatch
0 Pod-capacity overflow
0 runtime-capacity overflow
```

Observed scenario duration was approximately 21 minutes on the local validation cluster.

### HTTP

Validated final result:

```text
ExecutionCycleCount              = 2
MaximumConfiguredPodCount        = 3
RuntimeCountPerPod               = 5
MaximumRuntimeCapacity           = 15
TotalSubmittedRunCount           = 150
TotalCompletedRunCount           = 150
TotalLogicalStepCount            = 7500
ForcedChildRuntimeKillCount      = 2
ForcedPodDeletionCount           = 2
RecoveredSharedRunCount          = 12
RecoveryForensicsProofCount      = 12
FinalPhysicalPodCountBeforeCleanup = 3
WarmPoolReusedBetweenCycles      = true
IntermediateCleanupExecuted      = false
DuplicateDispatchDetected        = false
LostRunDetected                  = false
PodCapacityExceeded              = false
RuntimeCapacityExceeded          = false
```

Final deterministic cleanup completed with:

```text
RemainingPodCount = 0
```

Observed scenario duration was approximately 20.5 minutes.

---

## Aggregate Proof Across the Matrix

Across the four final scenarios:

```text
600 submitted DAGs
600 completed DAGs
30000 logical steps
8 child runtime crashes
8 full-boundary crashes
48 recovered runs
600 replay proofs
0 lost runs
0 failed runs
0 duplicate dispatch
0 configured-capacity violations
```

Full-boundary crashes consist of:

```text
4 ProcessHost crashes
4 Kubernetes Pod crashes
```

---

## What This Proves

The validation demonstrates that the same deterministic runtime contract survives both runtime-local and host-wide failure across two transports and two physical hosting models.

It specifically proves:

- runtime process identity is independent from ProcessHost and Pod identity;
- a child can fail without invalidating healthy siblings;
- a complete parent boundary can fail without contaminating other boundaries;
- in-flight recovery preserves durable `ExecutionId`;
- recovery is scoped to exact assigned work;
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

## Related Documents

- [Runtime Pool Architecture](runtime-pool-architecture.md)
- [Runtime Pool Failure Recovery](runtime-pool-failure-recovery.md)
- [Runtime Pool Failure Authority](runtime-pool-failure-authority.md)
- [Durable Runtime Lifecycle Journal](runtime-lifecycle-journal.md)
- [Testing Strategy](testing-strategy.md)
- [Enterprise Readiness](../enterprise-readiness.md)
