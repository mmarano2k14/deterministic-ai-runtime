# Testing Strategy

Status: Documentation split in progress / actively validated by a large unit and integration test suite.

This document describes the testing strategy used to validate the Deterministic AI Runtime.

The complete technical reference is currently preserved in:

- [runtime-internals.md](../runtime-internals.md)

---

## Purpose

The runtime is built around strong execution guarantees.

Those guarantees cannot be validated only with simple unit tests.

The runtime must prove that it behaves correctly under:

- distributed workers
- distributed runtime instances
- concurrent claims
- retries
- worker crashes
- recovery
- retention and compaction
- payload externalization
- resolver rehydration
- replay restoration
- queue control
- execution control
- concurrency throttling
- terminal finalization races
- deterministic convergence under pressure

The purpose of the testing strategy is to validate that the runtime behaves like reliable execution infrastructure, not only like isolated application code.

---

## Testing Philosophy

The runtime testing philosophy is based on one principle:

```text
If the runtime claims a distributed guarantee, there must be a test proving it.
```

The tests should validate:

- correctness under normal execution
- correctness under concurrency
- correctness under failure
- correctness under replay
- correctness under memory pressure
- correctness under control-plane operations
- correctness under aggressive distributed scenarios

The tests are not only checking that methods return values.

They are checking that runtime guarantees hold.

---

The current repository contains **more than 800 test cases** across unit, integration, distributed, replay, observability, control-plane, Redis, MCP, and runtime orchestration scenarios.

This number is important because the runtime is not validated only through happy-path execution.

The test suite is used as proof that the runtime can survive:

- concurrency races
- worker identity propagation
- multi-runtime-instance execution
- Redis Lua atomic transitions
- replay and snapshot reconstruction
- control-plane operations
- runtime queue operations
- shared queue dispatch
- MCP tool execution
- shutdown and lifecycle races

The goal is not test volume for its own sake.

The goal is broad evidence that the runtime behaves as reliable execution infrastructure.

---

## Why Testing Matters

The runtime is not a small helper library.

It coordinates execution state, distributed workers, Redis Lua transitions, policy-driven decisions, retry, recovery, retention, replay, and control-plane behavior.

A bug in this type of system can cause:

- duplicate step execution
- lost ownership
- corrupted terminal state
- retry storms
- leaked concurrency leases
- broken replay
- unbounded Redis memory growth
- cancelled executions finishing as completed
- workers advancing work while paused
- non-deterministic convergence

Testing is therefore part of the architecture.

It is the proof layer for the runtime guarantees.

---

## What Must Be Proven

The runtime should continuously prove answers to the enterprise questions:

| Enterprise Question | Test Evidence Required |
|---|---|
| What happens if a worker crashes? | Recovery tests for stale running steps. |
| How do you prevent duplicate executions? | Atomic claim and claim-token tests. |
| How do you replay a workflow? | Snapshot restore and deterministic replay tests. |
| How do you audit an AI decision? | Observability, trace, policy, and future ledger tests. |
| How do you limit concurrency? | Redis gate and throttling tests. |
| How do you pause/resume/cancel safely? | Execution control state tests. |
| How do you control human-in-the-loop? | Waiting-for-input and submit-input tests. |
| How do you keep memory/state bounded? | Retention, compaction, eviction, resolver tests. |
| How do you coordinate multiple runtime instances? | Distributed worker/runtime instance tests. |
| How do you prove deterministic convergence? | Fingerprint and convergence validation tests. |

---

## Test Categories

The test suite should be organized around runtime guarantees.

Main categories include:

- DAG execution tests
- distributed execution tests
- multi-runtime-instance tests
- retry and recovery tests
- retention and compaction tests
- distributed concurrency and throttling tests
- execution control state tests
- runtime queue control tests
- replay and snapshot tests
- observability and tracing tests
- policy engine tests
- config-driven runtime tests
- RAG pipeline tests
- deterministic convergence tests
- stress and chaos tests
- MCP control-plane integration tests

---

## Unit Tests

Unit tests validate isolated logic.

They are useful for:

- configuration parsing
- policy resolution
- retry decision computation
- retention decision computation
- concurrency definition merging
- input binding resolution
- step executor behavior
- helper classes
- validation logic

Unit tests should be fast and deterministic.

They should not depend on Redis, MongoDB, or distributed timing.

---

## Integration Tests

Integration tests validate actual runtime behavior across components.

They are essential because many runtime guarantees only exist when components work together.

Integration tests should cover:

- Redis DAG store behavior
- MongoDB payload persistence
- Redis Lua transitions
- retry state persistence
- retention and resolver interaction
- replay from snapshots
- background controller behavior
- distributed concurrency gate behavior
- execution control state behavior
- queue control behavior

Integration tests provide stronger evidence than isolated unit tests.

---
## Control-Plane Tests

Control-plane tests validate that external runtime operations are exposed through safe adapter-neutral abstractions.

They should cover:

- replay control-plane operations
- execution control-plane operations
- runtime queue control-plane operations
- runtime instance registry operations
- runtime instance control-plane operations
- run admission decisions
- shared runtime controller behavior
- shared run persistence
- shared queue coordination
- queue pump behavior
- scale-out request publication
- provider model preparation
- control-plane observability

Control-plane tests are especially important because they prove that runtime operations can be controlled without exposing or mutating internal runtime engine state directly.

---


## Redis Lua Transition Tests

Redis Lua scripts protect critical distributed transitions.

Tests should validate:

- only one worker can claim a step
- stale claim tokens are rejected
- completion requires valid ownership
- failure requires valid ownership
- retry scheduling is atomic
- stale running steps can be recovered
- retry-ready steps are claimed only after `NextRetryAtUtc`
- terminal finalization is idempotent
- concurrency gate admission is atomic
- expired leases are cleaned safely

Redis Lua tests are critical because they validate the distributed safety boundary.

---

## DAG Execution Tests

DAG tests should validate:

- dependency ordering
- parallel eligibility
- step readiness
- terminal convergence
- failed step handling
- completed step preservation
- dependency-safe execution
- deterministic final state

Example assertions:

```text
Step B cannot run before Step A if B depends on A.
Independent steps can run in parallel.
Execution completes only when all required steps are completed.
Execution fails when a non-retryable required step fails.
```

---

## Distributed Worker Tests

Distributed worker tests should validate execution under multiple workers.

They should prove:

- multiple workers can safely process the same execution
- steps are not duplicated
- ownership remains atomic
- workers can race safely
- retry-ready steps are claimed safely
- stale workers cannot overwrite state
- the final result converges
- terminal lifecycle is idempotent

These tests should simulate real distributed conditions rather than only sequential execution.

---

## Multi-Runtime-Instance Tests

Multi-runtime-instance tests validate that more than one runtime instance can participate safely.

They should check:

- runtime instance identity is separated from execution identity
- multiple runtime instances do not corrupt shared state
- workers coordinate through Redis
- terminal finalization remains safe
- retention and replay remain compatible
- convergence remains deterministic
- one `ExecutionId` can be advanced safely by distributed workers when configured for distributed execution
- isolated executions remain isolated when each run has a unique `ExecutionId`

This area is important for future Kubernetes and enterprise demo scenarios.

---

## Runtime Registry and Capacity Descriptor Tests

Runtime registry and capacity descriptor tests validate runtime instance visibility.

They should cover:

- runtime instance registration
- runtime instance heartbeat
- runtime instance unregister
- runtime role separation
- control-plane role visibility
- runtime role eligibility
- Redis-backed runtime instance registry behavior
- runtime capacity descriptor publication
- runtime capacity descriptor heartbeat updates
- runtime capacity descriptor removal on unregister
- worker count publication
- max run slot publication
- available run slot publication
- queue pressure publication
- paused queue visibility
- `CanAcceptRun` correctness
- stale or stopped runtime visibility

Important assertions:

```text
A control-plane registration must not be treated as a dispatchable runtime instance.

A runtime instance must publish capacity during registration and heartbeat.

CapacityStore resolution must not register duplicate stores.

Unregister must remove or stop the corresponding capacity descriptor.

Admission should eventually use capacity descriptors as the primary scheduling source of truth.
```

---


## Retry Tests

Retry tests should validate:

- `config.retry` resolution
- retry policy execution
- legacy string retry policies
- structured retry policy objects
- retry count increments
- max retry exhaustion
- retry delay calculation
- `WaitingForRetry` state
- retry-ready claim behavior
- retry after failure
- retry success after previous failure
- retry failure after max retries

Retry tests should also prove that hidden local retry loops are not required.

Retry is runtime state.

---

## Recovery Tests

Recovery tests should validate worker crash behavior.

They should prove:

- a stale running step can return to `Ready`
- recovery does not consume retry budget
- another worker can claim recovered work
- stale worker completion is rejected
- claim token mismatch protects state
- recovery works under distributed execution

Recovery tests are different from retry tests.

Retry means logic failed.

Recovery means ownership was abandoned.

---

## Retention and Compaction Tests

Retention tests should prove that hot state can be reduced without breaking execution.

They should cover:

- retention trigger evaluation
- `config.retention` policy resolution
- policy-driven retention decisions
- compaction
- eviction
- hybrid retention
- payload externalization
- resolver rehydration
- archive-backed reconstruction
- completed step accessibility after retention
- terminal lifecycle compatibility
- replay compatibility foundations

The most important retention guarantee is:

```text
Retention must reduce hot state without making required data inaccessible.
```

---

## Replay and Snapshot Tests

Replay tests should validate:

- terminal snapshot persistence
- snapshot normalization
- replay when live state already exists
- replay after live state deletion
- restored DAG state
- restored terminal status
- deterministic fingerprint comparison
- retry-count preservation
- replay compatibility with retention
- replay compatibility with externalized payloads

A key replay assertion is:

```text
Original terminal execution fingerprint
=
Restored execution fingerprint
```

Replay tests should not only verify that restore returns success.

They should verify that the restored execution represents the same terminal result.

---

## Execution Control State Tests

Execution control tests should validate `ExecutionId`-level control.

They should cover:

- durable Redis control state
- optimistic Redis version updates
- pause execution
- resume execution
- cancel execution
- claim blocking while paused
- claim blocking while waiting for input
- claim blocking while cancelling
- waiting for human input
- submitting human input
- `Pausing -> Paused`
- `Resuming -> Running`
- cancellation finalization override

Important assertion:

```text
If cancellation is requested before terminal finalization,
final execution status must be Cancelled even if the DAG naturally completes.
```

---

## Runtime Queue Control Tests

Queue control tests should validate `RunId`-level behavior.

They should cover:

- queue pause
- queue resume
- queued run cancellation
- unknown queued run cancellation
- running run cancellation bridge
- hot enqueue while controller is running
- hot enqueue while queue is paused
- queued run completion task behavior
- controller shutdown cancellation for queued runs
- `RunId` / `ExecutionId` separation

Important assertions:

```text
Cancelled queued run has no ExecutionId.
Running run cancellation delegates to ExecutionId control.
RunId must not be treated as ExecutionId.
Cancelled queued run must complete its completion task.
```

---

## MCP Control-Plane Tests

MCP tests validate that the runtime can be operated through an external control-plane adapter.

These tests are important because the MCP server is not only a demo surface.

It proves that runtime operations can be exposed safely outside the engine through tool-based control-plane commands.

MCP tests should validate:

- MCP host startup
- MCP control-plane service registration
- MCP host mode configuration
- `ControlPlaneOnly` behavior
- `ControlPlaneWithLocalRuntimeInstances` behavior
- `RuntimeInstanceOnly` preparation
- runtime role separation
- control-plane host not selected as executable runtime
- local runtime instance pool startup
- runtime instance registration and heartbeat
- Redis-backed runtime registry visibility
- Redis-backed runtime capacity descriptor publication
- shared run submission through MCP tools
- shared run listing through MCP tools
- shared queue drain through MCP tools
- runtime queue run-status polling through MCP tools
- replay execution through MCP tools
- replay report retrieval through MCP tools
- observability ledger retrieval through MCP tools
- observability trace retrieval through MCP tools
- execution control operations through MCP tools
- local runtime queue control through MCP tools
- idempotent runtime unregister during MCP host shutdown
- idempotent local runtime pool shutdown during MCP host shutdown

The MCP test suite validates the control-plane path:

```text
MCP Tool Call
    ↓
MCP Server
    ↓
Runtime Control Plane
    ↓
Shared Runtime Controller
    ↓
Admission
    ↓
Runtime Instance Dispatch
    ↓
Local Runtime Queue
    ↓
Workers
    ↓
DAG Execution Engine
```

Important MCP assertions include:

```text
The MCP control-plane host must not be selected as a dispatch target.

Only runtime-role instances can receive assigned runs.

A submitted shared run must be visible through MCP shared run tools.

A dispatched shared run must expose a LocalRunId.

A running local run must eventually expose an ExecutionId.

Replay tools must be able to load replay data for the ExecutionId.

Observability tools must be able to return ledger and trace data for the ExecutionId.

MCP host shutdown must unregister runtime instances once.

Repeated StopAsync or host disposal must be idempotent.
```

Example validated local MCP topology:

```text
mcp-control-plane
    Role = ControlPlane
    CanAcceptRun = false

mcp-runtime-1
    Role = Runtime
    WorkerCount = 10
    MaxRunSlots = 5

mcp-runtime-2
    Role = Runtime
    WorkerCount = 10
    MaxRunSlots = 5

mcp-runtime-3
    Role = Runtime
    WorkerCount = 10
    MaxRunSlots = 5
```

These tests prepare the runtime for future Kubernetes deployments where the MCP server can act as a control-plane pod and runtime instances can run as separate pods.

---


## Shared Runtime Controller and Shared Queue Tests

Shared runtime controller tests validate the orchestration layer above local runtime queues.

They should cover:

- shared run creation
- shared run retrieval
- shared run listing
- shared run cancellation
- admission assignment
- direct assigned-run dispatch
- global shared queue enqueue
- global shared queue claim
- global shared queue dispatch
- missing shared run requeue
- dispatch failure requeue
- mark shared queue item dispatched
- mark shared run dispatched
- queue pump cycles
- background queue service lifecycle
- scale-out request publication
- Redis-backed shared run store behavior
- Redis-backed shared queue behavior
- Redis atomic queue claim safety
- concurrent dispatch safety

Important assertions:

```text
Only one dispatcher can claim a pending shared queue item.

A shared run record must exist independently from local runtime queue state.

Assigned dispatch must preserve the local queue model.

Global queue fallback must not bypass admission.

Dispatch failures must requeue when policy requires it.

A shared run must not be marked dispatched unless dispatch succeeded.
```

---

## Distributed Concurrency and Throttling Tests

Concurrency tests should validate:

- `config.concurrency` resolution
- direct values remain authoritative
- generic `concurrency.throttle` matching
- provider throttle
- model throttle
- operation throttle
- step throttle
- step-type throttle
- pipeline throttle
- policy admission deny
- Redis ZSET lease acquisition
- lease expiration
- release on failed DAG claim
- diagnostic denial reasons

Important assertion:

```text
If Redis capacity is acquired but DAG claim fails,
the concurrency lease must be released immediately.
```

This prevents leaked distributed capacity.

---

## Policy Engine Tests

Policy tests should validate the shared policy model.

They should cover:

- legacy string policies
- structured policy definitions
- policy kind resolution
- policy registry lookup
- retry policy execution
- retention policy execution
- concurrency policy execution
- policy-specific configuration
- policy denial diagnostics

The runtime should prove that Retry, Retention, and Concurrency all follow the same policy-driven architecture.

---

## Config-Driven Runtime Tests

Configuration tests should validate:

- pipeline definition loading
- DAG step definition parsing
- `config.retry`
- `config.retention`
- `config.concurrency`
- provider/model/operation metadata
- step-level config
- pipeline-level config
- step override behavior
- invalid config rejection

Config-driven tests matter because runtime behavior is declared, not hardcoded.

---

## RAG Pipeline Tests

RAG pipeline tests should validate:

- retrieval step execution
- multiple provider retrieval
- providerKey-based retrieval
- operation-based dispatch
- merge step execution
- compose step execution
- dependency ordering
- parallel retrieval
- provider-based retrieval configuration
- deterministic composition
- resolver compatibility
- payload externalization compatibility

RAG tests should prove that AI workflow patterns benefit from the same runtime guarantees as any other DAG.

---

## Observability Tests

Observability tests should validate that runtime activity emits usable signals.

They may cover:

- execution lifecycle metrics
- retry metrics
- recovery metrics
- retention metrics
- resolver metrics
- storage metrics
- hot state metrics
- concurrency admission diagnostics
- diagnostic denial reasons
- trace/timeline events
- control-plane events

Observability tests should avoid making execution correctness depend on logs or UI.

The runtime must remain correct even if metrics or dashboards are disabled.

---

## Deterministic Convergence Tests

Deterministic convergence tests prove that final execution state is independent of runtime scheduling.

They should vary:

- worker count
- runtime instance count
- execution order
- batch size
- retry timing
- recovery timing
- retention behavior
- distributed scheduling
- concurrency admission timing

The expected result is:

```text
Same input + same pipeline definition
        ↓
same terminal state
        ↓
same deterministic fingerprint
```

This is one of the most important runtime guarantees.

---

## Stress and Chaos Tests

Stress and chaos tests validate runtime behavior under pressure.

They may include:

- large DAG executions
- many workers
- repeated runs
- aggressive distributed scenarios
- retry-heavy scenarios
- retention-heavy scenarios
- replay reconstruction after cleanup
- convergence validation after distributed execution
- queue/control operations during distributed execution

These tests help prove that the runtime model survives more than simple happy paths.

---

## Aggressive Distributed Scenario Evidence

The runtime has been tested under aggressive distributed scenarios.

Examples of evidence may include:

- large DAG executions
- multi-worker execution
- repeated execution runs
- retention and eviction during execution
- replay reconstruction after cleanup
- convergence validation after distributed execution
- queue and execution control operations under distributed execution

These tests are important because production AI execution failures often appear only under concurrency, pressure, timing races, and partial failure.

---

## Test Evidence and Documentation

Tests are part of the project evidence.

Documentation should reference validated behavior carefully.

When a capability is documented, it should be classified as:

- implemented
- implemented / validated
- foundation available
- planned

Avoid presenting roadmap items as finished product capabilities.

This is especially important for:

- official replay API
- durable decision ledger
- observability dashboard
- Kubernetes deployment
- public SDK polish
- cost governance

---

## Recommended Test Structure

A useful test structure is:

```text
Tests/
  Multiplexed.AI.Tests.Unit/
    Configuration/
    Policies/
    Retry/
    Retention/
    Concurrency/

  Multiplexed.AI.Tests.Integration/
    DagExecution/
    DistributedExecution/
    RetryAndRecovery/
    RetentionAndCompaction/
    ConcurrencyThrottling/
    ExecutionControl/
    RuntimeQueueControl/
    ReplayAndSnapshots/
    Observability/
    RagPipelines/
```

The exact repository layout may differ, but tests should be grouped around runtime guarantees.

---

## Example Assertions

Useful assertions include:

```text
Only one worker can claim a ready step.

A stale worker cannot complete a step after ownership has moved.

Recovery does not increment retry count.

Retry exhaustion marks the step failed.

Pause blocks new claims.

Cancel overrides natural completion during finalization.

Queued cancellation does not create an ExecutionId.

Cancelled queued run completes its completion task.

Replay from snapshot restores the same deterministic fingerprint.

Retention does not break required completed step resolution.

Provider throttle denies capacity when the limit is reached.

Lease is released when concurrency admission succeeds but DAG claim fails.

A distributed execution converges to the same terminal fingerprint.
```

---

## Current Status

| Test Area | Status |
|---|---|
| MCP control-plane tests | Implemented / ongoing |
| Shared runtime controller tests | Implemented / ongoing |
| Redis shared run store tests | Implemented / ongoing |
| Redis shared queue tests | Implemented / ongoing |
| Runtime registry and capacity descriptor tests | Implemented / ongoing |
| Runtime shutdown lifecycle tests | Implemented / ongoing |
| Runtime provider model tests | Planned |
| DAG execution tests | Implemented / ongoing |
| Redis Lua claim tests | Implemented / ongoing |
| Distributed worker tests | Implemented / ongoing |
| Multi-runtime-instance tests | Implemented / ongoing |
| Retry and recovery tests | Implemented / ongoing |
| Retention and resolver tests | Implemented / ongoing |
| Distributed concurrency tests | Implemented / ongoing |
| Execution control tests | Implemented / ongoing |
| Runtime queue control tests | Implemented / ongoing |
| Replay and snapshot tests | Implemented / validated foundations |
| Deterministic fingerprint tests | Implemented / validated foundations |
| Observability tests | Foundation available / ongoing |
| RAG pipeline tests | Implemented / ongoing |
| Kubernetes scenario tests | Planned |
| Full enterprise demo scenario | Planned |
| Durable decision ledger tests | Planned |

---

## Responsibilities by Test Type

| Test Type | Responsibility |
|---|---|
| Unit tests | Validate isolated logic quickly. |
| Integration tests | Validate real runtime component interactions. |
| Distributed tests | Validate multi-worker and multi-instance behavior. |
| Chaos tests | Validate behavior under failure and pressure. |
| Replay tests | Validate restoration and deterministic equivalence. |
| Observability tests | Validate runtime emits useful diagnostics. |
| Regression tests | Prevent previously fixed runtime bugs from returning. |

---

## Summary

The testing strategy validates the runtime as execution infrastructure.

It proves that:

- more than 800 test cases validate the runtime across unit, integration, distributed, Redis, replay, observability, control-plane, and MCP scenarios
- worker crashes can be recovered
- retries are deterministic and observable
- retention reduces hot state without losing required data
- replay restores equivalent terminal state
- queue and execution control are separated
- concurrency limits are enforced before execution
- policy-driven behavior is testable
- deterministic convergence holds under distributed execution

The goal is not only to test features.

The goal is to prove runtime guarantees.

---

## Related Documents

- [Architecture Overview](architecture-overview.md)
- [Distributed Execution](distributed-execution.md)
- [Retry and Recovery](retry-and-recovery.md)
- [Retention and Compaction](retention-and-compaction.md)
- [Distributed Concurrency and Throttling](distributed-concurrency-throttling.md)
- [Execution Control State](execution-control-state.md)
- [Runtime Queue Control](runtime-queue-control.md)
- [Runtime Control Plane](runtime-control-plane.md)
- [MCP Server as Runtime Control Plane](mcp-server-control-plane.md)
- [Runtime Instance Provider Model](runtime-instance-provider-model.md)
- [Replay and Audit](replay-and-audit.md)
- [Observability](observability.md)
- [Policy-Driven Execution](policy-driven-execution.md)
- [Config-Driven Runtime](config-driven-runtime.md)
- [RAG Pipelines](rag-pipelines.md)

---

## Documentation Rule

This document is a focused extraction from the complete technical reference.

The original technical depth remains preserved in:

- [runtime-internals.md](../runtime-internals.md)

Do not remove content from `runtime-internals.md` until the extracted documentation has been reviewed and validated.
