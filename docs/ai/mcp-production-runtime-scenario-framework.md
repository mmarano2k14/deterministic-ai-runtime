# MCP Production Runtime Scenario Framework

Status: Implemented / validated for HTTP and gRPC process-host runtime scenarios, including real `RuntimeInstanceOnly` child processes, provider-based scale-out, provider-agnostic crash recovery, replay / ledger / trace proof, runtime recovery forensics, and safe-tenant non-impact validation.

## Purpose

This document describes the MCP production runtime scenario framework introduced for validating the deterministic AI runtime in production-like conditions.

The framework validates more than isolated unit behavior. It proves the full process-host execution path with real runtime host processes, tenant-aware scale-out, runtime registration, capacity publication, provider transport dispatch, DAG execution, durable observability, and replay. The framework was first validated through HTTP and now validates the same process-host crash recovery contract through gRPC. It validates real runtime process crash recovery: unsafe runtime detection, assigned-work reconciliation, in-flight DAG resume, local-queued redispatch, runtime recovery forensics, control-plane ledger causal chain evidence, and safe-tenant non-impact.

The main production execution flow validated by this framework is:

```text
Submit
→ tenant-aware admission
→ shared queue
→ Redis scale-out request
→ scale-out watcher
→ selected runtime provider
→ Runtime Host Manager
→ RuntimeInstanceOnly host process
→ runtime registration / heartbeat / capacity
→ provider transport dispatch
→ DAG execution
→ ledger / trace / replay validation
```

The main production recovery flow validated by this framework is:

```text
real RuntimeInstanceOnly process killed
→ heartbeat becomes stale / runtime becomes unsafe
→ unsafe runtime capacity is no longer selected
→ execution recovery reconciler enumerates assigned work
→ in-flight DAG execution resumes with the same ExecutionId
→ local queued shared runs are redispatched through durable SharedRunId state
→ replacement runtime capacity is selected or created when required
→ recovered executions complete
→ runtime recovery forensics are queryable
→ ledger / trace / replay / control-plane causal chain proof is validated
→ safe tenant remains untouched
```

This workstream covers five related areas:

1. MCP Runtime Host Manager and host creation modes.
2. HTTP and gRPC process-host scale-out with real `RuntimeInstanceOnly` processes.
3. MCP production scenario tests validating Dedicated / Shared / Hybrid tenant runtime modes.
4. Real process-host runtime crash recovery with durable DAG resume and local-queued redispatch.
5. Tenant-scoped recovery proof through forensics, ledger, trace, replay, and safe-tenant isolation.

---

## Why this framework exists

The deterministic AI runtime already supports distributed execution concepts such as shared queue dispatch, runtime instance registration, capacity stores, Redis scale-out requests, replay, ledger, and trace observability.

However, fixture-only tests are not enough to validate production behavior.

The production scenario framework exists to prove that the control plane can:

- start from zero runtime capacity;
- submit tenant-aware runs through MCP;
- request runtime scale-out through Redis;
- materialize runtime capacity through provider-specific scale-out, including HTTP and gRPC;
- create a real runtime host process;
- wait for runtime readiness;
- dispatch queued runs to that runtime through the selected provider transport;
- execute DAG workloads;
- observe execution through durable stores;
- replay execution after crossing a process boundary;
- kill real runtime host processes and recover only the work assigned to those unsafe runtimes;
- preserve durable `ExecutionId` for in-flight DAG recovery;
- redispatch volatile local-queued work through durable `SharedRunId` state;
- prove that an unrelated safe tenant receives no recovery work, no recovery forensics, and no ledger contamination.

The key validation goals are:

```text
No fake runtime capacity is required for the final HTTP process-host production scenario.
The runtime instance is a real RuntimeInstanceOnly process.
Crash recovery is validated by killing real process-host runtimes, not by simulating recovery in memory.
```

---

## Main components

### MCP server

The MCP server is the production-facing entry point for integration scenarios.

It receives tenant-scoped requests and carries runtime context through:

- `TenantId`;
- `TenantGroupId`;
- `PipelineKey`;
- `ExecutionContextSnapshot`;
- runtime metadata;
- RBAC / access context.

The MCP client submits runs through the shared runtime controller using `AiSharedRuntimeControllerRequest`.

---

### Shared runtime controller

The shared runtime controller owns the high-level run lifecycle:

```text
Submit
→ admit
→ queue
→ request scale-out if no capacity exists
→ dispatch when capacity becomes available
→ track assigned runtime instance and local run id
```

For production scenarios, the controller request carries tenant information both directly and through metadata.

Important metadata keys include:

```text
tenant.id
tenant.group.id
pipelineName
runtimeInstanceIdPrefix
```

The `TenantGroupId` metadata is required so that scale-out fulfillment and queued-run requeue can match the correct tenant scope.

---

### Tenant-aware admission

Tenant-aware admission resolves runtime behavior from tenant settings.

The effective settings include:

- isolation mode;
- whether dedicated capacity is preferred;
- whether shared fallback is allowed;
- maximum runtime instances;
- worker count per runtime instance;
- maximum concurrent runs per runtime instance;
- local queue capacity;
- runtime instance id prefix.

These settings drive runtime capacity decisions and provider scale-out requests.

---

### Shared queue

When no suitable runtime capacity exists, the run is placed in the shared queue.

The shared queue allows the control plane to accept work even when runtime capacity must be created dynamically.

The queued run remains tenant-scoped and pipeline-scoped so that it can be safely requeued or dispatched after scale-out fulfillment.

---

### Redis scale-out request store

The Redis scale-out request store persists scale-out requests created by admission or dispatch flows.

A production scale-out request includes:

- `RequestId`;
- `SharedRunId`;
- `ControlPlaneId`;
- `TenantId`;
- `TenantGroupId`;
- `PipelineKey`;
- `IsolationMode`;
- `PreferDedicatedCapacity`;
- `AllowSharedFallback`;
- `RuntimeInstanceIdPrefix`;
- `WorkerCountPerInstance`;
- `MaxConcurrentRunsPerInstance`;
- `LocalQueueCapacity`;
- `MaxRuntimeInstances`;
- request status.

The scale-out request is the bridge between queued work and provider-driven runtime capacity creation.

---

### Scale-out watcher

The scale-out watcher polls pending scale-out requests and delegates them to the appropriate runtime provider.

For process-host scenarios, the watcher routes pending requests to the selected runtime provider.

Current validated provider hints include:

```text
http
grpc
```

---

### HTTP runtime provider

The HTTP runtime provider owns HTTP transport-based runtime dispatch and HTTP runtime scale-out.

It reports transport and endpoint failures through structured provider results. It does not own runtime crash recovery, kill/restart policy, or assigned-work recovery. Those responsibilities remain separated between runtime health reconciliation, execution recovery reconciliation, and the runtime lifecycle owner.

In process-host production scenarios, the provider uses:

```text
AiHttpRuntimeScaleOutProvisioner
→ IAiRuntimeHostManager
→ ProcessAiRuntimeHostCreationStrategy
→ RuntimeInstanceOnly host process
```

The provider is responsible for materializing capacity metadata and for routing queued runs to runtime instances through HTTP commands. It is also part of the validated transport-health boundary: HTTP failures can become endpoint health signals, but unsafe capacity selection and assigned-work recovery are owned above the transport layer.

---

### gRPC runtime provider

The gRPC runtime provider owns gRPC transport-based runtime dispatch and gRPC runtime scale-out.

It follows the same provider model as HTTP:

```text
AiRuntimeScaleOutRequestWatcherHostedService
    ↓
AiRuntimeScaleOutProviderSelector
    ↓
providerHint = grpc
    ↓
GrpcAiRuntimeInstanceProvider
    ↓
IAiGrpcRuntimeScaleOutProvisioner
    ↓
IAiRuntimeHostManager
    ↓
ProcessAiRuntimeHostCreationStrategy
    ↓
RuntimeInstanceOnly process
```

The runtime process registers as:

```text
provider.name = grpc
transport.name = grpc
```

The child process maps the gRPC runtime command service and must expose a gRPC-compatible HTTP/2 endpoint.

In the current process-host validation, gRPC readiness uses runtime registration / capacity publication as the reliable readiness signal. The previous HTTP command endpoint readiness probe is not valid for gRPC transport.

The validated gRPC process-host recovery scenarios prove that the same provider-agnostic crash recovery base works over gRPC:

```text
Grpc_ProcessHost_Should_Requeue_Real_InFlight_Dag_After_Runtime_Process_Kill

Grpc_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_With_Strict_Dag_Resume_Replay_Ledger_And_Trace

Grpc_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace
```

This proves that runtime process crash recovery is not tied to HTTP transport.

### Runtime Host Manager

The Runtime Host Manager is the abstraction responsible for creating or attaching runtime hosts.

It separates provider logic from host lifecycle mechanics.

The provider remains responsible for:

- provider selection;
- scale-out request handling;
- dispatch/status/control transport;
- effective runtime settings;
- capacity metadata.

The host manager is responsible for:

- creating or attaching runtime hosts;
- passing runtime identity and tenant settings;
- returning runtime host startup information.

---

## Host creation modes

The host manager design supports multiple host creation modes.

### Fixture

`Fixture` mode is used by integration tests that run runtime hosts through in-process test fixtures.

It is useful for fast tests but does not prove process-boundary behavior.

```text
Provider
→ HostManager
→ fixture-backed runtime host
```

### Process

`Process` mode launches a real runtime host executable or DLL as a child process.

This is the primary mode validated by the MCP production runtime scenario framework.

```text
Provider
→ HostManager
→ ProcessAiRuntimeHostCreationStrategy
→ Multiplexed.AI.McpServer.Host.dll
→ RuntimeInstanceOnly
```

This mode proves:

- real process creation;
- runtime startup outside the parent MCP process;
- durable store boundaries;
- runtime registration from the child process;
- provider transport dispatch to the child runtime endpoint.

### Attach

`Attach` mode is intended for attaching the control plane to an already running runtime host.

The host manager does not create the process. It resolves or accepts an existing endpoint and validates that the runtime can be used.

Expected use cases:

- local debugging against an already running runtime;
- external runtime host managed by another supervisor;
- pre-provisioned runtime capacity.

```text
Provider
→ HostManager
→ attach to existing runtime endpoint
```

### Kubernetes

`Kubernetes` mode is intended for production cluster scale-out.

The host manager or provider-specific implementation can create Kubernetes runtime pods and wait for readiness.

Expected future flow:

```text
Provider
→ HostManager
→ Kubernetes creation strategy
→ RuntimeInstanceOnly pod
→ service / endpoint readiness
→ registration / capacity
```

This mode remains a production target beyond the local process-host validation.

---

## Process-host full flow

```text
┌──────────────────────────────────────────────────────────────────────────────┐
│                              MCP Test Client                                 │
│                                                                              │
│  SubmitManyRunsAsync                                                         │
│  TenantId / TenantGroupId / PipelineKey / runtime input metadata             │
└───────────────────────────────────┬──────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                                MCP Server                                    │
│                                                                              │
│  Builds tenant-aware execution context                                       │
│  Carries RBAC context and ExecutionContextSnapshot                           │
│  Sends request to shared runtime controller                                  │
└───────────────────────────────────┬──────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                       Shared Runtime Controller                              │
│                                                                              │
│  AiSharedRuntimeControllerRequest                                            │
│  Operation = SubmitRun                                                       │
│  TenantId = tenant id                                                        │
│  Metadata:                                                                   │
│    tenant.id                                                                 │
│    tenant.group.id                                                           │
│    pipelineName                                                              │
│    runtimeInstanceIdPrefix                                                   │
└───────────────────────────────────┬──────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                         Tenant-Aware Admission                               │
│                                                                              │
│  Resolves tenant runtime settings                                            │
│  Evaluates Dedicated / Shared / Hybrid behavior                              │
│  Checks visible runtime capacity                                             │
│  Requests scale-out when no suitable capacity exists                         │
└───────────────────────────────────┬──────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                              Shared Queue                                    │
│                                                                              │
│  Queues the shared run while runtime capacity is being created               │
│  Keeps run tenant-scoped and pipeline-scoped                                 │
└───────────────────────────────────┬──────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                       Redis Scale-Out Request Store                          │
│                                                                              │
│  Stores pending scale-out request                                            │
│  Includes tenant / group / pipeline / isolation / sizing metadata            │
│  Deduplicates and tracks fulfillment status                                  │
└───────────────────────────────────┬──────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                            Scale-Out Watcher                                 │
│                                                                              │
│  Finds pending request                                                       │
│  Routes to selected provider                                                 │
│  Invokes selected runtime scale-out provider                                     │
└───────────────────────────────────┬──────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                         Runtime Provider                                │
│                                                                              │
│  HTTP/gRPC scale-out provisioner                                            │
│  Resolves effective runtime settings                                         │
│                                                                              │
│  Precedence:                                                                 │
│    tenant runtime settings                                                   │
│      > scale-out request values                                              │
│      > HTTP provider technical defaults                                      │
└───────────────────────────────────┬──────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                         Runtime Host Manager                                 │
│                                                                              │
│  Receives AiRuntimeHostStartRequest                                          │
│  HostCreationMode = Process                                                  │
│  Carries tenant id, tenant group id, isolation mode, sizing, endpoint        │
└───────────────────────────────────┬──────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                  ProcessAiRuntimeHostCreationStrategy                        │
│                                                                              │
│  Starts real child process                                                   │
│  Launches Multiplexed.AI.McpServer.Host.dll                                  │
│  Configures host as RuntimeInstanceOnly                                      │
│  Passes runtime identity and tenant settings                                 │
└───────────────────────────────────┬──────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                       RuntimeInstanceOnly Process                            │
│                                                                              │
│  Runs outside the parent MCP process                                         │
│  Registers runtime instance                                                  │
│  Publishes heartbeat and capacity                                            │
│  Exposes provider runtime command endpoint                                       │
└───────────────────────────────────┬──────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                    Runtime Registry / Capacity Store                         │
│                                                                              │
│  Runtime instance becomes visible to dispatch                                │
│  Capacity descriptor includes tenant and isolation metadata                  │
│  Visibility evaluator enforces tenant isolation                              │
└───────────────────────────────────┬──────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                         Scale-Out Fulfillment                                │
│                                                                              │
│  Scale-out request marked fulfilled                                          │
│  Queued run becomes dispatchable                                             │
│  Scope match requires TenantId / TenantGroupId / PipelineKey                 │
└───────────────────────────────────┬──────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                        Shared Queue Dispatcher                               │
│                                                                              │
│  Selects visible runtime capacity                                            │
│  Dispatches queued run to selected runtime endpoint                              │
│  Stores AssignedRuntimeInstanceId and LocalRunId                             │
└───────────────────────────────────┬──────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                              DAG Execution                                   │
│                                                                              │
│  Runtime workers execute DAG steps                                           │
│  Execution writes state, ledger entries, traces, and replay metadata         │
└───────────────────────────────────┬──────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                         Observability / Replay                               │
│                                                                              │
│  Parent MCP process validates durable execution output                       │
│  Queries ledger, trace, replay report, replay ledger, replay timeline        │
└──────────────────────────────────────────────────────────────────────────────┘
```

---

## Tenant runtime modes

### Dedicated

Dedicated tenants must use runtime capacity owned by the same tenant or tenant group.

Effective mode:

```text
IsolationMode = Dedicated
PreferDedicatedCapacity = true
AllowSharedFallback = false
```

Dedicated validation includes adversarial process-host testing:

```text
Tenant A creates dedicated runtime
Tenant A completes
Tenant B submits afterwards
Tenant B must not reuse Tenant A runtime
Tenant B must create or use its own dedicated runtime
```

This proves routing isolation beyond simple configuration propagation.

---

### Shared

Shared tenants are configured for shared runtime behavior.

Effective mode:

```text
IsolationMode = Shared
PreferDedicatedCapacity = false
AllowSharedFallback = true
```

The current process-host production scenarios validate Shared mode propagation and execution using the existing tenant-level runtime prefix behavior.

The final shared runtime pooling model is intentionally not forced yet.

Possible future shared capacity models:

- shared runtime per tenant;
- shared runtime per tenant group;
- global shared runtime pool.

---

### Hybrid

Hybrid tenants prefer owned runtime capacity but may fallback to shared capacity when allowed.

Effective mode:

```text
IsolationMode = Hybrid
PreferDedicatedCapacity = true
AllowSharedFallback = true
```

Current validation covers:

- Hybrid mode propagation;
- Hybrid visibility rules;
- Hybrid process-host runtime creation and execution.

Full process-host shared fallback should be added after shared runtime pooling semantics are finalized.

---

## Runtime visibility rules

Runtime visibility is evaluated by tenant settings and runtime instance metadata.

### Dedicated runtime visibility

A Dedicated runtime is visible when either:

```text
runtime.TenantId == request.TenantId
```

or:

```text
runtime.TenantGroupId == request.TenantGroupId
```

Otherwise it is not visible.

### Hybrid runtime visibility

A Hybrid runtime follows the same ownership visibility rule as Dedicated runtime capacity.

It is visible only when the tenant or tenant group matches.

### Shared runtime visibility

A Shared runtime is visible only when the requesting tenant settings allow shared runtime usage.

Examples:

```text
Dedicated + AllowSharedFallback = false
→ cannot see shared runtime

Hybrid + AllowSharedFallback = false
→ cannot see shared runtime

Hybrid + AllowSharedFallback = true
→ can see shared runtime
```

---

## HTTP scale-out effective settings

`AiHttpRuntimeScaleOutProvisioner` resolves effective settings with this precedence:

```text
tenant runtime settings
    >
scale-out request values
    >
HTTP provider technical defaults
```

This applies to:

- `RuntimeInstanceIdPrefix`;
- `WorkerCountPerInstance`;
- `MaxConcurrentRunsPerInstance`;
- `LocalQueueCapacity`;
- `MaxRuntimeInstances`.

The request values remain compatibility fallbacks for older request paths.

HTTP provider options are technical defaults only and should not override tenant runtime policy.

---

## Runtime health, recovery, and lifecycle boundaries

The framework validates the separation between transport health, runtime safety, assigned-work recovery, and runtime lifecycle ownership.

The boundary is:

```text
HTTP provider
    reports transport / endpoint failure signals

RuntimeInstanceHealthReconciler
    detects stale or unsafe runtime capacity
    prevents unsafe capacity from receiving new dispatch

Execution recovery reconciler
    enumerates work assigned to the unsafe runtime
    resumes in-flight DAG executions
    redispatches local queued shared runs

Runtime lifecycle owner
    creates or attaches replacement runtime capacity when required
```

The HTTP provider must not kill, restart, or directly recover runtime instances. It only reports transport outcomes such as timeout, endpoint unavailable, or circuit open.

A local runtime queue is volatile. It is allowed to die with the process. The source of truth for recovery is durable control-plane and DAG state:

```text
SharedRunStore
SharedQueue
RuntimeRunExecutionIndex
DAG execution store
Runtime registry / capacity stores
Ledger / trace / replay / recovery forensics
```

Recovery completion is not the same as replacement runtime creation. Recovery is considered validated only after assigned work is reconciled, resumed or redispatched, completed, and observable through replay, ledger, trace, and runtime recovery forensics.

## Process-boundary observability

Process-host mode validates an important production constraint:

```text
The parent MCP process cannot rely on in-memory observability from the child runtime process.
```

Therefore the production scenarios validate durable observability stores.

The framework validates:

- decision ledger availability;
- trace timeline availability;
- replay metadata availability;
- replay report;
- replay ledger;
- replay trace / timeline.

Durable stores are required so the parent MCP process can observe executions produced by child `RuntimeInstanceOnly` processes.

---

## Production scenario framework

The framework defines reusable production scenario objects:

- `ProductionRuntimeScenarioDefinition`;
- `ProductionTenantScenarioDefinition`;
- `ProductionRunScenarioDefinition`;
- `ProductionRuntimeScenarioResult`;
- `ProductionTenantScenarioResult`;
- `ProductionRunScenarioResult`;
- `ProductionScaleOutScenarioResult`;
- `IProductionRuntimeScenarioRunner`.

The HTTP process-host runner is:

```text
HttpProcessHostProductionScenarioRunner
```

The gRPC process-host settings path reuses the same production scenario structure while replacing the provider and transport contract with gRPC-specific settings.

The real process-host crash recovery scenarios now share a provider-agnostic base:

```text
ProcessHostRealRuntimeCrashRecoveryScenarioTestsBase
```

Provider-specific wrappers select the runtime profile:

```text
HttpProcessHostScenarioRuntimeProfile
GrpcProcessHostScenarioRuntimeProfile
```

It creates:

- parent MCP server test host;
- tenant-scoped MCP clients;
- real process-hosted runtime instances through the Host Manager;
- durable replay / ledger / trace validation queries.

---

## Scenario types

### Real runtime crash recovery scenarios

The framework now validates process-host crash recovery with real `RuntimeInstanceOnly` host processes.

Validated crash recovery scenarios include:

- one tenant runtime process killed and recovered;
- two tenant runtime processes killed in the same recovery window;
- a safe tenant running concurrently without recovery contamination;
- in-flight DAG resume with preserved durable `ExecutionId`;
- local queued work redispatched through durable `SharedRunId` state;
- final runtime recovery forensics queried after completion;
- replay, ledger, trace, and control-plane causal chain proof after recovery;
- no cross-tenant ledger leak, no duplicate recovery, and no safe-tenant recovery forensics.

Representative HTTP tests:

```text
Http_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_With_Strict_Dag_Resume_Replay_Ledger_And_Trace

Http_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace
```

Representative gRPC tests:

```text
Grpc_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_With_Strict_Dag_Resume_Replay_Ledger_And_Trace

Grpc_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace
```

The safe-tenant scenario is the strongest isolation proof. Tenant A and tenant B each lose one real runtime process. Tenant C, represented by `tenant-real-crash-safe`, is never killed, receives no recovery forensics, has zero recovered work, completes its three normal runs, and still produces replay, ledger, and trace evidence.

Validated safe-tenant invariants:

```text
SafeTenantRecoveryLeakDetected = false
SafeTenantNonImpactValidated = true
CrashImpacted = false
CrossTenantLedgerLeakDetected = false
SafeTenantRecoveryEntriesVisibleFromImpactedQueries = 0
```

### Focused tenant runtime mode scenarios

Focused scenarios validate individual runtime modes:

- single-tenant Dedicated;
- single-tenant Shared;
- single-tenant Hybrid;
- multi-tenant Dedicated isolation.

These are smaller tests designed to fail fast when a specific tenant mode rule breaks.

---

### Adversarial Dedicated isolation scenario

The multi-tenant Dedicated isolation scenario can run tenants sequentially.

This is important because parallel tenants may both scale out independently and hide a routing bug.

Sequential execution creates a stronger test:

```text
Tenant A submits first
Tenant A scales out
Tenant A creates runtime
Tenant A completes

Tenant B submits after Tenant A runtime exists
Tenant B must not route to Tenant A runtime
```

This validates real tenant isolation behavior.

---

### Mixed-tenant full production validation scenario

The final production scenario mixes all tenant runtime modes:

- Dedicated tenant;
- Shared tenant;
- Hybrid tenant.

It enables:

- retention;
- ledger;
- trace;
- replay;
- replay report;
- replay ledger;
- replay trace.

Representative validation scale:

```text
3 tenants
4 runs per tenant
35 DAG steps per run

Total:
12 runs
420 DAG steps
real RuntimeInstanceOnly processes
durable Mongo / Redis observability
```

This scenario validates the complete HTTP process-host production path before merging the workstream.

---

## Validated production behavior

The production scenario framework validates the following behavior:

- tenant metadata is propagated into shared controller requests;
- `TenantGroupId` is included in request metadata for scale-out requeue scope matching;
- scale-out requests are tenant-scoped and pipeline-scoped;
- HTTP and gRPC scale-out can materialize real runtime process capacity;
- runtime instances register with tenant and isolation metadata;
- runtime capacity descriptors expose effective worker / concurrency / queue settings;
- runtime visibility respects Dedicated / Shared / Hybrid rules;
- Dedicated tenants do not reuse another tenant's dedicated runtime;
- tenant runtime settings override request-level runtime sizing;
- child process execution remains observable from the parent MCP process;
- ledger, trace, replay report, replay ledger, and replay timeline work across process boundaries;
- runtime process crash recovery resumes in-flight DAG executions with preserved `ExecutionId`;
- volatile local queued work is redispatched through durable `SharedRunId` state;
- runtime recovery forensics are durable and queryable after convergence;
- control-plane ledger causal chain evidence is available for scale-out, host creation, recovery, and redispatch;
- safe tenant non-impact is validated with zero recovery work, zero recovery forensics, and zero recovery ledger contamination;
- gRPC dispatch can execute real process-host runs after scale-out fulfillment;
- gRPC process-host crash recovery preserves the original durable `ExecutionId` during in-flight DAG resume;
- HTTP and gRPC wrappers validate the same provider-agnostic recovery base.

---

## Validation checklist

Before merging this workstream, run:

```bash
dotnet test --filter "FullyQualifiedName~HttpProcessHostProductionScenarioTests"
dotnet test --filter "FullyQualifiedName~HttpProcessHostRealRuntimeCrashRecoveryScenarioTests"
dotnet test --filter "FullyQualifiedName~GrpcProcessHostRealRuntimeCrashRecoveryScenarioTests"
dotnet test --filter "FullyQualifiedName~AiRuntimeInstanceVisibilityEvaluatorTests"
dotnet test --filter "FullyQualifiedName~ProductionTenantRuntimeModeMapperTests"
dotnet test --filter "FullyQualifiedName~AiHttpRuntimeScaleOutProvisionerTests"
dotnet test --filter "FullyQualifiedName~HttpAiRuntimeInstanceProviderServiceCollectionExtensionsTests"
```

The most important end-to-end execution validation is:

```text
Http_ProcessHost_Should_Run_MixedTenant_Full_Production_Validation_Scenario
```

The most important HTTP end-to-end recovery validations are:

```text
Http_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_With_Strict_Dag_Resume_Replay_Ledger_And_Trace

Http_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace
```

The most important gRPC end-to-end recovery validations are:

```text
Grpc_ProcessHost_Should_Requeue_Real_InFlight_Dag_After_Runtime_Process_Kill

Grpc_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_With_Strict_Dag_Resume_Replay_Ledger_And_Trace

Grpc_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace
```

These validate:

- all tenant modes;
- real process-host runtime instances;
- scale-out from zero;
- dispatch over HTTP;
- DAG execution;
- retention;
- ledger;
- trace;
- replay;
- durable process-boundary observability;
- real process-host runtime crash detection and unsafe capacity suppression;
- in-flight DAG resume with preserved durable `ExecutionId`;
- local queued work redispatch through durable `SharedRunId` state;
- runtime recovery forensics timelines;
- control-plane ledger causal chain proof;
- safe tenant non-impact proof.

---

## Current limitations and future work

### Shared runtime pooling

The final shared runtime pooling model is not decided yet.

The current Shared process-host scenario validates Shared mode propagation and execution, but it does not force a global shared runtime pool.

Future work should decide between:

- shared runtime per tenant;
- shared runtime per tenant group;
- global shared runtime pool.

### Hybrid shared fallback

Hybrid process-host fallback to shared capacity should be validated only after the shared capacity pooling model is explicit.

### gRPC readiness hardening

The gRPC process-host scenarios currently rely on runtime registration and capacity publication as readiness evidence.

This is correct for the current validation because the runtime process self-registers and publishes usable capacity before dispatch, but the provider should eventually get a dedicated gRPC-native readiness strategy.

Future readiness options:

```text
registry / capacity readiness waiter
gRPC health service
provider-aware readiness abstraction
```

The old HTTP command readiness endpoint must not be reused for gRPC.

### Runtime health, endpoint signals, and recovery boundary

Runtime health reconciliation and execution recovery are separate responsibilities.

Circuit breaker open events from HTTP transport are endpoint health signals. They do not directly kill, restart, or recover runtime instances from the HTTP command provider.

Validated transport-health-to-recovery boundary:

```text
HTTP circuit open / timeout / endpoint unavailable
→ provider returns structured transport failure
→ runtime endpoint health signal is observable
→ health reconciler may mark runtime unhealthy, draining, or unsafe
→ dispatcher stops selecting unsafe runtime capacity
→ execution recovery reconciler recovers assigned work if the runtime becomes unsafe
→ replacement capacity is requested if required
→ lifecycle owner creates or attaches replacement runtime capacity
```

Only the lifecycle owner, such as the Host Manager / Kubernetes provider / local process provider, should restart or replace runtime instances.

The important rule is that transport failure reporting, unsafe-capacity suppression, assigned-work recovery, and runtime lifecycle replacement remain separate. This prevents the HTTP provider from becoming the runtime recovery owner.

---

## Related docs

This document complements:

- `http-runtime-provider.md`;
- `grpc-runtime-provider.md`;
- `runtime-instance-provider-model.md`;
- `runtime-discovery-registry-capacity.md`;
- `multi-tenant-runtime-flow.md`;
- `multi-tenant-control-plane-isolation.md`;
- `testing-strategy.md`;
- `runtime-process-crash-recovery.md`;
- `runtime-recovery-forensics.md`;
- `multi-tenant-runtime-crash-isolation.md`;
- `control-plane-ledger-causal-chain.md`;
- `recovery-replay-ledger-trace-proof.md`.

Those documents describe the general provider, registry, capacity, multi-tenant, and testing architecture.

This document focuses specifically on the MCP production runtime scenario framework and the provider-based process-host validation path, including HTTP and gRPC.
