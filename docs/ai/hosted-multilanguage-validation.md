# Hosted Multilanguage Validation

**Scope:** validation evidence for the server-side execution foundation described in [Hosted Multilanguage Execution](hosted-multilanguage-execution.md).

## Evidence status

The result counts below are derived from the individual `UnitTestResult` entries in the supplied TRX artifacts. They are not source-code test inventories. The supplied result files contain no failed individual tests.

The HTTP and gRPC ProcessHostPool continuation scenarios have a reported passing result. Their final TRX/log artifact is not included in this evidence set, so this record does not assign inspected per-scenario counts, durations, or a new crash-volume total to that report.

This is a consolidation of the hosted-language/publication evidence, not an additional test execution. The selections overlap, and the broader regression run predates the TypeScript compatibility update. Their counts must not be added into a single unique-test total or described as one full-suite run on a single revision. Hosted worker container isolation is validated separately in [Hosted Worker Isolation Validation](hosted-worker-isolation-validation.md).

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

These tests prove restoration from serialized state and interaction with the existing local DAG. They are not an operating-system kill of the runtime host, a database restart, or a complete distributed-queue recovery scenario. The MCP-effect identity tests in this historical selection do not themselves prove durable external-effect fencing or reconciliation; those capabilities are validated separately in [Durable MCP Effect Evidence Validation](durable-mcp-effect-evidence-validation.md).

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

These earlier trusted-process tests establish refusal of requirements that the process provider cannot enforce, consistent server-side environment binding, checked launch paths, and authorization before the outgoing tool call. They do not by themselves establish a container sandbox, enforced network denial, a fully sealed filesystem, or durable remote-effect replay. Container guarantees are covered by the isolated-provider evidence, while durable MCP effect fencing/replay/reconciliation is covered by the separate durable-effect evidence suite.

## Real language execution

| Dedicated selection | Profile/configuration | Real execution | Publication/journal/DAG | Additional integration |
|---|---:|---:|---:|---|
| TypeScript / Node.js | 38 | 23 | 4 | 10 compiler/import/integrity cases and 1 real hosted `Concurrency` policy; 76 passed in total. |
| Python | 21 | 23 | 4 | 1 real hosted `Concurrency` policy; 49 passed in total. |

The TypeScript selection validates the bundled compiler backend, including supported transformations/imports, exact environment binding, compiler-integrity rejection, and the existing result path. The Python selection validates actual synchronous/asynchronous functions, explicit source dependencies, protocol behavior, and pinned publication execution.

The broader regression artifact also includes the 31 .NET profile, process, and publication/DAG cases. Real process execution is distinct from profile-only acceptance of a version string. The dedicated TRX artifacts do not themselves record the selected interpreter's exact version; they must not be used to certify an unrecorded version or every future runtime release.

Standalone Python/Node test-runner results are not part of these .NET TRX counts. Published-DAG language tests use real language processes with controlled in-memory stores; database coverage is recorded separately below.

## Hosted custom policy family branch evidence

Hosted custom policy-family expansion is tracked separately from the earlier closure TRX artifacts. The implemented server capability matrix is finite:

| Family | Hosted status | Contract | Evidence status in this documentation update |
|---|---|---|---|
| `Concurrency` | Hosted | `concurrency/v1` | Existing hosted-policy evidence is part of the earlier language foundation. |
| `Retry` | Hosted | `retry/v1` | Targeted implementation tests were reported passing in the target .NET environment. |
| `Delegation` | Hosted | `delegation/v1` | Targeted implementation tests were reported passing in the target .NET environment. |
| `Retention` | Native-only | None | Explicitly not advertised as hosted. |
| `Timeout`, `CircuitBreaker`, `RateLimit`, `Validation`, `Routing` | No independent hosted checkpoint | None | Explicitly not advertised as hosted capabilities. |

The final cross-family closure suite defines five cases: capability-matrix closure, contract-identity separation, and real Python, TypeScript, and .NET process execution of all three hosted families. Python and TypeScript retain explicit process-test prerequisites, so a skipped language case is not closure evidence. A final all-language passing result is not claimed by this document until that closure run is recorded.

During the .NET real-process rerun, a validation-fixture environment issue was isolated: the test profile intentionally clears inherited environment variables but originally forwarded `SystemRoot` without `TEMP`/`TMP`, causing `Path.GetTempPath()` in the hosted .NET worker to resolve under `C:\Windows` and fail workspace creation before the required `ready` frame. The test profile now forwards the host temporary directory explicitly. The legacy direct .NET worker test was reported passing after that correction. This correction changes the validation fixture environment, not policy authority, worker protocol semantics, or DAG behavior.

## Deterministic dependency packaging branch evidence

Deterministic dependency packaging is tracked separately from the earlier hosted-language closure artifacts. Three finite package kinds are implemented on top of the existing immutable publication and environment identity:

| Package kind | Runtime status | Supported boundary | Evidence status in this documentation update |
|---|---|---|---|
| `PythonWheelBundle` | Hosted | Pure-Python wheel, exact manifest/hash/import roots, no native extension or namespace-package expansion | Targeted .NET tests were reported passing; the standalone Python worker suite also passed 53/53 during preparation. |
| `NodeLockedBundle` | Hosted | Closed TypeScript source bundle with exact package/version/entry point/file hashes; no package-manager or registry resolution | Targeted .NET tests were reported passing; the standalone Node suite also passed 45/45 during preparation. |
| `DotNetAssemblyClosure` | Hosted | Precompiled managed DLL closure with exact hashes and CLR assembly identity/version; no NuGet/MSBuild/native resolution | Targeted .NET worker/publication tests were reported passing in the target environment. |

The packaging model does not introduce another environment identity. Package manifests and captured bytes participate in the existing immutable publication/environment material; the canonical environment-document SHA-256 retains its existing meaning and remains distinct from host-runtime and OCI image-manifest digests.

The final cross-language compatibility closure defines seven cases: final capability/legacy compatibility, one republish-and-pin proof for each supported package kind using the real hosted worker path, and one missing-pinned-material refusal for each package kind. A final all-language closure result is not claimed by this document until that closure run is recorded. Python and TypeScript process prerequisites must execute rather than be skipped for complete branch closure.

The supported packaging boundary is deliberately narrower than a general package manager. No `pip install`, PyPI lookup, npm/yarn/pnpm install, `npx`, Node registry resolution, NuGet restore, tenant compilation, native extension/add-on discovery, or mutable `latest` lookup occurs in the runtime execution path.

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

Reported host success is recorded separately from inspected TRX results. Historical runtime-pool evidence remains documented in [Runtime Pool Production Validation](runtime-pool-production-validation.md) and is not recomputed here. That native host evidence is not used as proof for published custom Child DAG execution, hostile-code containment, or durable MCP external effects; the latter has its own dedicated validation boundary in [Durable MCP Effect Evidence Validation](durable-mcp-effect-evidence-validation.md).

## Published custom Child DAG branch evidence

Published custom Child DAG support is validated through a separate bounded branch-level suite rather than being inferred from the earlier hosted-execution TRX artifacts above. The branch adds four finite proof groups:

| Proof group | Boundary |
|---|---|
| Nested publication identity and compilation | Canonical nested `DefinitionPath`, child-local language resolution, exact code attachment, schema compatibility, and root/nested resolver isolation. |
| Immutable child execution binding | `ChildExecutionId` is bound before dispatch to the original publication and exact nested definition; nested target/material resolution reuses the existing hosted path. |
| Durability and recovery | Rehydration, republication pinning, lost binding-write acknowledgement, missing/changed material, snapshot-integrity checks, tenant isolation, stale result rejection, dispatch redrive, and duplicate continuation convergence. |
| Compatibility closure | Native-only compatibility, two explicit nested Child DAG levels, and mixed native/custom hosted execution for Python, TypeScript, and .NET through the existing journal, worker, completion, and continuation path. |

The compilation, execution-binding, and durability/recovery targets have reported passing target-environment results. The closure suite defines five targeted cases. Python and TypeScript retain their existing explicit process-test configuration; a skipped hosted-language case is not closure evidence for that language. No aggregate passing-test total is inferred without the corresponding result artifacts.

The published-custom nesting claim is intentionally bounded to the depth actually exercised by the closure suite: two Child DAG levels below the published root. The existing native Child DAG Depth3 evidence remains a separate proof domain and is not automatically transferred to published custom execution.

## Hosted worker isolation evidence

The selected OCI-backed `SandboxedContainer` provider is validated separately from the earlier trusted-process artifacts. The current closure contains three complementary evidence layers:

- a deterministic provider/isolation suite validating admission, immutable provider selection, no downgrade, launch/inspect ordering, applied-state refusal, cancellation/failure handling, cleanup, quarantine, owner-scope orphan reconciliation, journal/result-path compatibility, lease/epoch behavior, and deterministic package metadata crossing the isolated transport;
- explicit opt-in real Docker/Linux tests validating the selected kernel/cgroup-visible boundary plus real production-worker execution and cancellation;
- the fixture-free public-SDK runtime matrix validating live provider/artifact selection through the existing runtime path.

The recorded real-engine target is **4 passed, 0 failed, 0 skipped**. The real tests verify non-root execution, zero effective capabilities, `NoNewPrivs=1`, read-only root filesystem, writable bounded `/tmp`, denied outbound networking with only loopback visible, exact memory/PID/CPU cgroup limits, production Python hosted-worker execution, active cancellation cleanup, and force-removal of a running descendant container workload.

These results do not mean that deterministic provider tests, the 4 real-engine tests, and the public-SDK matrix prove the same thing. The deterministic layer proves runtime contracts and failure behavior, frequently through a controlled engine probe. The real-engine layer proves selected kernel/container enforcement plus production Python worker execution and cancellation. The 37/37 matrix separately proves live SDK/runtime provider and artifact selection. See [Hosted Worker Isolation Validation](hosted-worker-isolation-validation.md) for commands, image preparation, and limitations.

## Public SDK fixture-free runtime matrix closure

The historical TRX evidence above is now supplemented by a separate live public-SDK matrix. The Docker verifier reports **37/37** scenarios with `.NET`, TypeScript/JavaScript, and Python external clients, production hosted workers, publication pinning, deterministic dependency packages, hosted policy families, nested published Child DAGs, durable MCP effect evidence, cancellation, recovery, journal result acceptance, provider/artifact selection, and external-client dependency firewalls.

The final matrix run is fixture-free at the execution layer: the `implementations/matrix/fixtures` tree is not required. Reusable public SDK samples provide publishable user code, while execution uses the production hosted workers.

The executed provider boundary is exact: the original 33 scenarios remain the Docker `ProcessHostPool` baseline, while four additional scenarios close `TrustedProcess` versus `SandboxedContainer` and `HostRuntime` versus `OciImage` selection under `runtimeProvider=ProcessHostPool` with explicit worker-execution provider selection. This does not imply `KubernetesPool` parity or a rerun of all baseline scenarios under container isolation.

See [Multilanguage Runtime Matrix Validation](multilanguage-runtime-matrix-validation.md) for the exact scenario counts and non-claims.

## KubernetesPool external SDK evidence

The separate KubernetesPool closure is **3/3**: live HTTP routing, hierarchical runtime/Pod failure recovery, and external Python SDK publication/execution with a public `Completed` result and the uploaded-function marker verified. The combined record is **40 validated scenarios across two topologies (37 Docker + 3 Kubernetes)**, not a homogeneous `40/40` matrix. The final SDK invocation revalidated retained routing/recovery evidence; it did not rerun those campaigns. See [KubernetesPool Matrix Validation](kubernetes-pool-matrix-validation.md).

The current external Kubernetes path is Python-to-Python using `HostRuntime` and `TrustedProcess` in the runtime Pod. The image contains the three production language workers, but worker presence is not evidence of executing all three in Kubernetes. Historical TRX artifacts and their hashes below are unchanged and are not reclassified as Kubernetes SDK results.

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

The historical TRX evidence in this document predates the later public SDK boundary. It is now supplemented, rather than retroactively reinterpreted, by the dedicated fixture-free public-SDK runtime matrix evidence described above. Deterministic package bundles are captured before publication; no general runtime package installer, language-specific SDK client package, or durable external-effect guarantee is implied by these historical results. The public contract/server boundary, container isolation, and durable MCP effect evidence are separate implemented boundaries with their own dedicated evidence and limits.

Dedicated reruns supplement the earlier regression artifact without changing its recorded outcomes. The available hosted-language evidence does not certify every runtime version, deployment topology, or failure mode. Published custom Child DAG evidence does not establish operating-system host-kill recovery, Redis/MongoDB restart or failover, Kubernetes sandbox-provider coverage, or unlimited recursive depth. Selected OCI container isolation is evidenced separately and must not be generalized beyond its tested provider/platform boundary.

## Related documents

- [Hosted Multilanguage Execution](hosted-multilanguage-execution.md)
- [Testing Strategy](testing-strategy.md)
- [MCP Production Runtime Scenario Framework](mcp-production-runtime-scenario-framework.md)
- [Runtime Pool Production Validation](runtime-pool-production-validation.md)
- [Replay and Audit](replay-and-audit.md)
