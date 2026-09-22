# Engine Event Observation and Lifecycle Catalog

**Status:** Implemented and validated for centralized canonical engine-event observation, durable projection, deterministic lifecycle waiting, Runtime Pool recovery, and recursive Child DAG production scenarios.

This document is the canonical documentation entry point for the runtime's event-driven observation architecture.

The code remains the source of truth for exact canonical declarations and persisted/wire values. Documentation must never invent a semantic event string or substitute a similar event name for an exact declaration.

---

## 1. Architectural Principle

The runtime uses one semantic observation path for engine facts:

```text
Production engine
    ↓
One canonical engine fact
    ↓
Existing Event Manager
    ↓
Central Projection Catalog
    ├── Decision Ledger
    ├── Recovery Forensics
    ├── Execution Forensics
    ├── Runtime Lifecycle Journal
    ├── Metrics
    ├── Logging
    └── Realtime observers
```

The governing invariant is:

```text
ONE ENGINE FACT
=
ONE CANONICAL EVENT
=
ONE CANONICAL DECLARATION
=
ONE CENTRAL DISPATCH PATH
```

The engine emits facts. The Event Manager projects them.

No second event bus, lifecycle store, ledger, forensics store, metrics pipeline, or Child-DAG-specific observation system is introduced.

---

## 2. Canonical Event Namespace

Canonical engine-event declarations live under the dedicated namespace:

```text
Multiplexed.Abstractions.AI.Observability.Events
```

The namespace may contain multiple focused declaration holders by semantic domain, but a semantic event is declared only once.

Conceptually, the namespace covers:

```text
Recovery
Runtime lifecycle
Runtime Pool / capacity
Child DAG
Continuation
Policy
Retry / claim / admission
Snapshot
Storage
Replay
```

The structural rule is more important than the number of declaration classes:

```text
inside the canonical namespace
    multiple focused classes are allowed

outside the canonical namespace
    no competing canonical event declaration is allowed
```

Production code, the Event Manager, projection sinks, and tests consume the same declarations.

Persisted and wire values remain unchanged when ownership of a declaration moves into the canonical namespace.

---

## 3. Canonical Event Envelope

A canonical event uses the existing control-plane event model and carries only the identities relevant to that fact.

Supported correlation fields may include:

```text
EventId
SemanticEventType
TimestampUtc

TenantId
TenantGroupId
ControlPlaneId

SharedRunId
LocalRunId
ExecutionId
RuntimeInstanceId
WorkerId

RuntimePoolId / PoolId
HostId
KubernetesPodUid

ParentExecutionId
ChildExecutionId
ChildInvocationKey
ContinuationId

RuntimeFailureIncidentId / FailureIncidentId
ForensicsId

CorrelationId
CausationId

PreviousStatus
CurrentStatus
Outcome
Reason
Metadata
```

An event must not manufacture identities that do not belong to its semantic boundary.

---

## 4. Projection Authority

The Event Manager does not blindly send every event to every sink.

The central projection catalog is the single authority that determines which surfaces apply to each canonical event and how failures are handled.

Conceptually:

```text
Canonical event
    ↓
Projection descriptor
    ├── Ledger                Required / replayable / best effort / none
    ├── Recovery Forensics    Required / replayable / best effort / none
    ├── Execution Forensics   Required / replayable / best effort / none
    ├── Lifecycle Journal     Required / replayable / best effort / none
    ├── Metrics               Best effort / none unless stronger contract exists
    ├── Logging               Best effort / none
    └── Realtime              Best effort / none
```

This prevents mapping authority from drifting independently inside individual sinks.

A sink remains responsible for its own storage semantics. The Event Manager owns orchestration, not the internal implementation of the Ledger, Forensics, Journal, Metrics, Logging, or Realtime surface.

---

## 5. State and Event Ordering

A canonical event describes a fact that is already true at the required durability boundary.

The normal semantic order is:

```text
durable engine mutation
    ↓
canonical fact becomes true
    ↓
required durable projections / evidence
    ↓
best-effort projections
```

Where engine state and durable event evidence share the same real Redis or Mongo atomic boundary, they may commit together.

The architecture does not claim distributed atomicity across independent Redis and Mongo transactions when none exists.

Stable `EventId`, append-once behavior, idempotent projection, durable pending evidence, retry, and reconciliation are used where the mutation and projection cannot share one transaction.

---

## 6. Deterministic Lifecycle Observation

Event-driven production tests wait on the same canonical event architecture used by the runtime.

For durable facts, the deterministic observer uses the missed-event-safe pattern:

```text
check durable evidence
    ↓
subscribe to realtime canonical events
    ↓
re-check durable evidence
    ↓
await the canonical event when still required
    ↓
verify final durable state
```

This closes the race where an event is emitted immediately before the test subscribes.

Hard watchdogs remain mandatory. They are liveness boundaries, not the primary synchronization mechanism.

Timeout diagnostics should expose the expected event, the last observed event, durable Ledger/Forensics/Journal evidence, relevant identities, and elapsed time.

---

## 7. Compatibility with Polling

Historical polling-based production scenarios remain valid regression evidence.

The current synchronization policy is:

```text
pre-failure durable progress threshold
    → durable state remains authoritative

post-failure recovery synchronization
    → EventDriven canonical lifecycle observation

historical scenarios
    → Polling remains available as compatibility/fallback
```

The event-driven path is additive. Proven polling scenarios are not deleted simply to make the test suite appear event-driven.

---

# Canonical Event Catalog

The current canonical namespace contains **132 distinct persisted/wire semantic values**: **114** values in `AiEngineEvents` plus **18** runtime-infrastructure lifecycle values in `AiRuntimeLifecycleEvents`. The central projection-catalog guard test requires every canonical value to have exactly one projection descriptor.

The exact source authorities are:

```text
implementations/dotnet/src/Multiplexed.Abstractions/AI/Observability/Events/AiEngineEvents.cs
implementations/dotnet/src/Multiplexed.Abstractions/AI/Observability/Events/AiRuntimeLifecycleEvents.cs
implementations/dotnet/src/Multiplexed.AI/Runtime/ControlPlane/Observability/Projections/AiEngineEventProjectionCatalog.cs
```

`ExecutionForensics` is deliberately `None` for every current projection descriptor. This is guarded by a unit test so the architecture does not invent parallel execution-forensics ownership before a real existing implementation contract exists.

---

## 8. How to Read the Catalog

Each row shows the exact canonical declaration, exact physical value, semantic meaning, durability class, and current central projection contract. `None` targets are omitted from the compact projection column unless they are architecturally important for that event family.

Projection requirements mean:

```text
RequiredDurable   semantic emission is not successful until the durable projection succeeds
ReplayableDurable durable evidence may be projected/retried idempotently
BestEffort        observational failure does not fail the semantic event
None              target does not receive that canonical event
```

## 9. Execution Events

Durable execution lifecycle facts. These describe the lifetime of one durable execution identity.

**Projection profile:** `DurableLifecycleFact`; Ledger = RequiredDurable; Logging = BestEffort.

| Canonical declaration | Exact value | Meaning | Durability | Current projections |
|---|---|---|---|---|
| `AiEngineEvents.Execution.Created` | `execution.created` | Indicates that an execution was created. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Execution.Started` | `execution.started` | Indicates that an execution was started. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Execution.Completed` | `execution.completed` | Indicates that an execution completed successfully. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Execution.Failed` | `execution.failed` | Indicates that an execution failed. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Execution.Cancelled` | `execution.cancelled` | Indicates that an execution was cancelled. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Execution.Finalized` | `execution.finalized` | Indicates that an execution was finalized. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |

## 10. Run Events

Controller/runtime run lifecycle facts. `run.suspended` is especially important for Child DAG composition because it releases physical capacity while durable execution remains waiting.

**Projection profile:** `DurableLifecycleFact`; Ledger = RequiredDurable; Logging = BestEffort.

| Canonical declaration | Exact value | Meaning | Durability | Current projections |
|---|---|---|---|---|
| `AiEngineEvents.Run.Queued` | `run.queued` | Indicates that a run was queued. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Run.Dequeued` | `run.dequeued` | Indicates that a run was dequeued. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Run.Started` | `run.started` | Indicates that a run started. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Run.Completed` | `run.completed` | Indicates that a run completed. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Run.Suspended` | `run.suspended` | Indicates that a run released runtime capacity while its execution remains durably waiting. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Run.Failed` | `run.failed` | Indicates that a run failed. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Run.Cancelled` | `run.cancelled` | Indicates that a run was cancelled. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |

## 11. Queue Events

Runtime queue control transitions.

**Projection profile:** `DurableLifecycleFact`; Ledger = RequiredDurable; Logging = BestEffort.

| Canonical declaration | Exact value | Meaning | Durability | Current projections |
|---|---|---|---|---|
| `AiEngineEvents.Queue.Paused` | `queue.paused` | Indicates that the runtime queue was paused. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Queue.Resumed` | `queue.resumed` | Indicates that the runtime queue was resumed. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |

## 12. Dag Events

DAG scheduler readiness/blocking facts for individual steps.

**Projection profile:** `DurableLifecycleFact`; Ledger = RequiredDurable; Logging = BestEffort.

| Canonical declaration | Exact value | Meaning | Durability | Current projections |
|---|---|---|---|---|
| `AiEngineEvents.Dag.StepBecameReady` | `dag.step_became_ready` | Indicates that a DAG step became ready. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Dag.StepBlocked` | `dag.step_blocked` | Indicates that a DAG step was blocked. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Dag.StepUnblocked` | `dag.step_unblocked` | Indicates that a DAG step was unblocked. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Dag.StepSkipped` | `dag.step_skipped` | Indicates that a DAG step was skipped. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |

## 13. ChildDag Events

Recursive child-execution and durable parent-continuation facts. The physical continuation-delivery observation is intentionally transient; durable relation transitions remain the authority.

**Projection profile:** durable Child DAG facts use `DurableLifecycleFact` with Ledger/Logging/Realtime best-effort and Metrics disabled; `ContinuationDelivered` is a `TransientObservation` with no Ledger projection.

| Canonical declaration | Exact value | Meaning | Durability | Current projections |
|---|---|---|---|---|
| `AiEngineEvents.ChildDag.ExecutionCreated` | `child.execution.created` | Indicates that the durable child execution identity was created. | `DurableLifecycleFact` | Ledger = BestEffort; Metrics = None; Logging = BestEffort; Realtime = BestEffort |
| `AiEngineEvents.ChildDag.ExecutionStarted` | `child.execution.started` | Indicates that the child execution started. | `DurableLifecycleFact` | Ledger = BestEffort; Metrics = None; Logging = BestEffort; Realtime = BestEffort |
| `AiEngineEvents.ChildDag.ExecutionCompleted` | `child.execution.completed` | Indicates that the child execution completed successfully. | `DurableLifecycleFact` | Ledger = BestEffort; Metrics = None; Logging = BestEffort; Realtime = BestEffort |
| `AiEngineEvents.ChildDag.ExecutionFailed` | `child.execution.failed` | Indicates that the child execution failed. | `DurableLifecycleFact` | Ledger = BestEffort; Metrics = None; Logging = BestEffort; Realtime = BestEffort |
| `AiEngineEvents.ChildDag.ContinuationScheduled` | `child.continuation.scheduled` | Indicates that parent continuation delivery became durably scheduled. | `DurableLifecycleFact` | Ledger = BestEffort; Metrics = None; Logging = BestEffort; Realtime = BestEffort |
| `AiEngineEvents.ChildDag.ContinuationDelivered` | `child.continuation.delivered` | Indicates that the deterministic continuation was accepted for delivery. | `TransientObservation` | Ledger = None; Metrics = None; Logging = BestEffort; Realtime = BestEffort |
| `AiEngineEvents.ChildDag.ContinuationConsumed` | `child.continuation.consumed` | Indicates that durable parent progress proves scheduled continuation consumption. | `DurableLifecycleFact` | Ledger = BestEffort; Metrics = None; Logging = BestEffort; Realtime = BestEffort |
| `AiEngineEvents.ChildDag.ParentContinuationResumed` | `parent.continuation.resumed` | Indicates that the durable parent-child relation converged to resumed. | `DurableLifecycleFact` | Ledger = BestEffort; Metrics = None; Logging = BestEffort; Realtime = BestEffort |

### Durable Child DAG continuation sequence

```text
child.execution.created
→ child.execution.started
→ child.execution.completed / child.execution.failed
→ child.continuation.scheduled
→ child.continuation.delivered
→ child.continuation.consumed
→ parent.continuation.resumed
```

Emission follows the real state transition. `child.continuation.delivered` proves physical delivery acceptance; durable continuation truth is established by the relation/CAS transitions and parent progress. Cancellation is not silently normalized into `child.execution.failed`.

## 14. Claim Events

Distributed ownership and claim-lease facts.

**Projection profile:** `DurableDecisionFact`; Ledger = RequiredDurable; Logging = BestEffort.

| Canonical declaration | Exact value | Meaning | Durability | Current projections |
|---|---|---|---|---|
| `AiEngineEvents.Claim.Attempted` | `claim.attempted` | Indicates that a claim was attempted. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Claim.Acquired` | `claim.acquired` | Indicates that a claim was acquired. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Claim.Denied` | `claim.denied` | Indicates that a claim was denied. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Claim.Expired` | `claim.expired` | Indicates that a claim expired. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Claim.Released` | `claim.released` | Indicates that a claim was released. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Claim.LeaseRenewed` | `claim.lease_renewed` | Indicates that a claim lease was renewed. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Claim.LeaseExpired` | `claim.lease_expired` | Indicates that a claim lease expired. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |

## 15. Step Events

Individual execution-step lifecycle facts, including durable external parking.

**Projection profile:** `DurableLifecycleFact`; Ledger = RequiredDurable; Logging = BestEffort.

| Canonical declaration | Exact value | Meaning | Durability | Current projections |
|---|---|---|---|---|
| `AiEngineEvents.Step.Started` | `step.started` | Indicates that a step started. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Step.Completed` | `step.completed` | Indicates that a step completed. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Step.Failed` | `step.failed` | Indicates that a step failed. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Step.Parked` | `step.parked` | Indicates that a step voluntarily entered a durable external wait. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Step.TimedOut` | `step.timed_out` | Indicates that a step timed out. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |

## 16. Recovery Events

Recovery facts split between generic Ledger-owned recovery decisions and the exact durable Recovery Forensics causal chain.

**Projection profile:** the four generic recovery facts are Ledger-owned `DurableRecoveryFact` values; exact physical recovery-transition facts are Recovery-Forensics-owned `DurableRecoveryFact` values with Realtime/Logging best-effort.

| Canonical declaration | Exact value | Meaning | Durability | Current projections |
|---|---|---|---|---|
| `AiEngineEvents.Recovery.Detected` | `recovery.detected` | Indicates that a recoverable condition was detected. | `DurableRecoveryFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Recovery.Applied` | `recovery.applied` | Indicates that recovery was applied. | `DurableRecoveryFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Recovery.StepRecovered` | `recovery.step_recovered` | Indicates that a step was recovered. | `DurableRecoveryFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Recovery.ExecutionRecovered` | `recovery.execution_recovered` | Indicates that an execution was recovered. | `DurableRecoveryFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Recovery.RuntimeFailureDetected` | `runtime.failure.detected` | Indicates that a runtime failure was detected. | `DurableRecoveryFact` | RecoveryForensics = RequiredDurable; Logging = BestEffort; Realtime = BestEffort |
| `AiEngineEvents.Recovery.RuntimeHealthSuppressed` | `runtime.health.suppressed` | Indicates that runtime health was suppressed or marked unsafe. | `DurableRecoveryFact` | RecoveryForensics = RequiredDurable; Logging = BestEffort; Realtime = BestEffort |
| `AiEngineEvents.Recovery.RuntimeCapacityRemoved` | `runtime.capacity.removed` | Indicates that runtime capacity was removed or suppressed. | `DurableRecoveryFact` | RecoveryForensics = RequiredDurable; Logging = BestEffort; Realtime = BestEffort |
| `AiEngineEvents.Recovery.ExecutionRecoveryCandidateDetected` | `execution.recovery.candidate.detected` | Indicates that an execution recovery candidate was detected. | `DurableRecoveryFact` | RecoveryForensics = RequiredDurable; Logging = BestEffort; Realtime = BestEffort |
| `AiEngineEvents.Recovery.SharedRunRequeuedForResume` | `shared.run.requeued.for.resume` | Indicates that a shared run was requeued for in-flight resume recovery. | `DurableRecoveryFact` | RecoveryForensics = RequiredDurable; Logging = BestEffort; Realtime = BestEffort |
| `AiEngineEvents.Recovery.SharedRunRequeuedForLocalQueuedRecovery` | `SharedRunRequeuedForLocalQueuedRecovery` | Indicates that a shared run was requeued for local-queued recovery. | `DurableRecoveryFact` | RecoveryForensics = RequiredDurable; Logging = BestEffort; Realtime = BestEffort |
| `AiEngineEvents.Recovery.FailedLocalRunMarkedRequeuedForRecovery` | `failed.local.run.marked.requeued.for.recovery` | Indicates that a failed local run was marked requeued for recovery. | `DurableRecoveryFact` | RecoveryForensics = RequiredDurable; Logging = BestEffort; Realtime = BestEffort |
| `AiEngineEvents.Recovery.ReplacementRuntimeSelected` | `replacement.runtime.selected` | Indicates that a replacement runtime was selected. | `DurableRecoveryFact` | RecoveryForensics = RequiredDurable; Logging = BestEffort; Realtime = BestEffort |
| `AiEngineEvents.Recovery.ReplacementLocalRunRegistered` | `replacement.local.run.registered` | Indicates that a replacement local run was registered. | `DurableRecoveryFact` | RecoveryForensics = RequiredDurable; Logging = BestEffort; Realtime = BestEffort |
| `AiEngineEvents.Recovery.ResumeContextSeeded` | `resume.context.seeded` | Indicates that resume context was seeded. | `DurableRecoveryFact` | RecoveryForensics = RequiredDurable; Logging = BestEffort; Realtime = BestEffort |
| `AiEngineEvents.Recovery.DagResumeStarted` | `dag.resume.started` | Indicates that DAG resume started. | `DurableRecoveryFact` | RecoveryForensics = RequiredDurable; Logging = BestEffort; Realtime = BestEffort |
| `AiEngineEvents.Recovery.DagResumeCompleted` | `dag.resume.completed` | Indicates that DAG resume completed. | `DurableRecoveryFact` | RecoveryForensics = RequiredDurable; Logging = BestEffort; Realtime = BestEffort |
| `AiEngineEvents.Recovery.ExecutionRecoveryCompleted` | `execution.recovery.completed` | Indicates that execution recovery completed. | `DurableRecoveryFact` | RecoveryForensics = RequiredDurable; Logging = BestEffort; Realtime = BestEffort |
| `AiEngineEvents.Recovery.ExecutionRecoveryFailed` | `execution.recovery.failed` | Indicates that execution recovery failed. | `DurableRecoveryFact` | RecoveryForensics = RequiredDurable; Logging = BestEffort; Realtime = BestEffort |

### In-flight recovery causal chain

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

### Local-queued recovery causal chain

```text
SharedRunRequeuedForLocalQueuedRecovery
→ failed.local.run.marked.requeued.for.recovery
→ replacement.runtime.selected
→ replacement.local.run.registered
→ resume.context.seeded
```

The historical `SharedRunRequeuedForLocalQueuedRecovery` casing is intentionally preserved because it is a persisted compatibility contract. Local-queued work has no durable execution to resume; in-flight work does.

## 17. Retry Events

Retry decision and retry-attempt lifecycle facts.

**Projection profile:** `DurableDecisionFact`; Ledger = RequiredDurable; Logging = BestEffort.

| Canonical declaration | Exact value | Meaning | Durability | Current projections |
|---|---|---|---|---|
| `AiEngineEvents.Retry.Evaluated` | `retry.evaluated` | Indicates that retry eligibility was evaluated. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Retry.Scheduled` | `retry.scheduled` | Indicates that retry was scheduled. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Retry.Denied` | `retry.denied` | Indicates that retry was denied. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Retry.AttemptStarted` | `retry.attempt_started` | Indicates that a retry attempt started. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Retry.AttemptCompleted` | `retry.attempt_completed` | Indicates that a retry attempt completed. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Retry.BudgetExhausted` | `retry.budget_exhausted` | Indicates that the retry budget was exhausted. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |

## 18. Policy Events

Policy evaluation outcomes. The canonical namespace contains `policy.skipped`; its existence does not mean an event should be emitted merely because no policies are configured.

**Projection profile:** all policy facts are `DurableDecisionFact` values with required Ledger evidence. `Allowed`, `Denied`, and `Failed` additionally project Metrics best-effort.

| Canonical declaration | Exact value | Meaning | Durability | Current projections |
|---|---|---|---|---|
| `AiEngineEvents.Policy.Evaluated` | `policy.evaluated` | Indicates that a policy was evaluated. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Policy.Allowed` | `policy.allowed` | Indicates that a policy allowed the operation. | `DurableDecisionFact` | Ledger = RequiredDurable; Metrics = BestEffort; Logging = BestEffort |
| `AiEngineEvents.Policy.Denied` | `policy.denied` | Indicates that a policy denied the operation. | `DurableDecisionFact` | Ledger = RequiredDurable; Metrics = BestEffort; Logging = BestEffort |
| `AiEngineEvents.Policy.Skipped` | `policy.skipped` | Indicates that a policy was skipped. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Policy.Failed` | `policy.failed` | Indicates that a policy failed. | `DurableDecisionFact` | Ledger = RequiredDurable; Metrics = BestEffort; Logging = BestEffort |

## 19. Concurrency Events

Concurrency admission, throttle, and lease facts.

**Projection profile:** `DurableDecisionFact`; Ledger = RequiredDurable; Logging = BestEffort.

| Canonical declaration | Exact value | Meaning | Durability | Current projections |
|---|---|---|---|---|
| `AiEngineEvents.Concurrency.Evaluated` | `concurrency.evaluated` | Indicates that concurrency was evaluated. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Concurrency.Allowed` | `concurrency.allowed` | Indicates that concurrency allowed the operation. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Concurrency.Denied` | `concurrency.denied` | Indicates that concurrency denied the operation. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Concurrency.ThrottleApplied` | `concurrency.throttle_applied` | Indicates that throttling was applied. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Concurrency.LeaseAcquired` | `concurrency.lease_acquired` | Indicates that a concurrency lease was acquired. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Concurrency.LeaseReleased` | `concurrency.lease_released` | Indicates that a concurrency lease was released. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Concurrency.LeaseExpired` | `concurrency.lease_expired` | Indicates that a concurrency lease expired. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |

## 20. Control Events

Execution control facts for pause, resume, cancellation, and control-state changes.

**Projection profile:** `DurableLifecycleFact`; Ledger = RequiredDurable; Logging = BestEffort.

| Canonical declaration | Exact value | Meaning | Durability | Current projections |
|---|---|---|---|---|
| `AiEngineEvents.Control.PauseRequested` | `control.pause_requested` | Indicates that pause was requested. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Control.Paused` | `control.paused` | Indicates that the execution was paused. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Control.ResumeRequested` | `control.resume_requested` | Indicates that resume was requested. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Control.Resumed` | `control.resumed` | Indicates that the execution was resumed. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Control.CancelRequested` | `control.cancel_requested` | Indicates that cancellation was requested. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Control.CancelObserved` | `control.cancel_observed` | Indicates that cancellation was observed by the runtime. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Control.StateChanged` | `control.state_changed` | Indicates that execution control state changed. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |

## 21. HumanInput Events

Human-in-the-loop request, wait, response, rejection, and expiry facts.

**Projection profile:** `DurableLifecycleFact`; Ledger = RequiredDurable; Logging = BestEffort.

| Canonical declaration | Exact value | Meaning | Durability | Current projections |
|---|---|---|---|---|
| `AiEngineEvents.HumanInput.Requested` | `human_input.requested` | Indicates that human input was requested. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.HumanInput.Submitted` | `human_input.submitted` | Indicates that human input was submitted. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.HumanInput.Rejected` | `human_input.rejected` | Indicates that human input was rejected. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.HumanInput.Expired` | `human_input.expired` | Indicates that human input expired. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.HumanInput.Waiting` | `human_input.waiting` | Indicates that execution is waiting for human input. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |

## 22. Retention Events

Retention evaluation, compaction, and hot-state eviction facts.

**Projection profile:** `DurableDecisionFact`; Ledger = RequiredDurable; Logging = BestEffort.

| Canonical declaration | Exact value | Meaning | Durability | Current projections |
|---|---|---|---|---|
| `AiEngineEvents.Retention.Evaluated` | `retention.evaluated` | Indicates that retention was evaluated. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Retention.Triggered` | `retention.triggered` | Indicates that retention was triggered. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Retention.Skipped` | `retention.skipped` | Indicates that retention was skipped. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Retention.Compacted` | `retention.compacted` | Indicates that a payload or state was compacted. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Retention.Evicted` | `retention.evicted` | Indicates that hot state was evicted. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |

## 23. Payload Events

Payload externalization/rehydration and resolution-failure facts.

**Projection profile:** `DurableDecisionFact`; Ledger = RequiredDurable; Logging = BestEffort.

| Canonical declaration | Exact value | Meaning | Durability | Current projections |
|---|---|---|---|---|
| `AiEngineEvents.Payload.Externalized` | `payload.externalized` | Indicates that a payload was externalized. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Payload.Rehydrated` | `payload.rehydrated` | Indicates that a payload was rehydrated. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Payload.ResolutionFailed` | `payload.resolution_failed` | Indicates that payload resolution failed. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |

## 24. Snapshot Events

Snapshot creation/load and restore lifecycle facts.

**Projection profile:** `DurableDecisionFact`; Ledger = RequiredDurable; Logging = BestEffort.

| Canonical declaration | Exact value | Meaning | Durability | Current projections |
|---|---|---|---|---|
| `AiEngineEvents.Snapshot.Created` | `snapshot.created` | Indicates that a snapshot was created. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Snapshot.Loaded` | `snapshot.loaded` | Indicates that a snapshot was loaded. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Snapshot.RestoreRequested` | `snapshot.restore_requested` | Indicates that snapshot restore was requested. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Snapshot.RestoreCompleted` | `snapshot.restore_completed` | Indicates that snapshot restore completed. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |

## 25. Storage Events

Durable state-persistence success/failure facts.

**Projection profile:** `DurableDecisionFact`; Ledger = RequiredDurable; Logging = BestEffort.

| Canonical declaration | Exact value | Meaning | Durability | Current projections |
|---|---|---|---|---|
| `AiEngineEvents.Storage.StatePersisted` | `storage.state_persisted` | Indicates that runtime state was persisted. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Storage.StatePersistenceFailed` | `storage.state_persistence_failed` | Indicates that runtime state persistence failed. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |

## 26. Replay Events

Replay lifecycle, comparison, and convergence-proof facts.

**Projection profile:** `DurableDecisionFact`; Ledger = RequiredDurable; Logging = BestEffort.

| Canonical declaration | Exact value | Meaning | Durability | Current projections |
|---|---|---|---|---|
| `AiEngineEvents.Replay.Requested` | `replay.requested` | Indicates that replay was requested. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Replay.Started` | `replay.started` | Indicates that replay started. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Replay.Completed` | `replay.completed` | Indicates that replay completed. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Replay.Failed` | `replay.failed` | Indicates that replay failed. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Replay.ComparisonCompleted` | `replay.comparison_completed` | Indicates that replay comparison completed. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Replay.ConvergenceProofStarted` | `replay.convergence_proof_started` | Indicates that replay convergence proof started. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Replay.ConvergenceProofCompleted` | `replay.convergence_proof_completed` | Indicates that replay convergence proof completed. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Replay.ConvergenceProofFailed` | `replay.convergence_proof_failed` | Indicates that replay convergence proof failed. | `DurableDecisionFact` | Ledger = RequiredDurable; Logging = BestEffort |

## 27. Finalization Events

Distributed finalization lifecycle and race-resolution facts.

**Projection profile:** `DurableLifecycleFact`; Ledger = RequiredDurable; Logging = BestEffort.

| Canonical declaration | Exact value | Meaning | Durability | Current projections |
|---|---|---|---|---|
| `AiEngineEvents.Finalization.Started` | `finalization.started` | Indicates that finalization started. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Finalization.Completed` | `finalization.completed` | Indicates that finalization completed. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Finalization.Failed` | `finalization.failed` | Indicates that finalization failed. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Finalization.CancellationOverrideApplied` | `finalization.cancellation_override_applied` | Indicates that cancellation finalization override was applied. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |
| `AiEngineEvents.Finalization.RaceLost` | `finalization.race_lost` | Indicates that a distributed finalization attempt lost the optimistic finalization race because another worker already finalized or updated the execution. | `DurableLifecycleFact` | Ledger = RequiredDurable; Logging = BestEffort |

## 28. Runtime Infrastructure Lifecycle Events

Append-only runtime infrastructure lifecycle facts owned durably by the Runtime Lifecycle Journal.

**Projection profile:** `RuntimeJournalFact`; Runtime Lifecycle Journal = RequiredDurable; Logging = BestEffort; Realtime = BestEffort; Metrics = None.

| Canonical declaration | Exact value | Meaning | Durability | Current projections |
|---|---|---|---|---|
| `AiRuntimeLifecycleEvents.HostCreationRequested` | `host.creation.requested` | Indicates that host creation was requested. | `RuntimeJournalFact` | LifecycleJournal = RequiredDurable; Logging = BestEffort; Realtime = BestEffort; Metrics = None |
| `AiRuntimeLifecycleEvents.HostCreationStarted` | `host.creation.started` | Indicates that host creation started. | `RuntimeJournalFact` | LifecycleJournal = RequiredDurable; Logging = BestEffort; Realtime = BestEffort; Metrics = None |
| `AiRuntimeLifecycleEvents.HostCreationSucceeded` | `host.creation.succeeded` | Indicates that host creation succeeded. | `RuntimeJournalFact` | LifecycleJournal = RequiredDurable; Logging = BestEffort; Realtime = BestEffort; Metrics = None |
| `AiRuntimeLifecycleEvents.HostCreationFailed` | `host.creation.failed` | Indicates that host creation failed. | `RuntimeJournalFact` | LifecycleJournal = RequiredDurable; Logging = BestEffort; Realtime = BestEffort; Metrics = None |
| `AiRuntimeLifecycleEvents.RuntimeRegistered` | `runtime.registered` | Indicates that a runtime instance was registered. | `RuntimeJournalFact` | LifecycleJournal = RequiredDurable; Logging = BestEffort; Realtime = BestEffort; Metrics = None |
| `AiRuntimeLifecycleEvents.RuntimeReady` | `runtime.ready` | Indicates that a runtime instance became ready. | `RuntimeJournalFact` | LifecycleJournal = RequiredDurable; Logging = BestEffort; Realtime = BestEffort; Metrics = None |
| `AiRuntimeLifecycleEvents.RuntimeDraining` | `runtime.draining` | Indicates that a runtime instance entered draining state. | `RuntimeJournalFact` | LifecycleJournal = RequiredDurable; Logging = BestEffort; Realtime = BestEffort; Metrics = None |
| `AiRuntimeLifecycleEvents.RuntimeSuppressed` | `runtime.suppressed` | Indicates that a runtime instance was suppressed. | `RuntimeJournalFact` | LifecycleJournal = RequiredDurable; Logging = BestEffort; Realtime = BestEffort; Metrics = None |
| `AiRuntimeLifecycleEvents.RuntimeUnhealthy` | `runtime.unhealthy` | Indicates that a runtime instance became unhealthy. | `RuntimeJournalFact` | LifecycleJournal = RequiredDurable; Logging = BestEffort; Realtime = BestEffort; Metrics = None |
| `AiRuntimeLifecycleEvents.RuntimeStopped` | `runtime.stopped` | Indicates that a runtime instance stopped. | `RuntimeJournalFact` | LifecycleJournal = RequiredDurable; Logging = BestEffort; Realtime = BestEffort; Metrics = None |
| `AiRuntimeLifecycleEvents.HostDeletionRequested` | `host.deletion.requested` | Indicates that host deletion was requested. | `RuntimeJournalFact` | LifecycleJournal = RequiredDurable; Logging = BestEffort; Realtime = BestEffort; Metrics = None |
| `AiRuntimeLifecycleEvents.HostDeleted` | `host.deleted` | Indicates that a host was deleted. | `RuntimeJournalFact` | LifecycleJournal = RequiredDurable; Logging = BestEffort; Realtime = BestEffort; Metrics = None |
| `AiRuntimeLifecycleEvents.HostDisappeared` | `host.disappeared` | Indicates that a previously known host disappeared. | `RuntimeJournalFact` | LifecycleJournal = RequiredDurable; Logging = BestEffort; Realtime = BestEffort; Metrics = None |
| `AiRuntimeLifecycleEvents.RuntimeReplacementRequested` | `runtime.replacement.requested` | Indicates that runtime replacement was requested. | `RuntimeJournalFact` | LifecycleJournal = RequiredDurable; Logging = BestEffort; Realtime = BestEffort; Metrics = None |
| `AiRuntimeLifecycleEvents.RuntimeReplacementRegistered` | `runtime.replacement.registered` | Indicates that a replacement runtime was registered. | `RuntimeJournalFact` | LifecycleJournal = RequiredDurable; Logging = BestEffort; Realtime = BestEffort; Metrics = None |
| `AiRuntimeLifecycleEvents.WorkAssigned` | `work.assigned` | Indicates that work was assigned to a runtime. | `RuntimeJournalFact` | LifecycleJournal = RequiredDurable; Logging = BestEffort; Realtime = BestEffort; Metrics = None |
| `AiRuntimeLifecycleEvents.WorkReassigned` | `work.reassigned` | Indicates that work was reassigned to a runtime. | `RuntimeJournalFact` | LifecycleJournal = RequiredDurable; Logging = BestEffort; Realtime = BestEffort; Metrics = None |
| `AiRuntimeLifecycleEvents.WorkReleased` | `work.released` | Indicates that work was released from a runtime. | `RuntimeJournalFact` | LifecycleJournal = RequiredDurable; Logging = BestEffort; Realtime = BestEffort; Metrics = None |

### Unexpected host/Pod-loss lifecycle sequence

```text
host.disappeared
→ runtime.suppressed / runtime.unhealthy
→ runtime.replacement.requested
→ host.creation.requested
→ host.creation.started
→ host.creation.succeeded
→ runtime.replacement.registered
→ runtime.ready
→ work.reassigned
```

The exact subset depends on the physical failure boundary. A child runtime process failure does not imply that its parent ProcessHost or Kubernetes Pod disappeared.

## 29. Projection-Catalog Invariants

The central catalog is guarded by tests with these invariants:

```text
every canonical engine event value → exactly one projection descriptor
unknown canonical value          → not silently projected
duplicate descriptor             → construction failure
ExecutionForensics               → None for every current descriptor
runtime lifecycle                → Runtime Lifecycle Journal owns durable evidence
exact recovery transitions       → Recovery Forensics owns durable evidence
legacy/decision lifecycle        → Decision Ledger owns durable evidence where mapped
Child DAG delivery observation   → transient, never promoted into false durable truth
```

This is the key difference between a central event manager and a generic fan-out bus: semantic durability and projection ownership are explicit data, not sink-local guesses.

## 30. EventDriven Reference-Test Contract

Event-driven Runtime Pool canaries are the reference synchronization profile for recursive recovery validation.

Reference shape:

```csharp
[Theory]
[Trait("ObservationMode", "EventDriven")]
[Trait("ValidationProfile", "Canary")]
[InlineData(5, 5, 5, 2, 3)]
public Task Grpc_ProcessHostPool_EventDriven_Canary_Should_Reuse_The_Same_FullFailure_Scenario(
    int maximumProcessHostCount,
    int runtimeCountPerHost,
    int submissionIterationCount,
    int executionCycleCount,
    int childDepth)
{
    return this.ExecuteFullFailureProductionScenarioAsync(
        maximumProcessHostCount,
        runtimeCountPerHost,
        submissionIterationCount,
        executionCycleCount,
        childDepth,
        ProductionRecoveryObservationMode.EventDriven);
}
```

The same shared scenario core is used for transport/host variants; the observation mode changes synchronization, not the production recovery semantics.

Reference production invariants include:

```text
real physical child-runtime failure
distinct parent ProcessHost or Pod failure
same ExecutionId for in-flight resume
exact recovered SharedRun set
warm topology reuse across cycles
canonical RuntimeLifecycleJournal evidence
MCP replay
Ledger / trace / recovery Forensics
bounded capacity
no lost run
no duplicate durable dispatch
```

---

## 31. Recursive Child DAG Validation Status

Native durable Child DAG composition is documented as **Implemented / validated** after the recursive lifecycle-observation promotion gate was closed.

Validated progression includes:

```text
ChildDepth = 0 compatibility
ChildDepth = 1 production recovery
ChildDepth = 2 recursive closure
3×3×3×2×Depth3 recursive validation
5×5×5×2×Depth3 high-scale validation
```

The important distinction is between capability validation and future proof hardening.

Current validated guarantees include recursive durable execution, parent `WaitingForExternal`, deterministic continuation, real runtime/host failure recovery, same-`ExecutionId` resume, warm reuse, replay, lifecycle evidence, and authoritative durable terminal proof for nested DAG execution.

The exact `12,750` logical-step proof in the high-scale scenarios applies to **root parent logical steps**. Separately, the bounded recursive Depth3 production proof now validates exact child-level logical-step accounting for every recursive level through durable `step.completed` Ledger evidence with zero missing and zero unexpected duplicate child logical steps. These remain distinct proof scopes and are not silently conflated by the implemented/validated status.

---

## 32. Projection and Observer Tests

The event architecture should remain protected by four complementary test layers:

```text
1. Sink implementation tests
   Ledger / Forensics / Journal / Metrics / Logging / Realtime

2. Projection catalog contract tests
   canonical event → exact applicable surfaces + durability requirements

3. Canonical declaration governance tests
   no duplicate declarations / no inline canonical strings

4. EventDriven production canaries
   real failure → canonical event → deterministic wait → durable final proof
```

No layer replaces the others.

---

## 33. Failure Diagnostics

When an EventDriven wait times out, diagnostics should answer:

```text
Which canonical event was expected?
Which identities were used to correlate it?
Was durable evidence already present?
Which realtime event was observed last?
What did the Runtime Lifecycle Journal contain?
What did Recovery Forensics contain?
What did the Ledger contain?
Did the physical host/runtime disappear?
Did replacement capacity register?
Did the same ExecutionId resume?
```

A timeout should be the start of diagnosis, not a reason to increase the timeout automatically.

---

## 34. Source-of-Truth Rules

For event semantics:

```text
Canonical namespace
Multiplexed.Abstractions.AI.Observability.Events

Source declarations
implementations/dotnet/src/Multiplexed.Abstractions/AI/Observability/Events/AiEngineEvents.cs
implementations/dotnet/src/Multiplexed.Abstractions/AI/Observability/Events/AiRuntimeLifecycleEvents.cs
```

For event-to-surface behavior:

```text
implementations/dotnet/src/Multiplexed.AI/Runtime/ControlPlane/Observability/Projections/AiEngineEventProjectionCatalog.cs
```

For current infrastructure history:

```text
Runtime Lifecycle Journal
```

For exact recovery causality:

```text
Recovery Forensics
```

For durable runtime/control-plane decision evidence:

```text
Decision Ledger
```

Documentation summarizes these contracts. It does not replace them.

---

## Related Documents

- [Observability](observability.md)
- [Durable Runtime Lifecycle Journal](runtime-lifecycle-journal.md)
- [Runtime Recovery Forensics](runtime-recovery-forensics.md)
- [Execution-Correlated Decision Ledger](execution-correlated-ledger.md)
- [Durable Child DAG Composition](child-dag-composition.md)
- [Runtime Pool Production Validation](runtime-pool-production-validation.md)
- [Testing Strategy](testing-strategy.md)
