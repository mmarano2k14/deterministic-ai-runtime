# Durable MCP Effect Evidence Validation

**Evidence date:** 2026-09-16  
**Boundary:** outbound MCP durable effect identity, dispatch fencing, result evidence, outcome classification, explicit reconciliation, restart behavior, and MongoDB persistence.

## Purpose

Validation separates three different proof layers. They must not be collapsed into one claim.

```text
deterministic durable-effect tests
        |
        | prove runtime state/authority behavior
        v
real outbound MCP boundary tests
        |
        | prove the selected transport crosses the marker at tools/call
        v
opt-in MongoDB integration tests
        |
        | prove persistence and journal reconstruction against real MongoDB
        v
bounded durable-effect claim
```

A skipped infrastructure test is not a passing test.

## 1. Deterministic durable-effect suite

Primary command:

```cmd
dotnet test .\implementations\dotnet\Tests\Multiplexed.AI.Tests\Multiplexed.AI.Tests.csproj --filter "FullyQualifiedName~Runtime.Invocation.McpEffects.Durable&Category!=MongoIntegration"
```

This layer covers the runtime-side contract, including:

- immutable `EffectId` / `RequestDigest` intent preparation;
- same-effect intent conflict rejection;
- `Prepared -> Dispatching` CAS before physical invocation;
- concurrent dispatch with only one physical-call winner;
- `Completed` replay with zero second business call;
- expired deadlines with no dispatch authority;
- conservative fail-closed behavior for `Dispatching` and `Uncertain`;
- pre-boundary `NotSent` classification;
- post-boundary uncertainty classification;
- explicit reconciliation outcomes;
- unsupported and ambiguous reconciliation-provider handling;
- crash after remote result but before authoritative completion persistence;
- ambiguous completion-write acknowledgement;
- restart after durable dispatch authority but before physical call;
- concurrent reconciliation convergence through revision CAS;
- tenant and tenant-group scope isolation.

This suite can prove the runtime logic without requiring a real external MCP provider.

## 2. Existing outbound MCP compatibility and boundary suite

Command:

```cmd
dotnet test .\implementations\dotnet\Tests\Multiplexed.AI.Tests\Multiplexed.AI.Tests.csproj --filter "FullyQualifiedName~Runtime.Invocation.OutboundMcp"
```

This layer protects the historical direct outbound path and validates the selected real outbound transport boundary.

The boundary-specific tests demonstrate that:

- exact-target rejection happens before the possibly-sent marker;
- a real loopback `tools/call` crosses the marker immediately before the selected MCP client call.

A loopback transport test is stronger than a pure mock for boundary placement, but it is not proof of provider-specific idempotency, irreversible remote side effects, or every network failure schedule.

## 3. Opt-in MongoDB persistence suite

MongoDB tests are intentionally opt-in. They validate durable records and reconstruction against a configured test MongoDB instance.

### PowerShell

```powershell
$env:MULTIPLEXED_TEST_MONGO_MCP_EFFECT_CONNECTION_STRING = "mongodb://localhost:27017"

dotnet test .\implementations\dotnet\Tests\Multiplexed.AI.Tests\Multiplexed.AI.Tests.csproj --filter "FullyQualifiedName~Runtime.Invocation.McpEffects.Durable&Category=MongoIntegration"
```

### cmd.exe

```cmd
set "MULTIPLEXED_TEST_MONGO_MCP_EFFECT_CONNECTION_STRING=mongodb://localhost:27017"

dotnet test .\implementations\dotnet\Tests\Multiplexed.AI.Tests\Multiplexed.AI.Tests.csproj --filter "FullyQualifiedName~Runtime.Invocation.McpEffects.Durable&Category=MongoIntegration"
```

Do not use `set NAME=value` as a PowerShell environment assignment. In PowerShell, use `$env:NAME = "value"`.

If the environment variable is absent, the integration tests report `SKIP`. A test assembly reporting overall success while all selected Mongo tests are skipped is **not** MongoDB validation evidence.

The MongoDB layer covers scenarios such as:

- immutable-intent conflict persistence;
- concurrent preparation converging on one effect identity;
- `NotSent` roundtrip;
- reconciliation candidate discovery;
- `Dispatching` surviving store/journal reconstruction and blocking re-emission;
- `Completed` surviving reconstruction and remaining locally replayable.

## Evidence matrix

| Area | Proof source | Bounded interpretation |
|---|---|---|
| Logical identity and immutable intent | deterministic suite | Runtime detects changed intent for one logical effect. |
| Durable dispatch authority | deterministic suite | Only the `Prepared -> Dispatching` CAS winner may call the physical transport. |
| Completed replay | deterministic/restart tests | A confirmed stored response can be reused without another business call. |
| Pre-call classification | deterministic + outbound boundary tests | Selected transport can prove some failures happened before `tools/call`. |
| Possibly-sent failure | deterministic suite | Ambiguous post-boundary failures fail closed rather than being blindly retried. |
| Explicit reconciliation | deterministic suite | Evidence can converge to `Completed`, `NotSent`, or remain unknown without business-call reissue. |
| Restart behavior | deterministic suite; Mongo when configured | Reconstructed runtime preserves blocking/replay semantics. |
| Mongo durability | Mongo integration suite only | Counts only when the suite actually runs, not when it is skipped. |
| Provider-specific remote truth | Not established generically | Requires an explicit real provider/tool reconciliation implementation and its own evidence. |

## Bounded claims

The current evidence does not establish:

- generic exactly-once execution in an external provider;
- automatic safe redelivery of `NotSent` effects;
- automatic resolution of `Dispatching` or `Uncertain` effects;
- a background reconciliation scanner;
- provider-specific idempotency-key behavior;
- provider-specific compensation workflows;
- correctness across every external MCP server, network stack, proxy, or failure schedule.

The safety rule is intentionally conservative:

> If the runtime cannot prove the outcome, it preserves uncertainty and refuses blind re-emission.

## Related documentation

- [Durable MCP Effect Evidence](durable-mcp-effect-evidence.md)
- [Hosted Multilanguage Execution](hosted-multilanguage-execution.md)
- [Hosted Multilanguage Validation](hosted-multilanguage-validation.md)
- [MCP Server Control Plane](mcp-server-control-plane.md)
- [Replay and Audit](replay-and-audit.md)
- [Testing Strategy](testing-strategy.md)
