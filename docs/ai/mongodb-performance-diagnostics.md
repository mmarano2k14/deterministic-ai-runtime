# MongoDB Performance Diagnostics

Status: Implemented and validated for distributed runtime scenarios using MongoDB-backed durable runtime stores.

This document explains how to enable MongoDB performance diagnostics, how cross-process measurements should be interpreted, which runtime optimizations are reflected in the results, and where the runtime deliberately stops because correctness risk is greater than the remaining measured gain.

The diagnostic mode is opt-in. It is disabled by default and does not change execution success or failure semantics.

---

## Purpose

MongoDB server counters show total database activity, but they do not explain which runtime responsibility produced each command.

MongoDB performance diagnostics combine three views:

- server-side command, operation, connection, network, document, and latency deltas for the complete measurement window;
- application-side semantic attribution grouped by stable runtime operation family;
- MongoDB driver-level cluster, pool, connection, checkout, and command observations.

Together, these views make it possible to distinguish:

- command-count amplification from document volume;
- runtime traffic from harness or operational traffic;
- logical `MongoClient` construction from physical driver pool ownership;
- best-effort observability writes from authoritative durable writes;
- repeated global reconciliation from legitimate point reads;
- document-shape cost from query-index selection.

The mode is intended for bounded performance investigations, regression comparisons, and validation runs. It is not intended to remain enabled continuously in production.

---

## Enable the Diagnostic Mode

### Production scenario framework

Enable the diagnostic flag before starting the normal integration scenario:

PowerShell:

```powershell
$env:MULTIPLEXED_PERF2_MONGO_ATTRIBUTION = "1"

dotnet test <integration-test-project> <existing-test-options>
```

Bash:

```bash
export MULTIPLEXED_PERF2_MONGO_ATTRIBUTION=1

dotnet test <integration-test-project> <existing-test-options>
```

The production scenario framework creates a unique measurement scope and propagates it to participating runtime processes.

Redis performance attribution can be enabled at the same time when a combined datastore measurement is required:

```powershell
$env:MULTIPLEXED_PERF1_REDIS_ATTRIBUTION = "1"
$env:MULTIPLEXED_PERF2_MONGO_ATTRIBUTION = "1"
```

### Custom hosts and launchers

A custom launcher should provide both the activation flag and one non-empty scope shared by every participating process:

PowerShell:

```powershell
$env:MULTIPLEXED_PERF2_MONGO_ATTRIBUTION = "1"
$env:MULTIPLEXED_PERF2_MONGO_ATTRIBUTION_SCOPE = [Guid]::NewGuid().ToString("N")
```

Bash:

```bash
export MULTIPLEXED_PERF2_MONGO_ATTRIBUTION=1
export MULTIPLEXED_PERF2_MONGO_ATTRIBUTION_SCOPE="$(uuidgen | tr -d '-')"
```

Concurrent measurements must use different scopes.

---

## Disable the Diagnostic Mode

PowerShell:

```powershell
Remove-Item Env:MULTIPLEXED_PERF2_MONGO_ATTRIBUTION -ErrorAction SilentlyContinue
Remove-Item Env:MULTIPLEXED_PERF2_MONGO_ATTRIBUTION_SCOPE -ErrorAction SilentlyContinue
```

Bash:

```bash
unset MULTIPLEXED_PERF2_MONGO_ATTRIBUTION
unset MULTIPLEXED_PERF2_MONGO_ATTRIBUTION_SCOPE
```

For production-like validation, both Redis and MongoDB attribution flags should be removed when the goal is to confirm that the optimized runtime behavior exists without diagnostic collection.

---

## What the Diagnostic Mode Measures

Application-side attribution uses stable operation families such as:

- `Mongo.Ledger.Sequence.Next`
- `Mongo.Ledger.Entry.Append`
- `Mongo.Trace.Append`
- `Mongo.Snapshot.Load`
- `Mongo.Snapshot.Upsert`
- `Mongo.ChildRelation.Query`
- `Mongo.ChildRelation.ChildExecution.Load`
- `Mongo.ChildRelation.Identity.Load`
- replay metadata
- payload stores
- step payload index
- lifecycle journal
- recovery forensics
- runtime-pool failure journal

Each bounded attribution row can record information such as:

| Field | Meaning |
|---|---|
| `Operation` | Stable semantic runtime operation family. |
| `Command` | Expected MongoDB command family such as `FIND`, `INSERT`, `UPDATE`, or `FINDANDMODIFY`. |
| `Calls` | Physical attributed database operations. |
| `RequestedDocuments` | Logical documents requested or written by the operation. |
| `ReturnedDocuments` | Documents returned by read operations where applicable. |
| `AggregateDuration` | Aggregate observed duration for the operation family. |
| `Failures` | Failed attributed calls. |
| `Cancellations` | Cancelled attributed calls. |
| `DuplicateKeyRetries` | Explicit duplicate-key retry activity on supported paths. |

For batched operations, `Calls` and `RequestedDocuments` intentionally have different meanings.

Example:

```text
Mongo.Trace.Append
Calls               = physical InsertMany batches
RequestedDocuments  = logical trace records inside those batches
```

This distinction prevents a reduction in physical commands from being mistaken for a reduction in logical evidence.

---

## Driver and Server Corroboration

Semantic attribution is corroborated with MongoDB driver and server observations.

Driver-level diagnostics include bounded observations for:

- logical client instances;
- driver clusters / pools;
- connections opened and closed;
- connection checkouts;
- checkout failures;
- command starts and completions.

Server-side deltas include:

- total operations;
- query/read/write/command operations;
- network requests;
- network input/output bytes;
- new and rejected connections;
- inserted/returned/updated document counts;
- aggregate read/write/command latency;
- command-family counters such as `INSERT`, `FINDANDMODIFY`, `FIND`, `UPDATE`, `GETMORE`, `CREATEINDEXES`, `HELLO`, and `ISMASTER`;
- selected storage-engine and query-executor counters.

No high-cardinality execution, tenant, runtime, payload, or connection-string values are used as attribution labels.

---

## Measurement Integrity

MongoDB driver instrumentation must not accidentally change the pool topology being measured.

The validated diagnostic implementation uses one process-wide shared cluster configurator delegate for equivalent clients. This preserves MongoDB.Driver cluster sharing while still allowing application roles to report logical client construction.

This distinction is important:

```text
Logical MongoClient objects
!=
Physical MongoDB driver pools
```

Equivalent client reuse can reduce application object construction even when the driver is already sharing the same underlying physical pool.

A performance result should never treat instrumentation-induced pool splitting as a real runtime baseline.

---

## Runtime Optimizations Reflected in the Results

### Equivalent MongoClient reuse

Equivalent MongoDB configurations are reused through one application-level client per exact configuration inside each process.

The measured application-level construction count dropped from approximately:

```text
109 equivalent logical clients
to
23
```

This is an ownership/lifetime improvement. It does not claim that 109 physical pools previously existed.

### Best-effort trace batching

Mongo-backed runtime trace persistence is best-effort and is not execution authority.

The trace writer therefore uses a bounded batching path with:

- non-blocking producer enqueue;
- bounded queue capacity;
- bounded batch size;
- bounded flush interval;
- graceful shutdown draining;
- an explicit flush barrier before reads;
- drop and flush-failure metrics.

Authoritative stores are not routed through this batching helper.

Measured trace command behavior changed from:

| Metric | Before | After | Result |
|---|---:|---:|---:|
| `Mongo.Trace.Append` physical calls | `52,765` | `18,904` | **-64.17%** |
| Logical trace documents | `52,765` | `52,326` | approximately unchanged for the hard-kill workload |
| Average logical documents per physical trace command | `1.00` | `2.77` | command coalescing |

The production proof continued to validate trace evidence.

### Control-plane ownership for global Child DAG reconciliation

The largest read-amplification issue was architectural.

The same durable global Child DAG reconciler had been running in multiple `RuntimeInstanceOnly` worker processes against the same control-plane state.

The optimized ownership model keeps global reconciliation on control-plane-capable hosts while runtime workers continue normal execution, child relation writes, CAS transitions, continuation, and recovery participation.

No global mutable cache is introduced.

Measured read-family reductions included:

| Read family | Before | After | Result |
|---|---:|---:|---:|
| `Mongo.ChildRelation.Query` | `31,437` | `2,358` | **-92.50%** |
| `Mongo.ChildRelation.ChildExecution.Load` | `12,094` | `1,425` | **-88.22%** |
| `Mongo.ChildRelation.Identity.Load` | `1,880` | `862` | **-54.15%** |
| Server `FIND` in the matched stage comparison | `47,275` | `6,472` | **-86.31%** |

This optimization removes duplicated ownership rather than hiding authoritative reads behind caching.

### Snapshot physical shape alignment

The MongoDB snapshot document previously persisted the step graph twice:

```text
State.Steps   = authoritative durable state
Steps         = duplicate top-level projection
```

The optimized physical shape persists only the authoritative `State.Steps` representation.

On load, the public top-level `Steps` view is rehydrated from `State.Steps`, preserving the existing runtime contract.

This reduces duplicated BSON without changing replay authority.

The snapshot path remains latency-sensitive, so no stable snapshot-latency percentage is claimed from a single run.

---

## Optimizations Deliberately Rejected

### Decision-ledger sequence range allocation

The decision ledger sequence allocator remains one atomic sequence allocation per ledger append.

Range or HiLo allocation was rejected because the sequence participates in distributed ordering.

For example:

```text
Process A reserves 1..64 and writes sequence 1
Process B reserves 65..128 and writes sequence 65
Later Process A writes sequence 2
```

Sorting by sequence would place A2 before B1 even if B1 completed before A2 started.

Range allocation can also introduce additional crash gaps.

The sequence allocator is therefore treated as part of the durable evidence contract, not as a command-count implementation detail.

### Speculative index expansion

Snapshot lookup is already aligned to `ExecutionId` with an exact unique index.

Repeated `CREATEINDEXES` traffic was measured as non-material relative to the workload.

The runtime therefore does not add speculative indexes or change initialization behavior merely to reduce a small diagnostic counter.

---

## Final Production-Like Results

The clean comparison below uses:

- the pre-Mongo-optimization uninstrumented reference after Redis performance work; and
- the final runtime with both Redis and MongoDB attribution disabled.

The same adversarial workload and durable proof contract were preserved.

| Metric | Before | Final | Result |
|---|---:|---:|---:|
| Mongo total operations | `418,327` | `337,288` | **-19.37%** |
| Mongo network requests | `417,067` | `301,851` | **-27.63%** |
| Mongo network input | `600,260,655 B` | `484,872,183 B` | **-19.22%** |
| Mongo network output | `509,231,875 B` | `315,505,886 B` | **-38.04%** |
| Mongo new connections | `717` | `111` | **-84.52%** |
| Mongo read operations | `47,554` | `6,631` | **-86.06%** |
| Mongo `FIND` commands | `47,232` | `6,309` | **-86.64%** |
| Mongo `INSERT` commands | `209,266` | `155,067` | **-25.90%** |
| Mongo write operations | `366,455` | `292,728` | **-20.12%** |
| Mongo command operations | `3,046` | `2,479` | **-18.61%** |
| Aggregate command latency | `317,058 µs` | `216,815 µs` | **-31.62%** |
| Scenario duration | `13:39.632` | `13:54.075` | **+1.76% observed** |

The final production-like run reported:

- zero rejected MongoDB connections;
- exactly 36 completed parent executions;
- 108 recursive child executions;
- 144 total durable executions;
- exactly 7,308 logical steps;
- exactly eight recoveries;
- zero missing recursive child steps;
- zero unexpected duplicate child steps;
- zero ownership transition violations;
- parent replay proof 36 / 36;
- process-kill identity continuity 2 / 2;
- ledger, trace, lifecycle, and recovery-forensics proof passing.

Dedicated recursive-child replay remains a separate proof domain and is not claimed here.

---

## Interpreting the Results Correctly

MongoDB resource reduction and end-to-end duration measure different things.

The final clean run performs far fewer MongoDB reads and network requests, but it is not faster than the selected pre-optimization clean reference.

That means the correct conclusion is:

> The runtime asks MongoDB to perform substantially less unnecessary work while preserving the same durable execution contract.

It does **not** mean:

> Every workload will finish a fixed percentage faster.

For stable latency claims:

1. use the same topology, workload, data set, transport, and host configuration;
2. warm the system before measured runs;
3. collect several alternating baseline and candidate samples;
4. report median, p25, p75, minimum, maximum, and coefficient of variation;
5. compare phase timings, not only total elapsed time;
6. keep diagnostic configuration identical on both sides of an attributed comparison.

Do not add percentage reductions from different measurement windows.

---

## Remaining Cost Signals

The final reduction in `FIND` count does not eliminate all read latency.

A small number of snapshot operations remain comparatively expensive.

The query-executor document-scan counter also remains high relative to the final number of `FIND` commands.

Those signals should be investigated as separate future studies before changing authoritative persistence or adding indexes.

They are not unfinished work in the current MongoDB optimization pass.

---

## Operational Boundaries

The accepted optimizations preserve the following contracts:

- MongoDB remains authoritative for durable ledger, replay metadata, snapshots, lifecycle history, recovery forensics, pool-failure authority, payload metadata, and Child DAG relations where configured;
- decision-ledger sequence ordering is unchanged;
- authoritative writes are not moved to best-effort batching;
- trace batching remains bounded and best-effort;
- runtime execution does not block on trace queue capacity;
- a trace read performs a flush barrier for locally accepted pending records;
- global Child DAG reconciliation remains durable, but its ownership is centralized to control-plane-capable hosts;
- runtime workers do not introduce a broad authoritative-state cache;
- CAS transitions and stale-state guards remain in place;
- snapshot replay authority remains `State.Steps`;
- physical snapshot shape reduction does not change the public loaded snapshot contract;
- repeated index initialization is left unchanged when its measured cost is non-material;
- diagnostic publication is bounded and does not write attribution data to MongoDB;
- instrumentation must not alter MongoDB driver pool identity.

These boundaries prioritize deterministic recovery, replay, and evidence integrity over more aggressive command reduction.

---

## Related Documentation

- [Redis Performance Diagnostics](redis-performance-diagnostics.md)
- [Observability](observability.md)
- [Runtime Metrics](runtime-metrics.md)
- [Observability and Tracing](observability-tracing.md)
- [Replay and Audit](replay-and-audit.md)
- [Runtime Recovery Forensics](runtime-recovery-forensics.md)
- [Runtime Pool Failure Authority](runtime-pool-failure-authority.md)
- [Durable Child DAG Composition](child-dag-composition.md)
- [Distributed Execution](distributed-execution.md)
- [Testing Strategy](testing-strategy.md)
