# Durable MCP Effect Evidence

**Status:** Implemented server-side as an opt-in durable boundary around outbound MCP business effects.

## Purpose

Outbound MCP can cross a business side-effect boundary that the runtime cannot atomically commit together with its own database state. A connection loss can therefore leave an ambiguous question:

> Did the external tool apply the effect even though the runtime did not receive or persist the final response?

Durable MCP effect evidence addresses that ambiguity without creating another scheduler, DAG engine, retry engine, or recovery authority. It records the logical external effect, durably fences physical dispatch, preserves confirmed outcomes, and exposes unresolved effects for explicit reconciliation.

The existing runtime remains authoritative for execution ownership, DAG transitions, retry policy, recovery, publication selection, leases/epochs, and continuation.

## Inbound MCP versus outbound MCP

The MCP server/control plane and outbound MCP tool execution are separate boundaries.

```text
Inbound MCP control plane
    -> submit / inspect / replay / pause / resume / diagnostics

Outbound MCP effect
    existing DAG step
        -> authorized server-owned connection/tool
        -> durable effect evidence boundary
        -> physical tools/call
```

This document covers the second path.

## Logical identity

Three identities have distinct roles:

| Identity | Meaning |
|---|---|
| `EffectId` | Stable logical external action derived from tenant, tenant group, execution, and call site. |
| `RequestDigest` | Canonical frozen intent: trusted invocation context, exact connection revision, tool, and resolved arguments. |
| `RequestId` | Correlation for one physical attempt. |

A replacement worker, runtime claim, deadline, or request id does not create a new logical effect. Reusing one `EffectId` with a different frozen intent is an integrity conflict, not an overwrite.

The durable intent does not persist endpoint URIs, credentials, secret headers, worker ids, claim tokens, or deadlines.

## Durable evidence state machine

```text
Prepared
   |
   | durable revision CAS before tools/call
   v
Dispatching
   | \
   |  \ business boundary may have been crossed
   |   v
   | Uncertain
   |   | \
   |   |  \ explicit provider/tool reconciliation
   |   |   +-> Completed
   |   |   +-> NotSent
   |   |   +-> remain Uncertain
   |   |
   |   +------------------------> Completed
   |
   +---- confirmed pre-tools/call failure -----------> NotSent
```

Legal transitions are intentionally narrow:

```text
Prepared    -> Dispatching
Dispatching -> Completed
Dispatching -> Uncertain
Dispatching -> NotSent
Uncertain   -> Completed
Uncertain   -> NotSent
```

`Completed` and `NotSent` are resolved evidence states. `Dispatching` and `Uncertain` are fail-closed states.

A tool-reported MCP `isError` response is still a known completed remote outcome. It is different from transport uncertainty.

## Durable dispatch fence

When `AiMcpEffectEvidenceJournal` is installed, the existing outbound MCP transport is decorated by `AiDurableMcpToolTransport`.

```text
schema-2 MCP request
        |
        v
prepare immutable intent
        |
        v
CAS Prepared -> Dispatching
        |
        | only the CAS winner owns physical emission authority
        v
existing physical MCP transport
        |
        v
normalized result
        |
        v
CAS Dispatching -> Completed
        |
        v
return through the existing DAG path
```

The `Dispatching` transition is durable before the physical transport is entered. Losing that CAS does not grant network emission authority.

Existing evidence changes later behavior:

| Existing state | Behavior |
|---|---|
| `Prepared` | Compete for the one durable dispatch fence. |
| `Completed` | Replay the stored result locally; do not call the business tool again. |
| `Dispatching` | Fail closed; do not blindly re-emit. |
| `Uncertain` | Fail closed; explicit reconciliation only. |
| `NotSent` | Preserve confirmed non-emission evidence; do not treat it as automatic retry permission. |

A local replay may use the current physical `RequestId` for correlation while the durable record retains the original attempt and confirmed response.

## Physical outcome classification

The selected real outbound transport exposes a one-way boundary immediately before `CallToolAsync`.

Operations such as exact target resolution, client/session preparation, and argument preparation occur before that boundary.

```text
classified failure before tools/call boundary
    -> NotSent

failure after boundary may have been crossed
    -> Uncertain

transport without boundary classification
    -> Uncertain conservatively
```

If cancellation or evidence persistence prevents recording a terminal classification, the durable record can remain `Dispatching`. That state is still fail-closed and reconciliation-visible.

An already-expired request cannot acquire physical dispatch authority and remains `Prepared`.

## Confirmed result acceptance

A remote result is not returned through the normal DAG path until `Completed` evidence has been durably accepted.

This closes an important replay window:

```text
remote response received
        |
        +-- Completed durable commit succeeds
        |       -> result can be returned
        |       -> later invocation replays locally
        |
        +-- completion persistence is not authoritative
                -> fail closed
                -> no blind second tools/call
```

This is not a generic distributed transaction with the remote provider. It is a local durable acceptance boundary.

## Explicit reconciliation

`AiMcpEffectReconciliationService` resolves stale or uncertain evidence through explicit `IAiMcpEffectReconciliationProvider` implementations.

Exactly one provider must declare support for the frozen connection/tool intent. Zero matching providers fail as unsupported; multiple matching providers are ambiguous and fail without mutating evidence.

A provider may return only:

| Outcome | Meaning |
|---|---|
| `Completed` | Provider/query evidence identifies the original result; persist the normalized response. |
| `NotSent` | Provider/query evidence proves the selected attempt did not apply the business call. |
| `Unknown` | No authoritative proof; stale `Dispatching` becomes or remains unresolved `Uncertain`. |

Reconciliation is a read/query boundary. It must not issue the original business `tools/call`, does not mutate DAG state, does not schedule retries, and does not create a second recovery loop.

There is no automatic reconciliation scanner in this capability. Candidate discovery and reconciliation execution remain separate responsibilities.

## Tenant scope and storage

The durable address includes `TenantId`, `TenantGroupId`, and `EffectId`. Wrong-scope lookup or reconciliation cannot observe or mutate another tenant scope.

`MongoAiMcpEffectEvidenceStore` provides the durable store with:

- unique `(TenantId, TenantGroupId, EffectId)` identity;
- immutable-intent conflict detection;
- revision CAS for state changes;
- majority read concern;
- majority journaled writes;
- discovery of `Uncertain` and sufficiently old `Dispatching` candidates;
- no TTL deletion of effect evidence;
- no business tool invocation;
- no DAG mutation.

The durable feature remains explicit host configuration. If the journal is not installed, the historical direct outbound MCP path remains unchanged.

When the durable decorator is active, historical schema-1 effectless envelopes are refused rather than executed outside the fence.

## Recovery behavior

Restart does not create new emission authority.

Important crash windows behave as follows:

```text
remote effect may have happened
+ completion write fails before commit
    -> durable state remains Dispatching
    -> reconstructed runtime does not re-emit
    -> explicit reconciliation required

Completed commits
+ persistence acknowledgement is lost
    -> reconstructed runtime reads Completed
    -> result replays locally
    -> no second business call

Prepared -> Dispatching commits
+ process dies before tools/call
    -> reconstructed runtime sees Dispatching
    -> no automatic send
    -> reconciliation may later prove NotSent
```

Concurrent reconcilers converge through revision CAS on one authoritative terminal record.

## What this guarantees

Within the implemented server boundary, durable MCP effect evidence provides:

- stable logical effect identity;
- immutable intent conflict detection;
- one durable dispatch fence before the business call;
- local replay of `Completed` without a second business call;
- conservative `NotSent` versus possibly-sent classification where the transport exposes the boundary;
- fail-closed `Dispatching` and `Uncertain` handling;
- explicit reconciliation without reissuing the original business call;
- restart-safe blocking/replay semantics;
- tenant-scoped evidence access.

## What this does not guarantee

This capability does **not** claim generic exactly-once external side effects.

Local durable evidence cannot prove what an arbitrary external system committed after a connection loss unless that provider exposes usable evidence. Stronger business recovery may require one or more of:

- a provider-recognized idempotency key;
- a provider-specific query/reconciliation contract;
- a compensating business action;
- explicit human/operator resolution.

`NotSent`, `Dispatching`, and `Uncertain` do not grant automatic redelivery authority. The MCP evidence layer does not own retry scheduling or DAG recovery decisions.

## SDK boundary

The public SDK contract/server boundary is now implemented without depending on engine DLLs or internal CLR contracts. Durable MCP effect state remains server-owned and is not promoted into client-side retry authority: external client libraries must preserve the same rule that uncertain effects are not blindly re-emitted.

## Related documentation

- [Durable MCP Effect Evidence Validation](durable-mcp-effect-evidence-validation.md)
- [Hosted Multilanguage Execution](hosted-multilanguage-execution.md)
- [MCP Server Control Plane](mcp-server-control-plane.md)
- [Replay and Audit](replay-and-audit.md)
- [Testing Strategy](testing-strategy.md)
- [Public SDK Boundary](public-sdk-boundary.md)
- [Architecture Overview](architecture-overview.md)
