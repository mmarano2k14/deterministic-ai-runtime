# TODO — Phase 3: Recovery Hardening

## Workstream 1 — Formalize step lease model

### Tasks
- [x] Review current `Running` step ownership model
- [x] Ensure step metadata clearly supports:
  - [x] `ClaimedAtUtc`
  - [x] `LeaseExpiresAtUtc`
  - [x] `ClaimOwner` or equivalent
  - [x] `ClaimToken` or equivalent exclusive ownership marker
- [x] Define lease expiration semantics
- [x] Define reclaim eligibility semantics
- [x] Define interaction between retry timing and reclaim timing
- [x] Document legal state transitions for claimed steps

---

## Workstream 2 — Harden timed-out step recovery

### Tasks
- [x] Review `TryRecoverTimedOutStepsAsync` behavior
- [x] Ensure reclaim is atomic in distributed mode
- [x] Ensure only expired running steps are reclaimable
- [x] Ensure completed or failed steps cannot be reclaimed
- [x] Ensure retry waiting steps are not incorrectly reclaimed as running steps
- [x] Ensure reclaim renews lease metadata correctly
- [x] Ensure reclaim does not duplicate execution rights
- [x] Ensure reclaim is safe under repeated calls

---

## Workstream 3 — Define crash consistency rules

### Tasks
- [x] Enumerate all critical transitions:
  - [x] claim
  - [x] step execution start
  - [x] completion persist
  - [x] failure persist
  - [x] retry scheduling
  - [x] retry reclaim
  - [x] convergence evaluation
  - [x] terminal persist
  - [x] cleanup trigger
- [ ] For each transition, document:
  - [ ] crash before transition
  - [ ] crash during transition
  - [ ] crash after transition
  - [ ] replay behavior
  - [ ] concurrency collision behavior
- [ ] Mark each transition as:
  - [ ] atomic
  - [ ] idempotent
  - [ ] monotonic
  - [ ] best-effort only

---

## Workstream 4 — Couple recovery with convergence

### Tasks
- [x] Revalidate convergence rules for:
  - [x] `Running`
  - [x] `Waiting`
  - [x] `Completed`
  - [x] `Failed`
- [ ] Ensure `Completed` requires:
  - [ ] all steps completed
  - [ ] no valid running claims
  - [ ] no reclaimable steps
  - [ ] no waiting retries
- [ ] Ensure `Failed` requires:
  - [ ] terminal failure condition reached
  - [ ] no remaining valid retry path
  - [ ] no valid running claim
  - [ ] no reclaimable work that could still change the outcome
- [x] Ensure `Waiting` includes:
  - [x] future retry eligibility
  - [x] active running lease not yet expired
  - [x] blocked dependency scenarios with possible future progress
- [ ] Ensure terminal status cannot regress

---

## Workstream 5 — Protect terminal finalization

### Tasks
- [ ] Review distributed terminal persistence flow
- [ ] Ensure finalization is optimistic-concurrency safe
- [ ] Ensure finalization is replay-safe
- [ ] Ensure terminal state is monotonic
- [ ] Ensure no worker can finalize while another valid execution path still exists
- [ ] Ensure post-recovery re-evaluation leads to the same terminal result
- [ ] Ensure cleanup trigger only occurs after authoritative terminal persistence

---

## Workstream 6 — Add expert-level recovery tests

### Core tests
- [x] Only one worker can reclaim an expired running step
- [x] No reclaim before lease expiration
- [x] Reclaim fails if step completed in the meantime
- [x] Crash after claim, before completion, allows safe reclaim
- [x] Crash during retry scheduling does not lose retry state
- [x] Crash during convergence does not corrupt final state
- [x] Crash during finalization remains replay-safe
- [x] Execution cannot remain stuck forever when forward progress is still possible
- [x] Recovery operations are idempotent under repeated scans
- [x] High concurrency reclaim test with 20 workers
- [x] High concurrency reclaim test with 50 workers

### Optional advanced tests
- [x] Simulated Redis latency during recovery
- [x] Simulated worker death mid-retry
- [x] Simulated duplicate recovery scan calls
- [x] Simulated claim token race across workers

---

## Deliverables

- [x] Lease semantics formalized
- [x] Timed-out step recovery hardened
- [ ] Crash consistency rules documented
- [ ] Recovery-aware convergence finalized
- [ ] Terminal persistence hardened
- [x] Recovery integration test suite expanded
- [ ] Project documentation updated

---

## Definition of done

Phase 3 is complete when:

- [x] abandoned running steps can be safely reclaimed
- [x] recovery is atomic in distributed mode
- [x] retries and reclaim do not conflict
- [ ] convergence reflects future recoverable work correctly
- [ ] terminal state cannot be persisted prematurely
- [ ] crash scenarios are documented and covered by tests
- [x] repeated recovery attempts do not corrupt state
- [x] runtime behavior remains deterministic under concurrency