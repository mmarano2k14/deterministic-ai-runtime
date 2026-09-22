# A Green Test Is Not a Proof

### A deterministic adversarial validation matrix for durable recursive AI execution — 36 frozen failure schedules, one unchanged definition of correctness

> **Documentation role:** validation thesis and engineering rationale for the deterministic adversarial runtime matrix.
>
> **Canonical evidence:** [Adversarial Runtime Validation Matrix](adversarial-runtime-validation-matrix.md) · [Evidence Index](adversarial-runtime-validation-evidence-index.md) · [Raw validation archive](../files/adversarial-runtime-validation-logs.zip)
>
> **Related architecture:** [Durable Child DAG composition](child-dag-composition.md) · [Runtime Pool architecture](runtime-pool-architecture.md) · [Runtime Pool failure recovery](runtime-pool-failure-recovery.md) · [Testing strategy](testing-strategy.md)

A single large end-to-end scenario turning green tells you that the runtime *can* succeed. It does not tell you the architecture is *correct*. Those are different claims, and the distance between them is where durable systems quietly fail.

A scenario that reaches `Completed` can still have generated a duplicate child, dispatched the same durable work twice, lost a logical step, bound a recovery to the wrong owner, resumed a parent that should have stayed parked, or produced the right final answer from a semantically different execution. None of those show up as a red test. They show up in production, months later, as an anomaly no one can reproduce.

This reference closes that gap for a specific and increasingly important class of system: a **durable runtime that executes recursive AI workflows** — parents that delegate to durable child executions, children that recursively delegate again, continuations that must resume after a child completes, and executions that survive the loss of the process, runtime, host, or Pod that started them. For that class, terminal completion is necessary but nowhere near sufficient.

The thesis is narrow and testable:

> **A durable AI runtime is not proven correct because one large scenario turns green. It becomes credible only when the same frozen definition of correctness survives deliberately different deterministic failure schedules — and tells the same story about what actually executed under every one of them.**

The instrument for that is a **deterministic adversarial validation matrix**: a fixed set of correctness invariants, and a small set of failure schedules that each move exactly one adversarial coordinate — where the crash lands, which recursive execution owns the failing work, in what order work is interleaved, which transport carries it, and which provider hosts it. The definition of success never changes. Only the adversary does.

What follows is the matrix, the reasoning behind its shape, the results, and — deliberately — the exact boundary of what those results do and do not prove. Every number in this document is recomputable from the published archive of raw test artifacts; the point of the exercise is that you should not have to trust the narrative.

---

## A green test is not a proof

The ordinary integration test asks one question: *did this scenario complete?* It is a useful question and a weak one. For a durable recursive runtime, the question has to become: *did the same logical computation remain exact under an intentionally changed failure schedule, and can the system demonstrate why?*

That reframing matters because the failure modes that destroy a durable runtime are not crashes — the runtime is built to survive crashes. They are *silent semantic errors that survive a crash*: the extra child, the lost continuation, the double dispatch, the ownership ambiguity. A test that only checks for `Completed` is blind to every one of them.

So correctness here is not a boolean. It is a set of independently observable invariants that must all hold simultaneously, and a successful run must demonstrate the relevant subset of them:

```text
exact recursive child generation       no duplicate durable dispatch
exact logical-step accounting          valid runtime ownership transitions
no missing recursive child work        exact recovery convergence
no unexpected duplicate child work     preserved execution identity where required
no lost parent run                      durable terminality
parent replay correctness              ledger / lifecycle / forensics / trace evidence
bounded pool topology                  warm-capacity reuse
```

The matrix changes the adversarial schedule. It never changes that list. That invariance — *the definition of success is frozen while the adversary moves* — is the whole idea, and everything below is a consequence of taking it seriously.

---

## The architecture under test

To make the proof legible, a short sketch of what is being validated. The runtime executes durable DAGs inside **reusable runtime pools**. A parent DAG can delegate to a **child DAG**, which is not a callback or a sub-task but a *normal durable execution* with its own execution identity, its own DAG definition, its own policies, its own ledger evidence, its own recovery, and its own replay path. Children can themselves delegate, recursively.

Three separations carry the entire design, and each is a distinction the matrix later attacks directly:

```text
logical execution identity    !=  physical runtime attempt
durable ownership authority   !=  best-effort observation signal
continuation delivered        !=  continuation durably consumed
```

The first says a logical execution (`ExecutionId`) can outlive the physical attempt (`LocalRunId`), the runtime instance, and the host or Pod beneath it. Recovery is therefore a *continuation of the same logical identity*, not a restart. The second says that which runtime owns a piece of work is decided by a durable atomic authority — a Redis shared-queue claim token and an exact-owner compare-and-set — not by whichever component observed a failure first. The third says a continuation being *delivered* over transport is not the same fact as a parent durably *consuming* it; only durable execution progress counts as authority.

When a parent delegates and waits, it does not hold a runtime slot for the duration. It parks durably, releases physical capacity, and is resumed by a durable continuation once the child completes. That is what lets a bounded runtime pool host more long-lived logical workflows than physical slots — and it is why "is this execution alive?" and "is this slot occupied?" are different questions the runtime must answer separately.

### Recursive composition: logical identity vs physical placement

```text
LOGICAL EXECUTION TREE                         PHYSICAL PLACEMENT

Parent ExecutionId P                           ProcessHost / Pod A
        |                                              |
        +--> Child C1 ExecutionId                      +--> Runtime R1
                    |                                        |
                    +--> Child C2 ExecutionId                +--> LocalRun L1
                                |
                                +--> Child C3 ExecutionId

Failure / recovery can change physical placement without changing
the durable logical execution identity:

C2 ExecutionId  ----------------------------------------------+
                                                               |
Runtime R1 / LocalRun L1  -- dies                              |
                                                               v
Runtime R7 / LocalRun L9  -- replacement ----------------> same C2 ExecutionId
```

The reference workload for every schedule below is fixed:

```text
3 parent boundaries x 3 runtimes = 9 bounded runtime slots
2 submission waves x 2 warm-reuse cycles = 36 parent DAGs
ChildDepth = 3
1,836 parent logical steps
5,472 recursive child logical steps
7,308 total logical steps per row
```

Holding the workload constant is deliberate: if the topology and counts never change, then any divergence between schedules is attributable to the adversarial coordinate, not to a different amount of work.

---

## The frozen proof contract

Before any adversarial schedule runs, the meaning of a passing row is frozen into a contract. This is the part most validation efforts skip, and skipping it is how a test suite slowly redefines success to match whatever the code currently does.

A row passes only when a fixed schema of invariants resolves cleanly. The load-bearing ones:

```text
ExpectedRecursiveChildLogicalStepCountTotal = DistinctChildStepCompletedTotal
DistinctChildStepCompletedTotal             = RawChildStepCompletedTotal
MissingRecursiveChildLogicalStepCountTotal  = 0
UnexpectedDuplicateRecursiveChildStepTotal  = 0
RuntimeOwnershipTransitionViolationCount    = 0
LostRunCount                                = 0
DuplicateDurableDispatchCount               = 0
ParentReplayProven / ParentReplayExpected   = full
ProcessKillIdentityContinuity               = <killed>/<killed>
```

Two properties of this contract are worth stating precisely, because they are exactly where a careless proof overclaims.

First, the child-step invariant is three counts, not one. *Expected equals distinct* proves completeness — nothing missing. *Raw equals distinct* proves there is no duplicate inflation — the ledger did not double-count. Requiring both, per recursion depth, is a much stronger statement than "the children finished."

Second, the contract distinguishes what it proves *here* from what it delegates *elsewhere*, and it says so in its own fields. Runtime **ownership transition** correctness — every handoff from a failed owner to a valid replacement, with zero violations — is proven inside each row. Continuous ownership **interval exclusivity** — the stronger property that no two runtimes ever executed the same work for even an instant — is *not* proven by these scenarios. It is delegated to a separate Redis claim-token and exact-owner CAS proof, and every row records that delegation explicitly rather than implying the stronger claim:

```text
RuntimeOwnershipTransitionProof          = PASS
RuntimeOwnershipIntervalProofRef         = <CAS proof>
RuntimeOwnershipIntervalProofIncluded    = False
```

Likewise, replay is scoped honestly. The contract proves **parent** replay and marks recursive-child replay as not yet evaluated — `RecursiveChildReplayProof = NOT_EVALUATED` — rather than letting a reader infer full-tree replay from a parent result. A proof that names its own boundaries is worth more than one that hides them.

For the row-by-row canonical schema and evidence pointers, see [Adversarial Runtime Validation Matrix](adversarial-runtime-validation-matrix.md) and [Adversarial Runtime Validation Evidence Index](adversarial-runtime-validation-evidence-index.md).

---

## The matrix is not a cross-product

The tempting mistake is to treat "matrix" as "run every combination of everything." That produces an unbounded, unreproducible suite whose green is meaningless. The opposite discipline is the point: choose a *small* number of *high-information* adversarial coordinates, and move one at a time, holding everything else fixed.

Four coordinates attack different assumptions:

```text
A  - move the failure          where in the lifecycle the crash lands
B  - move the owner            which recursive execution owns the failing work
C  - reorder deterministically the interleaving of concurrent work
P  - project across providers  transport x host boundary
```

Nine semantic failure schedules span A, B, and C. Each of the nine is then **projected** across four provider/transport combinations — the two transports the runtime supports crossed with the two host-boundary models — producing `9 x 4 = 36` rows.

Because the schedules are deterministic and seeded, a failing row is not a flaky anomaly to chase; it is a *reproducible contract violation* with a fixed coordinate. That is the difference between adversarial validation and undirected chaos: one gives you a stable experiment that can be repeated and compared; the other may find a failure without giving you the same path back to it.

### Matrix coordinates around one frozen contract

```text
                         A - FAILURE POSITION
                                  |
                                  |
                                  v
                        +-------------------+
                        |                   |
B - RECURSIVE OWNER --->|  FROZEN CONTRACT  |<--- C - SEEDED ORDER
                        |                   |
                        +-------------------+
                                  |
                                  |
                                  v
                    P - PROVIDER / TRANSPORT
                     HTTP / gRPC
                 KubernetesPool / ProcessHostPool

Only the adversarial coordinate moves.
The correctness contract does not.
```

---

## Matrix A — move the failure

Matrix A holds the workload and recursion fixed and moves only *where the crash lands* along a parent's 51-step lifecycle. The same invariants must hold whether the failure strikes near the start, mid-execution, at a child-invocation boundary, or — hardest of all — after a child has already produced a durable result.

```text
A0  baseline              kill after 25 completed steps
A1  crash-early           kill after 1 completed step
A2  child-invocation      kill at 49, at a delegation boundary
A3  continuation-consume  kill at 50, while consuming a child result
```

If moving only the crash location changed the logical output, the architecture would be timing-dependent — correct by luck of scheduling rather than by design. The purpose of A is to prove it is not.

### A3 — continuation-consume

**A3 is the most important row in the matrix.** It attacks the point *after* the child has completed and produced a durable result, while the parent is in the act of consuming that continuation. This is precisely the boundary agentic systems live on: a recursive child can be a research sub-agent, a risk evaluator, a tool-execution branch, or a planning subgraph. If the parent crashes after such a child has produced a durable result, the runtime must not ask the child to "do the work again" unless the logical contract explicitly demands it. The continuation is the durable seam between two autonomous pieces of work, and A3 tests whether the runtime understands that seam.

A3 also taught a second-order lesson that belongs in any honest account of this kind of work:

> A deterministic runtime can be correct while a naive test harness for a micro-boundary is not.

The continuation-consume window is narrow, and an early version of the harness could observe it non-deterministically — reading state after it had already advanced, or waiting on a signal without an exact durable identity and missing the historical evidence.

The response was not to weaken A3. It was to make the *proof mechanism* as deterministic as the system it validates:

1. derive the exact `ParentExecutionId`;
2. derive the exact durable child-invocation identity;
3. derive the exact continuation `SharedRunId`;
4. pre-arm the exact physical process handle;
5. release the existing historical preparation checkpoint;
6. watch the exact durable continuation ownership state;
7. freeze the exact pre-armed physical process at the target boundary;
8. prove the durable boundary after freeze;
9. kill that same physical process;
10. let normal in-flight recovery run.

No synthetic production gate and no new production lifecycle event were introduced. The proof rides the real continuation path.

This distinction deserves to be stated as a principle in its own right:

> The validation system must be at least as deterministic as the execution system it claims to validate.

A flaky failure-injection boundary does not merely produce noisy results; it invalidates the experiment. If the harness sometimes reads state after it has advanced, then a green run no longer distinguishes "the runtime was correct" from "the harness happened to look at the right moment."

The fix is not to add a sleep, widen a timeout, or rerun until the window behaves. Those replace a structural race with luck. The fix is to key observation to a durable identity the runtime itself commits, so the proof synchronizes on the same durable fact the system uses.

A green obtained by rerunning a nondeterministic boundary is not evidence of determinism; it is evidence that the harness is not yet trustworthy.

The resulting A3 evidence is unambiguous:

```text
RelationStatus        = Completed
ContinuationStatus    = Scheduled
SharedRunStatus       = Dispatched
Parent DAG            = Waiting
CompletedStepsAtKill  = 50 / 51
PhysicalKillProof     = PASS
RecoveryKind          = InFlightExecution
ExecutionIdBefore    == ExecutionIdAfter
```

The killed runtime's logical `ExecutionId` is preserved across a physical-owner change, the continuation converges on the intended durable outcome, and no second logical child appears.

### A3 physical/durable boundary

```text
Child execution
      |
      v
Relation = Completed
Continuation = Scheduled
      |
      v
exact continuation SharedRun
Status = Dispatched
ExecutionId / LocalRunId / RuntimeInstanceId known
      |
      v
freeze exact pre-armed physical process
      |
      +--> prove parent still non-terminal
      +--> prove exact running continuation attempt
      +--> prove durable ownership unchanged
      |
      v
kill same physical process
      |
      v
normal in-flight recovery
      |
      +--> same ExecutionId
      +--> replacement RuntimeInstanceId
      +--> replacement LocalRunId
      |
      v
deterministic convergence
```

---

## Matrix B — move the owner down the recursive tree

Matrix A moves the failure along a *parent's* timeline. Matrix B moves it *down the recursion* — the failing runtime is the one executing a child at depth two, then depth three, rather than a root-level owner.

```text
B1  depth-2 runtime failure
B2  depth-3 runtime failure
```

This attacks a specific and dangerous assumption: that recovery is independent of *where in the tree* the failure occurs. A failure deep in a recursive composition must be recovered at the affected execution boundary, and everything already completed or unaffected higher in the tree must remain intact. The root parent must not restart because a grandchild's runtime died.

The invariant that carries B is the separation the whole architecture rests on:

```text
logical execution tree   !=   physical runtime topology
```

A recursive execution tree can survive the replacement of physical runtimes beneath it, one branch at a time. B1 and B2 hold the exact per-depth child-step accounting — `918 + 918 + 900 = 2,736` logical steps per cycle across depths one, two, and three — with zero missing and zero duplicated steps, while the failure is injected at successively deeper levels.

Consider a live composition:

```text
P -> C1 -> C2 -> C3
```

`P` has delegated to `C1` and parked. `C1` has delegated to `C2` and parked. `C2` is mid-flight, executing its own steps, having not yet delegated to `C3`. Now the runtime hosting `C2` dies.

In a naive model, three things are ambiguous at once:

- whether `C2`'s partial work is lost;
- whether `C1`, which is parked waiting on `C2`, must be considered failed;
- whether `P` must restart.

In this runtime, `C2` is a normal durable execution with its own `ExecutionId`. It recovers through the ordinary in-flight recovery path onto replacement capacity, preserving that same `ExecutionId`, and resumes from remaining logical work rather than creating a new logical execution.

`C1` and `P` remain durably parked on the same child relation identity. A parked parent waiting on an unchanged logical child identity has nothing to recover.

When `C2` completes, the upward continuation chain proceeds through the normal durable continuation semantics. B2 pushes the same physical failure one level deeper, to a `C3` runtime mid-flight. The intended result is the same: failure, recovery, and replacement are localized to the affected recursive execution boundary.

A grandchild's runtime dying is a grandchild-scoped physical event; the logical execution tree above it remains durable.

---

## Matrix C — deterministic interleavings instead of random chaos

Matrices A and B move *where* a failure lands. Matrix C moves *the order in which concurrent work is scheduled*, using seeded deterministic interleavings rather than random concurrency.

```text
C1  seed-a / reverse
C2  seed-b / outside-in
C3  seed-c / center-out
```

The reason to prefer seeded reorderings over random stress is reproducibility. A random suite might surface an ordering-dependent duplicate once and never again, leaving you with a screenshot and no reliable way back.

A seeded interleaving that reorders submission and claim timing attacks the same class of race — duplicate dispatch or ownership conflict — but does so repeatably, so a violation becomes a debuggable contract breach rather than folklore.

Under reordered claim timing, `DuplicateDurableDispatchCount` and `RuntimeOwnershipTransitionViolationCount` must stay at zero.

The runtime's atomic ownership layer is the mechanism under pressure here: claim-token and exact-owner compare-and-set coordination provide mutation authority, while the adversarial matrix verifies the resulting ownership transitions under the selected deterministic schedules.

---

## Provider and transport projection

The nine semantic schedules are projected across the two transports and two reusable host-boundary models:

```text
+--------------------+--------------------+--------------------+
|                    | KubernetesPool     | ProcessHostPool    |
+--------------------+--------------------+--------------------+
| gRPC               | 9 / 9 VERIFIED     | 9 / 9 VERIFIED     |
| HTTP               | 9 / 9 VERIFIED     | 9 / 9 VERIFIED     |
+--------------------+--------------------+--------------------+

Total = 36 / 36 VERIFIED
```

The parity claim is narrow: transport and host boundary may change the physical failure surface — a Pod deletion differs from a process-host failure, and HTTP framing differs from gRPC framing — but they must not change the *logical* execution and recovery semantics.

The same frozen contract, identities, and proof fields are applied across all four projections.

### One semantic contract projected across four physical models

```text
                 NINE SEMANTIC SCHEDULES
                         |
                         v
              +-----------------------+
              |    FROZEN CONTRACT    |
              +-----------------------+
                  /       |       \
                 /        |        \
                v         v         v

       gRPC + K8s     HTTP + K8s
          9 / 9         9 / 9

       gRPC + Proc    HTTP + Proc
          9 / 9         9 / 9

Physical transport / boundary differs.
Logical invariants remain the same.
```

Reusing one shared proof harness across the four combinations is itself a design decision with teeth. Duplicating a bespoke definition of success per provider would allow the parity claim to drift.

One harness, four projections, one contract keeps the semantic comparison honest.

---

## The proof is the intersection of independent authorities

A single record asserting "recovery succeeded" proves very little, because a bug in the component that writes that record could also corrupt the proof.

Strength comes from *independent* durable authorities that would have to disagree visibly if the execution story diverged.

For each row, the same logical outcome is cross-checked across surfaces written by different components for different responsibilities:

```text
DAG execution records
    -> what logical steps completed per execution

durable parent -> child relation
    -> durable delegation / terminal-result authority

SharedRun durable state
    -> durable shared work / ownership state

Redis claim token + exact-owner CAS
    -> atomic mutation ownership

Decision Ledger
    -> durable step / decision evidence

Runtime Lifecycle Journal
    -> append-only host / runtime / placement history

Recovery Forensics
    -> causal recovery timeline

Replay
    -> reconstruction of parent execution evidence
```

When completed-step accounting, child relation terminality, recovery forensics, lifecycle evidence, replay, and ownership transitions all tell the same story, the result is materially stronger than a single terminal status.

These surfaces are written at different times, by different components, for different purposes.

Observability is therefore not decoration bolted onto the runtime. It is part of the externally inspectable execution contract.

This is also why durable authority and best-effort signals remain separate. A realtime event can wake a waiter or accelerate observation; it is not automatically the correctness authority for ownership or recovery. Durable stores remain the proof surface.

See [Engine event observation and lifecycle catalog](engine-event-observation.md), [Runtime Lifecycle Journal](runtime-lifecycle-journal.md), [Runtime recovery forensics](runtime-recovery-forensics.md), and [Replay and audit](replay-and-audit.md).

---

## The verified evidence

The matrix has been run to completion and its evidence frozen.

The bounded claim is:

> Across four provider/transport projections × nine deterministic adversarial schedules, all **36 rows** hold the frozen correctness contract. Each row is bound to its own raw test artifact, and the evidence archive is published with a SHA-256 fingerprint.

Aggregated across the 36 rows:

| Dimension | Volume |
|---|---:|
| Provider/transport projections | 4 |
| Deterministic adversarial rows | 36 |
| Execution cycles | 72 |
| Parent runs submitted | 1,296 |
| Parent runs completed | 1,296 |
| Parent logical steps | 66,096 |
| Recursive child executions | 3,888 |
| Recursive child logical steps | 196,992 |
| Total executions | 5,184 |
| Total logical steps | 263,088 |
| Parent replay proofs | 1,296 / 1,296 |
| Recovered SharedRuns | 288 |
| Process-kill identity-continuity proofs | 72 / 72 |
| Injected child-runtime failures | 72 |
| Injected busy-boundary failures | 72 |
| Missing recursive child logical steps | 0 |
| Unexpected duplicate recursive child logical steps | 0 |
| Ownership transition violations | 0 |

Raw archive:

```text
docs/files/adversarial-runtime-validation-logs.zip
```

SHA-256:

```text
a8e252b2b7277c196d594f0da6963b2e39eab3ad0e2a6415306974d2a8497c03
```

From this page under `docs/ai/`, the relative archive link is:

[Download / inspect the raw validation archive](../files/adversarial-runtime-validation-logs.zip)

The number that matters is not the total step count. It is that a *single frozen contract* held while the adversary moved through the selected coordinates.

The reason to publish raw artifacts and hashes rather than only a summary is the spirit of the exercise. A skeptical reader should be able to inspect the source evidence rather than trust prose.

For the canonical row inventory and per-artifact evidence mapping, use the [Evidence Index](adversarial-runtime-validation-evidence-index.md).

---

## What must not be claimed

An honest proof is defined as much by its stated limits as by its results.

This matrix does **not** claim:

### All possible failures are proven

It proves 36 *selected deterministic schedules*, chosen to be high-information. It does not enumerate the full state space of failures, timings, and orderings.

This is validation of a frozen contract under a chosen deterministic adversary, not universal certification.

### Random concurrency is exhaustively explored

The interleavings are seeded and deterministic by design.

That is a feature — reproducibility — and also a boundary. Exhaustive concurrency exploration is a different effort.

### Recursive child replay is proven

Replay in this campaign is parent-scoped.

Recursive-child replay is explicitly:

```text
RecursiveChildReplayProof = NOT_EVALUATED
```

Recursive children are proven here through exact logical-step accounting, durable terminality, and recovery evidence — not through a dedicated recursive-child replay assertion.

`NOT_EVALUATED` describes this campaign's assertion boundary. It does not mean the Child DAG architecture lacks an execution identity or replay path.

The stronger future proof is replay of **recovered child executions**, because that would demonstrate reconstruction after a child's runtime failed mid-flight.

### Ownership exclusivity is proven at every instant

The scenarios prove ownership **transition** correctness.

Continuous ownership **interval** exclusivity is a stronger property and is not independently proven by this matrix.

Mutation exclusivity relies on the runtime's atomic claim-token and compare-and-set coordination primitives. The matrix verifies the selected resulting ownership-transition behavior; it does not turn that mechanism into an exhaustive interval proof.

### Recovery-of-recovery is proven

A replacement runtime failing during an active recovery chain remains a separate adversarial dimension.

### Redis Cluster, multi-node Kubernetes, and multi-control-plane recovery are closed

They are not.

Redis Cluster slot-locality/failover, multi-node Kubernetes, and durable multi-control-plane recovery ownership remain further-hardening areas.

Naming these boundaries is not a weakness in the proof; it is what makes the proof trustworthy.

The strongest defensible statement is:

> Across deliberately different deterministic schedules, physical failure was allowed to change the runtime process, host, Pod, local run, transport path, and recovery binding — without changing the expected logical computation.

---

## Why this matters for agentic AI

The abstract result — logical exactness under adversarial physical failure — is exactly the kind of guarantee agentic systems need.

An agent plan is naturally a recursive delegation tree. A planner decomposes a goal; sub-agents take sub-problems; tools and further sub-agents hang beneath them; some branches wait on external conditions for minutes or hours.

Framed only as in-memory task objects and awaited futures, that tree is tied to the process that owns those objects.

Recursive Child DAGs instead turn each delegated branch into durable execution state with durable identity and recovery semantics.

A planner becomes durable delegation.

Sub-agents become ordinary child DAGs.

Fan-out is keyed by committed durable invocation identity rather than process-local position.

Fan-in becomes a durable continuation rather than an in-memory future.

Human-in-the-loop approval uses the same class of durable wait: logical execution remains durable while physical capacity is released.

A tool call cannot magically be made idempotent by the runtime. But the runtime can still reason about durable execution evidence: whether the step completed, whether a retry was allowed, whether recovery preserved execution identity, and what replay mode is being performed.

The matrix is what turns those architectural statements into evidence for selected failure schedules.

A recursive child at continuation-consume, killed at step 50 of 51, can recover through the same logical execution identity while physical ownership changes. That is the kind of property an agent runtime has to make explicit before "multi-agent" becomes durable infrastructure rather than an in-memory demo.

### Worked example: a research plan that crashes mid-flight

Consider a research assistant that must assess a claim and produce a sourced brief with a dissenting view:

```text
Planner (parent)
 |
 +-- Retrieval agent
 |      gather sources
 |
 +-- Analysis agent
 |      build thesis
 |        |
 |        +-- Fact-check agent
 |             verify claims
 |
 +-- Skeptic agent
 |      construct counter-view
 |
 +-- Human review gate
        durable external wait
```

In a purely in-memory model, the plan is represented by process-local tasks and awaited futures. If that process disappears, the execution framework must reconstruct enough state to know what already happened, what can safely happen again, and which branch still owns the remaining work.

In the durable model:

- the retrieval child can have a durable terminal result;
- the analysis child can be durably parked waiting on the fact-check child;
- the fact-check child can have its own `ExecutionId`;
- the human approval can remain a durable external wait without consuming runtime capacity.

Now let the fact-check runtime fail while the parent branches are parked.

The affected child execution can recover through the ordinary in-flight recovery path on replacement capacity under the same logical `ExecutionId`.

The parked parents remain associated with the same durable child identity.

When the recovered child converges, continuation processing resumes the waiting logical execution through the same durable continuation mechanism.

The matrix validates these underlying failure dimensions independently:

- continuation-consume at A3;
- recursive runtime failure at depth 2 / depth 3 in Matrix B.

This worked example illustrates how those independently validated contracts compose architecturally. It is **not** presented as a separate combined-failure row unless such a row is explicitly added to the matrix.

That distinction matters.

The execution goal can therefore remain one durable logical computation while the process, runtime instance, local attempt, or Pod beneath parts of the plan changes over time.

That is the difference between merely orchestrating agent calls and providing durable execution authority beneath them.

---

## What actually differentiates this

Durable execution itself is not novel.

Mature workflow and agent systems already provide important capabilities such as durable waiting, sub-workflows, human-in-the-loop suspension, durable timers, checkpointing, and replay models.

The defensible question is therefore not whether durable execution is useful. It is what execution model and proof posture this runtime emphasizes.

### Durable-state resume instead of making workflow-history re-execution the primary recovery mechanism

A common durable-workflow model reconstructs execution by replaying workflow code against recorded history.

This runtime emphasizes a different recovery shape: durable execution state records what logical work completed, and recovery resumes remaining logical work under preserved execution identity.

A completed child is represented by durable child relation state rather than requiring the logical child to become a new execution.

The tradeoff is explicit: dedicated recursive-child replay remains a separate proof boundary in the current campaign.

That is why this document does not claim that one model universally replaces another. It documents a different execution contract and the evidence currently available for it.

### Distributed execution authority beyond an in-process checkpoint

A checkpoint is a durable save point. It is not, by itself, distributed mutation ownership.

This runtime places explicit ownership coordination underneath execution:

```text
SharedRun durable state
        +
claim token
        +
exact-owner compare-and-set
        +
runtime registry / capacity
        +
durable recovery authority
```

The adversarial matrix verifies selected ownership-transition behavior under real process and Pod failures and deterministic interleavings.

It does not claim that the matrix alone proves continuous interval exclusivity.

### Multi-tenant execution context is part of the durable execution model

Tenant and authorization context are not merely request metadata.

`ExecutionContextSnapshot` persists execution context beyond the lifetime of the original API or MCP request so queued work, recovery, and background continuation can restore the required tenant/RBAC context.

See [Multi-tenant control-plane isolation](multi-tenant-control-plane-isolation.md).

### The proof posture is itself part of the design

Many systems claim recovery.

This project publishes:

- a frozen correctness contract;
- deterministic semantic schedules;
- provider/transport parity projections;
- raw per-row artifacts;
- an evidence index;
- a content-addressable archive fingerprint;
- explicit `NOT_EVALUATED` fields for assertions not run.

"Validated as an invariant, with evidence attached" is not a substitute for production maturity, ecosystem, or operational history.

It is a disciplined way to state exactly what has been demonstrated.

---

## Closing: the same story under every failure

The most important decision in validating a durable AI runtime is made before any test runs: freezing what correctness *means*, and then refusing to let the adversary — or the code — quietly redefine it.

A green scenario proves the runtime can succeed.

A deterministic adversarial matrix proves something stronger and more useful: that the same logical computation remains exact when the failure moves along the lifecycle, moves down the recursive tree, is reordered under seeded interleavings, and is projected across transports and host boundaries — for the selected schedules that were actually executed.

The runtime may change its process, runtime instance, host, Pod, local attempt, execution ordering, and recovery binding.

The logical computation must not change with them.

> A system is not correct because it survived the failure we expected. It becomes credible when deliberately different failures cannot make it tell a different story about what actually executed.

Thirty-six schedules. One frozen definition of correctness. The same story every time.

---

## Evidence and related documentation

- [Adversarial Runtime Validation Matrix](adversarial-runtime-validation-matrix.md)
- [Adversarial Runtime Validation Evidence Index](adversarial-runtime-validation-evidence-index.md)
- [Raw validation archive](../files/adversarial-runtime-validation-logs.zip)
- [Durable Child DAG composition](child-dag-composition.md)
- [Runtime Pool architecture](runtime-pool-architecture.md)
- [Runtime Pool failure recovery](runtime-pool-failure-recovery.md)
- [Runtime Pool failure authority](runtime-pool-failure-authority.md)
- [Runtime Pool production validation](runtime-pool-production-validation.md)
- [Engine event observation and lifecycle catalog](engine-event-observation.md)
- [Runtime Lifecycle Journal](runtime-lifecycle-journal.md)
- [Runtime recovery forensics](runtime-recovery-forensics.md)
- [Replay and audit](replay-and-audit.md)
- [Multi-tenant control-plane isolation](multi-tenant-control-plane-isolation.md)
- [Testing strategy](testing-strategy.md)
- [Complete documentation index](../index.md)
