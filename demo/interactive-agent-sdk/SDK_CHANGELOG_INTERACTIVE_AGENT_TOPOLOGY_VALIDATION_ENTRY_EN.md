---

## Interactive Agent SDK Topology Validation - Canonical Scenario Contract

## Scope

Frozen the canonical interactive-agent scenario and machine-readable acceptance contract used by the pre-main topology validation campaign.

This work introduces validation artifacts only. It does not change public SDK operations, runtime execution semantics, DAG behavior, Child DAG behavior, RBAC authority, recovery authority, or deterministic replay semantics.

---

## Canonical Scenario

Added a single semantic scenario definition shared by the .NET, TypeScript, and Python validation paths.

The root flow is:

```text
plan
    -> delegate-analysis
    -> await-review
    -> final-answer
    -> publish-result
```

The delegated Child DAG is:

```text
child-analysis
    -> publish-child-result
```

The scenario preserves the existing public authentication, access-context, immutable publication, durable execution, Child DAG, human-input, final-result, and replay paths.

---

## Acceptance Contract

Added a machine-readable evidence schema requiring:

```text
publication accepted
human-review boundary reached
human input accepted
same ExecutionId after human input
terminal status Completed
all expected root steps Completed
public result available
OpenAI provider/model metadata present
response payload present
replay succeeded
replay deterministic
```

OpenAI response-text equality is explicitly excluded from deterministic assertions.

---

## Public Boundary

The topology runner is required to use the public SDK boundary only.

Validation shortcuts through matrix endpoints, direct Redis/Mongo mutation, internal DAG stores, internal controller APIs, or direct `IAiPublicSdkBoundary` invocation are excluded from the acceptance contract.

---
