# HTTP Runtime Provider

Status: Implemented and validated for hardened HTTP dispatch, provider-based HTTP scale-out, Runtime Host Manager process-host provisioning, Redis-backed scale-out request fulfillment, tenant-aware Shared / Dedicated / Hybrid runtime policies, and real process-host crash recovery with ledger, trace, replay, and runtime recovery forensics evidence.

This document describes the **HTTP runtime provider** for the Deterministic AI Runtime control plane.

The HTTP provider is part of the runtime instance provider model. It allows the control plane to communicate with runtime instances over HTTP and to participate in provider-based scale-out through the same `IAiRuntimeScaleOutProvider` routing model used by local and future providers.

The general provider model is described in:

- [Runtime Instance Provider Model](runtime-instance-provider-model.md)
- [Runtime Discovery, Registry, and Capacity](runtime-discovery-registry-capacity.md)
- [Multi-Tenant Control Plane Isolation](multi-tenant-control-plane-isolation.md)
- [MCP Server as Runtime Control Plane](mcp-server-control-plane.md)
- [MCP Production Runtime Scenario Framework](mcp-production-runtime-scenario-framework.md)

---

## Purpose

The HTTP runtime provider exists to support runtime instances that are reachable through HTTP endpoints.

It currently supports the following responsibilities:

- dispatching shared runs to HTTP runtime instances;
- querying runtime status through HTTP transport;
- sending runtime control operations through HTTP transport;
- applying HTTP dispatch timeout protection;
- applying retry and backoff behavior;
- applying circuit breaker protection;
- returning structured dispatch failure reasons;
- preserving queued-run state when dispatch cannot proceed;
- persisting dispatch failure state through the shared run store;
- participating in provider-based scale-out;
- delegating HTTP scale-out to `IAiHttpRuntimeScaleOutProvisioner`;
- delegating runtime host creation to the Runtime Host Manager when host-manager mode is enabled;
- launching real `RuntimeInstanceOnly` host processes in process-host scenarios;
- waiting for runtime registration / capacity readiness;
- preserving tenant-aware runtime settings during HTTP scale-out;
- validating Shared, Dedicated, and Hybrid runtime isolation behavior;
- validating ledger, trace, replay, and retention across process boundaries;
- validating real process-host crash recovery for impacted tenants;
- validating in-flight DAG resume with preserved `ExecutionId`;
- validating durable redispatch of runtime-local queued work;
- validating safe-tenant non-impact during concurrent tenant crash recovery;
- validating runtime recovery forensics and control-plane ledger causal-chain evidence.

The HTTP provider must not execute DAG steps directly.

The target runtime instance remains responsible for:

```text
local runtime queue
worker execution
DAG execution
runtime-local status
runtime-local cancellation
```

---

## Provider Responsibilities

The HTTP provider has two related but separate responsibilities.

### 1. HTTP Dispatch Transport

The HTTP provider can dispatch a run to an already registered runtime instance whose capacity descriptor contains HTTP transport metadata.

```text
Shared queue dispatcher
    ↓
Admission selects tenant-visible runtime capacity
    ↓
Capacity descriptor contains provider.name=http
    ↓
HttpAiRuntimeInstanceProvider
    ↓
HTTP runtime endpoint
    ↓
Runtime instance local queue
    ↓
DAG execution
```

Dispatch is transport-specific, but execution remains runtime-local.

### 2. HTTP Scale-Out Capability

The HTTP provider also implements `IAiRuntimeScaleOutProvider`.

This means it can be selected by the scale-out provider selector when a persisted scale-out request has:

```text
providerHint = http
```

The provider delegates scale-out to:

```text
IAiHttpRuntimeScaleOutProvisioner
```

Current validated process-host flow:

```text
AiRuntimeScaleOutRequestWatcherHostedService
    ↓
AiRuntimeScaleOutProviderSelector
    ↓
providerHint = http
    ↓
HttpAiRuntimeInstanceProvider
    ↓
IAiHttpRuntimeScaleOutProvisioner
    ↓
IAiRuntimeHostManager
    ↓
ProcessAiRuntimeHostCreationStrategy
    ↓
RuntimeInstanceOnly process starts
    ↓
runtime self-registers
    ↓
runtime publishes heartbeat / capacity
    ↓
readiness is observed
    ↓
ScaleOutRequest.Status = Fulfilled
    ↓
queued run is dispatched through HTTP
```

This proves that HTTP is no longer only a dispatch transport. It can also participate in provider-based runtime capacity creation through the Runtime Host Manager.

---

## Validated Real Process Crash Recovery

The HTTP process-host path is now validated not only for scale-out and dispatch, but also for real runtime process crash recovery.

Validated flow:

```text
MCP submit
    ↓
tenant-aware admission
    ↓
HTTP process-host runtime created
    ↓
run dispatched to runtime-local queue
    ↓
real OS runtime process killed
    ↓
heartbeat becomes stale / runtime marked unsafe
    ↓
execution recovery reconciler enumerates assigned work
    ↓
in-flight DAG execution resumes on replacement runtime
    ↓
local queued work is redispatched through durable SharedRun state
    ↓
replay / ledger / trace / forensics proof is validated
```

The HTTP provider does not own recovery. It reports endpoint and transport failure signals and participates in capacity creation when selected by the scale-out provider routing model. Runtime health reconciliation and assigned-work recovery remain separate control-plane responsibilities.

The validated recovery model distinguishes two categories of assigned work:

```text
In-flight execution
    durable ExecutionId exists
    recovery mode = resume-existing-execution
    replacement runtime must continue the same ExecutionId

Local queued run
    durable SharedRunId exists
    no ExecutionId exists yet
    recovery mode = requeue-local-queued-run
    replacement runtime must redispatch the SharedRun without duplicate submission
```

The local runtime queue is intentionally treated as volatile state. If the runtime process dies, the local queue is allowed to die with it. Durable recovery truth comes from the shared run store, shared queue, runtime run execution index, DAG store, registry/capacity state, ledger, trace, replay, and recovery forensics.

Validated multi-tenant safety invariant:

```text
Tenant A runtime process killed -> only Tenant A assigned work is recovered
Tenant B runtime process killed -> only Tenant B assigned work is recovered
Tenant C safe runtime not killed -> zero recovered work, zero recovery forensics, zero recovery contamination
```

---

## Provider vs Transport

Provider identity and transport identity must remain separate.

```text
providerHint=http
```

means the HTTP provider handles the scale-out request.

```text
transport.name=http
```

means the resulting runtime capacity is contacted through HTTP.

For the current HTTP provider process-host flow, these values are usually the same.

For Kubernetes, they may diverge:

```text
providerHint=kubernetes
transport.name=http
```

or:

```text
providerHint=kubernetes
transport.name=grpc
```

This separation allows Kubernetes, Docker, ECS, Nomad, local process launchers, or a remote MCP host manager to create runtime capacity while dispatch still uses HTTP or gRPC.

---

## HTTP Dispatch Hardening

HTTP dispatch is hardened with provider-level options.

Current options:

```text
AiHttpRuntimeInstanceProvider:DispatchTimeout
AiHttpRuntimeInstanceProvider:EnableRetry
AiHttpRuntimeInstanceProvider:MaxRetryAttempts
AiHttpRuntimeInstanceProvider:RetryBaseDelay
AiHttpRuntimeInstanceProvider:RetryMaxDelay
AiHttpRuntimeInstanceProvider:RetryTimeouts
AiHttpRuntimeInstanceProvider:EnableCircuitBreaker
AiHttpRuntimeInstanceProvider:CircuitBreakerFailureThreshold
AiHttpRuntimeInstanceProvider:CircuitBreakerBreakDuration
```

Default intent:

```text
DispatchTimeout = 30 seconds
EnableRetry = true
MaxRetryAttempts = 1
RetryBaseDelay = 200 milliseconds
RetryMaxDelay = 2 seconds
RetryTimeouts = false
EnableCircuitBreaker = true
CircuitBreakerFailureThreshold = 5
CircuitBreakerBreakDuration = 30 seconds
```

The provider fails fast and returns structured failure results when an HTTP runtime endpoint is unavailable, invalid, timing out, repeatedly failing, or circuit-open.

---

## Structured Failure Reasons

HTTP dispatch failures use stable failure reason constants.

Current failure reasons include:

```text
http-endpoint-missing
http-endpoint-invalid
http-provider-unavailable
http-dispatch-timeout
http-command-failed
http-command-non-retryable
http-command-invalid-response
http-circuit-open
http-command-cancelled
http-command-exception
```

These failure reasons should be persisted and observable through:

```text
SharedRunStore
logs
runtime control-plane events
decision ledger
trace timeline
future dashboards
```

The HTTP provider reports endpoint and command failure reasons, but it must not directly kill, restart, recover, or replace runtime instances.

Health management belongs to runtime instance health reconciliation. Assigned-work recovery belongs to the runtime execution recovery reconciler. Runtime replacement and host lifecycle mechanics belong to the lifecycle owner of the runtime instance, such as the Runtime Host Manager, process provider, Kubernetes provider, or an external supervisor.

---

## Retry, Timeout, and Circuit Breaker Behavior

The provider applies timeout protection around HTTP dispatch operations.

Retry behavior is intended for transient failures.

Non-retryable HTTP failures should not be retried.

Circuit breaker behavior prevents repeated dispatch attempts against a runtime endpoint that is already known to be failing.

Conceptual behavior:

```text
dispatch request
    ↓
endpoint metadata validated
    ↓
circuit state checked
    ↓
HTTP request executed with timeout
    ↓
retry applied when allowed
    ↓
failure reason mapped
    ↓
result returned to dispatcher
    ↓
shared run failure reason persisted when dispatch cannot proceed
```

Timeout, retry, and circuit breaker behavior must remain transport-level hardening.

They must not bypass admission, shared queue state, runtime local queue ownership, or DAG execution ownership.

---

## Circuit Open Semantics

A circuit breaker open event is an endpoint health signal.

It is not a runtime lifecycle command.

Expected behavior:

```text
HTTP dispatch detects circuit open
    ↓
provider returns http-circuit-open
    ↓
shared controller / dispatcher keeps the shared run safe
    ↓
run can remain queued or be requeued depending on dispatcher policy
    ↓
future health reconciler may mark runtime unhealthy or draining
    ↓
replacement capacity may be requested if needed
```

The HTTP command provider should not restart or close the runtime instance directly.

Only the lifecycle owner should restart or replace runtime instances.

Examples of lifecycle owners:

```text
Runtime Host Manager
local process provider
Kubernetes provider
external supervisor
```

---

## HTTP Scale-Out with Runtime Host Manager

The HTTP provider now supports process-host scale-out through the Runtime Host Manager.

The provider implements:

```text
IAiRuntimeScaleOutProvider
```

and delegates to:

```text
IAiHttpRuntimeScaleOutProvisioner
```

The provisioner can then delegate host creation to:

```text
IAiRuntimeHostManager
```

The process-host validated flow is:

```text
MCP submit
    ↓
Admission sees no tenant-visible capacity
    ↓
SharedRun.Status = ScaleOutRequested / queued globally
    ↓
Redis scale-out request created
    ↓
Scale-out watcher observes request
    ↓
Selector resolves HTTP provider
    ↓
HTTP provider delegates to provisioner
    ↓
Provisioner resolves tenant runtime settings
    ↓
Provisioner calls Runtime Host Manager
    ↓
Process host creation strategy starts RuntimeInstanceOnly process
    ↓
Runtime self-registers and publishes capacity
    ↓
Readiness is observed
    ↓
Scale-out request marked Fulfilled
    ↓
Queued run becomes dispatchable
    ↓
Shared queue dispatcher dispatches run over HTTP
```

This validates the complete HTTP scale-out loop with a real process-hosted runtime instance.

---

## Runtime Host Manager

The Runtime Host Manager separates provider selection from host lifecycle mechanics.

The HTTP provider remains responsible for:

- selecting and handling the HTTP provider scale-out path;
- resolving effective runtime settings;
- producing runtime instance identity and endpoint metadata;
- dispatching runs over HTTP;
- reporting HTTP transport failures.

The Runtime Host Manager is responsible for:

- creating or attaching runtime hosts;
- passing runtime identity and tenant context to the host;
- returning host startup information;
- supporting multiple host creation modes.

Supported or planned host creation modes:

```text
Fixture
Process
Attach
Kubernetes
```

### Fixture

Fixture mode uses test-host infrastructure.

It is useful for integration tests but does not prove process-boundary behavior.

### Process

Process mode starts a real runtime host executable or DLL.

This mode is validated by the production scenario framework.

Example process-host target:

```text
Multiplexed.AI.McpServer.Host.dll
```

configured as:

```text
RuntimeInstanceOnly
```

### Attach

Attach mode is intended for connecting to an already running runtime host.

The host manager does not create the process. It attaches to an existing endpoint and validates that the runtime can be used.

### Kubernetes

Kubernetes mode is intended for future cluster-based runtime capacity creation.

Expected future flow:

```text
HTTP or Kubernetes provider
    ↓
Host manager / Kubernetes creation strategy
    ↓
RuntimeInstanceOnly pod
    ↓
service endpoint readiness
    ↓
runtime registration and capacity heartbeat
```

---

## Readiness Requirement

HTTP process-host scale-out should not be considered fulfilled until the runtime is visible and usable.

Readiness should require:

```text
registry contains runtimeInstanceId
capacity store contains runtimeInstanceId
runtime status is Ready
CanAcceptRun = true
transport.name = http
transport.endpoint is present
tenant metadata matches request
isolation mode matches request
runtime instance prefix matches request
```

This readiness behavior should be provider-reusable.

The readiness model can support HTTP, gRPC, Kubernetes, ECS, Docker, Nomad, and other providers.

Example abstraction:

```csharp
public interface IAiRuntimeInstanceReadinessWaiter
{
    Task<AiRuntimeInstanceReadinessResult> WaitUntilReadyAsync(
        AiRuntimeInstanceReadinessRequest request,
        CancellationToken cancellationToken = default);
}
```

---

## Tenant-Aware HTTP Scale-Out

HTTP scale-out requests preserve tenant runtime settings.

The following fields must flow from admission into the Redis scale-out request, watcher provider request, HTTP provisioner, host manager request, registry metadata, and capacity metadata:

```text
TenantId
TenantGroupId
IsolationMode
PreferDedicatedCapacity
AllowSharedFallback
MaxRuntimeInstances
RuntimeInstanceIdPrefix
WorkerCountPerInstance
MaxConcurrentRunsPerInstance
LocalQueueCapacity
ExecutionContextSnapshot
```

Validated runtime modes:

```text
Dedicated
    IsolationMode = Dedicated
    PreferDedicatedCapacity = true
    AllowSharedFallback = false

Shared
    IsolationMode = Shared
    PreferDedicatedCapacity = false
    AllowSharedFallback = true

Hybrid
    IsolationMode = Hybrid
    PreferDedicatedCapacity = true
    AllowSharedFallback = true
```

Important behavior:

```text
Dedicated tenants must not silently use shared HTTP capacity.

Hybrid tenants may use shared HTTP capacity only when shared fallback is enabled.
```

---

## Tenant Runtime Settings Precedence

`AiHttpRuntimeScaleOutProvisioner` resolves effective runtime settings using this precedence:

```text
tenant runtime settings
    >
scale-out request values
    >
HTTP provider technical defaults
```

This applies to:

```text
RuntimeInstanceIdPrefix
WorkerCountPerInstance
MaxConcurrentRunsPerInstance
LocalQueueCapacity
MaxRuntimeInstances
```

Request values remain compatibility fallbacks for older request paths.

HTTP provider options remain technical defaults only.

They should not override tenant runtime policy.

---

## Tenant Visibility and HTTP Capacity

HTTP capacity descriptors are filtered by the same tenant visibility rules as local capacity.

Dedicated HTTP runtime capacity is visible only to:

```text
matching TenantId
or matching TenantGroupId
```

Hybrid HTTP runtime capacity is visible only to:

```text
matching TenantId
or matching TenantGroupId
```

Shared HTTP runtime capacity is visible to tenants whose runtime settings allow shared usage.

Examples:

```text
Dedicated + AllowSharedFallback = false
    → cannot see shared runtime

Hybrid + AllowSharedFallback = false
    → cannot see shared runtime

Hybrid + AllowSharedFallback = true
    → can see shared runtime
```

Hybrid fallback means:

```text
a Hybrid tenant may use Shared runtime capacity
```

It does not mean:

```text
every Hybrid tenant can see every Hybrid runtime capacity
```

This prevents cross-tenant capacity leakage.

---

## Metadata and Transport Keys

HTTP provisioned registry and capacity descriptors should include provider metadata, transport metadata, tenant metadata, runtime mode metadata, and scale-out metadata.

Provider metadata:

```text
provider.name = http
provider = http
```

Transport metadata:

```text
transport.name = http
transport.endpoint = http://...
runtime.instance.id = ...
```

Tenant metadata:

```text
tenant.id = tenant-a
tenant.group.id = tenant-group-id-xxx
runtime.isolationMode = Dedicated
runtime.preferDedicatedCapacity = True
runtime.allowSharedFallback = False
runtime.maxRuntimeInstances = 3
runtime.instanceIdPrefix = tenant-a-runtime
runtime.workerCountPerInstance = 10
runtime.maxConcurrentRunsPerInstance = 5
runtime.localQueueCapacity = 500
```

Scale-out metadata:

```text
scaleout.provider = http
scaleout.requestId = scale-out-...
scaleout.sharedRunId = ...
controlPlaneId = ...
```

Metadata is useful for diagnostics and compatibility, but tenant isolation must remain based on execution context, tenant runtime settings, and tenant-visible registry/capacity filtering.

---

## Configuration

HTTP dispatch hardening options:

```text
AiHttpRuntimeInstanceProvider:DispatchTimeout
AiHttpRuntimeInstanceProvider:EnableRetry
AiHttpRuntimeInstanceProvider:MaxRetryAttempts
AiHttpRuntimeInstanceProvider:RetryBaseDelay
AiHttpRuntimeInstanceProvider:RetryMaxDelay
AiHttpRuntimeInstanceProvider:RetryTimeouts
AiHttpRuntimeInstanceProvider:EnableCircuitBreaker
AiHttpRuntimeInstanceProvider:CircuitBreakerFailureThreshold
AiHttpRuntimeInstanceProvider:CircuitBreakerBreakDuration
```

HTTP scale-out options:

```text
AiHttpRuntimeScaleOut:Enabled
AiHttpRuntimeScaleOut:DefaultRuntimeInstanceIdPrefix
AiHttpRuntimeScaleOut:EndpointTemplate
AiHttpRuntimeScaleOut:HostCreationMode
AiHttpRuntimeScaleOut:ReadinessTimeoutSeconds
AiHttpRuntimeScaleOut:HeartbeatIntervalSeconds
```

Example process-host test configuration:

```text
AiMcpHost:Mode = ControlPlaneWithHttpRuntimeInstances
AiRuntimeInstanceRegistration:ProviderName = http
AiRunAdmission:EnableScaleOutRequest = true
AiRunAdmission:EnableGlobalQueueFallback = false
AiRunAdmission:RejectWhenNoCapacity = false
AiRuntimeScaleOutRequestWatcher:Enabled = true
AiRuntimeScaleOutRequestWatcher:WatcherId = mcp-scaleout-watcher
AiHttpRuntimeScaleOut:Enabled = true
AiHttpRuntimeScaleOut:HostCreationMode = Process
AiHttpRuntimeScaleOut:EndpointTemplate = http://127.0.0.1:{port}
```

---

## Validated Scenarios

The HTTP provider has been validated through unit and integration scenarios.

Validated dispatch hardening behavior:

```text
provider unavailable -> http-provider-unavailable
timeout -> http-dispatch-timeout
circuit open -> http-circuit-open
retry success -> dispatch succeeds
retry exhausted -> http-command-failed
non-retryable HTTP 4xx -> http-command-non-retryable
invalid endpoint -> http-endpoint-invalid
missing endpoint -> http-endpoint-missing
invalid response -> http-command-invalid-response
options binding -> configured provider options loaded
```

Validated scale-out and process-host behavior:

```text
MCP submit
    ↓
tenant-aware admission
    ↓
no tenant-visible capacity
    ↓
Redis scale-out request
    ↓
scale-out watcher
    ↓
providerHint = http
    ↓
HTTP provider
    ↓
HTTP scale-out provisioner
    ↓
Runtime Host Manager
    ↓
ProcessAiRuntimeHostCreationStrategy
    ↓
RuntimeInstanceOnly process
    ↓
runtime registration / capacity
    ↓
scale-out fulfilled
    ↓
HTTP dispatch
    ↓
DAG execution
```

Validated tenant runtime behavior:

```text
Dedicated process-host scale-out -> dedicated tenant runtime capacity
Shared process-host scale-out -> shared-mode tenant runtime capacity
Hybrid process-host scale-out -> hybrid-mode tenant runtime capacity
Dedicated tenant does not reuse another tenant's dedicated runtime
Hybrid tenant visibility respects shared fallback rules
Tenant settings override request-level runtime sizing
TenantGroupId is preserved for scale-out and requeue scope matching
```



Validated real process-host crash recovery behavior:

```text
single tenant runtime process kill -> assigned work recovered
multiple tenant runtime process kills in the same recovery window -> each tenant recovered independently
in-flight DAG execution -> same durable ExecutionId resumes on replacement runtime
local queued run -> durable SharedRunId redispatched without duplicate submission
safe tenant active during crash -> no runtime kill, no recovered work, no recovery forensics
runtime recovery forensics -> per-work-item timelines persisted and queryable after completion
control-plane ledger causal chain -> scale-out, host creation, capacity, recovery, redispatch evidence validated
replay / ledger / trace proof -> recovered and safe executions remain replay-ready and observable
```

Validated production scenario behavior:

```text
Dedicated + Shared + Hybrid tenants
multiple runs per tenant
many DAG steps per run
real RuntimeInstanceOnly processes
retention enabled
ledger enabled
trace enabled
replay enabled
replay report enabled
replay ledger enabled
replay trace enabled
Mongo / Redis durable observability
```

---

## Production Scenario Framework

The MCP production runtime scenario framework validates the full HTTP process-host path.

The most important scenario is:

```text
Http_ProcessHost_Should_Run_MixedTenant_Full_Production_Validation_Scenario
```

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

This scenario validates:

- MCP submission;
- real process-host crash recovery;
- runtime unsafe detection through heartbeat / health reconciliation;
- recovery of in-flight DAG executions with preserved `ExecutionId`;
- redispatch of local queued work through durable `SharedRunId`;
- safe-tenant non-impact during impacted tenant recovery;
- runtime recovery forensics timelines;
- control-plane ledger causal-chain evidence;
- tenant-aware admission;
- Redis scale-out request creation;
- scale-out watcher processing;
- HTTP provider scale-out;
- Runtime Host Manager process launch;
- runtime registration and capacity publication;
- HTTP dispatch;
- DAG execution;
- retention;
- ledger;
- trace;
- replay;
- replay report;
- replay ledger;
- replay trace.

For the full framework details, see:

```text
mcp-production-runtime-scenario-framework.md
```

---

## Current Limitations

The HTTP provider process-host path is validated for scale-out, dispatch, tenant-aware runtime policies, and real process-host crash recovery. Some production lifecycle concerns remain separate future work.

Current limitations:

- final shared runtime pooling semantics are not decided yet;
- Shared mode currently validates shared-mode propagation and execution, not a forced global shared runtime pool;
- Hybrid fallback to a shared process-host pool should be tested after shared pooling semantics are finalized;
- provider endpoint health signals remain separate from runtime instance health reconciliation;
- circuit-open is still a transport-level signal and does not by itself own runtime recovery;
- Kubernetes host creation remains a future provider/host-manager mode;
- crash recovery is validated for process-host runtime instances, not yet for Kubernetes pods or attached external hosts.

These limitations are intentional boundaries.

---

## Future Implementation Targets

Recommended next steps:

```text
1. Finalize shared runtime pooling semantics.
2. Add Hybrid shared fallback process-host validation after shared pooling is explicit.
3. Reuse the Host Manager readiness model for gRPC and Kubernetes.
4. Add Kubernetes process-equivalent crash recovery validation.
5. Add Attach-mode crash / disconnect recovery validation.
6. Expand dashboards over control-plane ledger causal-chain and runtime recovery forensics records.
```

Future transport-health-to-recovery flow:

```text
HTTP circuit open
    ↓
failure reason = http-circuit-open
    ↓
runtime endpoint health signal emitted
    ↓
health reconciler may mark runtime unhealthy or draining
    ↓
dispatcher stops selecting unsafe runtime capacity
    ↓
execution recovery reconciler recovers assigned work if the runtime becomes unsafe
    ↓
replacement capacity requested if required
    ↓
lifecycle owner creates or attaches replacement runtime capacity
```

---

## Documentation Rule

Do not treat the HTTP command provider as the runtime lifecycle owner.

Current completed capability:

```text
HTTP provider can participate in provider-based scale-out, use the Runtime Host Manager, launch real RuntimeInstanceOnly processes in process-host mode, wait for runtime registration/capacity readiness, dispatch queued runs over HTTP, and support validated real process-host crash recovery through control-plane health, assigned-work recovery, ledger, trace, replay, and forensics evidence.
```

The runtime boundaries must remain unchanged:

```text
Admission decides.
Providers transport or scale.
Runtime Host Manager creates or attaches runtime hosts.
Runtime instances self-register.
Registry/capacity stores expose readiness.
Shared queue owns queued shared run dispatch.
Local runtime queues own RunId.
DAG engine owns ExecutionId.
ExecutionContextSnapshot carries durable tenant context.
RuntimeInstanceHealthReconciler detects unsafe capacity.
Execution recovery reconciler recovers assigned work.
Local queue state is volatile.
SharedRunStore + SharedQueue + RuntimeRunExecutionIndex + DAG store are durable recovery truth.
Ledger / trace / replay / forensics prove recovery after convergence.
```
