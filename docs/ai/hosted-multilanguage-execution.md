# Hosted Multilanguage Execution

**Status:** Implemented server-side foundation with targeted execution, persistence, authorization, and restoration validation. The external SDK library and production isolation for hostile code are separate deliverables.

## Purpose and scope

Hosted execution allows the existing DAG runtime to invoke published Python, TypeScript, and .NET functions without giving those functions orchestration authority. The same language infrastructure also evaluates custom `Concurrency`, `Retry`, and `Delegation` policies at their existing family checkpoints. Outbound MCP is a separate invocation mode, not another language or a replacement for the inbound MCP control plane.

This reference covers the implemented contracts and their limits. [Hosted Multilanguage Validation](hosted-multilanguage-validation.md) records the supplied test results and distinguishes process execution, controlled restoration, infrastructure integration, and reported host scenarios.

| Capability | Current boundary |
|---|---|
| Published custom functions | Explicit DAG execution with immutable code, supplied dependencies, and a pinned environment. |
| Hosted languages | Python source, TypeScript source compiled with a bundled toolchain, and precompiled .NET assemblies. |
| Custom policies | `Concurrency`, `Retry`, and `Delegation`, each evaluated at its existing checkpoint with a distinct closed result contract. `Retention` remains native-only. |
| Outbound MCP | Real Streamable HTTP transport, existing RBAC, server-owned connections, and logical-effect metadata. |
| Isolation | Trusted-process execution with capability checks and verified launch paths; no hostile-code sandbox. |
| External SDK | Not delivered. Public models and clients must remain independent of engine DLLs and internal CLR contracts. |

Registration is opt-in. Existing hosts do not automatically activate every hosted capability. Internal publication/run services are not a new public upload API, and run creation alone does not enqueue or start execution.

## Execution and authority boundaries

Orchestration mode, invocation kind, and execution language are independent choices.

```text
Pinned DAG definition
    -> existing pipeline resolver
       -> native invocation: existing registered implementation
       -> custom invocation: durable hosted-function adapter
       -> MCP invocation: authorized outbound-tool adapter
```

For a custom function, the execution path is:

```text
Existing DAG admission and claim
    -> resolve declared inputs and prepare durable invocation
    -> Park while the authoritative result is unavailable
    -> supervisor assigns a hosted process under journal authority
    -> process executes the pinned function
    -> journal accepts the result
    -> existing continuation path resumes the same call site
    -> DAG persists the result and application receipt
```

A result may arrive before `Park`; the sequence is not an ordering assumption that permits an early result to be discarded.

| Responsibility | Authority |
|---|---|
| Dependencies, claim, retries, transitions, recovery, and finalization | Existing DAG, runners, and coordinated stores. |
| Authorization and restored execution ownership | Existing RBAC and execution-context infrastructure. |
| Code, dependencies, and environment selection | Immutable publication and run pin. |
| Invocation identity, assignment epoch, and accepted result | Durable invocation journal. |
| Process lifecycle, liveness, and bounded launch capacity | Hosted worker supervisor and process transport. |
| Function body and portable business result | Hosted function process. |
| Continuation acknowledgement | Observation of the exact applied result and terminal parent state. |

A hosted function worker is not a trusted `RuntimeInstance`. Its assignment lease does not replace the DAG claim. It cannot issue `Park`, choose a successor, grant permissions, or operate the runtime stores. The worker response is data; lifecycle decisions remain server-side.

## Language resolution and contextual binding

For a custom function, the explicit local language overrides the pipeline default:

```text
custom step ExecutionLanguage
    -> otherwise pipeline ExecutionLanguage
```

A local override does not change the language of subsequent or parallel declarations. Missing or invalid effective languages are rejected rather than falling back to .NET. Native and MCP invocations remain native or MCP even when the pipeline has a default language.

Custom policy resolution preserves the declaration's original scope. An explicit policy language wins. A locally attached policy may otherwise inherit its custom step's language, then the pipeline default. A pipeline-scoped policy keeps the pipeline scope and does not inherit an unrelated local override merely because it is evaluated during that step's admission.

The language and invocation fields are separate from the existing `Execution` retry settings. Legacy definitions without the new fields retain the native path. JSON converters, resolved-plan copies, admission projections, and pinned definitions preserve the effective metadata.

Resolving a plan does not start a worker. Admission receives the resolved binding without executing the step body or preparing its business invocation. Evaluating an explicitly configured hosted custom policy can itself start a short policy worker at that family's existing checkpoint; this is distinct from executing the step body or preparing a durable custom-function invocation.

Native implementations remain in their existing registries. Contextual factories create hosted and MCP adapters; a missing custom capability cannot silently select a native implementation with the same name. Contextual policy adapters are excluded from native singleton discovery. Hosted step adapters are not discovered as attributed native plugins.

## Immutable publication and run pinning

Publication attaches code and supplied dependency bytes to supported custom declarations and ordered `Concurrency`, `Retry`, and `Delegation` policy sites. The compiler generates implementation references from the captured content; it does not require a tenant-maintained mutable handler registry. Installed runtime profiles are still explicitly approved by the server.

The publication includes the definition, implementation metadata, source or assembly files, dependency files, and environment snapshots. Raw file hashes and canonical document hashes identify different byte sequences. The existing immutable payload store is reused rather than introducing another publication database.

All referenced documents are written before the manifest. An interruption before the manifest may leave reusable immutable content, but it does not publish an incomplete manifest. Repeating an identical publication converges on the same content identities.

`AiPublishedDagRunService` persists an immutable pin before calling the existing exact DAG creator. The pin binds the run key to its publication, execution owner, and inputs. Conflicting reuse is rejected; retrieving an existing compatible run does not reseed its state.

The pin covers the complete published definition, including functions that have not started. Republishing new code does not change an unstarted function in an existing execution. Resolution and restoration use the pin, never `latest`. Missing or changed required content fails explicitly.

Publish, read, and execute operations use the existing RBAC engine with server-configured capabilities. Immutable publication material is not treated as ordinary disposable per-execution payload. Retaining environments and artifacts required by existing runs remains an operational obligation; changed code or requirements require a new publication rather than in-place replacement.

### Published custom Child DAGs

The published custom-function path also supports exact inline nested Child DAG definitions. Nested custom declarations are captured in the same immutable publication and identified by a canonical `DefinitionPath`; root declarations retain `DefinitionPath == null` for compatibility. A root execution continues to use its immutable `AiPublicationRunPin`. Before an allocated published child is dispatched, an immutable child binding associates its `ChildExecutionId` with the original publication and exact nested definition.

Nested target resolution and worker materialization use that execution association to select the exact call site and original source/dependency/environment material. Republishing does not upgrade an unstarted or recovered child. Missing bindings, changed artifacts, corrupted frozen definition bytes, tenant/owner mismatches, and conflicting immutable content fail explicitly rather than selecting newer material.

Publication authority can propagate from a root pin to a child binding and then to a deeper child binding. This reuses the existing Child DAG relation, dispatcher, durable invocation journal, hosted worker supervisor, result application, completion coordinator, and parent continuation. A native-only child subtree remains unbound and follows the historical native path.

## Durable invocation and result application

Logical invocation identity is separate from the process assigned to execute it. Preparation freezes the selected publication, implementation, environment, language, and resolved input content. Conflicting preparation under the same identity is rejected. The current DAG bridge uses the initial logical generation; a retry or replacement claim is not an implicit new business action.

An assignment carries a worker identity, lease, epoch, and token. Reassignment changes authority while retaining the logical operation and frozen preparation. A stale assignment cannot replace the authoritative result. An accepted identical completion is recognized as a duplicate; contradictory terminal results are rejected.

The MongoDB store uses conditional writes and uniqueness for this boundary. Lease-sensitive acceptance also checks database time rather than trusting only the caller's clock. Production code reuses the existing `IMongoDatabase` instead of creating another `MongoClient`.

Result acceptance and the pending continuation obligation are persisted together. Continuation state distinguishes:

| State | Meaning |
|---|---|
| `Pending` | A terminal invocation result requires reconciliation with the DAG. |
| `Scheduled` | Continuation remains a convergence obligation; queue acceptance is not application evidence. |
| `Applied` | The exact result receipt and terminal parent have been observed. |
| `Suppressed` | The parent is terminal without an applicable exact result receipt; no new execution is requested. |

`AiDurableInvocationApplicationReceipt` associates the operation with the accepted result hash. Reading a result, observing a ready step, or successfully enqueueing a continuation does not acknowledge application. A completed call site without final parent convergence remains subject to reconciliation.

An early result stays available until the persisted wait or exact application can be observed. After an interruption following result acceptance, reconstructed services reuse the recorded result rather than preparing or launching the function again. Duplicate continuation delivery uses the existing external-wait and DAG transitions.

The local, batch, distributed, and Redis failure paths preserve a custom result and its receipt where required, instead of retaining only an error message. Native failures without such a receipt keep their existing behavior. This is an extension of result persistence, not a replacement scheduler or recovery engine.

A stable operation key and local duplicate acceptance do not guarantee exactly-once effects in a remote system. Expired-assignment reexecution is disabled by default in the supervised dispatch path. Explicitly permitted reassignment still requires an appropriate external idempotency or reconciliation contract.

## Hosted process transport

A server-owned process profile selects the executable, literal arguments, approved files, environment variables, and runtime identity. Launch does not use a shell or arbitrary tenant-supplied executable paths. The transport clears inherited environment values and passes explicitly configured values.

The private UTF-8 JSON protocol uses closed `invoke`, `ready`, `heartbeat`, and `result` envelopes. Correlation and size limits are checked. Readiness, heartbeat silence, execution time, output, and cleanup are bounded; unknown commands and invalid terminal sequences fail.

The supervisor acquires journal authority before materializing the authorized publication, renews only with validated liveness and confirmed journal writes, and accepts results through the journal. It does not advance the DAG. Technical failures remain technical failures rather than manufactured business denials or permission for immediate reexecution.

Launch capacity is bounded. A slot is quarantined when termination of its root process cannot be confirmed. This does not establish complete containment of descendants after every host failure. Optional dispatch polling uses explicit control-plane/tenant/language partitions and bounded pagination rather than discovering arbitrary tenant work.

### Python

The Python loader verifies the published `.py` closure before invoking the declared function. It supports synchronous and asynchronous functions, regular packages and explicit source dependencies, with frozen inputs and read-only portable metadata. Module/path collisions and invalid bytes or results are refused.

The current profile accepts exact CPython 3.12.x and 3.13.x identities. Python compatibility is not broadened by the Node backend's version policy. There is no `pip install`, wheel resolution, native-extension installation, or ambient dependency substitution in this path.

Published source is loaded in the function process, not inside the .NET engine. Output handling separates ordinary/raw stdout diagnostics from the runtime protocol, but does not make hostile Python code a sandboxed workload.

### TypeScript and Node.js

The current backend runs a normal `.mjs` loader and compiles verified `.ts` files to JavaScript in a separate function child. It uses the bundled TypeScript 5.8.3 compiler with fixed emission settings and verified bytes. It does not use Node's experimental TypeScript execution flags or a fixed major-version allow-list.

Compatibility depends on the Node APIs required by the loader and published code. An exact installed version and executable hash are still required. Accepting a version label is not certification of every historical or future Node release.

The compiler performs per-file transpilation, not whole-program type checking. Enums, namespaces, constructor parameter properties, and supported relative/dynamic imports are emitted under the fixed contract. Supplied dependency aliases resolve to the published closure; there is no npm install, global compiler discovery, or registry access. Declaration-only files are not executable entry points.

`AiTypeScriptWorkerProcessProfile.CreateRuntime` binds the compiler/emission contract and loader digest into the approved runtime reference before publication. The Node executable digest keeps its original meaning. A changed compiler or loader requires a new environment; existing pins must not be relabeled in place.

The function child returns its result through IPC. Child stdout is not the parent's runtime-protocol channel. Bundled compiler bytes and their license/notices retain their own integrity and distribution requirements.

### .NET

The .NET backend executes immutable published assemblies and explicit dependency DLLs in a separate function process. The runtime does not compile tenant C# or restore tenant NuGet packages during invocation.

An entry point identifies `Fully.Qualified.Type::Method` and receives portable JSON inputs and context. Supported synchronous/asynchronous results must satisfy the portable business-result contract. Assembly loading and dependency selection are scoped to the published material; loading isolation is not an operating-system security sandbox.

The standalone worker remains distinct from engine assemblies. Tenant console output does not become readiness, heartbeat, or result frames in the parent protocol.

## Environment identity and execution requirements

Environment content addressing and executable-artifact addressing are different identities:

```text
EnvironmentSha256
    = hash of the canonical environment snapshot
      (runtime identity, explicit dependencies, execution descriptor)

ExecutionDescriptor.Artifact.Digest
    = sha256:<hex> identifying the selected executable artifact
```

`EnvironmentRef` retains its existing `env-<hash>` form. A host-runtime artifact digest identifies approved executable bytes, not every shared library, framework file, or operating-system component. An OCI descriptor represents an image-manifest digest with media type and platform metadata; it neither downloads nor attests an image. The current process provider refuses OCI execution.

Historical environment snapshots omit the descriptor. Versioned snapshots include and validate it; a version/descriptor mismatch is rejected. Requirements participate in publication identity and are carried server-side without changing the closed worker JSON. They are not taken from worker responses.

| Requirement or capability | Default requirement for a new descriptor | Actual process-provider capability |
|---|---|---|
| Isolation | `SandboxedContainer` | `TrustedProcess` |
| Network egress | `DenyAll` | `HostNetwork` |
| Path protection | `SealedClosure` | `ValidatedPaths` |

These columns must not be conflated. The process provider rejects unsupported requirements before reading launch files or starting a process. An explicitly approved trusted-process/host-network/validated-path profile can run; a sandbox requirement cannot silently downgrade to that profile. Existing legacy profiles remain a compatibility path, not newly sandboxed environments.

Launch checks validate approved roots, path containment, links/reparse points, collisions, and file hashes. Verified file handles are retained for the versioned path. These checks do not seal every mutable filesystem dependency, enforce network isolation, or eliminate all filesystem races on every platform. Stronger requirements remain refused until an enforcing provider exists.

## Hosted custom policy families

Hosted policy execution is enabled only for families that have an existing runtime checkpoint and an explicit family contract. It reuses immutable publication, restored execution ownership, RBAC, and the existing Python/TypeScript/.NET worker transports. Policy evaluation is short and deadline-bounded; it is not a durable custom-function invocation and does not receive DAG lifecycle authority.

| Family | Existing checkpoint | Contract | Hosted result boundary | Runtime authority retained |
|---|---|---|---|---|
| `Concurrency` | Admission | `concurrency/v1` | Explicit allow/deny admission evidence. | Concurrency engine, native governance guards, lease/admission flow. |
| `Retry` | Retry engine | `retry/v1` | `pass`, `retry` with optional bounded `suggestedDelayMs`, or `stop` with a required reason. | Retry budget, retry count, backoff, jitter, final delay, `WaitingForRetry`, terminal failure. |
| `Delegation` | `DelegationPolicyPending` before child allocation | `delegation/v1` | `approve` or `deny` with a required reason. | Durable relation decision CAS, `ChildExecutionId` allocation, dispatch, parent park/resume, continuation and recovery. |
| `Retention` | Retention engine | Native-only | No hosted contract in the current capability matrix. | Existing retention engine and native policies. |

`Timeout`, `CircuitBreaker`, `RateLimit`, `Validation`, and `Routing` remain policy taxonomy values without independent hosted runtime checkpoints in the current implementation. In particular, `retry.timeout.default` and `retry.rate-limit.default` are native policies of the `Retry` family; their names do not create independent policy engines.

A custom policy resolves through the run-pinned publication and original declaration scope. When the evaluating execution is itself a published Child DAG, policy materialization reuses the child publication binding and exact `DefinitionPath`; it does not resolve a mutable current publication. Missing custom capability cannot silently fall back to a native policy with the same name.

Technical failure is fail-closed at the family boundary. Invalid response shape, timeout, failed transport, unavailable immutable material, authorization/ownership failure, or worker-level failure cannot become implicit `Allow`, `Retry`, or `Approve`. No universal boolean policy transport or replacement policy engine is introduced. Recorded policy observations are not a durable replay store for every hosted evaluation.

## Outbound MCP and effect identity

Inbound MCP exposes runtime-control operations. Outbound MCP executes a tool selected by a runtime invocation. The hosted-function protocol is a third, private boundary.

```text
Existing MCP step adapter
    -> trusted execution identity and server-owned target
    -> existing RBAC decision
    -> declared input resolution
    -> effect metadata and request validation
    -> outbound Streamable HTTP session and tools/call
    -> existing result mapping
```

Endpoint, connection revision, credentials, and capabilities come from server configuration. The transport checks tenant/connection/revision/tool consistency before network activity. HTTPS is required except for explicitly enabled loopback HTTP; redirects and cookies are disabled. Configurable secret headers cannot override protected routing/protocol headers.

Tool-reported errors are distinct from protocol, network, authorization, deadline, and cancellation failures. The transport does not add automatic `tools/call` retries for an uncertain external effect.

| Field | Meaning |
|---|---|
| `RequestId` | Correlation for one attempt. |
| `EffectId` | Stable logical action derived from tenant, tenant group, execution, and call-site name. |
| `RequestDigest` | Digest of canonical intent, including logical context, connection reference/revision, tool, and resolved arguments. |
| `ConnectionRevision` | Exact server-owned connection configuration selected for the attempt. |

Claim replacement, worker replacement, or a new deadline does not create a new logical effect. Changed arguments or connection configuration change the request digest, not the effect identity. An intentional new action requires a distinct execution or call site under the current contract.

Object property order does not change the canonical digest; array order does. Number spellings remain significant. The canonicalization is a versioned local contract, not a claim of RFC 8785 compliance. Internal request schema 2 requires coherent effect metadata; legacy schema 1 remains a compatibility path without equivalent effect evidence.

These fields are not a signature, authorization grant, persistence receipt, or provider-recognized idempotency key. They are not injected into tool arguments or HTTP headers. Without stored prior intent, recomputation cannot detect a historical intent conflict. Two explicit calls are not deduplicated merely because their effect metadata matches.

Durable outbound-effect storage, uncertain-outcome reconciliation, and audit replay without re-emission are not implemented by this transport. Read-only or explicitly idempotent tools define the current validation boundary. Successful local tool execution must not be presented as proof for irreversible external actions.

## Compatibility and remaining scope

Native discovery, existing RBAC, shared queues, runtime-instance transports, and recovery retain their roles. Server-side extensions reuse the current DAG, journal, payload stores, and result transitions. New publication/security requirements are not automatically enabled in existing hosts and do not retrofit older records with stronger guarantees.

The following remain outside the implemented foundation:

| Area | Remaining scope |
|---|---|
| Public integration | Independent SDK libraries, public publication/submission models, and Gateway/API productization. |
| Hostile code | Enforced container isolation, CPU/memory limits, filesystem policy, network egress, descendant containment, and cleanup after host loss. |
| Dependencies | Python wheels/native extensions/namespace packages; general npm/lockfile bundles and native add-ons; automatic .NET dependency-closure/native packaging. |
| Additional policies | `Retention` is native-only in the current matrix. Taxonomy values without an independent checkpoint are not hosted. Any further family requires its own existing checkpoint, request/response contract, authority analysis, and bounded validation. |
| Published-child validation breadth | Broader provider/store failure matrices, operating-system host-kill proofs, and unlimited recursive-depth claims are not implied by the bounded published-child closure. |
| External effects | Durable MCP evidence, reconciliation, schema pinning, connection-catalog lifecycle, and credential-provider integrations. |

No arbitrary network package installation, implicit `latest` resolution, or tenant C# compilation belongs in the current execution path. A future SDK describes and publishes work; the platform hosts it and the runtime governs it. No direct or transitive engine-DLL dependency is required of that SDK.

## Implementation references

These are server implementation points, not public SDK contracts.

| Boundary | Source |
|---|---|
| Effective invocation binding | [`AiInvocationBindingResolver.cs`](../../implementations/dotnet/src/Multiplexed.AI/Runtime/Invocation/AiInvocationBindingResolver.cs) |
| Concurrency policy scope and language | [`AiConcurrencyPolicyBindingResolver.cs`](../../implementations/dotnet/src/Multiplexed.AI/Runtime/Invocation/AiConcurrencyPolicyBindingResolver.cs) |
| Retry policy scope and language | [`AiRetryPolicyBindingResolver.cs`](../../implementations/dotnet/src/Multiplexed.AI/Runtime/Invocation/AiRetryPolicyBindingResolver.cs) |
| Delegation policy scope and language | [`AiDelegationPolicyBindingResolver.cs`](../../implementations/dotnet/src/Multiplexed.AI/Runtime/Invocation/AiDelegationPolicyBindingResolver.cs) |
| Publication validation | [`AiPublicationCompiler.cs`](../../implementations/dotnet/src/Multiplexed.AI/Runtime/Publication/AiPublicationCompiler.cs) |
| Immutable run creation | [`AiPublishedDagRunService.cs`](../../implementations/dotnet/src/Multiplexed.AI/Runtime/Publication/AiPublishedDagRunService.cs) |
| Published Child DAG binding | [`AiPublishedChildDagBindingCoordinator.cs`](../../implementations/dotnet/src/Multiplexed.AI/Runtime/Publication/AiPublishedChildDagBindingCoordinator.cs) |
| Durable invocation authority | [`AiDurableInvocationJournal.cs`](../../implementations/dotnet/src/Multiplexed.AI/Runtime/Invocation/Durable/AiDurableInvocationJournal.cs) |
| Continuation acknowledgement | [`AiDurableInvocationDagContinuationCoordinator.cs`](../../implementations/dotnet/src/Multiplexed.AI/Runtime/Invocation/Durable/Dag/AiDurableInvocationDagContinuationCoordinator.cs) |
| Provider capability checks | [`AiWorkerExecutionAdmission.cs`](../../implementations/dotnet/src/Multiplexed.AI/Runtime/Invocation/Workers/AiWorkerExecutionAdmission.cs) |
| Launch-file boundary | [`AiWorkerLaunchPaths.cs`](../../implementations/dotnet/src/Multiplexed.AI/Runtime/Invocation/Workers/AiWorkerLaunchPaths.cs) |
| TypeScript environment binding | [`AiTypeScriptWorkerProcessProfile.cs`](../../implementations/dotnet/src/Multiplexed.AI/Runtime/Invocation/Workers/TypeScript/AiTypeScriptWorkerProcessProfile.cs) |
| Hosted Concurrency policy transport | [`AiHostedConcurrencyPolicyTransport.cs`](../../implementations/dotnet/src/Multiplexed.AI/Runtime/Invocation/Workers/Policies/AiHostedConcurrencyPolicyTransport.cs) |
| Hosted Retry policy transport | [`AiHostedRetryPolicyTransport.cs`](../../implementations/dotnet/src/Multiplexed.AI/Runtime/Invocation/Workers/Policies/AiHostedRetryPolicyTransport.cs) |
| Hosted Delegation policy transport | [`AiHostedDelegationPolicyTransport.cs`](../../implementations/dotnet/src/Multiplexed.AI/Runtime/Invocation/Workers/Policies/AiHostedDelegationPolicyTransport.cs) |
| MCP intent identity | [`AiMcpEffectIdentities.cs`](../../implementations/dotnet/src/Multiplexed.AI/Runtime/Invocation/Mcp/AiMcpEffectIdentities.cs) |
| Outbound network boundary | [`AiOutboundMcpToolTransport.cs`](../../implementations/dotnet/src/Multiplexed.AI.McpServer/Invocation/Outbound/AiOutboundMcpToolTransport.cs) |

## Related documents

- [Architecture Overview](architecture-overview.md)
- [Config-Driven Runtime](config-driven-runtime.md)
- [Step Plugins](step-plugins.md)
- [Policy-Driven Execution](policy-driven-execution.md)
- [MCP Server as Runtime Control Plane](mcp-server-control-plane.md)
- [Replay and Audit](replay-and-audit.md)
- [Durable Child DAG Composition](child-dag-composition.md)
- [Hosted Multilanguage Validation](hosted-multilanguage-validation.md)
- [Developer Experience, API, SDK, and CLI](../product-roadmap/developer-experience-api-sdk-cli.md)
