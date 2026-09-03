# Redis Performance Diagnostics

Status: Implemented and validated for distributed runtime scenarios using standalone Redis, with topology-safe behavior preserved for Redis Cluster.

This document explains how to enable Redis performance diagnostics, how the collected measurements should be interpreted, and which runtime optimizations are reflected in the results.

The diagnostic mode is opt-in. It is disabled by default and does not change execution success or failure semantics.

---

## Purpose

Redis server counters show how much traffic occurred, but they do not identify which runtime operation produced each command.

Redis performance diagnostics combine two views:

- server-side command and network deltas for the complete measurement window;
- application-side attribution grouped by semantic runtime operation and Redis command.

Together, these views make it possible to distinguish workload changes from command-shape improvements, identify read amplification, and verify that a reduction comes from the intended runtime path.

The mode is intended for bounded performance investigations, regression comparisons, and validation runs. It is not intended to remain enabled continuously in production.

---

## Enable the Diagnostic Mode

### Production scenario framework

When using the production scenario framework, enable the diagnostic flag before starting the normal test command. The framework creates a unique measurement scope and propagates it to participating child processes and Kubernetes Pods.

PowerShell:

```powershell
$env:MULTIPLEXED_PERF1_REDIS_ATTRIBUTION = "1"

dotnet test <integration-test-project> <existing-test-options>
```

Bash:

```bash
export MULTIPLEXED_PERF1_REDIS_ATTRIBUTION=1

dotnet test <integration-test-project> <existing-test-options>
```

The accepted enabled values are `1`, `true`, `yes`, and `on`, compared without case sensitivity.

### Custom hosts and launchers

A custom launcher must provide both the activation flag and one non-empty scope shared by every participating process:

PowerShell:

```powershell
$env:MULTIPLEXED_PERF1_REDIS_ATTRIBUTION = "1"
$env:MULTIPLEXED_PERF1_REDIS_ATTRIBUTION_SCOPE = [Guid]::NewGuid().ToString("N")
```

Bash:

```bash
export MULTIPLEXED_PERF1_REDIS_ATTRIBUTION=1
export MULTIPLEXED_PERF1_REDIS_ATTRIBUTION_SCOPE="$(uuidgen | tr -d '-')"
```

The scope isolates one measurement window. All runtime processes included in the comparison must inherit the same scope. Concurrent runs must use different scopes.

The Runtime Pool process launcher inherits the active environment. The Kubernetes Runtime Pool resource factory also propagates the active flag and scope to created Pods.

The production scenario framework flushes, aggregates, and prints the scoped result automatically. A custom launcher should perform a final `AiRedisReadAttributionDiagnostics.FlushCurrentProcessAsync(...)` and aggregate the shared scope with `AiRedisReadAttributionDiagnostics.CollectAsync(...)` before ending the measurement.

---

## Disable the Diagnostic Mode

Remove both environment variables after the measurement.

PowerShell:

```powershell
Remove-Item Env:MULTIPLEXED_PERF1_REDIS_ATTRIBUTION -ErrorAction SilentlyContinue
Remove-Item Env:MULTIPLEXED_PERF1_REDIS_ATTRIBUTION_SCOPE -ErrorAction SilentlyContinue
```

Bash:

```bash
unset MULTIPLEXED_PERF1_REDIS_ATTRIBUTION
unset MULTIPLEXED_PERF1_REDIS_ATTRIBUTION_SCOPE
```

---

## What the Diagnostic Mode Measures

The application records bounded in-memory counters on instrumented Redis read paths. Each row contains:

| Field | Meaning |
|---|---|
| `Operation` | Stable semantic runtime operation. |
| `Command` | Redis command used by that operation, such as `GET`, `MGET`, `HGET`, `HMGET`, `HGETALL`, `SMEMBERS`, or `LUA`. |
| `Calls` | Number of successful attributed calls. |
| `ResponsePayloadBytes` | UTF-8 bytes represented by values returned to the application. |

Payload bytes do not include RESP framing, network packet overhead, TLS overhead, or client-library allocations.

The summary also compares attributed calls with Redis server deltas:

| Field | Meaning |
|---|---|
| `ServerCalls` | Total command delta reported by Redis during the measurement window. |
| `AttributedCalls` | Calls assigned to instrumented runtime operations. |
| `ResidualCalls` | Server calls not assigned to those operations. |
| `CoveragePercent` | Attributed calls divided by server calls. |

Residual traffic can include connection management, monitoring, diagnostic publication, non-instrumented callers, and activity outside the measured runtime operation families.

---

## Cross-Process Collection Model

Recording stays in process memory on the hot path. Each active process publishes an absolute snapshot to a scope-specific Redis hash approximately every two seconds.

The collection model has the following properties:

- publication is best-effort and does not affect application success or failure;
- process identity combines machine name and process identifier;
- snapshots use a two-hour expiry to avoid permanent diagnostic state;
- the parent performs a final local flush before aggregation;
- collection completes before the final Redis server snapshot, preventing attribution from extending beyond the server measurement window.

A process that is terminated abruptly may lose its final partial publication interval. In that case, the missing calls remain in the residual server count. Coverage is therefore conservative.

---

## Runtime Optimizations Reflected in the Results

### No-mutation state reuse

The runtime now reuses state that is already materialized when an authoritative operation confirms that durable data did not change.

Two cases are covered:

- a recovery scan reports that no durable recovery mutation occurred;
- retention reports that final convergence did not mutate durable state.

When either operation does mutate durable data, the original Redis reload remains mandatory. This preserves recovery authority and prevents stale state from being reused after a write.

### Combined DAG record and state retrieval

On standalone Redis, the execution record and state blob are loaded with one two-key `MGET` instead of two separate `GET` commands.

This changes command shape without changing the stored values, serialization, or execution semantics.

### Batched runtime-registry list retrieval

Runtime-registry list operations continue to read the scoped identifier index first. On standalone Redis, the corresponding registry entries are then loaded with one `MGET` rather than one sequential `GET` per entry.

Direct point reads remain individual `GET` commands because they protect mutable ownership, health, and recovery decisions.

### Redis Cluster safety

The optimized keys do not currently share a guaranteed Redis Cluster hash slot. Both batching paths therefore detect Cluster topology and retain the original individual reads when a cross-slot `MGET` would be unsafe.

This preserves correctness across standalone and Cluster deployments while using the lower-command path where the topology permits it.

---

## Measured Gains

The following results were obtained from matched distributed recovery workloads. Directly attributed reductions are separated from server-wide observations.

| Area | Before | After | Result | Interpretation |
|---|---:|---:|---:|---|
| State reconstruction reads after confirmed no-mutation paths | `133,305` | `109,211` | `-24,094` (`-18.07%`) | Direct operation-family comparison. |
| Combined DAG record/state retrieval | Two commands per retrieval | One command per retrieval | `37,276` commands avoided | Exact code-path accounting for `37,276` combined reads. |
| Runtime-registry entry retrieval | `52,746` commands | `20,497` commands | `-32,249` (`-61.14%`) | Direct registry command-family comparison. |
| Total Redis commands | `1,135,957` | `1,048,945` | `-87,012` (`-7.66%`) | Server-wide observation; not fully attributable to one change. |
| Redis `GET` commands | `668,466` | `548,789` | `-119,677` (`-17.90%`) | Server-wide observation of reduced individual reads. |
| Elapsed time | `14:19.302` | `13:39.632` | `-39.670 s` (`-4.62%`) | Single-run observation, not a latency guarantee. |

The `MGET` count increased during the same comparison. That increase is expected: each new `MGET` replaces a larger number of individual `GET` commands. A higher `MGET` count together with fewer total commands and fewer `GET` commands indicates successful command coalescing.

The complete validation workload remained green across all 36 scenarios, with 144 completed executions, eight induced recoveries, and no missing execution, duplicate completion, or ownership violation.

---

## Interpreting Performance Results Correctly

Redis command reduction and wall-clock duration measure different things.

Command attribution can prove that a specific read family was reduced. End-to-end duration also includes process or Pod startup, scheduling, recovery timing, logging, MongoDB activity, host load, and transport behavior. A single faster or slower run is therefore not sufficient to claim a stable latency change.

For reliable comparisons:

1. use the same workload, topology, runtime count, transport, data set, and configuration;
2. use a unique diagnostic scope for every run;
3. compare direct operation families before interpreting global Redis counters;
4. run multiple repetitions and use median and percentile durations for latency claims;
5. verify that completion, recovery, ownership, and replay guarantees remain unchanged;
6. keep diagnostic overhead enabled in both the baseline and candidate run.

Do not add reductions from different comparison windows unless they were measured against the same baseline. Direct attribution is the primary evidence for a runtime change; server totals are corroborating evidence.

---

## Operational Boundaries

The accepted optimizations deliberately preserve the following contracts:

- Redis and Lua remain authoritative for atomic claim, completion, recovery, and finalization transitions;
- durable state is reloaded after every confirmed mutation;
- direct ownership-sensitive reads are not cached or broadly coalesced;
- stored keys and serialized values are unchanged;
- standalone Redis receives safe multi-key batching;
- Redis Cluster retains individual reads where keys can occupy different slots;
- HTTP and gRPC use the same storage optimizations below the transport layer;
- diagnostic publication is best-effort and cannot fail the workload.

These boundaries prioritize deterministic recovery and ownership correctness over more aggressive but higher-risk reductions.

---

## Related Documentation

- [Observability](observability.md)
- [Runtime Metrics](runtime-metrics.md)
- [Distributed Execution](distributed-execution.md)
- [Runtime Discovery, Registry, and Capacity](runtime-discovery-registry-capacity.md)
- [Provider-Agnostic Process-Host Recovery](provider-agnostic-process-host-recovery.md)

