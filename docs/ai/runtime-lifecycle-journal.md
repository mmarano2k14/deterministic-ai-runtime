# Durable Runtime Lifecycle Journal

**Status:** Implemented with append-only MongoDB persistence and validated for host, Pod, runtime, failure, replacement, and run-placement reconstruction.

The Runtime Lifecycle Journal is the durable source of infrastructure history. It complements, but does not replace, the current Runtime Registry, Decision Ledger, Runtime Pool Failure Journal, or Recovery Forensics store.

---

## Responsibility Boundary

```text
Runtime Registry
    = what is currently alive / ready / selectable

Runtime Lifecycle Journal
    = what happened to hosts, Pods, runtimes, and placements over time

Runtime Pool Failure Journal
    = immutable correctness failure facts

Decision Ledger
    = why runtime/control-plane decisions were made

Recovery Forensics
    = exact recovery timeline for affected work
```

The journal is append-only so cleanup of runtime infrastructure does not erase the audit trail.

---

## Lifecycle Event Families

Creation and readiness:

```text
host.creation.requested
host.creation.started
host.creation.succeeded
host.creation.failed
runtime.registered
runtime.ready
```

Failure and removal:

```text
runtime.draining
runtime.suppressed
runtime.unhealthy
runtime.stopped
host.deletion.requested
host.deleted
host.disappeared
```

Replacement and placement:

```text
runtime.replacement.requested
runtime.replacement.registered
work.assigned
work.reassigned
work.released
```

---

## First-Class Correlation

The journal supports typed correlation by identities such as:

```text
ControlPlaneId
PoolId
HostId
KubernetesPodUid
RuntimeInstanceId
RuntimeFailureIncidentId
SharedRunId
ExecutionId
CorrelationId
CausationId
LedgerEntryId
ForensicsId
```

Metadata remains diagnostic or provider-specific; values needed for correctness remain first-class.

---

## Durable Topology Reconstruction

Lifecycle history can reconstruct a topology after infrastructure has been replaced or cleaned up.

The projection distinguishes:

- active host boundaries;
- historical host boundaries;
- active runtimes;
- historical runtimes;
- initial run placement;
- final run placement;
- moved recovered work;
- stable unaffected work.

This is important because a current-state registry cannot prove what existed before a crash.

---

## Incident Expansion

The topology projector can expand causal history from observed failure identities:

```text
current scenario events
    ↓
observed RuntimeFailureIncidentId values
    ↓
complete incident history
    ↓
merge and deduplicate by EventId
```

A moved placement can also lead back to its original runtime, failure incident, deleted Pod/host, and replacement membership.

---

## Runtime Alias Preservation

A run can reference a runtime identity that is no longer present in the current physical snapshot.

The lifecycle projection preserves these runtime aliases through placement events so completed and recovered runs retain a usable historical identity rather than becoming `Unknown` merely because their old process or Pod no longer exists.

---

## Validated Historical Reconstruction

The durable journal has been validated to reconstruct deleted infrastructure and run placement after cleanup, including:

- deleted Pods;
- historical runtimes;
- replacement runtimes;
- impacted moved runs;
- stable unaffected runs;
- exclusion of unrelated control planes;
- parallel asynchronous journal aggregation.

It is also used by the final Runtime Pool production proofs for lifecycle and topology evidence.

---

## Engine Lifecycle Observation Expansion

The current Runtime Lifecycle Journal is intentionally strongest around **infrastructure lifecycle**: hosts, Pods, runtime processes, incidents, replacements, and placement history.

A separate near-term hardening effort is to expose the complete **execution-engine lifecycle** through the existing lifecycle event infrastructure and align it with the durable Ledger and Forensics. This includes Child DAG transitions such as child completion, continuation scheduling/delivery/consumption, and parent resume.

This expansion does **not** change the journal into a competing execution store and does not introduce another event bus. It closes the observability gap between infrastructure lifecycle and nested execution lifecycle.

Child DAG composition remains **Experimental** until this engine-lifecycle observation contract and deeper nested validation are complete.

See [Durable Child DAG Composition](child-dag-composition.md).

---

## Related Documents

- [Runtime Pool Architecture](runtime-pool-architecture.md)
- [Runtime Pool Failure Authority](runtime-pool-failure-authority.md)
- [Runtime Pool Production Validation](runtime-pool-production-validation.md)
- [Execution-Correlated Ledger](execution-correlated-ledger.md)
- [Runtime Recovery Forensics](runtime-recovery-forensics.md)
