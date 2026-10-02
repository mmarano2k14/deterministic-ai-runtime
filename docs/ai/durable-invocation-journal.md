# Durable Invocation Journal and Hosted Worker Authority

**Status:** Implemented on the current Invocation hardening branch with targeted correctness, MongoDB integration, and production-query-plan validation green. Broader legacy/new matrix revalidation is being rerun before branch closure; historical full-matrix evidence remains historical until those reruns complete.

## Purpose

The durable invocation subsystem controls one published hosted-function operation without giving the physical worker orchestration authority.

The durable flow remains:

```text
immutable invocation
    -> durable invocation journal
    -> worker lease
    -> assignment epoch
    -> hosted worker
    -> result acceptance CAS
    -> durable continuation
    -> DAG resume
```

The journal is not a second workflow engine. The existing DAG engine remains authoritative for scheduling, claims, retries, `Park`, continuation application, finalization, and recovery.

## Authority boundaries

The following responsibilities stay distinct:

| Responsibility | Authority |
|---|---|
| Immutable code/environment selection | Publication and whole-run pinning |
| Logical invocation identity and frozen input | Durable invocation journal |
| Current physical assignment | Worker lease + assignment epoch |
| Result acceptance | Journal CAS + authoritative predicates |
| Parent DAG progression | Existing DAG/continuation path |
| Runtime placement and recovery | Existing runtime/control-plane authorities |
| Physical process/container lifecycle | Selected hosted-worker transport |

A dispatch-page record, worker process, container, timer, or SDK client is never ownership authority by itself.

## Ownership revalidation for hosted policy families

Hosted `Concurrency`, `Retry`, and `Delegation` policies now share the same security/lifecycle revalidation infrastructure while retaining different business contracts.

The common revalidation boundary covers:

```text
ExecutionId
TenantId
TenantGroupId
UserId
Project
CurrentNamespace
parent lifecycle / terminal state
```

The shared preparation path restores the persisted execution context, authorizes the selected publication, resolves immutable code/package material, and revalidates ownership after publication/package I/O.

The following remain family-specific:

```text
Concurrency decision semantics
Retry decision semantics
Delegation decision semantics
family-specific merge/precedence rules
```

No generic boolean-policy engine is introduced.

## Execution-language and time authorities

Hosted execution language support is centralized through the shared execution-language contract:

```text
dotnet
python
typescript
```

`AiExecutionLanguages.All` and `AiExecutionLanguages.IsSupported(...)` are the common source of truth used by normalized invocation/worker paths.

Invocation deadline and lease-related timing uses the configured `TimeProvider`. A physical timer is a wake-up mechanism, not a second time authority. The worker lease guard rechecks lease time through `TimeProvider` before declaring lease loss.

This preserves deterministic fake-clock testing without changing production lease duration, renewal interval, epoch fencing, or stale-result rules.

## Dispatch read-amplification reduction

The normal paged dispatch path already reads a complete durable candidate. That snapshot is now reused for the first worker-lease CAS attempt.

Conceptually:

```text
dispatch page candidate
    -> supervisor(candidate snapshot)
    -> first lease CAS using candidate revision
```

On an uncontended path, no additional point read is required before that first CAS.

If the CAS loses:

```text
CAS rejected
    -> use classified current durable record when supplied
       or reload durable truth
    -> reevaluate legality
    -> retry only when appropriate
```

The candidate remains an optimization hint. MongoDB CAS remains authoritative.

Invalid admission arguments continue to fail before datastore access.

## Classified CAS outcomes

Invocation persistence distinguishes three internal outcomes:

```text
Applied
RevisionConflict
AuthorityPredicateRejected
```

Behavior:

```text
Applied
    -> transition succeeded

RevisionConflict
    -> reevaluate current durable state
    -> retry when appropriate

AuthorityPredicateRejected
    -> stop retrying immediately
```

MongoDB server-side predicates, including authoritative time checks such as `$$NOW`, remain intact.

When the classified CAS result already contains the current durable record, the journal reuses it instead of performing a redundant `GetAsync`.

Legacy stores that do not provide classified outcomes retain conservative conflict/reload behavior.

## Continuation fairness without durable fairness writes

Continuation reconciliation no longer needs to mutate durable records only to rotate scanner fairness.

The MongoDB path uses non-authoritative keyset paging:

```text
(UpdatedAtUtc, OperationId)
    -> page
    -> transient cursor
    -> next page
    -> wrap at end
```

The cursor is process-local and disposable. Losing it during process restart only restarts scanning; it does not lose authoritative state.

Durable continuation transitions remain CAS protected.

## Shared stdio protocol, separate physical lifecycles

Trusted-process and OCI-container workers share one stdio protocol/session implementation for:

```text
request framing
ready handshake
heartbeat processing
result frame parsing
EOF rules
frame-size limits
timeout/deadline handling
bounded protocol diagnostics
```

Physical lifecycle/security responsibilities remain separate.

Trusted process retains:

```text
executable validation
process launch
process cleanup
trusted-process rules
```

Container execution retains:

```text
OCI launch
engine attestation
ownership labels
force removal
cleanup/quarantine
```

The shared stdio session does not become process/container lifecycle authority.

## MongoDB attribution

Fine-grained MongoDB attribution is available for Invocation:

```text
Mongo.Invocation.Get
Mongo.Invocation.PrepareInsert
Mongo.Invocation.DispatchScan
Mongo.Invocation.ContinuationScan
Mongo.Invocation.CAS
Mongo.Invocation.ResultAcceptance
```

and durable MCP effect evidence:

```text
Mongo.McpEffect.Get
Mongo.McpEffect.PrepareInsert
Mongo.McpEffect.ReconcileScan
Mongo.McpEffect.CAS
```

Result acceptance is separated from generic CAS attribution so performance investigations can distinguish ordinary transitions from terminal-result writes.

Low-level `ReplaceOne` plumbing is shared by composition where behavior is truly identical. Invocation Journal and MCP Effect Evidence state machines remain separate authorities.

## Production dispatch query strategy

Measured experiments showed that one global dispatch index cannot efficiently serve both Prepared work and expired leased work.

The production strategy therefore splits discovery into two ordered branches.

### Prepared branch

```text
status = Prepared
ORDER BY updatedAt, _id
LIMIT pageSize
```

Index:

```text
ix_durable_invocation_dispatch_prepared_v2

controlPlaneId
tenantId
tenantGroupId
language
status
updatedAt
_id
```

### Expired-leased branch

```text
status = Leased
leaseExpiresAt <= authoritative query time
ORDER BY updatedAt, _id
LIMIT pageSize
```

It retains the expiry-oriented index:

```text
ix_durable_invocation_dispatch

controlPlaneId
tenantId
tenantGroupId
language
status
leaseExpiresAt
updatedAt
```

The two sorted pages are merged by `(UpdatedAtUtc, OperationId)`, deduplicated by `OperationId`, and truncated to the requested page size.

Discovery does not replace lease/epoch/CAS authority.

## Continuation query index

Continuation keyset paging uses:

```text
ix_durable_invocation_continuation_v2

controlPlaneId
tenantId
tenantGroupId
continuationStatus
status
updatedAt
_id
```

This supports the status/continuation filters while allowing MongoDB to merge ordered index ranges instead of performing one large blocking sort.

The previous continuation index may remain temporarily during migration; the v2 index is the selected production query index.

## Measured query evidence

Controlled 10,000-document experiments were used to choose the production strategy.

### Initial baseline

A representative baseline returned 100 records while examining approximately:

```text
dispatch      5,000 keys / 5,000 docs + blocking SORT
continuation  5,000 keys / 5,000 docs + blocking SORT
```

### Dispatch candidate experiments

A global sort-first dispatch index was excellent for Prepared-heavy/mixed data but regressed a lease-heavy population:

```text
live-lease-heavy
current expiry-oriented index:
    1,000 keys / 1,000 docs / ~8 ms

global sort-first:
    9,100 keys / 100 docs / ~35 ms
```

That result rejected a global index replacement.

### Hybrid dispatch experiment

The split strategy preserved first-page semantic equivalence in all tested distributions.

Representative results:

```text
prepared dense
current: 10,000 keys / 10,000 docs
hybrid:     100 keys /    100 docs

live-lease-heavy
current: 1,000 keys / 1,000 docs
hybrid:  1,000 keys / 1,000 docs

realistic mix
current: 7,000 keys / 7,000 docs
hybrid:  3,100 keys / 3,100 docs
```

### Final production-plan verification

The final production `explain("executionStats")` run verified the expected hints:

```text
Prepared       -> ix_durable_invocation_dispatch_prepared_v2
Expired Leased -> ix_durable_invocation_dispatch
Continuation   -> ix_durable_invocation_continuation_v2
```

On that run:

```text
Prepared
    100 returned
    100 keys examined
    100 docs examined
    LIMIT -> FETCH -> IXSCAN

Expired Leased probe
    2,500 expired + 2,500 live
    100 returned
    2,500 keys examined
    2,500 docs examined
    expiry-oriented index retained

Continuation
    100 returned
    100 keys examined
    100 docs examined
    LIMIT -> FETCH -> SORT_MERGE -> IXSCAN...
```

The reported millisecond values are single controlled server measurements, not throughput guarantees. The important proof is the selected plan shape, index identity, examined-key/document counts, and preserved semantic ordering.

## Validation boundary

Targeted Invocation correctness/regression tests and the controlled MongoDB query-plan experiments are green on the current branch.

These targeted results do not replace the historical distributed/runtime matrices. Before branch closure, the established legacy/new runtime matrices are being rerun selectively/full where appropriate to detect cross-subsystem regressions.

Do not reinterpret historical `36/36`, Docker `37/37`, or KubernetesPool `3/3` evidence as a fresh Invocation-branch rerun unless that campaign was actually executed.

## Preserved invariants

The hardening work does not change the following authorities in principle:

```text
immutable invocation identity
lease ownership
epoch / fencing semantics
MongoDB CAS
stale-result rejection
result acceptance
durable continuation
immutable publication / run pinning
DAG scheduling
runtime recovery authority
```

## Related documentation

- [Hosted Multilanguage Execution](hosted-multilanguage-execution.md)
- [Hosted Worker Isolation](hosted-worker-isolation.md)
- [Durable MCP Effect Evidence](durable-mcp-effect-evidence.md)
- [MongoDB Performance Diagnostics](mongodb-performance-diagnostics.md)
- [Policy-Driven Execution](policy-driven-execution.md)
- [Testing Strategy](testing-strategy.md)
- [Architecture Quick Start](architecture-quick-start.md)
