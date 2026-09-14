# Hosted Multilanguage Validation

**Scope:** validation evidence for the server-side execution foundation described in [Hosted Multilanguage Execution](hosted-multilanguage-execution.md).

## Evidence status

The result counts below are derived from the individual `UnitTestResult` entries in the supplied TRX artifacts. They are not source-code test inventories. The supplied result files contain no failed individual tests.

The HTTP and gRPC ProcessHostPool continuation scenarios have a reported passing result. Their final TRX/log artifact is not included in this evidence set, so this record does not assign inspected per-scenario counts, durations, or a new crash-volume total to that report.

This is a consolidation of existing evidence, not an additional test execution. The selections overlap, and the broader regression run predates the TypeScript compatibility update. Their counts must not be added into a single unique-test total or described as one full-suite run on a single revision.

## Inspected result artifacts

| Artifact | Individual results | Passed | Failed | Not executed |
|---|---:|---:|---:|---:|
| `sdk-closure-durability.trx` | 152 | 152 | 0 | 0 |
| `sdk-closure-boundaries.trx` | 185 | 185 | 0 | 0 |
| `sdk-closure-regressions.trx` | 1,152 | 1,098 | 0 | 54 |
| `sdk-closure-node-compatibility.trx` | 76 | 76 | 0 | 0 |
| `sdk-closure-python.trx` | 49 | 49 | 0 | 0 |
| `sdk-closure-infrastructure.trx` | 20 | 20 | 0 | 0 |

The broader regression artifact contains 54 individual `NotExecuted` results even though its summary counter reports `notExecuted="0"`. Individual outcomes and skip messages take precedence. The skipped groups were Python, TypeScript, and opt-in MongoDB/Redis tests. Later dedicated artifacts execute those paths; they do not retroactively change the original artifact. Parameterized tests can expand to additional cases once their runtime prerequisites are enabled.

The TRX files are evidence identifiers, not repository paths guaranteed to exist in a checkout. Raw test output and local machine paths are not duplicated into the public documentation.

## Durability and continuation

The 152-result selection contains 24 lease tests, 31 DAG execution/continuation tests, 13 cold-restoration cases, 23 supervision tests, and 61 MCP-effect tests.

| Guarantee | Evidence boundary |
|---|---|
| Result before `Park` | The recorded result remains pending until the persisted wait or exact application permits convergence. |
| Replaced assignment | Lease/epoch tests reject obsolete authority rather than accepting a late worker as current. |
| Duplicate continuation | Repeated delivery does not prepare a new logical operation or apply a new result. |
| Interruption after accepted result | Rebuilt services restore serialized publication, journal, and execution state, then apply the recorded result without launching a replacement function. |
| Exact application acknowledgement | A recorded result or scheduled continuation is insufficient; the exact receipt and terminal parent are required. |

Twelve cold-restoration cases use controlled transport across language bindings and success/failure/continuation states. A thirteenth produces the initial result with a real hosted .NET process. After reconstruction, preparation and transport calls are configured to fail if reused.

These tests prove restoration from serialized state and interaction with the existing local DAG. They are not an operating-system kill of the runtime host, a database restart, or a complete distributed-queue recovery scenario. MCP-effect identity tests in this selection do not make MCP effects durable in the custom-function journal.

## Authorization, execution requirements, and paths

The 185-result selection covers:

| Group | Passed |
|---|---:|
| Outbound MCP configuration, real transport, and RBAC path | 32 |
| MCP logical-effect identity, adapter, and transport validation | 61 |
| Environment descriptor validation, publication, and policy projection | 48 |
| Provider admission and real protocol-process exchange | 22 |
| Launch-file, path, and symbolic-link validation | 22 |

All three symbolic-link cases ran and passed. The protocol-process case validates the explicitly trusted process profile; it is not execution of tenant functions in all three languages.

The tests establish refusal of requirements that the process provider cannot enforce, consistent server-side environment binding, checked launch paths, and authorization before the outgoing tool call. They do not establish a container sandbox, enforced network denial, a fully sealed filesystem, or a durable remote-effect replay implementation.

## Real language execution

| Dedicated selection | Profile/configuration | Real execution | Publication/journal/DAG | Additional integration |
|---|---:|---:|---:|---|
| TypeScript / Node.js | 38 | 23 | 4 | 10 compiler/import/integrity cases and 1 real hosted `Concurrency` policy; 76 passed in total. |
| Python | 21 | 23 | 4 | 1 real hosted `Concurrency` policy; 49 passed in total. |

The TypeScript selection validates the bundled compiler backend, including supported transformations/imports, exact environment binding, compiler-integrity rejection, and the existing result path. The Python selection validates actual synchronous/asynchronous functions, explicit source dependencies, protocol behavior, and pinned publication execution.

The broader regression artifact also includes the 31 .NET profile, process, and publication/DAG cases. Real process execution is distinct from profile-only acceptance of a version string. The dedicated TRX artifacts do not themselves record the selected interpreter's exact version; they must not be used to certify an unrecorded version or every future runtime release.

Standalone Python/Node test-runner results are not part of these .NET TRX counts. Published-DAG language tests use real language processes with controlled in-memory stores; database coverage is recorded separately below.

## MongoDB and Redis integration

All 20 formerly skipped infrastructure cases have passing results in the dedicated infrastructure artifact.

| Group | Passed | Boundary |
|---|---:|---|
| Durable invocation MongoDB integration | 8 | Conditional assignment/result writes, uniqueness, duplicate/conflicting completion, and lease-sensitive database-time checks. |
| MongoDB worker dispatch | 4 | Candidate lookup, partitioning, and assignment against the real store. |
| MongoDB publication and run pinning | 4 | Immutable material, conflicting publication/admission, and pinned run behavior. |
| Redis DAG failure transitions | 4 | Full custom result/receipt persistence, retry-budget preservation, native compatibility, and stale-claim rejection. |

These cases exercise real MongoDB storage and Redis transitions. They do not restart either service or prove survival of every deployment-level failure.

## Host and native-runtime regression boundary

The existing HTTP and gRPC ProcessHostPool `ContinuationConsume` scenarios were reported passing after the infrastructure selection. Their purpose remains startup, runtime-process loss, and recursive native Child DAG convergence through the existing harness. No harness change is part of the documentation update.

The broad regression artifact also contains passing policy discovery/startup tests. Discovery compatibility matters because contextual adapters must not enter native singleton registration.

Reported host success is recorded separately from inspected TRX results. It does not promote nested published custom Child DAG execution, hostile-code containment, or durable MCP external effects into supported capabilities. Historical runtime-pool evidence remains documented in [Runtime Pool Production Validation](runtime-pool-production-validation.md) and is not recomputed here.

## Artifact integrity

SHA-256 identifies the exact supplied result files used for this summary.

| Artifact | SHA-256 |
|---|---|
| `sdk-closure-boundaries.trx` | `d7b6194805a9fe0920986955929f1ed07f9064808a78a68caa648145ed8e5735` |
| `sdk-closure-durability.trx` | `15833f64ed10e19a67df8d26c959a977feb5298dfec3c197cd1058d705f97f6b` |
| `sdk-closure-infrastructure.trx` | `2bca41a9bd3beaac7a84a09d8fdd33d62434913598c89c3ae977e4d0f205c871` |
| `sdk-closure-node-compatibility.trx` | `4e9066596c959c6711ed555e79405d03b98188156383627bfca47091c95646a5` |
| `sdk-closure-python.trx` | `a7602fe7c6d51513dc1100fad7748fbdd4278e68203ef1b352a610a173c07db9` |
| `sdk-closure-regressions.trx` | `c45a748ab7520eaed5aad10c877f9f86abcdb12ecf4aa858191ab03c48b93b57` |

## Interpretation limits

The evidence supports the stated implementation boundaries, not a completed public SDK product. No new public publication endpoint, SDK package, sandbox, package installer, or external-effect ledger is implied by these results.

Dedicated reruns supplement the earlier regression artifact without changing its recorded outcomes. The available evidence does not certify every runtime version, deployment topology, or failure mode.

## Related documents

- [Hosted Multilanguage Execution](hosted-multilanguage-execution.md)
- [Testing Strategy](testing-strategy.md)
- [MCP Production Runtime Scenario Framework](mcp-production-runtime-scenario-framework.md)
- [Runtime Pool Production Validation](runtime-pool-production-validation.md)
- [Replay and Audit](replay-and-audit.md)
