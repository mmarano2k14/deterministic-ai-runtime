# Deterministic AI Runtime
A deterministic, multi-tenant .NET runtime for durable AI workflow execution across local workers, real child processes, reusable Runtime Pools, and Kubernetes-hosted runtime instances.

Deterministic AI Runtime treats AI orchestration as a distributed-systems problem. It provides durable DAG execution, Redis-backed coordination, provider-based dispatch, bounded reusable capacity, crash recovery, deterministic replay, audit, observability, execution control, tenant isolation, configuration-driven runtime behavior, policy-driven execution, pluggable steps, and deterministic convergence behind one shared control plane.

Native durable Child DAG composition is **implemented and validated**: a parent DAG can delegate to a durable child execution, enter `WaitingForExternal` without holding runtime capacity, and resume through a deterministic continuation. Recursive production validation reaches `ChildDepth = 3`, including an intermediate `3×3×3×2×Depth3` proof and larger `5×5×5×2×Depth3` high-scale scenarios. Canonical Child DAG, continuation, recovery, and infrastructure lifecycle facts are observed through the existing Event Manager and correlated with the durable Ledger, Runtime Lifecycle Journal, replay, trace, and Recovery Forensics.

The runtime is content-agnostic. A unit of work can execute an LLM call, RAG operation, MCP tool, database command, human approval, HTTP/gRPC service, or polyglot component. The engine does not judge the answer; it guarantees the lifecycle of the execution that produced it.

Most AI tooling starts at prompts, agents, and RAG. This runtime starts one layer down, at execution:

> **Who owns the work, what survives a crash, what may execute again, and can the result be replayed and audited afterward?**

See [ecosystem positioning](docs/comparison-existing-tools.md) for how this execution layer compares with Temporal, Dapr, Dagster, Prefect, and LangGraph.

[![Version](https://img.shields.io/badge/Version-1.0.8.4-blue)](./CHANGELOG.md)

[![Changelog](https://img.shields.io/badge/Changelog-view-lightgrey)](./CHANGELOG.md)

![AI Runtime](https://img.shields.io/badge/AI-Deterministic%20Execution-purple)

![Runtime](https://img.shields.io/badge/Runtime-distributed-brightgreen)

![Redis](https://img.shields.io/badge/Redis-required-red?logo=redis)

![MongoDB](https://img.shields.io/badge/MongoDB-required-green?logo=mongodb)

![Kubernetes](https://img.shields.io/badge/Kubernetes-supported-326CE5?logo=kubernetes&logoColor=white)

![HTTP](https://img.shields.io/badge/Transport-HTTP-0A66C2)

![gRPC](https://img.shields.io/badge/Transport-gRPC-244C5A)

![Status](https://img.shields.io/badge/Status-active%20development-orange)

![Child DAG](https://img.shields.io/badge/Child%20DAG-Validated-brightgreen)

![Observation](https://img.shields.io/badge/Observation-EventDriven-brightgreen)

---
## Start Here
- [Architecture overview](docs/ai/architecture-overview.md)

- [Durable Child DAG composition — implemented / validated](docs/ai/child-dag-composition.md)

- [Runtime Pool architecture](docs/ai/runtime-pool-architecture.md)

- [Runtime Pool failure recovery](docs/ai/runtime-pool-failure-recovery.md)

- [Runtime Pool failure authority](docs/ai/runtime-pool-failure-authority.md)

- [Runtime Pool production validation](docs/ai/runtime-pool-production-validation.md)

- [Runtime Lifecycle Journal](docs/ai/runtime-lifecycle-journal.md)

- [Engine event observation and lifecycle catalog](docs/ai/engine-event-observation.md)

- [Event-driven testing strategy](docs/ai/testing-strategy.md)

- [Local Kubernetes / Minikube environment](docs/ai/kubernetes-local-environment.md)

- [Configuration-driven runtime](docs/ai/config-driven-runtime.md)

- [Policy-driven execution](docs/ai/policy-driven-execution.md)

- [Step plugins](docs/ai/step-plugins.md)

- [Replay and audit](docs/ai/replay-and-audit.md)

- [Concurrency hardening and adversarial validation](docs/ai/concurrency-hardening-and-adversarial-validation.md)

- [Kubernetes Runtime Host Provider](docs/ai/kubernetes-runtime-host-provider.md)

- [Multi-tenant control-plane isolation](docs/ai/multi-tenant-control-plane-isolation.md)

- [Enterprise readiness](docs/enterprise-readiness.md)

- [Complete documentation index](docs/index.md)

---
## Quick Start
### Prerequisites
- A .NET SDK compatible with the solution

- Redis

- MongoDB

- Docker

- Kubernetes for Kubernetes-backed scenarios

### Build
```powershell

dotnet build implementations/dotnet/Multiplexed.sln

```

### Run the Core Runtime Tests
```powershell

dotnet test implementations/dotnet/Tests/Multiplexed.AI.Tests/Multiplexed.AI.Tests.csproj

```

### Run the MCP Production Integration Tests
The real process-host, Runtime Pool, transport, crash-recovery, multi-tenant, replay, ledger, trace, and Kubernetes production scenarios live in the MCP Server integration test project.

```powershell

dotnet test implementations/dotnet/Tests/Multiplexed.AI.McpServer.Tests.Integration/Multiplexed.AI.McpServer.Tests.Integration.csproj

```

Long-running ProcessHostPool and KubernetesPool production proofs should normally be run with targeted test filters. They require the relevant built runtime hosts plus reachable Redis, MongoDB, and Kubernetes infrastructure.

### Local KubernetesPool Setup

For a fresh local Kubernetes environment, use the complete [Kubernetes / Minikube installation and recovery guide](docs/ai/kubernetes-local-environment.md).

The integration-test source contract is:

```text
implementations\dotnet\Tests\Multiplexed.AI.McpServer.Tests.Integration\
Scenarios\Production\Providers\Base\KubernetesSdkScenarioConstants.cs
```

`KubernetesSdkScenarioConstants.RuntimeImage`, `ImagePullPolicy`, and `Namespace` are the source of truth for the local Kubernetes test contract.

Current validated image contract:

```text
RuntimeImage     = multiplexed-ai-runtime:k8s-debug-131
ImagePullPolicy  = Never
Namespace        = ai-runtime
```

The runtime image is built from:

```text
implementations\dotnet\src\Multiplexed.AI.McpServer.Host\Dockerfile
```

From the repository root:

```powershell
docker build -f .\implementations\dotnet\src\Multiplexed.AI.McpServer.Host\Dockerfile -t multiplexed-ai-runtime:k8s-debug-131 .
minikube image load multiplexed-ai-runtime:k8s-debug-131
```

Because the integration contract uses `ImagePullPolicy = Never`, the exact image tag declared by `KubernetesSdkScenarioConstants.RuntimeImage` must already exist inside Minikube before a KubernetesPool scenario starts.

The validated local large-scenario baseline uses an 8 CPU / 12 GiB Minikube profile together with Envoy Gateway, Gateway API CRDs, the `ai-runtime` namespace, and host-reachable Redis and MongoDB. The complete bootstrap, pre-flight checks, monitoring, and recovery procedure are documented in [Local Kubernetes / Minikube environment](docs/ai/kubernetes-local-environment.md).

---
# What the Runtime Proves Today
| Area | Current evidence |
|---|---|
| Deterministic execution | Dependency-aware DAG execution with durable state, atomic claims, retry, recovery, retention, replay, and deterministic convergence. |
| Durable Child DAG composition | **Implemented / validated** — native parent/child DAG composition, durable `WaitingForExternal`, deterministic child identity, continuation, recovery, warm-capacity execution, and recursive `ChildDepth = 3` production validation are green. Validation includes `3×3×3×2×Depth3` and `5×5×5×2×Depth3` profiles. |
| Centralized engine-event observation | Canonical semantic engine facts are emitted through the existing Event Manager and routed by a central projection catalog to Ledger, Recovery Forensics, Runtime Lifecycle Journal, Metrics, Logging, and Realtime where applicable. |
| Deterministic EventDriven lifecycle observation | EventDriven canaries use durable evidence check → realtime subscribe → durable evidence re-check → canonical event wait → final durable-state verification, with hard watchdogs retained. |
| Deterministic replay and audit | Persisted snapshots, fingerprints, ledger events, lifecycle history, trace timelines, and recovery forensics support post-execution validation without re-running external side effects. |
| Configuration-driven runtime | Pipeline, provider, transport, retry, retention, concurrency, observability, hosting, and queue behavior are resolved from explicit configuration. |
| Policy-driven execution | Retry, retention, concurrency, admission, isolation, recovery, and governance decisions are evaluated through dedicated policy boundaries. |
| Pluggable execution | Domain operations remain external to the orchestration engine and can execute LLM, RAG, MCP, database, human, HTTP/gRPC, or polyglot workloads. |
| Real process boundaries | HTTP and gRPC providers dispatch into real external runtime processes. |
| Reusable ProcessHostPool | Multiple external parent ProcessHosts each own several independently registered child runtimes. |
| Reusable KubernetesPool | Multiple Kubernetes Pods each host several independently registered in-Pod runtime processes. |
| Exact routing | The control plane targets one `RuntimeInstanceId`; transport routing does not silently select a sibling. KubernetesPool replacement runtimes keep a new execution identity while transport gateway routing remains a separate reachability concern. |
| Child-failure isolation | One failed child runtime can be suppressed, recovered, and replaced while its parent boundary and healthy siblings survive. |
| Full-boundary recovery | One fully busy ProcessHost or Kubernetes Pod can disappear and have its exact failed membership recovered. |
| External infrastructure failure | The same full-boundary recovery proof passes when the exact armed ProcessHost or Kubernetes Pod is destroyed manually from outside the test harness. |
| Durable failure authority | Runtime Pool failure facts are shared through a durable MongoDB failure journal with first-class failure identities. |
| Durable lifecycle history | Host, Pod, runtime, incident, and run-placement history is append-only and queryable after cleanup. |
| Durable recovery semantics | In-flight work resumes the same `ExecutionId`; durable queued work is redispatched from shared state. |
| Claim-protected recovery | Exact recovery candidates are protected by deterministic claim ownership before mutation. |
| Multi-tenant isolation | Registry, capacity, admission, scale-out, recovery, ledger, replay, trace, lifecycle, and forensics remain tenant-scoped. |
| Bounded warm capacity | Production proofs reuse existing warm ProcessHostPool and KubernetesPool capacity before introducing replacement capacity. |
| Adversarial concurrency | P10–P35 campaigns validate convergence under real process kills, datastore pressure, lifecycle collisions, and machine saturation. |

---
# Durable Child DAG Composition

**Status:** **Implemented / validated**

**Status date:** `2026-08-24`

**Validated boundary:** native durable Child DAG composition is validated through recursive `ChildDepth = 3` production scenarios. The lifecycle-observation promotion gate is closed through the centralized Event Manager, canonical engine events, Runtime Lifecycle Journal, durable Ledger, Recovery Forensics, replay, trace, and EventDriven production validation.

Child DAG composition lets a running DAG delegate work to another durable DAG execution, wait without keeping a runtime slot occupied, and resume the same durable parent execution when the child reaches a terminal result.

```text
Parent ExecutionId
    ↓
ExecuteChildDag
    ↓
durable child relation
    ↓
Child ExecutionId
    ↓
parent step = WaitingForExternal
    ↓
parent claim / lease / runtime capacity released
    ↓
child executes / retries / recovers through the existing runtime
    ↓
child terminal result frozen
    ↓
deterministic continuation scheduled through the existing shared queue
    ↓
same parent ExecutionId resumes
```

The capability does **not** introduce a second orchestration engine. It reuses the existing DAG engine, Shared Run Store, Shared Queue, policy boundaries, execution stores, recovery ownership, Ledger, tracing, lifecycle infrastructure, and Forensics.

The logical and physical identities remain deliberately separate:

```text
one durable ExecutionId
    ↓
zero or more LocalRunId physical attempts
    ↓
possibly different RuntimeInstanceId / ProcessHost / Pod UID after recovery
```

This allows a child execution to survive physical runtime replacement while preserving the same durable execution identity.

## Recursive validation ladder

Validation was expanded incrementally rather than jumping directly to the largest topology:

```text
Depth 1                    GREEN
Depth 2                    GREEN
3×3×3×2×Depth3             GREEN — recursive Depth3 validation
5×5×5×2×Depth3             GREEN — high-scale validation
```

The `3×3×3×2×Depth3` scenario is the intermediate recursive Depth3 correctness proof.

The `5×5×5×2×Depth3` profile validates the same recursive contract under larger bounded capacity.

The high-scale profile represents:

```text
5 parent boundaries
× 5 runtimes per boundary
= 25 bounded runtime slots

25 runtime slots
× 5 submission iterations per cycle
= 125 parent DAGs per cycle

125 parent DAGs
× 2 warm-reuse cycles
= 250 parent DAGs total

250 parent DAGs
× 51 root logical steps
= 12,750 exact root logical steps
```

Validated behaviors include:

```text
recursive ChildDepth = 3
real child-runtime failure after durable progress
same durable ExecutionId preserved across in-flight recovery
parent ProcessHost / Pod survives isolated child failure
exact child membership replacement
distinct fully busy parent failure boundary
exact affected SharedRun recovery
authoritative durable Child DAG terminal proof
EventDriven lifecycle synchronization
Runtime Lifecycle Journal proof
MCP replay
Ledger
trace
Recovery Forensics
warm topology reuse across cycles
no intermediate cleanup
final deterministic cleanup
```

The earlier `ChildDepth = 1` Kubernetes warm-reuse production proof remains valid historical evidence:

```text
5 Kubernetes Pods × 5 runtime processes
= 25 bounded runtime slots

2 warm-reuse cycles
50 parent DAGs per cycle
100 parent DAGs total
5,100 parent logical steps total

1 exact child-runtime kill + recovery per cycle
1 distinct busy Pod failure + recovery per cycle
12 recovered shared runs across the scenario

same durable ExecutionId preserved through in-flight recovery
warm capacity reused between cycles
no intermediate cleanup
no duplicate dispatch
no lost run
no Pod/runtime capacity exceed
final deterministic cleanup
```

`ChildDepth` is a validation-scenario parameter used to exercise nested delegation. It is not a second production execution mode.

## Lifecycle observation and continuation proof

Recursive Child DAG execution is now observable through the same canonical lifecycle architecture used by the rest of the runtime.

Representative durable chain:

```text
child.execution.created
→ child.execution.started
→ child.execution.completed
→ child.continuation.scheduled
→ child.continuation.delivered
→ child.continuation.consumed
→ parent.continuation.resumed
```

Recovery and infrastructure lifecycle facts remain correlated through the same first-class execution, runtime, failure, correlation, and causation identities.

The existing Event Manager, durable Ledger, Runtime Lifecycle Journal, trace correlation, Recovery Forensics, Metrics, Logging, and Realtime implementations are reused. No second event bus, lifecycle store, Ledger, Child-DAG-specific queue, or alternate source of truth has been introduced.

## Exactness scope

The current `12,750` high-scale logical-step proof is intentionally scoped to **root parent logical steps**:

```text
RawStepCompletedLedgerEntryCount = 12,750
DistinctLogicalStepCompletedLedgerCount = 12,750
RecoveryCoveredDuplicateStepCompletedLedgerEntryCount = 0
```

Recursive child terminality is validated through authoritative durable DAG execution records.

Separate exact child-level step accounting for each nested `ChildExecutionId` remains proof-hardening work and is not implied by the root-step count.

Current proof scope:

```text
Root parent step exactness          validated
Nested Child DAG terminality        validated
Nested child step exactness         future proof hardening
```

See:

- [Durable Child DAG composition](docs/ai/child-dag-composition.md)
- [Engine event observation](docs/ai/engine-event-observation.md)
- [Testing strategy](docs/ai/testing-strategy.md)

---

# Production Runtime Pool Validation

## EventDriven recursive validation baseline

The Runtime Pool production validation now includes recursive Child DAG execution using deterministic EventDriven post-failure synchronization.

```text
3×3×3×2×Depth3       GREEN — recursive Depth3 validation
5×5×5×2×Depth3       GREEN — high-scale validation
```

The high-scale recursive profile completes:

```text
250 / 250 parent DAGs
12,750 exact root parent logical steps
2 warm-reuse cycles
2 deterministic child-runtime failures
2 distinct parent failure-boundary failures
12 recovered SharedRuns
same-ExecutionId in-flight resume
authoritative Child DAG terminal proof
canonical RuntimeLifecycleJournal observation
replay / Ledger / trace / Recovery Forensics validation
```

High-scale variants are validated on gRPC KubernetesPool and both HTTP/gRPC ProcessHostPool transports.

HTTP KubernetesPool transport parity is considered closed through the shared KubernetesPool production path, existing HTTP coverage, and the same recursive EventDriven recovery/lifecycle contract; the long-running high-scale permutation is not repeated solely to change transport when it introduces no new failure boundary or recovery mechanism.

The historical flat Runtime Pool validation below remains important independent evidence and is preserved unchanged except where the distinction between flat and recursive logical-step counts must be explicit.

The same hierarchical failure contract is validated across both transport providers and both Runtime Pool boundary models, in **two independent variants**:

1. **Automatic boundary failure** — the production harness injects the full parent-boundary failure.

2. **External/manual boundary failure** — the harness arms an exact fully busy target and waits while the operator destroys that ProcessHost or Kubernetes Pod from outside the test.

```text

                              Automatic failure   External/manual failure

gRPC + ProcessHostPool             PASS                   PASS

HTTP + ProcessHostPool             PASS                   PASS

gRPC + KubernetesPool              PASS                   PASS

HTTP + KubernetesPool              PASS                   PASS

```

The final validated profiles are intentionally not uniform:

| Transport / Pool | Parent boundaries | Runtimes / boundary | Submission iterations / cycle | Cycles | DAGs / scenario | Logical steps / scenario |
|---|---:|---:|---:|---:|---:|---:|
| gRPC ProcessHostPool | 7 | 5 | 20 | 2 | 1,400 | 70,000 |
| HTTP ProcessHostPool | 3 | 5 | 5 | 2 | 150 | 7,500 |
| gRPC KubernetesPool | 5 | 5 | 5 | 2 | 250 | 12,500 |
| HTTP KubernetesPool | 3 | 5 | 5 | 2 | 150 | 7,500 |

The historical flat Runtime Pool profiles in this matrix execute 50 logical steps per parent DAG. Recursive Child DAG canaries use 51 root parent logical steps, where the additional root step is the durable child-delegation boundary.

## gRPC ProcessHostPool scale spotlight — `7 × 5 × 20 × 2`
The largest final Runtime Pool proof uses:

```text

7 ProcessHosts

× 5 runtimes per ProcessHost

= 35 independently selectable runtime slots

35 runtime slots

× 20 submission iterations per cycle

= 700 DAG executions per cycle

700 DAGs

× 2 warm-reuse cycles

= 1,400 DAG executions per scenario

1,400 DAGs

× 50 logical steps

= 70,000 logical steps per scenario

```

Both failure variants are green:

```text

automatic parent failure

    1,400 completed DAGs

    70,000 logical steps

    2 child-runtime failures

    2 complete ProcessHost failures

    12 exact recoveries

external/manual parent kill

    1,400 completed DAGs

    70,000 logical steps

    2 child-runtime failures

    2 complete ProcessHost failures

    12 exact recoveries

```

Across those **two independent gRPC ProcessHostPool executions**:

```text

2,800 completed DAGs

140,000 logical steps

4 child-runtime failures

4 complete ProcessHost failures

24 exact recoveries

```

The 2,800-DAG figure is an aggregate of two separate production proofs; it is not presented as one single test execution.

## Failure sequence validated in every final scenario
Each cycle performs:

```text

warm bounded capacity

        ↓

submit full-capacity workload

        ↓

kill one exact child runtime after durable DAG progress

        ↓

recover exactly the affected child-runtime work

        ↓

preserve the parent boundary + healthy sibling identities

        ↓

restore exact child membership

        ↓

reuse converged warm capacity

        ↓

select one distinct fully busy parent boundary

        ↓

automatic variant:

    harness destroys the parent boundary

external/manual variant:

    harness exposes the exact target

    and waits for an operator to destroy it externally

        ↓

detect disappearance of the exact parent incarnation

        ↓

recover exact failed-boundary membership

        ↓

drain every DAG

        ↓

replay + ledger + trace + lifecycle + recovery forensics

        ↓

reuse the converged warm pool in cycle 2

        ↓

final deterministic cleanup only after the last cycle

```

The manual variants deliberately keep the child-runtime failure automatic; only the **distinct full parent boundary** is destroyed externally. This preserves the same hierarchical failure contract while proving that recovery does not depend on the test harness being the source of the infrastructure failure.

For manual KubernetesPool proofs, keep this watcher open:

```powershell

Get-Content "$env:TEMP\multiplexed-ai-manual-kubernetes-kill.txt" -Wait

```

For manual ProcessHostPool proofs:

```powershell

Get-Content "$env:TEMP\multiplexed-ai-manual-processhost-kill.txt" -Wait

```

The signal file exposes the exact target and the command to execute only after the boundary is fully armed and busy.

## Aggregate final Runtime Pool evidence
One pass of the four **automatic** final scenarios represents:

```text

1,950 completed DAGs

97,500 logical steps

8 child-runtime failures

8 complete parent-boundary failures

48 exact recoveries

```

The four **external/manual** scenarios repeat the same validated profiles independently.

Across both matrices:

```text

3,900 completed DAGs

195,000 logical steps

16 child-runtime failures

16 complete parent-boundary failures

96 exact recoveries

```

The 16 full parent-boundary failures consist of:

```text

8 complete ProcessHost failures

8 Kubernetes Pod failures

```

The gRPC KubernetesPool external/manual proof provides a concrete example of the external-failure contract:

```text

250 / 250 completed DAGs

12,500 logical steps

2 child-runtime kills

0 harness-forced Pod deletions

2 externally observed Pod deletions

12 exact recoveries

warm pool reused between cycles

no intermediate cleanup

no lost run

no duplicate dispatch

no Pod-capacity overflow

no runtime-capacity overflow

final remaining Pod count = 0

```

The production admission phases in that closing gRPC KubernetesPool run also completed with zero false HTTP 429 retries after the RBAC context/concurrency fix.

These numbers are validation evidence for the tested configurations, not universal throughput claims.

See [Runtime Pool production validation](docs/ai/runtime-pool-production-validation.md).

---
# Event-Driven Reference Validation

EventDriven canaries are the reference synchronization profile for current recursive Runtime Pool validation.

The reference pattern is:

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

The shared scenario core remains the same. `ProductionRecoveryObservationMode.EventDriven` changes how post-failure recovery completion is synchronized; it does not create a second recovery implementation.

The deterministic wait contract is:

```text
durable evidence check
→ subscribe to realtime canonical events
→ durable evidence re-check
→ await the canonical event when still required
→ verify final durable state
```

The subscribe/re-check sequence closes the missed-event race where a transition happens immediately before the realtime subscription becomes active.

Hard watchdogs remain mandatory.

Historical Polling scenarios remain compatibility and fallback regression coverage. Proven polling tests are not deleted merely because EventDriven is now the reference profile.

The pre-kill crash threshold can continue to use durable-state observation:

```text
pre-kill durable progress authority
    = durable state / compatibility polling

post-kill recovery synchronization
    = canonical EventDriven lifecycle observation
```

Run the reference ProcessHostPool canary with:

```powershell
dotnet test implementations/dotnet/Tests/Multiplexed.AI.McpServer.Tests.Integration/Multiplexed.AI.McpServer.Tests.Integration.csproj `
  --filter "FullyQualifiedName~Grpc_ProcessHostPool_EventDriven_Canary_Should_Reuse_The_Same_FullFailure_Scenario" `
  --logger "console;verbosity=normal"
```

See [Testing strategy](docs/ai/testing-strategy.md) for the complete reference matrix and synchronization contract.

---

# Runtime Pool Failure Model
A Runtime Pool is an identity model before it is a capacity model.

The hierarchy is explicit:

```text

Logical Runtime Pool

        │

        ├── Parent boundary

        │      ├── Runtime A1

        │      ├── Runtime A2

        │      ├── Runtime A3

        │      ├── ...

        │

        ├── Parent boundary

        │      ├── Runtime B1

        │      ├── Runtime B2

        │      ├── ...

        │

        └── ...

```

The parent boundary is:

```text

ProcessHostPool  → external parent ProcessHost

KubernetesPool   → Kubernetes Pod

```

A parent boundary is **not** an execution identity.

A child `RuntimeInstanceId` is independently selectable execution capacity.

This distinction makes two different failures possible:

```text

child runtime failure

    → one runtime becomes unsafe

    → parent remains alive

    → healthy siblings remain valid

parent boundary failure

    → every runtime in that boundary disappears

    → complete failed membership becomes unsafe

    → replacement boundary restores capacity

```

The recovery scope follows the failure scope exactly.

---
## Exact Child Runtime Recovery
A child crash is injected only after durable execution progress.

```text

DAG running

    ↓

at least 25 logical steps completed

(flat profiles use 50 root steps; recursive Child DAG canaries use 51 root parent steps)

    ↓

exact child OS process killed

    ↓

failure recorded

    ↓

exact runtime marked unsafe

    ↓

exact affected inventory enumerated

    ↓

one recovery claim acquired

    ↓

same ExecutionId resumed

    ↓

replacement runtime restores membership

```

The parent ProcessHost or Pod survives and healthy sibling runtime identities remain preserved.

---
## Exact Full-Boundary Recovery
After child recovery and warm convergence, a distinct parent boundary is selected only when it is fully busy.

For the validated `3 × 5` topology:

```text

target boundary

    RuntimeCount = 5

    ActiveRunCount = 5

```

The complete boundary is then killed.

```text

failed parent boundary

        ↓

5 failed runtimes

        ↓

exact failure identity

        ↓

5 exact recovery candidates

        ↓

5 accepted claims / transitions

        ↓

replacement boundary

        ↓

5 recovered runs

```

Historical incident evidence is retained. Current recovery correctness is scoped to the current failure identity rather than inferred from historical counts.

---
# Durable Failure, Lifecycle, and Recovery Authority
Correctness is deliberately split across independent stores and responsibilities.

```text

Runtime Registry

    → current runtime state and current capacity

Runtime Pool Failure Journal

    → authoritative durable failure facts

Runtime Lifecycle Journal

    → append-only infrastructure and placement history

Recovery Claim Store

    → mutation exclusivity

Decision Ledger

    → durable decisions and execution evidence

Recovery Forensics

    → work-item-level recovery timeline

```

These stores are correlated through first-class identities instead of being physically merged.

Important identities include:

| Identity | Responsibility |
|---|---|
| `ControlPlaneId` | Logical control-plane scope. |
| `PoolId` | Logical reusable capacity group. |
| `HostId` | Parent boundary incarnation. |
| `KubernetesPodUid` | Kubernetes failure-boundary identity. |
| `RuntimeInstanceId` | Independently selectable execution capacity. |
| `RouteId` | Transport-route incarnation where applicable. |
| `FailureId` | Exact Runtime Pool failure observation. |
| `RuntimeFailureIncidentId` | Durable incident correlation across lifecycle evidence. |
| `SharedRunId` | Durable shared work identity. |
| `LocalRunId` | Runtime-local work identity. |
| `ExecutionId` | Durable DAG execution identity. |
| `ClaimId` | Deterministic recovery claim identity. |
| `LeaseId` | Active claim-acquisition generation. |
| `CorrelationId` | Cross-component correlation identity. |
| `CausationId` | Causal relationship identity. |

Correctness-critical values remain typed. Diagnostic metadata is not used as the routing, tenant, lifecycle, or recovery authority.

These durable stores remain independent authorities for their specific responsibilities. Canonical semantic observation is centralized above them through the Event Manager and projection catalog; centralization does not collapse independent persistence boundaries into a fictitious distributed transaction.

See:

- [Runtime Pool failure authority](docs/ai/runtime-pool-failure-authority.md)

- [Runtime Lifecycle Journal](docs/ai/runtime-lifecycle-journal.md)

- [Runtime Pool failure recovery](docs/ai/runtime-pool-failure-recovery.md)

---
# Centralized Engine Event Observation

Canonical engine facts are centralized through the existing Event Manager. The runtime does not introduce a parallel event bus or duplicate observability model.

```text
PRODUCTION ENGINE
      │
      │ semantic engine fact
      ▼
CANONICAL EVENT NAMESPACE
      │
      ▼
EXISTING EVENT MANAGER
      │
      ▼
CENTRAL PROJECTION CATALOG
      │
      ├── Ledger
      ├── Recovery Forensics
      ├── Runtime Lifecycle Journal
      ├── Metrics
      ├── Logging
      └── Realtime
              │
              ▼
     Deterministic lifecycle observer
              │
              ▼
      EventDriven test waiting
```

The architectural rule is:

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

Projection targets retain their existing responsibilities and implementations. Centralization changes orchestration ownership; it does not physically merge the Ledger, Forensics, Runtime Lifecycle Journal, Metrics, Logging, or Realtime surfaces.

The central projection catalog defines projection durability semantics including:

```text
RequiredDurable
ReplayableDurable
BestEffort
None
```

This avoids treating every projection as if it shared the same transactional boundary.

Representative event families include:

```text
Execution
Run
Queue
DAG
Child DAG
Claim
Step
Recovery
Retry
Policy
Concurrency
Execution Control
Human Input
Retention
Payload
Snapshot
Storage
Replay
Finalization
Runtime Infrastructure Lifecycle
```

The complete canonical event catalog, including physical semantic values, emission boundaries, durability classification, identities, projections, and reference lifecycle sequences, is documented in [Engine event observation and lifecycle catalog](docs/ai/engine-event-observation.md).

---

# Architecture at a Glance
```text

Client / API / MCP

        ↓

RBAC ExecutionContext

        ↓

durable ExecutionContextSnapshot

        ↓

Shared Runtime Controller

        ↓

Shared Run Store / Shared Queue

        ↓

Tenant-Aware Admission

        ↓

Registry / Capacity / Reservations

        ↓

Provider Selection

        ↓

Local / HTTP / gRPC Provider

        ↓

Runtime Host Manager

        │

        ├── Fixture

        ├── Process

        ├── Attach

        ├── Kubernetes

        ├── ProcessHostPool

        └── KubernetesPool

                ↓

        exact RuntimeInstanceId selection

                ↓

        independently registered runtime

                ↓

Local Runtime Queue

        ↓

DAG Execution Engine

        ↓

Redis Hot State + Lua Coordination

        ↓

Stateless Workers / Step Executors

        ↓

MongoDB Payloads / Snapshots

        ↓

Canonical Engine Event

        ↓

Existing Event Manager / Central Projection Catalog

        ↓

Failure Journal / Lifecycle Journal / Ledger / Metrics / Logging / Realtime

        ↓

Trace / Replay / Recovery Forensics / Deterministic Lifecycle Observation

```

---
## Responsibility Boundaries
- The control plane owns admission, capacity selection, scale-out, dispatch, and recovery coordination.

- Providers own command transport.

- Runtime Host Manager strategies own host lifecycle.

- ProcessHostPool and KubernetesPool own bounded reusable runtime capacity.

- Parent ProcessHosts and Kubernetes Pods are infrastructure failure boundaries.

- `RuntimeInstanceId` remains the execution-capacity identity.

- Route registries own exact transport reachability.

- Pool routers forward to exact selected capacity; they are not hidden schedulers.

- Health and safety state remove unsafe capacity from admission.

- Failure journals own durable Runtime Pool failure facts.

- Lifecycle journals own append-only infrastructure and placement history.

- Recovery enumerators identify work assigned to failed capacity.

- Recovery claim coordination owns mutation exclusivity.

- Existing recovery transition boundaries own durable resume and redispatch semantics.

- Configuration defines runtime structure and operating parameters.

- Policies decide retry, retention, concurrency, admission, isolation, selection, and recovery behavior.

- Step plugins implement domain operations without owning orchestration correctness.

- Replay reconstructs and validates persisted execution evidence independently from live external side effects.

- Production components emit canonical semantic facts through the existing Event Manager rather than directly orchestrating migrated observability projections.

- The central projection catalog owns canonical event-to-projection routing and durability requirements.

- Deterministic lifecycle observation combines durable evidence with realtime canonical events for EventDriven test synchronization.

---
# Runtime Hosting Models
| Mode | Boundary model | Transport | Reusable capacity | Status |
|---|---|---|---:|---|
| Local | In-process runtime | Local | N/A | Implemented |
| Process | One external runtime process | HTTP / gRPC | No | Implemented / validated |
| Kubernetes | One `RuntimeInstanceOnly` per Pod/Service | HTTP / gRPC | No | Implemented / validated |
| ProcessHostPool | External parent ProcessHost containing multiple runtimes | HTTP / gRPC | Yes | Implemented / validated |
| KubernetesPool | Kubernetes Pod containing multiple runtimes | HTTP / gRPC | Yes | Implemented / validated |

The existing Kubernetes mode remains available independently from KubernetesPool.

KubernetesPool is additive; it does not redefine the legacy one-runtime-per-Pod behavior.

---
# Kubernetes Runtime Pool
KubernetesPool moves the reusable Runtime Pool model inside a real Kubernetes failure boundary.

```text

Kubernetes Node

    ↓

Pod = failure boundary

    ↓

in-Pod Runtime Pool Manager

    ├── RuntimeInstanceId A1

    ├── RuntimeInstanceId A2

    ├── RuntimeInstanceId A3

    ├── RuntimeInstanceId A4

    └── RuntimeInstanceId A5

```

The Pod does not become the execution identity.

The control plane still selects exact runtime capacity.

KubernetesPool validates:

- multiple independent runtimes per Pod;

- bounded Pod count;

- bounded runtimes per Pod;

- HTTP transport;

- gRPC transport;

- exact in-Pod child failure;

- parent Pod survival during child failure;

- sibling identity preservation;

- exact child replacement;

- distinct fully busy Pod failure;

- external/manual force-deletion of the exact armed Pod, detected by Pod UID disappearance;

- exact failed-Pod work recovery;

- warm Pod reuse between cycles;

- deterministic final cleanup.

Tenant ownership is validated from typed Registry state rather than runtime-name conventions.

For local KubernetesPool validation, the exact runtime image contract is defined by `Tests/Multiplexed.AI.McpServer.Tests.Integration/Scenarios/Production/Providers/Base/KubernetesSdkScenarioConstants.cs`. The image is built from `src/Multiplexed.AI.McpServer.Host/Dockerfile` and must be loaded into Minikube when `ImagePullPolicy = Never`. See [Local Kubernetes / Minikube environment](docs/ai/kubernetes-local-environment.md).

Transient historical Registry visibility is not counted as active capacity after an intentionally failed runtime has already been declared unsafe.

---
# Warm Capacity Reuse
Runtime Pools are designed to reuse healthy capacity rather than recreate infrastructure for every run.

The production proof makes that behavior explicit.

```text

cycle 1

    ↓

create bounded pool

    ↓

execute + recover failures

    ↓

leave healthy converged capacity alive

cycle 2

    ↓

reuse same warm pool

    ↓

no intermediate cleanup

    ↓

execute + recover new failures

    ↓

final deterministic cleanup

```

The second cycle is explicitly validated as a warm start:

```text

ColdStart = false

```

For KubernetesPool, the proof validates reuse of the existing warm Pod/runtime inventory before failure-driven replacement occurs — including the final gRPC `5 Pods × 5 runtimes = 25 runtime slots` profile and the HTTP `3 × 5 = 15` profile.

---
# Deterministic Recovery Semantics
## In-Flight Work
An in-flight candidate already has a durable `ExecutionId`.

```text

RuntimeInstanceId = failed-runtime

LocalRunId        = local-flight

ExecutionId       = execution-01

```

Recovery preserves the same execution identity:

```text

ExecutionIdBefore == ExecutionIdAfter

```

The execution resumes rather than becoming a logically new DAG.

---
## Durable Queued Work
Queued work that exists only in the dead process-local queue is not treated as durable truth.

Durable redispatch starts from shared state and the `SharedRunId`.

```text

dead local queue

    ≠ durable recovery authority

SharedRunId

    = durable redispatch identity

```

---
## Deterministic Recovery Claim
Exact candidate inventory is fingerprinted before mutation authority is granted.

```text

FailureId

PoolId

HostId

RuntimeInstanceId

RouteId

InventoryFingerprint

ClaimId

LeaseId

```

Concurrent coordinators may observe the same failure, but recovery mutation is protected by claim ownership.

---
# Replay-Driven Evidence
Replay is not treated as a log dump or best-effort reconstruction.

The runtime persists enough evidence to validate execution after completion or recovery:

```text

terminal snapshot

    + deterministic fingerprint

    + dependency graph

    + step state

    + payload references

    + decision ledger

    + lifecycle history

    + trace timeline

    + recovery forensics

    = replayable execution evidence

```

Validated foundations include:

- audit-only replay;

- restore replay;

- deterministic fingerprint validation;

- replay metadata;

- replay ledger loading;

- replay trace loading;

- lifecycle reconstruction;

- post-crash recovery replay proof;

- exact logical-step identity proof.

External model or tool side effects do not need to be invoked again to inspect the durable execution history.

---
# Multi-Tenant Control-Plane Isolation
The durable tenant boundary is:

```text

ExecutionContextSnapshot.TenantId

```

RBAC context is captured and persisted before the request leaves its original API or MCP scope.

```text

MCP / API

    ↓

RBAC ExecutionContext

    ↓

ExecutionContextSnapshot

    ↓

SharedRunRecord

    ↓

Shared Queue

    ↓

Tenant-Aware Admission

    ↓

selected RuntimeInstanceId

    ↓

Runtime Local Queue

    ↓

DAG Execution

```

The runtime validates:

- tenant-aware Registry and capacity visibility;

- Shared, Dedicated, and Hybrid isolation;

- explicit shared fallback policy;

- tenant-scoped scale-out settings;

- safe-tenant non-impact during crash recovery;

- no cross-tenant ledger leakage;

- no cross-tenant recovery-forensics leakage;

- typed tenant ownership on runtime snapshots.

`ContextKey` remains useful for RBAC lookup, correlation, and diagnostics. It is not the durable tenant-isolation boundary.

RBAC context lifetime and concurrency exhaustion are also distinguished explicitly: missing or expired context is authorization failure (`403`), while a valid context that actually exceeds its concurrency allowance is throttled (`429`). The closing Runtime Pool proofs completed without the previous false-429 admission loop.

---
# Adversarial Concurrency Evidence
The earlier Process-host concurrency campaigns remain an important independent stress proof.

Both HTTP and gRPC P35 campaigns completed 35/35.

Per transport:

```text

parallel scenarios             35

tenants                        105

real DAG executions            315

real external process kills    70

affected jobs recovered        210

logical DAG step completions   15,750

```

The measured HTTP P35 batch generated:

```text

Redis commands                 2,913,328

MongoDB operations             1,278,120

combined datastore operations  4,191,448

measured datastore traffic     18.29 GiB

```

The machine slowed down before correctness broke.

The campaign preserved:

- exact pre-crash inventory;

- durable crash checkpoints;

- the same `ExecutionId` for in-flight resume;

- durable redispatch for work that had not started;

- no duplicate logical-step completion;

- no contested runtime ownership;

- no safe-tenant recovery contamination;

- consistent ledger, trace, replay, and recovery forensics.

P35 represents the experimental edge of the tested local machine, not a universal production throughput guarantee.

---
# Core Capabilities
| Capability | Status |
|---|---:|
| Deterministic DAG execution | Implemented |
| Durable Child DAG composition | Implemented / validated — recursive `ChildDepth = 3`, deterministic continuation, recovery, warm reuse, replay, lifecycle, Ledger, trace, and Forensics evidence |
| Redis hot state and Lua atomic coordination | Implemented |
| Distributed workers and step claims | Implemented |
| Retry, stale-work recovery, and deterministic convergence | Implemented |
| Execution pause, resume, cancel, and human input | Implemented |
| Run-level queue control | Implemented |
| Retention, compaction, eviction, and payload externalization | Implemented |
| Snapshot and Replay API foundations | Implemented |
| Audit-only and restore replay | Implemented |
| Replay ledger and trace evidence | Implemented / validated |
| Configuration-driven pipeline and runtime behavior | Implemented foundation |
| Policy-driven retry, retention, concurrency, admission, and recovery | Implemented |
| Pluggable external execution model | Implemented foundation |
| Execution-correlated decision ledger | Implemented |
| Runtime Lifecycle Journal | Implemented / validated |
| Durable Runtime Pool Failure Journal | Implemented / validated |
| Metrics, tracing, and realtime event foundations | Implemented |
| Centralized canonical engine-event observation | Implemented / validated |
| Deterministic EventDriven lifecycle observer | Implemented / validated |
| RBAC execution-context propagation | Implemented / validated |
| Shared, Dedicated, and Hybrid tenant isolation | Implemented / validated |
| Redis registry, capacity, discovery, and admission reservations | Implemented / validated |
| Shared Runtime Controller and shared queue pump | Implemented / validated |
| Local runtime provider and scale-out | Implemented / validated |
| HTTP runtime provider and process-host scale-out | Implemented / validated |
| gRPC runtime provider and process-host scale-out | Implemented / validated |
| Kubernetes Runtime Host Provider | Implemented / validated |
| Provider-agnostic HTTP/gRPC crash recovery | Implemented / validated |
| ProcessHostPool | Implemented / validated |
| KubernetesPool | Implemented / validated |
| Stable Runtime Pool HTTP and gRPC routing | Implemented / validated |
| Exact child Runtime Pool failure isolation | Implemented / validated |
| Exact parent-boundary failure recovery | Implemented / validated |
| External/manual parent-boundary failure recovery | Implemented / validated |
| Warm Runtime Pool reuse | Implemented / validated |
| Claim-protected deterministic Runtime Pool recovery | Implemented / validated |
| Replay/ledger/trace/lifecycle/forensics production proof | Implemented / validated |
| Redis Cluster compatibility and failover validation | Further hardening |
| Multi-control-plane durable claim arbitration | Further hardening |
| Public API / SDK polish | Planned |

---
# Why This Exists
Prototype AI systems often focus on prompts, agents, models, tools, and RAG.

Production execution infrastructure must also answer:

- Who owns the work?

- What happens if one worker crashes?

- What happens if an entire ProcessHost or Kubernetes Pod disappears?

- Can an in-flight execution resume without receiving a new identity?

- Can durable queued work be recovered without trusting a dead local queue?

- Can two recovery coordinators mutate the same work?

- Can one failed child be removed without sacrificing healthy sibling capacity?

- Can a whole failed boundary be replaced without losing or duplicating its work?

- Can warm capacity be reused safely across repeated cycles?

- Can unrelated tenants remain provably untouched?

- Can infrastructure history be reconstructed after cleanup?

- Can the execution be replayed and audited?

- Can concurrency, retry, retention, admission, and provider pressure be governed?

- Can the same execution protocol survive local, process, Runtime Pool, and Kubernetes boundaries?

- Can runtime behavior change through configuration instead of rewriting the engine?

- Can domain operations remain pluggable without weakening execution guarantees?

- Can one durable DAG delegate to another without blocking a physical runtime while it waits?

- Can nested child execution, continuation, retry, and recovery converge without creating duplicate logical work?

Deterministic AI Runtime exists to make those guarantees explicit, durable, and testable.

> **The runtime does not need to understand the answer. It needs to guarantee what happens to the execution that produced it.**

---
# Current Boundaries
This repository is an advanced, test-driven execution-infrastructure project under active development. It is not presented as a finished commercial platform.

Implemented and validated foundations now include:

- deterministic DAG execution and durable coordination;

- durable Child DAG composition with `WaitingForExternal`, deterministic child identity, continuation, recovery, and recursive `ChildDepth = 3` production validation;

- HTTP/gRPC process-host execution;

- one-runtime-per-Pod Kubernetes hosting;

- ProcessHostPool reusable capacity;

- KubernetesPool reusable in-Pod capacity;

- exact runtime routing;

- targeted child runtime replacement;

- exact parent-boundary replacement;

- external/manual ProcessHost and Kubernetes Pod failure detection and recovery;

- durable shared Runtime Pool failure authority;

- append-only Runtime Lifecycle Journal;

- claim-protected deterministic recovery;

- warm-capacity reuse;

- deterministic replay, replay ledger, replay trace, lifecycle, and recovery-forensics evidence;

- configuration-driven runtime behavior;

- policy-driven retry, retention, concurrency, admission, and recovery boundaries;

- pluggable external execution;

- tenant-aware control-plane isolation;

- adversarial process-failure and bounded-capacity production proofs;

- centralized canonical engine-event observation through the existing Event Manager and projection catalog;

- deterministic EventDriven lifecycle waiting with durable evidence re-checks and hard watchdogs.

Areas for continued hardening include:

- exact recursive child-level step accounting beyond the current root parent logical-step proof;

- seeded deterministic adversarial failure schedules covering additional crash positions, targets, and reproducible interleavings;

- atomic runtime ownership / lease-overlap proof independent from periodic binding sampling;

- Redis read-amplification and MongoDB connection-churn characterization;

- Redis Cluster key-slot and failover validation;

- durable multi-control-plane recovery-claim arbitration;

- larger distributed Kubernetes node-level scale testing;

- production dashboarding and managed-hosting packaging;

- public API and SDK polish;

- broader platform-level performance characterization.

See the [project roadmap](docs/roadmap.md).

---
# Documentation
## Architecture and Runtime
- [Architecture overview](docs/ai/architecture-overview.md)

- [Durable Child DAG composition — implemented / validated](docs/ai/child-dag-composition.md)

- [Ecosystem positioning and comparison](docs/comparison-existing-tools.md)

- [Runtime Pool architecture](docs/ai/runtime-pool-architecture.md)

- [Runtime Pool failure recovery](docs/ai/runtime-pool-failure-recovery.md)

- [Runtime Pool failure authority](docs/ai/runtime-pool-failure-authority.md)

- [Runtime Pool production validation](docs/ai/runtime-pool-production-validation.md)

- [Runtime Lifecycle Journal](docs/ai/runtime-lifecycle-journal.md)

- [Engine event observation and lifecycle catalog](docs/ai/engine-event-observation.md)

- [Local Kubernetes / Minikube environment](docs/ai/kubernetes-local-environment.md)

- [Runtime control plane](docs/ai/runtime-control-plane.md)

- [Runtime discovery, registry, and capacity](docs/ai/runtime-discovery-registry-capacity.md)

- [Runtime instance provider model](docs/ai/runtime-instance-provider-model.md)

- [HTTP runtime provider](docs/ai/http-runtime-provider.md)

- [gRPC runtime provider](docs/ai/grpc-runtime-provider.md)

- [Kubernetes Runtime Host Provider](docs/ai/kubernetes-runtime-host-provider.md)

## Recovery, Concurrency, and Evidence
- [Concurrency hardening and adversarial validation](docs/ai/concurrency-hardening-and-adversarial-validation.md)

- [Provider-agnostic process-host recovery](docs/ai/provider-agnostic-process-host-recovery.md)

- [Runtime process crash recovery](docs/ai/runtime-process-crash-recovery.md)

- [Runtime recovery forensics](docs/ai/runtime-recovery-forensics.md)

- [Multi-tenant runtime crash isolation](docs/ai/multi-tenant-runtime-crash-isolation.md)

- [Recovery replay, ledger, and trace proof](docs/ai/recovery-replay-ledger-trace-proof.md)

- [Testing strategy](docs/ai/testing-strategy.md)

## Product Direction
- [Enterprise readiness](docs/enterprise-readiness.md)

- [Project roadmap](docs/roadmap.md)

- [Product roadmap index](docs/product-roadmap/index.md)

- [Current product foundation](docs/product-roadmap/current-foundation.md)

- [Managed hosting model](docs/product-roadmap/managed-hosting-model.md)

The complete documentation map is available at [docs/index.md](docs/index.md).

---
# License
This project is licensed under the **Business Source License 1.1 (BSL)**.

- Free for development, testing, and internal use

- Commercial production use requires a license

- Automatically converts to Apache 2.0 on 2029-01-01

See the repository license file for full terms.
