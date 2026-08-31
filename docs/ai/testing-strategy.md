# Testing Strategy

Status: Actively validated by a large unit and integration suite covering MCP, Redis, local runtime pools, HTTP/gRPC ProcessHostPool, KubernetesPool, historical one-runtime-per-Pod Kubernetes hosting, real process and Pod failure, hierarchical child/full-boundary recovery, operator-triggered external parent-boundary failure, multi-tenant isolation, replay, ledger, lifecycle, trace, and forensics proof.

This document describes the testing strategy used to validate the Deterministic AI Runtime.

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
- queue-first dispatch behavior
- shared queue pump/manual drain behavior
- shared queue pump readiness behavior
- runtime instance provider dispatch behavior
- HTTP pooled runtime provider behavior
- HTTP runtime provider hardening
- HTTP dispatch timeout, retry, and circuit breaker behavior
- HTTP structured dispatch failure persistence
- HTTP runtime scale-out provider/provisioner behavior
- gRPC runtime provider dispatch behavior
- gRPC runtime scale-out provider/provisioner behavior
- gRPC process-host `RuntimeInstanceOnly` scale-out behavior
- Runtime Host Manager host creation behavior
- Kubernetes Pod/Service lifecycle, layered readiness, endpoint exposure, and termination behavior
- process-host `RuntimeInstanceOnly` scale-out behavior
- real HTTP and gRPC process-host runtime crash recovery behavior
- in-flight DAG resume with preserved `ExecutionId`
- local-queued work redispatch through durable `SharedRunId`
- safe-tenant non-impact during impacted tenant recovery
- runtime recovery forensics and incident timelines
- recovery replay / ledger / trace proof after convergence
- MCP production runtime scenario framework behavior
- tenant-aware HTTP/gRPC Shared/Dedicated/Hybrid scale-out behavior
- durable replay / ledger / trace validation across process boundaries
- Redis control-plane discovery and id resolution
- Redis registry and capacity cleanup
- Redis admission reservation behavior
- Redis scale-out request persistence
- scale-out watcher behavior
- provider-based scale-out selector behavior
- local runtime instance scaler behavior
- fulfilled scale-out shared run requeue behavior
- scale-out dispatch and execution completion
- worker-capacity saturation

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

The current repository contains **more than 1000 test cases** across unit, integration, distributed, replay, observability, control-plane, Redis, MCP, provider-hosting, shared queue, runtime orchestration, HTTP hardening, HTTP scale-out, and tenant-aware runtime isolation scenarios.

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
- queue-first submit mode
- manual shared queue drain
- background shared queue pump behavior
- dispatch-time admission
- runtime provider-hosting scenarios
- HTTP pooled runtime dispatch
- HTTP provider timeout/retry/circuit-breaker behavior
- HTTP dispatch failure persistence
- HTTP runtime scale-out provider/provisioner behavior
- gRPC runtime provider and gRPC scale-out provider behavior
- Runtime Host Manager process-host provisioning
- real process-host runtime crash recovery
- multi-tenant runtime crash isolation with safe tenant proof
- in-flight execution resume and local queued redispatch
- runtime recovery forensics, replay, ledger, and trace proof after recovery
- tenant-aware HTTP/gRPC scale-out and fallback policy behavior
- Dedicated / Shared / Hybrid process-host production scenarios
- durable replay / ledger / trace validation across process boundaries
- Redis discovery/registry/capacity lifecycle
- Redis admission reservations
- Redis scale-out request lifecycle
- local runtime scale-out from zero executable capacity
- fulfilled-run requeue and pump dispatch
- worker capacity visibility
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
| What happens if an entire runtime process dies? | Process-host runtime crash recovery tests. |
| How do you prove only impacted tenants recovered? | Multi-tenant crash isolation and safe-tenant non-impact tests. |
| How do you prove recovery is auditable? | Recovery forensics plus replay / ledger / trace validation. |
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
- shared queue pump/manual drain tests
- provider-based runtime hosting tests
- HTTP pooled runtime provider tests
- HTTP runtime provider hardening tests
- HTTP runtime scale-out provider tests
- gRPC runtime provider tests
- gRPC runtime scale-out provider tests
- Runtime Host Manager process-host tests
- MCP production runtime scenario framework tests
- production runtime crash recovery scenario tests
- multi-tenant runtime crash isolation tests
- runtime recovery forensics tests
- recovery replay / ledger / trace proof tests
- tenant-aware HTTP/gRPC scale-out scenario tests
- durable process-boundary observability tests
- Redis discovery and control-plane id resolver tests
- Redis runtime registry and capacity cleanup tests
- Redis admission reservation tests
- Redis scale-out request store tests
- scale-out watcher tests
- scale-out provider selector tests
- local runtime scaler tests
- fulfilled scale-out run requeue tests
- MCP Redis local scale-out execution tests
- runtime worker capacity tests

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
- runtime instance capacity store operations
- control-plane discovery store operations
- control-plane id resolver operations
- runtime instance control-plane operations
- run admission decisions
- admission reservation behavior
- shared runtime controller behavior
- shared run persistence
- shared queue coordination
- queue pump behavior
- scale-out request publication
- Redis scale-out request persistence
- scale-out watcher processing
- scale-out provider selector resolution
- HTTP scale-out provider selector resolution
- HTTP scale-out provisioner behavior
- gRPC scale-out provider selector resolution
- gRPC scale-out provisioner behavior
- Runtime Host Manager provisioning behavior
- process-host runtime readiness behavior
- runtime health reconciliation and unsafe capacity suppression behavior
- execution recovery reconciliation behavior for assigned work
- local runtime scale-out provider behavior
- fulfilled scale-out shared run requeue
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

This area is used directly by Kubernetes multi-Pod and enterprise demo scenarios.

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
- Redis-backed runtime instance registry cleanup
- control-plane discovery descriptor publication
- control-plane id resolver behavior
- runtime capacity descriptor publication
- runtime capacity descriptor heartbeat updates
- runtime capacity descriptor removal on unregister
- runtime capacity descriptor removal during shutdown
- cleanup without late rediscovery dependency
- worker count publication
- active worker count publication
- available worker count publication
- max local workers per execution publication
- max run slot publication
- available run slot publication
- queue pressure publication
- paused queue visibility
- worker-aware `CanAcceptRun` correctness
- stale or stopped runtime visibility

Important assertions:

```text
A control-plane registration must not be treated as a dispatchable runtime instance.

A runtime instance must publish run capacity and worker capacity during registration and heartbeat.

CapacityStore resolution must not register duplicate stores.

Unregister must remove or stop the corresponding capacity descriptor.

Admission should use capacity descriptors and reservation state as primary scheduling inputs in provider-based dispatch scenarios.

Runtime unregister and capacity removal should not depend on rediscovery after the runtime instance has already registered or published capacity.
```

---


## Shared Queue Pump and Queue-First Tests

Shared queue pump tests validate the control-plane path above local runtime queues.

They should cover:

- queue-first shared run submission
- shared run remains `QueuedGlobally` before dispatch
- shared queue item remains `Pending` before dispatch
- background pump dispatch
- background pump readiness gate
- manual drain dispatch
- manual drain while background pump is disabled
- local runtime dispatch after manual drain
- HTTP runtime dispatch after manual drain
- HTTP pooled runtime dispatch after manual drain
- HTTP pooled runtime dispatch through background pump
- no automatic dispatch when the hosted pump is disabled
- queue item marked `Dispatched` only after successful dispatch
- shared run marked `Dispatched` only after successful dispatch
- dispatch failure requeues correctly
- missing shared run requeues correctly
- pump stops when no item is available
- pump respects max dispatches per cycle
- pump waits for visible runtime capacity before automatic dispatch

Important assertions:

```text
QueueFirst submit must create a shared run and queue item without creating a local RunId.

Manual drain must work when AiSharedQueuePump is enabled even if the background hosted pump is disabled.

A shared queue item must not become Dispatched unless runtime dispatch succeeds.

The background pump must not dispatch before at least one runtime instance is visible, ready, and capacity-published.

A failed dispatch must requeue the shared queue item and preserve the shared run as QueuedGlobally.
```

---

## Scale-Out Request Lifecycle Tests

Scale-out tests validate the path from admission requesting capacity to the original shared run being executed after capacity is created.

They should cover:

- scale-out request publication when admission returns `RequestScaleOut`
- store-backed scale-out request publisher behavior
- Redis-backed scale-out request persistence
- pending request listing by control-plane id
- request observed transition
- request fulfilled transition
- request rejected transition
- provider failure handling
- provider rejection handling
- provider hint propagation
- control-plane id propagation
- shared run id propagation
- metadata propagation
- watcher id propagation
- idempotent request observation
- scale-out provider selector resolution
- local provider scale-out capability
- gRPC provider scale-out capability
- local runtime instance scaler behavior
- fulfilled scale-out shared run requeue
- shared queue pump dispatch after scale-out fulfillment
- runtime execution completion after dynamic capacity creation
- HTTP provider scale-out request fulfillment
- gRPC provider scale-out request fulfillment
- HTTP provider metadata/capacity publication
- tenant-aware HTTP Dedicated no-shared-fallback behavior
- tenant-aware HTTP Hybrid shared-fallback behavior

Important assertions:

```text
A run submitted with DirectDispatch and no runtime capacity should become ScaleOutRequested when scale-out is enabled.

A Redis scale-out request should be persisted with the expected RequestId, SharedRunId, ControlPlaneId, ProviderHint, and metadata.

The watcher should mark a request observed before calling the provider.

The selector should resolve the provider using:
    request.ProviderHint
    -> AiRuntimeInstanceRegistrationOptions.ProviderName
    -> local

Local scale-out should create/register/start a runtime instance.

A fulfilled scale-out request should not dispatch directly from the watcher.

A fulfilled scale-out request should requeue the original shared run.

HTTP scale-out fulfillment should publish registry/capacity metadata through the HTTP provisioner and mark the request fulfilled without bypassing the normal shared queue lifecycle.

A Dedicated tenant scale-out request must create or expose only dedicated tenant-visible HTTP capacity and must not silently fall back to shared HTTP capacity when fallback is disabled.

A Hybrid tenant with shared fallback enabled may use existing shared HTTP capacity when policy allows it.

The shared queue pump should claim the requeued item and perform dispatch-time admission.

After the new runtime instance publishes capacity, admission should select it.

The runtime provider should dispatch into the new instance local queue.

The local run should expose a LocalRunId and eventually an ExecutionId.

The runtime run should reach a terminal completed status.
```

Validated end-to-end evidence:

```text
Initial ActiveLocalInstances = 0
Admission = RequestScaleOut
SharedRun.Status = ScaleOutRequested
ScaleOutRequest.Status = Fulfilled
ScaleOutRuntimeInstanceId = host-...:mcp-scaleout-runtime-1
ActiveLocalInstances = 1
SharedRun.Status = Dispatched
QueueStatus = Dispatched
LocalRunId = available
ExecutionId = available
RuntimeRunStatus = completed
```

Primary MCP integration scenario:

```text
ControlPlaneWithLocalRuntimeInstances_With_No_Runtime_Capacity_Should_ScaleOut_Requeue_Dispatch_And_Execute_Run
```

This scenario proved the reusable scale-out control loop that is now also consumed by the Kubernetes Host Manager path.

Validated HTTP scale-out evidence:

```text
ProviderHint = http
ScaleOutRequest.Status = Pending -> Observed -> Fulfilled
HTTP provider implements IAiRuntimeScaleOutProvider
IAiHttpRuntimeScaleOutProvisioner resolves effective tenant runtime settings
Runtime Host Manager receives the scale-out request
HostCreationMode = Process launches a real RuntimeInstanceOnly process
RuntimeInstanceOnly process self-registers
Redis runtime registry = HTTP provider/runtime metadata published
Redis runtime capacity store = HTTP transport metadata published
Readiness confirms usable capacity
Dedicated tenant = no shared HTTP fallback when disabled
Hybrid tenant = shared fallback only when allowed
```

Validated gRPC scale-out evidence:

```text
ProviderHint = grpc
ScaleOutRequest.Status = Pending -> Observed -> Fulfilled
gRPC provider implements IAiRuntimeScaleOutProvider
IAiGrpcRuntimeScaleOutProvisioner resolves effective tenant runtime settings
Runtime Host Manager receives the scale-out request
HostCreationMode = Process launches a real RuntimeInstanceOnly process
RuntimeInstanceOnly process self-registers with provider.name = grpc and transport.name = grpc
Redis runtime registry = gRPC provider/runtime metadata published
Redis runtime capacity store = gRPC transport metadata published
Kestrel child process endpoint is configured for HTTP/2
gRPC dispatch reaches the RuntimeInstanceOnly command service
```

Primary gRPC integration scenarios:

```text
Grpc_ProcessHost_Should_Requeue_Real_InFlight_Dag_After_Runtime_Process_Kill
Grpc_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_With_Strict_Dag_Resume_Replay_Ledger_And_Trace
Grpc_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace
```

Primary HTTP integration scenarios:

```text
ControlPlaneWithHttpRuntimeInstances_With_No_Runtime_Capacity_Should_Fulfill_Redis_ScaleOut_Request_Using_Http_Provider
ControlPlaneWithHttpRuntimeInstances_With_Dedicated_Tenant_Should_Fulfill_Tenant_Aware_Redis_ScaleOut_Request_Using_Http_Provider
ControlPlaneWithHttpRuntimeInstances_With_Hybrid_Tenant_Should_Fulfill_Tenant_Aware_Redis_ScaleOut_Request_Using_Http_Provider
ControlPlaneWithHttpRuntimeInstances_With_Dedicated_Tenant_Should_Not_Fallback_To_Shared_Http_Runtime_When_Available
ControlPlaneWithHttpRuntimeInstances_With_Hybrid_Tenant_Should_Fallback_To_Shared_Http_Runtime_When_Available
Http_ProcessHost_Should_Run_MixedTenant_Full_Production_Validation_Scenario
```

These scenarios validate both the HTTP control-plane scale-out loop and the stronger process-host production path through the Runtime Host Manager.

---

## Dispatch-Time Admission Tests

Shared queue dispatch now re-evaluates admission at drain time.

Tests should prove:

- pump identity is not automatically the assigned runtime identity
- `PumpRuntimeInstanceId` identifies who is draining
- `AssignedRuntimeInstanceId` identifies who receives the run
- admission can select a different runtime instance during drain
- fake admission can deterministically assign a runtime target for tests
- multi-instance pump tests remain deterministic when each pump injects its own assigned target
- no-double-dispatch behavior still holds after dispatch-time admission

Important assertions:

```text
PumpRuntimeInstanceId must not be treated as the dispatch target by default.

Dispatch target must come from admission.

Tests that expect pump-local dispatch must explicitly configure admission to assign the pump runtime instance.
```

---

## Runtime Provider Hosting Tests

Provider-hosting tests validate that control-plane and runtime-instance responsibilities can be separated.

They should cover:

- local runtime instance provider flow
- HTTP runtime provider flow
- HTTP pooled runtime provider flow
- gRPC runtime provider flow
- `RuntimeInstanceOnly` host mode
- `ControlPlaneWithLocalRuntimeInstances` host mode
- `ControlPlaneWithHttpRuntimeInstances` host mode
- `ControlPlaneWithGrpcRuntimeInstances` host mode
- runtime instance registration with provider metadata
- control-plane discovery resolution before runtime-only registration
- provider metadata propagation
- dispatch through selected runtime instance provider path
- dispatch to pooled `runtime-http-*` child runtime instances
- queue-first run completion through local provider
- queue-first run completion through HTTP provider
- pump disabled / manual drain behavior with provider-hosted runtime instances
- local provider scale-out capability
- gRPC provider scale-out capability
- local runtime scaler creation path
- provider-based scale-out selector behavior
- scale-out request fulfilled/rejected behavior

Important assertions:

```text
The selected runtime provider must deliver the run into the target runtime instance local queue.

The control-plane host must not execute DAG steps directly when operating as a control-plane-only participant.

Provider-hosted runtime instances must expose local RunId and ExecutionId after dispatch.

For HTTP pooled runtime scenarios, assertions should target the assigned child runtime instance, such as `runtime-http-1`, `runtime-http-2`, or `runtime-http-3`, not the parent HTTP transport host identity.
```

---

## HTTP Pooled Runtime Provider Tests

HTTP pooled runtime provider tests validate the current production-oriented provider hosting model.

They should cover:

- `ControlPlaneWithHttpRuntimeInstances`
- `RuntimeInstanceOnly` HTTP host
- internal local runtime instance pool
- child runtime identities using the `runtime-http-*` prefix
- provider metadata for HTTP transport
- dispatch to assigned child runtime instance
- local run status through HTTP provider
- execution id visibility after execution starts
- queue-first submit with manual drain
- queue-first submit with background pump
- long-running execution pause/resume through HTTP provider
- long-running execution cancellation through HTTP provider
- runtime queue cancellation routing to the assigned child runtime instance
- larger pipelines through HTTP provider
- multiple submitted runs distributed across pooled child runtimes

Important assertions:

```text
The HTTP host is transport and hosting infrastructure.

The child runtime instances are the dispatch targets.

AssignedRuntimeInstanceId should point to a pooled runtime-http-* child instance.

The provider must route status, queue control, and cancellation to the assigned child runtime instance.
```

## HTTP Runtime Provider Hardening Tests

HTTP runtime provider hardening tests validate that dispatch failures are bounded, observable, persisted, and safe to retry when appropriate.

They should cover:

- dispatch timeout behavior
- retry-on-transient-failure behavior
- retry exhaustion behavior
- non-retryable HTTP 4xx behavior
- invalid response behavior
- provider unavailable behavior
- circuit breaker open behavior
- cancellation behavior
- exception handling behavior
- structured failure reason mapping
- shared run dispatch failure persistence
- queue requeue behavior after provider dispatch failure
- options binding for timeout, retry, and circuit breaker settings

Important assertions:

```text
A timeout should return http-dispatch-timeout.

A provider unavailable case should return http-provider-unavailable.

A non-retryable HTTP error should return http-command-non-retryable.

A retryable HTTP error should retry according to configured options.

A circuit-open dispatch should return http-circuit-open without attempting the command again.

A dispatch failure should be persisted through the shared run store.

The shared queue dispatcher should not mark a queue item dispatched when HTTP dispatch fails.
```

These tests prove that HTTP provider dispatch failure is a controlled runtime outcome, not an unstructured transport exception.

## HTTP Runtime Scale-Out Provider Tests

HTTP runtime scale-out provider tests validate that the HTTP provider participates in the same scale-out capability model used when the Host Manager selects Process or Kubernetes lifecycle.

They should cover:

- `HttpAiRuntimeInstanceProvider` implementing `IAiRuntimeScaleOutProvider`
- no-provisioner rejection behavior
- `IAiHttpRuntimeScaleOutProvisioner` registration through DI
- `AiHttpRuntimeScaleOutOptions` binding
- HTTP provider selector resolution from `ProviderHint = http`
- provider request propagation to the HTTP provisioner
- runtime id prefix propagation
- worker count propagation
- max concurrent run slot propagation
- local queue capacity propagation
- tenant id propagation
- tenant group id propagation
- isolation mode propagation
- shared fallback flags propagation
- runtime registry metadata publication
- runtime capacity metadata publication
- fulfilled request status transition
- rejected request status transition when provisioner fails
- current metadata-only provisioner behavior
- future Remote MCP Runtime Host Manager direction

Important assertions:

```text
ProviderHint = http should resolve the HTTP provider through AiRuntimeScaleOutProviderSelector.

HTTP scale-out should preserve TenantId, TenantGroupId, IsolationMode, RuntimeInstanceIdPrefix, WorkerCountPerInstance, MaxConcurrentRunsPerInstance, LocalQueueCapacity, and MaxRuntimeInstances.

The HTTP provisioner should publish registry and capacity descriptors with provider.name = http and HTTP transport metadata.

The HTTP watcher path should mark the Redis scale-out request Fulfilled only after the provider returns success.

The validated process-host HTTP provisioner path can delegate to the Runtime Host Manager and launch a real RuntimeInstanceOnly process when HostCreationMode = Process.
```

## gRPC Runtime Provider and Scale-Out Provider Tests

gRPC runtime provider tests validate that the provider model is not HTTP-specific.

They should cover:

- `GrpcAiRuntimeInstanceProvider` dispatch through typed gRPC commands;
- `GrpcAiRuntimeInstanceProvider` implementing `IAiRuntimeScaleOutProvider`;
- `ControlPlaneWithGrpcRuntimeInstances` host mode;
- `RuntimeInstanceOnly` gRPC command service mapping;
- gRPC provider selector resolution from `ProviderHint = grpc`;
- gRPC scale-out provisioner behavior;
- Runtime Host Manager process launch from gRPC scale-out;
- child process registration with `provider.name = grpc`;
- child process registration with `transport.name = grpc`;
- Kestrel HTTP/2 configuration for gRPC process-host tests;
- Redis scale-out request fulfillment through the gRPC provider;
- gRPC dispatch into the replacement runtime local queue;
- structured `grpc-*` dispatch failure reasons such as circuit-open or provider-unavailable;
- gRPC process-host crash recovery using the same provider-agnostic recovery base as HTTP.

Important assertions:

```text
ProviderHint = grpc should resolve the gRPC scale-out provider.

The gRPC provider should use the Runtime Host Manager for process-host scale-out.

The spawned RuntimeInstanceOnly process should self-register with provider.name = grpc and transport.name = grpc.

The child runtime endpoint must support HTTP/2 for gRPC commands.

gRPC dispatch should produce a LocalRunId and ExecutionId through the normal runtime local queue.

gRPC process-host crash recovery should preserve the same ExecutionId for in-flight DAG resume.
```

## Tenant-Aware HTTP Scale-Out Scenario Tests

Tenant-aware HTTP scale-out scenario tests validate that HTTP capacity follows the same Shared, Dedicated, and Hybrid rules as local scale-out.

They should cover:

- shared/default HTTP scale-out request fulfillment
- Dedicated tenant HTTP scale-out request fulfillment
- Hybrid tenant HTTP scale-out request fulfillment
- Dedicated tenant visibility through Redis registry and capacity stores
- Hybrid tenant visibility through Redis registry and capacity stores
- Dedicated tenant no shared HTTP fallback when fallback is disabled
- Hybrid tenant shared HTTP fallback when fallback is enabled
- tenant-aware request fields preserved by Redis scale-out request store
- tenant-aware request fields preserved by in-memory scale-out request store
- tenant-aware request fields passed from watcher to provider
- tenant-aware metadata published by HTTP provisioner

Important assertions:

```text
A Dedicated tenant must not silently consume shared HTTP runtime capacity when AllowSharedFallback = false.

A Hybrid tenant may consume shared HTTP runtime capacity when AllowSharedFallback = true.

Tenant-aware registry and capacity listing should require an execution context snapshot for tenant-specific visibility checks.

Scale-out request stores must preserve tenant runtime settings when cloning, reading, listing, or passing requests to the watcher.
```

These tests prevent regressions where HTTP scale-out becomes best-effort multi-tenancy instead of explicit policy-driven isolation.


## Runtime Host Manager and Process-Host Tests

Runtime Host Manager tests validate that provider scale-out can create or attach runtime hosts without making the provider own host lifecycle directly.

They should cover:

- Runtime Host Manager mode selection;
- Fixture host creation mode;
- Process host creation mode;
- Attach host creation mode preparation;
- Kubernetes host creation mode with Fake and Kubernetes SDK clients;
- HTTP provider delegating scale-out to `IAiRuntimeHostManager`;
- `ProcessAiRuntimeHostCreationStrategy` launching a real host process;
- `RuntimeInstanceOnly` host startup;
- runtime registration after process launch;
- capacity publication after process launch;
- readiness waiting before scale-out fulfillment;
- tenant id propagation;
- tenant group id propagation;
- isolation mode propagation;
- runtime instance prefix propagation;
- worker count propagation;
- max concurrent run propagation;
- local queue capacity propagation;
- `ExecutionContextSnapshot` propagation into host start requests.

Important assertions:

```text
The HTTP provider should not directly own process creation.

The HTTP provider should delegate host lifecycle to the Runtime Host Manager.

HostCreationMode = Process should launch a real RuntimeInstanceOnly process.

Scale-out should not be considered fulfilled until runtime registration and capacity are visible.

The runtime process should self-register instead of being treated as fake capacity.

Tenant runtime settings should be preserved from scale-out request to host start request.
```


## Kubernetes Runtime Host Provider Tests

Kubernetes tests are separated by the boundary they prove.

### Builder and metadata tests

Validate deterministic Pod names, Kubernetes-safe labels, tenant/isolation propagation, RuntimeInstanceOnly environment, gRPC HTTP/2 Kestrel settings, and protection against configured environment variables overriding request identity.

### Fake client lifecycle tests

Validate dependency injection selection, Host Manager mode routing, convergence, structured failure behavior, ownership-aware cleanup, and publication composition without requiring a cluster.

A Fake-client scenario must not be treated as live HTTP/gRPC endpoint evidence.

### Kubernetes SDK lifecycle tests

Validate real Pod and Service creation, Pod readiness, AlreadyExists identity convergence, exact Pod UID deletion, and registry/capacity publication/removal.

### Transport and Gateway tests

Validate direct/per-runtime endpoint resolution, kubectl port-forward reachability, gRPC HTTP/2 command readiness, GatewayClass/Gateway/route readiness, runtime-header routing, and exact runtime selection through HTTPRoute or GRPCRoute.

### Kubernetes crash recovery tests

Validate that a real Pod executes work, reaches the intended pre-crash boundary, disappears by exact UID after kill, loses executable capacity, is replaced, and recovers assigned work through the same health/recovery split used by process hosts. In-flight recovery must preserve `ExecutionId`; local-queued recovery must preserve `SharedRunId`; safe tenants must remain unaffected.

See [Kubernetes Runtime Host Provider](kubernetes-runtime-host-provider.md).

## MCP Production Runtime Scenario Framework Tests

MCP production runtime scenario framework tests validate the full production-like path.

They should cover:

- parent MCP server test host;
- tenant-scoped MCP clients;
- Redis-backed shared run store;
- Redis-backed shared queue;
- Redis-backed scale-out request store;
- HTTP provider scale-out;
- gRPC provider scale-out;
- Runtime Host Manager process or Kubernetes lifecycle selection;
- real `RuntimeInstanceOnly` child process or Pod;
- runtime registration / heartbeat / capacity;
- HTTP or gRPC dispatch to the created runtime;
- DAG execution;
- retention;
- decision ledger;
- trace timeline;
- replay operation;
- replay report;
- replay ledger;
- replay trace;
- Dedicated tenant process-host scenario;
- Shared tenant process-host scenario;
- Hybrid tenant process-host scenario;
- adversarial multi-tenant Dedicated isolation;
- mixed-tenant full production validation.

Important assertions:

```text
Submit should work from zero executable runtime capacity.

The scale-out request should be persisted in Redis.

The watcher should call the HTTP provider.

The HTTP and gRPC providers should use the Runtime Host Manager.

A real RuntimeInstanceOnly process should start.

The runtime should register and publish capacity.

The dispatcher should use normal provider-specific dispatch after capacity becomes visible.

The run should complete through the DAG engine.

Ledger, trace, replay report, replay ledger, and replay trace should be visible from the parent MCP process.

Dedicated tenants must not reuse another tenant's dedicated runtime.

The mixed-tenant production scenario should validate Dedicated, Shared, and Hybrid tenants together.
```

Representative full production validation:

```text
3 tenants
4 runs per tenant
35 DAG steps per run

Total:
12 runs
420 DAG steps
real RuntimeInstanceOnly processes
retention enabled
ledger enabled
trace enabled
replay enabled
Mongo / Redis durable observability
```

Primary HTTP scenario:

```text
Http_ProcessHost_Should_Run_MixedTenant_Full_Production_Validation_Scenario
```

Primary gRPC recovery scenarios:

```text
Grpc_ProcessHost_Should_Requeue_Real_InFlight_Dag_After_Runtime_Process_Kill
Grpc_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_With_Strict_Dag_Resume_Replay_Ledger_And_Trace
Grpc_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace
```


## Production Runtime Crash Recovery Scenario Tests

Production runtime crash recovery tests validate what happens when real process-host runtime instances disappear while tenant workloads are active.

These tests are different from stale running step recovery.

Stale running step recovery handles abandoned step ownership inside an execution.

Runtime process crash recovery handles all work assigned to an unsafe runtime instance, including:

- in-flight DAG executions that already have a durable `ExecutionId`;
- local queued shared runs that were dispatched to the dead runtime but did not start execution yet;
- tenant-scoped replacement runtime selection;
- recovery forensics for each recovered work item;
- replay / ledger / trace validation after recovery.

Important assertions:

```text
A killed runtime process should be detected through missing heartbeat / unsafe runtime capacity.

The health reconciler should prevent unsafe runtime capacity from being selected for new work.

The execution recovery reconciler should enumerate work assigned to the unsafe runtime.

An in-flight DAG execution should resume with the same durable ExecutionId.

A local queued run should be redispatched through its durable SharedRunId without creating a duplicate submission.

The HTTP provider should report transport / endpoint failure signals, but it should not own recovery or runtime lifecycle replacement.

Replacement capacity should be created or attached by the lifecycle owner, such as the Runtime Host Manager.

Recovery should not consume business retry budget.

Recovery should produce durable forensics, ledger, trace, and replay evidence.
```

Primary HTTP process-host recovery scenarios:

```text
Http_ProcessHost_Should_Recover_Tenant_After_Real_Runtime_Process_Kill_With_Strict_Dag_Resume_Replay_Ledger_And_Trace

Http_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_With_Strict_Dag_Resume_Replay_Ledger_And_Trace

Http_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace
```

Primary gRPC process-host recovery scenarios:

```text
Grpc_ProcessHost_Should_Requeue_Real_InFlight_Dag_After_Runtime_Process_Kill

Grpc_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_With_Strict_Dag_Resume_Replay_Ledger_And_Trace

Grpc_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace
```

Validated recovery evidence:

```text
InFlightExecution resumes the same ExecutionId.
LocalQueued work is recovered through SharedRunId redispatch.
Recovered work count matches expected impacted work.
No duplicate recovery is allowed.
Replay reports are readable after recovery.
Replay ledger is readable after recovery.
Replay trace is readable after recovery.
Execution ledger evidence exists after recovery.
Execution trace evidence exists after recovery.
Step completion evidence exists after recovery.
```

---

## Multi-Tenant Runtime Crash Isolation Tests

Multi-tenant crash isolation tests validate that recovery does not become a global panic path.

The strongest scenario runs three tenants in one shared control plane:

```text
Tenant A runtime process is killed.
Tenant B runtime process is killed.
Tenant C runtime process is not killed and remains safe.
```

The safe tenant proves isolation.

Important assertions:

```text
Tenant A and tenant B should each recover exactly their impacted assigned work.

Tenant C should complete normally.

Tenant C should have zero recovered work.

Tenant C should have zero runtime recovery forensics.

Tenant C should have no recovery contamination visible from impacted tenant queries.

Cross-tenant ledger leakage should be false.

SafeTenantNonImpactValidated should be true.

SafeTenantRecoveryLeakDetected should be false.

CrashImpacted should be false for the safe tenant.
```

Representative workload:

```text
3 tenants
3 runs per tenant
50 DAG steps per run
2 impacted tenants
1 safe tenant
6 expected recovered work items
0 expected safe-tenant recovered work items
9 replay-validated executions
```

These tests prove that runtime crash recovery remains tenant-scoped after convergence.

---

## Runtime Recovery Forensics Tests

Runtime recovery forensics tests validate that recovery is queryable as durable audit evidence, not only visible through logs.

They should cover:

- runtime failure incident id creation;
- per-work-item forensics id creation;
- in-flight recovery timeline;
- local-queued recovery timeline;
- duplicate recovery prevention;
- forensics query by tenant;
- no safe-tenant recovery forensics;
- durable forensics availability after completion, replay, ledger, and trace validation.

Important forensics timelines:

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

```text
SharedRunRequeuedForLocalQueuedRecovery
→ failed.local.run.marked.requeued.for.recovery
→ replacement.runtime.selected
→ replacement.local.run.registered
→ resume.context.seeded
```

Important assertion:

```text
Recovery forensics should be written only for impacted work and impacted tenants.
```

---

## Recovery Replay, Ledger, and Trace Proof Tests

Recovery completion is not considered sufficient by itself.

After recovery, the test suite must prove that every recovered or safe execution remains observable and replayable.

These tests should validate:

- strict replay validation;
- replay report readability;
- replay ledger readability;
- replay trace readability;
- execution ledger evidence;
- execution trace evidence;
- completion evidence;
- step completion evidence;
- tenant-scoped ledger query isolation;
- control-plane causal chain evidence.

Important assertions:

```text
A recovered execution should not only complete; it should remain auditable.

Replay after recovery should validate real recovered executions, not synthetic reconstructions.

Control-plane ledger entries should explain scale-out, provider selection, runtime host creation, capacity visibility, execution recovery reconciliation, and recovered work redispatch.

Tenant-scoped ledger queries should not expose unrelated tenant recovery entries.
```

---


## Heavy HTTP Dispatch Tests

Heavy HTTP dispatch tests validate Redis-backed shared coordination under pressure.

They should cover:

- Redis shared run store usage
- Redis shared queue usage
- Redis admission reservation store usage
- queue-first submit mode
- manual or background queue drain
- 50 shared runs
- 100 steps per run
- 3 pooled HTTP runtime child instances
- multi-runtime distribution
- assigned runtime identity visibility
- no duplicate dispatch
- completion or dispatch success depending on scenario scope

Important evidence:

```text
Runs = 50
StepsPerRun = 100
RuntimeInstances = runtime-http-1, runtime-http-2, runtime-http-3
RedisAiSharedRunStore = validated
RedisAiSharedQueue = validated
RedisAiRuntimeAdmissionReservationStore = validated
```

The distribution does not need to be perfectly even.

The important guarantee is that work is distributed across valid child runtime instances and does not collapse onto a deprecated single-runtime fixture model.

## Redis Discovery, Registry, Capacity, and Reservation Tests

Redis lifecycle tests validate the runtime visibility and shutdown safety layer.

They should cover:

- Redis control-plane discovery descriptor publication
- control-plane id resolver behavior
- runtime-only hosts resolving MCP/control-plane identity before registration
- Redis runtime instance registry registration
- Redis runtime instance heartbeat
- Redis runtime instance listing
- Redis runtime instance draining
- Redis runtime instance unregister
- Redis runtime capacity descriptor publication
- Redis runtime capacity descriptor listing
- Redis runtime capacity descriptor cleanup
- Redis admission reservation create/check/release/expiry behavior
- cleanup using known resolved control-plane id
- cleanup without late rediscovery dependency

Important assertions:

```text
Runtime-only hosts must register under the MCP-published control-plane id.

Registry unregister must not require rediscovery during shutdown.

Capacity descriptor removal must not require rediscovery during shutdown.

Disposed logging, Redis, or discovery dependencies must not fail otherwise successful tests during teardown.
```


## Runtime Worker Capacity Tests

Runtime worker capacity tests validate the worker-aware queue state and runtime instance visibility.

They should cover:

- `WorkerCount` publication
- `ActiveWorkerCount` publication
- `AvailableWorkerCount` publication
- `MaxLocalWorkersPerExecution` publication
- worker-aware `CanAcceptRun`
- saturation when all workers are reserved
- runtime instance list exposes worker capacity fields
- capacity descriptor uses real queue state values
- runtime registry heartbeat preserves worker capacity fields
- Redis registry preserves worker capacity fields
- in-memory registry preserves worker capacity fields

Important assertions:

```text
A runtime instance with no available workers should report CanAcceptRun = false when the published queue/capacity snapshot represents true worker saturation.

MaxLocalWorkersPerExecution should cap the number of local workers used by one execution.

Worker capacity values should flow from local queue state to runtime instance snapshots.

Distributed worker participation tests must explicitly configure MaxLocalWorkersPerExecution when they expect all configured workers to participate.

Tests that assert worker saturation must keep the execution active long enough for capacity publication and MCP/runtime instance listing to observe the saturated state.
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

Recovery tests validate abandoned work at two levels.

### Stale running step recovery

Stale running step recovery validates worker crash behavior inside an existing execution.

It should prove:

- a stale running step can return to `Ready`;
- recovery does not consume retry budget;
- another worker can claim recovered work;
- stale worker completion is rejected;
- claim token mismatch protects state;
- recovery works under distributed execution.

### Runtime instance crash recovery

Runtime instance crash recovery validates process-level failure.

It should prove:

- a runtime process that stops heartbeating becomes unsafe for admission;
- unsafe runtime capacity is not selected for new work;
- the execution recovery reconciler enumerates assigned work;
- in-flight DAG executions resume the same durable `ExecutionId`;
- local queued work is redispatched through durable `SharedRunId`;
- local runtime queue state is treated as volatile, not durable truth;
- recovery does not consume retry budget;
- recovery forensics are written for impacted work only;
- replay, ledger, and trace remain valid after recovery;
- unrelated safe tenants are not impacted.

Recovery tests are different from retry tests.

Retry means logic failed.

Recovery means ownership or runtime capacity was abandoned.

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
- runtime queue state visibility
- worker capacity visibility
- worker-aware `CanAcceptRun`
- max local workers per execution saturation

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
- `ControlPlaneWithHttpRuntimeInstances` behavior
- HTTP pooled runtime child instance dispatch
- runtime role separation
- control-plane host not selected as executable runtime
- local runtime instance pool startup
- runtime instance registration and heartbeat
- Redis-backed runtime registry visibility
- Redis-backed runtime capacity descriptor publication
- Redis control-plane discovery descriptor publication
- control-plane id resolver behavior
- Redis admission reservation usage
- Redis scale-out request persistence
- scale-out watcher behavior
- provider-based scale-out selector resolution
- local runtime scaler behavior
- fulfilled scale-out run requeue
- shared run submission through MCP tools
- shared run listing through MCP tools
- shared queue drain through MCP tools
- queue-first submit through MCP tools
- manual queue drain while background pump is disabled
- local provider queue-first dispatch through MCP
- HTTP provider queue-first dispatch through MCP
- HTTP pooled runtime dispatch to `runtime-http-*` child instances through MCP
- HTTP scale-out request fulfillment through MCP/control-plane scenarios
- Runtime Host Manager process-host provisioning through MCP production scenarios
- real `RuntimeInstanceOnly` process launch through HTTP scale-out
- tenant-aware Dedicated HTTP scale-out through MCP/control-plane scenarios
- tenant-aware Hybrid HTTP scale-out and shared fallback through MCP/control-plane scenarios
- local scale-out dispatch to `mcp-scaleout-runtime-*` runtime instances through MCP
- runtime execution completion after scale-out
- runtime worker capacity visibility through MCP
- runtime queue run-status polling through MCP tools
- replay execution through MCP tools
- replay report retrieval through MCP tools
- replay ledger retrieval through MCP tools
- replay trace retrieval through MCP tools
- observability ledger retrieval through MCP tools
- observability trace retrieval through MCP tools
- execution control operations through MCP tools
- local runtime queue control through MCP tools
- idempotent runtime unregister during MCP host shutdown
- idempotent capacity descriptor cleanup during MCP/runtime host shutdown
- idempotent local runtime pool shutdown during MCP host shutdown
- shutdown cleanup without late rediscovery dependency

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

The MCP server should publish discovery before runtime-only hosts that require discovery are started.

Runtime-only hosts should resolve the MCP-published control-plane id before registering child runtime instances or publishing capacity.

A direct-dispatch run with no runtime capacity should become ScaleOutRequested when scale-out is enabled.

A fulfilled scale-out request should requeue the shared run and let the shared queue pump dispatch it normally.

An HTTP scale-out request should be fulfilled through the HTTP provider/provisioner path and publish tenant-aware registry/capacity metadata.

A Dedicated HTTP tenant should not fall back to shared HTTP capacity when fallback is disabled.

A Hybrid HTTP tenant may fall back to shared HTTP capacity when fallback is enabled.

A dynamically created local runtime instance should expose LocalRunId, ExecutionId, and completed runtime status.
```

Example validated local MCP topology:

```text
mcp-control-plane
    Role = ControlPlane
    CanAcceptRun = false

mcp-runtime-1
    Role = Runtime
    WorkerCount = 10
    ActiveWorkerCount = 0
    AvailableWorkerCount = 10
    MaxLocalWorkersPerExecution = 4
    MaxRunSlots = 5

mcp-runtime-2
    Role = Runtime
    WorkerCount = 10
    ActiveWorkerCount = 0
    AvailableWorkerCount = 10
    MaxLocalWorkersPerExecution = 4
    MaxRunSlots = 5

mcp-runtime-3
    Role = Runtime
    WorkerCount = 10
    ActiveWorkerCount = 0
    AvailableWorkerCount = 10
    MaxLocalWorkersPerExecution = 4
    MaxRunSlots = 5
```

These tests also support the implemented Kubernetes topology where the MCP server acts as control plane and runtime instances run as separate `RuntimeInstanceOnly` Pods.

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
- manual queue drain
- queue-first submit mode
- dispatch-time admission
- pump identity vs assigned runtime identity separation
- background queue service lifecycle
- background queue readiness gate
- scale-out request publication
- Redis-backed scale-out request store behavior
- scale-out watcher behavior
- provider-based scale-out selector behavior
- HTTP provider scale-out selector behavior
- gRPC provider scale-out selector behavior
- fulfilled scale-out requeue behavior
- Redis-backed shared run store behavior
- Redis-backed shared queue behavior
- Redis admission reservation behavior
- Redis atomic queue claim safety
- concurrent dispatch safety

Important assertions:

```text
Only one dispatcher can claim a pending shared queue item.

A shared run record must exist independently from local runtime queue state.

Assigned dispatch must preserve the local queue model.

Global queue fallback must not bypass admission.

Dispatch-time admission must select the assigned runtime target.

Pump identity must remain separate from assigned runtime identity.

Dispatch failures must requeue when policy requires it.

A shared run must not be marked dispatched unless dispatch succeeded.

When admission reservations are enabled, selected runtime capacity should be reserved before provider dispatch and released or expired safely if dispatch fails.

When admission returns RequestScaleOut, the shared run should be persisted as ScaleOutRequested and a scale-out request should be published.

Scale-out fulfillment should requeue the shared run rather than dispatching directly from the watcher.
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
- runtime worker capacity visibility
- ledger metadata for max local workers per execution
- ledger metadata for effective worker count per execution

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
- queue-first shared dispatch under pressure
- shared queue pump/manual drain under pressure
- worker capacity saturation scenarios
- scale-out from zero runtime capacity scenarios
- fulfilled scale-out requeue scenarios
- heavy HTTP pooled runtime dispatch scenarios
- HTTP scale-out provider scenarios
- tenant-aware HTTP Shared/Dedicated/Hybrid scale-out scenarios
- Redis-backed shared queue dispatch under pressure
- shutdown lifecycle races under Redis discovery/registry/capacity
- real process-host runtime kills during active DAG execution
- multi-tenant crash recovery with safe-tenant non-impact

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


## Parallel Process-Host Crash-Recovery Validation

The production process-host campaign now validates concurrency and failure as a combined protocol.

Each scenario contains:

```text
two impacted tenants
    each with:
    - one in-flight execution
    - two local-queued runs
    - one real external runtime process kill

one safe tenant
    - three runs
    - no process kill
    - zero recovery contamination
```

The harness requires an exact assigned-work inventory before the kill:

```text
TotalWorkCount = 3
InFlightExecutionCount = 1
LocalQueuedRunCount = 2
```

The crash boundary is state-driven:

```text
durable gate armed
-> DAG reaches persisted checkpoint
-> exact inventory confirmed
-> process killed
-> gate released
-> replacement runtime resumes
```

This prevents the test from depending on laptop speed or elapsed-time assumptions.

### Validation Ladder

| Level | Scenarios | Tenants | Runs | Logical DAG steps | Processes killed | Affected jobs recovered | Classification |
|---|---:|---:|---:|---:|---:|---:|---|
| P10 | 10 | 30 | 90 | 4,500 | 20 | 60 | Repeatedly green |
| P15 | 15 | 45 | 135 | 6,750 | 30 | 90 | Green intermediate validation |
| P20 | 20 | 60 | 180 | 9,000 | 40 | 120 | Heavy-pressure validation |
| P30 | 30 | 90 | 270 | 13,500 | 60 | 180 | Reproducibly stable validated ceiling |
| P35 | 35 | 105 | 315 | 15,750 | 70 | 210 | Successful on HTTP and gRPC; experimental local-machine edge |

The final HTTP P35 validation recorded:

```text
35 passed
0 failed
4,191,448 Redis + MongoDB operations
18.29 GiB datastore traffic
0 Redis evictions
0 MongoDB rejected connections
```

The final gRPC P35 validation completed the same logical workload through the gRPC process-host path.

An independent raw-log sample contained 6,170 unique step-completion events with zero duplicate `(ExecutionId, step)` pairs and zero contested runtime ownership.

### Failure Classification

Failures must be classified before changing code:

```text
infrastructure saturation
runtime lifecycle
recovery convergence
harness race
```

A larger timeout cannot recover an inventory state that has already disappeared.

The detailed proof model is documented in:

- [Concurrency Hardening and Adversarial Validation](concurrency-hardening-and-adversarial-validation.md)
- [Adversarial Runtime Validation Matrix](adversarial-runtime-validation-matrix.md)
- [Adversarial Runtime Validation Evidence Index](adversarial-runtime-validation-evidence-index.md)


## Runtime Pool Validation

Runtime Pool validation is layered so failures remain diagnosable.

### Identity and lifecycle gates

- `PoolId` and `HostId` stability;
- independent `RuntimeInstanceId`;
- membership state;
- graceful drain;
- targeted replacement;
- stale mutation protection.

### Routing gates

- exact route registration;
- `RouteId` immutability;
- forwarding lease concurrency;
- HTTP stable endpoint;
- gRPC stable endpoint;
- no sibling fallback;
- response identity validation;
- suppression before transport.

### Failure-recovery gates

- exact A1 failure journal;
- exact A1 suppression;
- A2/A3 non-contamination;
- assigned-work runtime boundary;
- deterministic inventory order;
- deterministic fingerprint;
- one claim winner under concurrency;
- stale `LeaseId` rejection;
- in-flight same-`ExecutionId` transition;
- local-queued `SharedRunId` redispatch;
- claim retained after transition failure.

### Real process-host gate

The final proof uses real external child processes and a real OS kill.

```text
A1/A2/A3 ready
    -> seed A1 and sibling control work
    -> kill A1
    -> observe A4
    -> acquire one claim
    -> recover A1 inventory only
```

### Regression gate

After the Runtime Pool implementation:

```text
historical Process HTTP P10: green
historical Process gRPC P10: green
existing Kubernetes HTTP P5: green
existing Kubernetes gRPC P5: green
```

The P5 Kubernetes gate remains compatibility evidence for the existing one-runtime-per-Pod modes. Dedicated KubernetesPool production scenarios separately prove several in-Pod runtimes, child replacement, Pod-wide failure recovery, warm reuse, and bounded capacity.

---

## Recursive Child DAG and EventDriven Reference Validation

Child DAG composition is validated as an extension of the existing production runtime scenarios rather than through a separate test runtime. The capability is **implemented / validated**, with recursive Depth3 production evidence now forming the reference baseline.

Current recursive evidence includes:

- historical `ChildDepth = 0` compatibility;
- durable parent suspension through `WaitingForExternal`;
- deterministic child identity and parent continuation;
- ProcessHost HTTP/gRPC transport parity;
- KubernetesPool execution;
- real child-runtime process failure and same-`ExecutionId` recovery;
- distinct parent ProcessHost or Pod failure and replacement;
- warm topology reuse across two cycles;
- canonical Runtime Lifecycle Journal evidence;
- MCP replay, Ledger, trace, and Recovery Forensics evidence;
- recursive `ChildDepth = 3` validation at `3×3×3×2×Depth3`;
- high-scale `5×5×5×2×Depth3` validation.

### Reference observation profile

EventDriven canaries are the reference synchronization profile for current recursive Runtime Pool validation.

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

The shared scenario core remains the same. `ProductionRecoveryObservationMode.EventDriven` changes how recovery completion is synchronized; it does not create a second recovery implementation.

### Reference canary matrix

The canonical reference profile is the EventDriven recursive full-failure scenario. Transport/host variants reuse the shared scenario semantics rather than defining different correctness contracts.

| Provider / host model | Reference canary | High-scale profile | Validation role |
|---|---|---:|---|
| gRPC / ProcessHostPool | `Grpc_ProcessHostPool_EventDriven_Canary_Should_Reuse_The_Same_FullFailure_Scenario` | `5×5×5×2×Depth3` | Reference ProcessHostPool recovery + recursive Child DAG proof |
| HTTP / ProcessHostPool | `Http_ProcessHostPool_EventDriven_Canary_Should_Reuse_The_Same_FullFailure_Scenario` | `5×5×5×2×Depth3` | HTTP transport parity on the same ProcessHostPool semantics |
| gRPC / KubernetesPool | `Grpc_KubernetesPool_EventDriven_Canary_Should_Reuse_The_Same_FullFailure_Scenario` | Depth3 reference / high-scale validation | Kubernetes Pod-boundary + in-Pod runtime recovery proof |
| HTTP / KubernetesPool | `Http_KubernetesPool_EventDriven_Canary_Should_Reuse_The_Same_FullFailure_Scenario` | Depth3 parity profile | HTTP transport parity on the shared KubernetesPool scenario |

The long high-scale gates are intentionally not duplicated merely to prove transport syntax. A parity variant must reuse the same production scenario contract and be exercised at the appropriate gate without weakening any invariant.

### Running the reference canaries

Build once:

```powershell
dotnet build .\implementations\dotnet\Tests\Multiplexed.AI.McpServer.Tests.Integration\Multiplexed.AI.McpServer.Tests.Integration.csproj
```

Then run a named EventDriven canary using an exact/partial fully-qualified-name filter, for example:

```powershell
dotnet test .\implementations\dotnet\Tests\Multiplexed.AI.McpServer.Tests.Integration\Multiplexed.AI.McpServer.Tests.Integration.csproj `
  --no-build `
  --filter "FullyQualifiedName~Grpc_ProcessHostPool_EventDriven_Canary_Should_Reuse_The_Same_FullFailure_Scenario" `
  --logger "console;verbosity=normal"
```

For KubernetesPool gates, complete the [Local Kubernetes and Minikube Environment](kubernetes-local-environment.md) pre-flight first. ProcessHostPool and KubernetesPool long-running full-failure scenarios should be run deliberately rather than in parallel on a resource-constrained development workstation.

### EventDriven wait contract

For post-failure recovery, tests use canonical lifecycle events with durable evidence protection against missed realtime events:

```text
durable evidence check
→ realtime subscribe
→ durable evidence re-check
→ await canonical event
→ final durable-state verification
```

The pre-kill crash threshold may still read durable progress state. This is intentional: the durable execution store is the authority for deciding that exactly N steps have completed before injecting a failure.

Historical Polling scenarios remain compatibility/fallback regression coverage. Do not delete proven Polling tests merely because EventDriven is now the reference profile.

### Reference proof ladder

```text
Depth0 compatibility          GREEN
Depth1 recovery               GREEN
Depth2 recursive closure      GREEN
3×3×3×2×Depth3               GREEN — recursive Depth3 validation
5×5×5×2×Depth3               GREEN — high-scale validation
```

The high-scale exact logical-step proof applies to root parent steps. Nested child execution is additionally proven through authoritative durable DAG terminality and a separate bounded recursive Depth3 Ledger proof with exact per-depth child logical-step accounting, zero missing child logical steps, and zero unexpected duplicate child logical steps.

Deeper tests must never be made green by extending watchdogs or timeouts. A stalled nested execution must identify the missing durable transition or infrastructure condition first.

See [Durable Child DAG Composition](child-dag-composition.md) and [Engine Event Observation and Lifecycle Catalog](engine-event-observation.md).

---

## Deterministic Adversarial Runtime Matrix

The recursive Runtime Pool validation now includes a dedicated semantic failure-boundary matrix across all four provider/transport combinations:

```text
                    ProcessHostPool    KubernetesPool
gRPC                     VERIFIED          VERIFIED
HTTP                     VERIFIED          VERIFIED
```

Each combination executes the same nine rows:

```text
Baseline
CrashEarly
ChildInvocationBoundary
ContinuationConsume
Depth2RuntimeFailure
Depth3RuntimeFailure
SeedA
SeedB
SeedC
```

This is 36 validated matrix rows. The rows deliberately target semantic boundaries rather than simply repeating the same full-failure scenario at different sizes.

`ContinuationConsume` is the most timing-sensitive ProcessHostPool boundary. Its final harness derives the exact deterministic continuation identity, pre-arms physical runtime process handles, watches the exact durable continuation `SharedRun`, freezes the first exact `Dispatched` owner before performing proof reads, and kills that same suspended physical process. No synthetic production lifecycle event or artificial post-child production gate is required.

The proof remains state-driven:

```text
canonical event / diagnostic signal = synchronization aid
durable SharedRun / DAG / relation / index = authority
hard timeout = watchdog
```

The matrix proves the selected deterministic schedules. It does not claim exhaustive state-space exploration, recovery-of-recovery, or dedicated recursive-child replay.

See [Adversarial Runtime Validation Matrix](adversarial-runtime-validation-matrix.md) and [Adversarial Runtime Validation Evidence Index](adversarial-runtime-validation-evidence-index.md).

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
- cluster autoscaling/HPA and production deployment packaging
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

Queue-first submit does not create a local RunId until dispatch.

Manual drain can dispatch queued work while the background pump is disabled.

MaxLocalWorkersPerExecution caps local worker participation.

Runtime instance snapshots expose active and available worker capacity.

HTTP pooled runtime dispatch assigns runs to runtime-http-* child instances.

Runtime-only hosts resolve the MCP-published control-plane id before registration.

Registry and capacity cleanup do not depend on late rediscovery during shutdown.

Heavy HTTP QueueFirst dispatch validates Redis shared run store, Redis shared queue, and Redis admission reservations.

DirectDispatch with no runtime capacity can request scale-out.

A fulfilled scale-out request requeues the shared run.

The shared queue pump dispatches the requeued run after new capacity appears.

A local scale-out-created runtime instance executes the run to completed.

HTTP provider timeout, retry, and circuit breaker behavior produce structured dispatch failure reasons.

HTTP provider dispatch failures are persisted and do not mark queue items as dispatched.

ProviderHint = http resolves the HTTP scale-out provider.

HTTP scale-out preserves tenant runtime settings from request store to watcher to provisioner.

Dedicated HTTP tenants do not fall back to shared HTTP capacity when fallback is disabled.

Hybrid HTTP tenants can fall back to shared HTTP capacity when fallback is enabled.

Runtime Host Manager process mode launches a real RuntimeInstanceOnly process.

Kubernetes SDK mode creates a real RuntimeInstanceOnly Pod and per-runtime Service.

Pod Running does not publish capacity before required command readiness succeeds.

A Kubernetes kill waits for exact old Pod UID disappearance before recovery continues.

Kubernetes hosting preserves provider.name and transport.name as HTTP or gRPC.

The mixed-tenant full production scenario validates Dedicated, Shared, and Hybrid tenants with retention, ledger, trace, and replay enabled.

A killed real RuntimeInstanceOnly process should cause unsafe capacity suppression and assigned-work recovery.

An in-flight recovered DAG execution should preserve the same ExecutionId before and after recovery.

Local queued work assigned to a killed runtime should be redispatched through SharedRunId without duplicate submission.

A safe tenant running in the same control plane should complete normally with zero recovered work and zero recovery forensics.

Recovery validation should include replay report, replay ledger, replay trace, execution ledger, execution trace, completion evidence, and step completion evidence.
```

---

## Runtime Pool Test Status

| Validation area | Status |
|---|---|
| Process pool manager and real child lifecycle | Green |
| Exact route registry and draining | Green |
| Stable HTTP pool routing | Green |
| Stable gRPC pool routing | Green |
| Targeted A1-to-A4 replacement | Green |
| Exact failure journal and suppression | Green |
| Assigned-work enumeration | Green |
| Deterministic claims and lease generations | Green |
| Claimed recovery transitions | Green |
| Final real process-host recovery proof | Green |
| Historical Process HTTP/gRPC regression | P10 green |
| Existing Kubernetes HTTP/gRPC regression | P5 green |
| KubernetesPool hierarchical child + Pod failure proof | Green over HTTP and gRPC |
| External-manual full-boundary failure proof | Green over HTTP/gRPC × ProcessHostPool/KubernetesPool |

---

## Current Status

| Test Area | Status |
|---|---|
| MCP control-plane tests | Implemented / ongoing |
| MCP queue-first/manual drain tests | Implemented / validated |
| Shared runtime controller tests | Implemented / ongoing |
| Shared queue pump tests | Implemented / validated |
| Dispatch-time admission tests | Implemented / validated |
| Redis shared run store tests | Implemented / ongoing |
| Redis shared queue tests | Implemented / ongoing |
| Runtime registry and capacity descriptor tests | Implemented / validated |
| Runtime worker capacity visibility tests | Implemented / validated |
| Runtime shutdown lifecycle tests | Implemented / validated |
| Runtime provider model tests | Implemented foundations / validated for local, HTTP, and gRPC providers |
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
| Provider-based local runtime hosting tests | Implemented / validated |
| Provider-based HTTP runtime hosting tests | Implemented / validated |
| HTTP pooled runtime provider tests | Implemented / validated |
| HTTP runtime provider hardening tests | Implemented / validated |
| HTTP runtime scale-out provider tests | Implemented / validated |
| Tenant-aware HTTP scale-out scenario tests | Implemented / validated |
| Runtime Host Manager process-host tests | Implemented / validated |
| MCP production runtime scenario framework tests | Implemented / validated |
| Mixed-tenant full production validation scenario | Implemented / validated |
| Durable replay / ledger / trace process-boundary tests | Implemented / validated |
| Heavy HTTP dispatch tests | Implemented / validated |
| Redis control-plane discovery tests | Implemented / validated |
| Redis admission reservation tests | Implemented / validated |
| Redis scale-out request store tests | Implemented / validated |
| Store-backed scale-out request publisher tests | Implemented / validated |
| Scale-out watcher tests | Implemented / validated |
| Scale-out provider selector tests | Implemented / validated |
| Local runtime scaler tests | Implemented / validated |
| Local provider scale-out tests | Implemented / validated |
| Fulfilled scale-out run requeue tests | Implemented / validated |
| MCP Redis local scale-out execution tests | Implemented / validated |
| gRPC runtime provider tests | Implemented / validated |
| gRPC runtime scale-out provider tests | Implemented / validated |
| gRPC process-host crash recovery scenario tests | Implemented / validated |
| Kubernetes builder/Fake lifecycle tests | Implemented / validated |
| Kubernetes SDK Pod/Service/readiness tests | Implemented / validated |
| gRPC Kubernetes Host Manager and Pod crash-recovery scenarios | Implemented / validated |
| Kubernetes Gateway/controller production interoperability matrix | Ongoing |
| Full enterprise demo scenario | Planned |
| Durable decision ledger tests | Implemented / validated through replay and recovery ledger scenarios |
| Production runtime crash recovery scenario tests | Implemented / validated |
| Multi-tenant runtime crash isolation tests | Implemented / validated |
| Safe tenant non-impact recovery tests | Implemented / validated |
| Runtime recovery forensics tests | Implemented / validated |
| Recovery replay / ledger / trace proof tests | Implemented / validated |
| Control-plane causal chain ledger tests | Implemented / validated |
| Parallel HTTP/gRPC process-host concurrency hardening | Implemented / validated through P35; P35 classified as experimental local-machine edge |

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

- more than 1000 test cases validate the runtime across unit, integration, distributed, Redis, replay, observability, control-plane, MCP, provider-hosting, shared queue, HTTP hardening, HTTP/gRPC scale-out, and tenant-aware isolation scenarios
- worker crashes can be recovered
- retries are deterministic and observable
- retention reduces hot state without losing required data
- replay restores equivalent terminal state
- queue and execution control are separated
- concurrency limits are enforced before execution
- policy-driven behavior is testable
- deterministic convergence holds under distributed execution
- queue-first and manual drain behavior are validated
- provider-hosted runtime instance flows are validated
- HTTP pooled runtime provider flows are validated
- HTTP runtime provider timeout, retry, circuit breaker, and structured failure behavior are validated
- HTTP runtime provider scale-out and provisioner behavior are validated
- gRPC runtime provider scale-out and provisioner behavior are validated
- Runtime Host Manager process-host provisioning is validated
- real `RuntimeInstanceOnly` processes can be launched from HTTP and gRPC scale-out
- real `RuntimeInstanceOnly` processes can be killed and recovered through control-plane recovery
- in-flight DAG recovery preserves durable `ExecutionId`
- local queued work is recovered through durable `SharedRunId` redispatch
- safe tenants remain unaffected during impacted tenant recovery
- runtime recovery forensics, replay, ledger, and trace are validated after recovery
- tenant-aware HTTP/gRPC Shared/Dedicated/Hybrid scale-out and fallback policies are validated
- mixed-tenant production validation proves Dedicated, Shared, and Hybrid execution with retention, ledger, trace, and replay enabled
- Redis discovery, registry, capacity, and admission reservation flows are validated
- heavy HTTP dispatch validates Redis-backed shared coordination under pressure
- Redis-backed scale-out request lifecycle is validated
- local runtime scale-out from zero executable capacity is validated
- fulfilled scale-out shared runs are requeued and dispatched through the normal pump
- scale-out-created runtime instances execute runs to completion
- runtime worker capacity is visible and enforceable
- adversarial process-host validation preserves identity, ownership, recovery evidence, and safe-tenant isolation through the P35 campaign
- the deterministic semantic adversarial matrix is verified 36/36 across HTTP/gRPC × ProcessHostPool/KubernetesPool with archived row-level xUnit evidence

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
- [Shared Runtime Controller / Shared Queue Usage](shared-controller-usage.md)
- [Runtime Control Plane](runtime-control-plane.md)
- [MCP Server as Runtime Control Plane](mcp-server-control-plane.md)
- [Runtime Instance Provider Model](runtime-instance-provider-model.md)
- [HTTP Runtime Provider](http-runtime-provider.md)
- [gRPC Runtime Provider](grpc-runtime-provider.md)
- [Kubernetes Runtime Host Provider](kubernetes-runtime-host-provider.md)
- [MCP Production Runtime Scenario Framework](mcp-production-runtime-scenario-framework.md)
- [Runtime Process Crash Recovery](runtime-process-crash-recovery.md)
- [Runtime Recovery Forensics](runtime-recovery-forensics.md)
- [Multi-Tenant Runtime Crash Isolation](multi-tenant-runtime-crash-isolation.md)
- [Control-Plane Ledger Causal Chain](control-plane-ledger-causal-chain.md)
- [Recovery Replay Ledger Trace Proof](recovery-replay-ledger-trace-proof.md)
- [Shared Queue Pump and Worker Capacity](shared-queue-pump-and-worker-capacity.md)
- [Concurrency Hardening and Adversarial Validation](concurrency-hardening-and-adversarial-validation.md)
- [Replay and Audit](replay-and-audit.md)
- [Observability](observability.md)
- [Policy-Driven Execution](policy-driven-execution.md)
- [Config-Driven Runtime](config-driven-runtime.md)
- [RAG Pipelines](rag-pipelines.md)


---

## Final Hierarchical Runtime Pool Production Matrix

The final Runtime Pool production tests use the same hierarchical correctness contract across both transports and both hosting boundaries. Two trigger modes are validated.

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

Current closure profiles:

```text
gRPC ProcessHostPool   = 7 × 5 capacity, 20 submission iterations/cycle, 2 cycles, 1400 DAGs, 70000 steps
HTTP ProcessHostPool   = 3 × 5 capacity, 5 submission iterations/cycle, 2 cycles, 150 DAGs, 7500 steps
gRPC KubernetesPool    = 5 × 5 capacity, 5 submission iterations/cycle, 2 cycles, 250 DAGs, 12500 steps
HTTP KubernetesPool    = 3 × 5 capacity, 5 submission iterations/cycle, 2 cycles, 150 DAGs, 7500 steps
```

Every scenario still performs two child-runtime failures at >=25 / 50 completed steps, two later distinct fully busy parent-boundary failures, 12 exact recoveries, warm reuse across two cycles, and final cleanup only after the second cycle.

The external-manual variants keep the child-runtime kill automatic but replace the parent ProcessHost/Pod kill call with an operator gate. The test publishes an exact target outside the buffered xUnit log and waits until that exact boundary disappears before re-entering the existing recovery path.

Across one automatic matrix the closure workload is 1950 DAGs and 97500 logical steps. The manual/external matrix repeats the same workload profiles. Across both trigger modes the suite therefore closes 3900 DAGs and 195000 logical steps with 16 child failures, 16 full-boundary failures, and 96 exact recovered runs.

See [Runtime Pool Production Validation](runtime-pool-production-validation.md).
