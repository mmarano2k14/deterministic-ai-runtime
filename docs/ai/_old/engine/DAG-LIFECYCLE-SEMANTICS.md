# 🧠 Distributed DAG Execution — Lifecycle Semantics (Full Specification)

This document defines the **complete and authoritative lifecycle semantics** for distributed DAG execution in the AI runtime.

It formalizes:

- Deterministic execution rules
- Distributed convergence behavior
- Step vs global state separation
- Retry, failure, and waiting semantics
- Terminal conditions

This specification is intended to be **production-grade** and remove ambiguity across distributed workers.

---

# 1. Core Principles

## Determinism

Execution outcome must be:

- Independent of worker count
- Independent of execution order (within DAG constraints)

## Distributed Safety

- Multiple workers can operate safely
- No double execution
- No race-condition corruption

## State-Driven Execution

> Execution is derived from state, not driven by events.

---

# 2. Global Execution Status

| Status        | Description                                                                 | Terminal |
|---------------|-----------------------------------------------------------------------------|----------|
| Initializing  | Execution created, pipeline resolved                                        | No       |
| Running       | Work exists or is currently executing                                       | No       |
| Waiting       | No work currently executable, but execution is not finished                 | No       |
| Completed     | All required steps completed successfully                                   | Yes      |
| Failed        | Execution cannot complete successfully                                      | Yes      |
|---------------|-----------------------------------------------------------------------------|----------|

---

## Rules

- Running includes runnable steps
- Waiting is temporary
- Completed and Failed are terminal
- No transitions allowed from terminal states

---

# 3. Step State Model

Each step maintains:

- Status (Pending, Running, Completed, Failed, Retrying)
- RetryCount
- LastError
- Output

---

# 4. Transition Matrix

## Allowed

Initializing → Running  
Running ↔ Waiting  
Running → Completed  
Waiting → Completed  
Running → Failed  
Waiting → Failed  

## Forbidden

Completed → *  
Failed → *  

---

# 5. Completion Semantics

Execution is Completed when:

- All steps are Completed
- No Running steps
- No Pending steps
- No Retrying steps
- No Claim locks exist

---

# 6. Failure Semantics

Execution is Failed when:

- At least one step has terminal failure
- AND no valid execution path remains

---

## Failure Modes

### Strict Mode
Any failure = DAG failure

### Tolerant Mode
Only critical steps cause failure

---

# 7. Waiting Semantics

Waiting occurs when:

- Dependencies unresolved
- Steps claimed by others
- Retry delay active
- Timeout recovery pending

---

## Important

Waiting ≠ failure  
Waiting ≠ deadlock  

---

# 8. Running Semantics

Running if:

- At least one step executing
- OR at least one step ready

---

# 9. Distributed Convergence

Each worker loop:

1. Load execution state
2. Select runnable step
3. Attempt atomic claim (Redis/Lua)
4. Execute step
5. Persist result
6. Re-evaluate global state

---

## Key Property

> No central orchestrator required

---

# 10. Retry Semantics

Per step:

- MaxRetryCount
- Backoff strategy
- Retryable exceptions

---

## Impact

Retry keeps execution in Running or Waiting  
Failure only occurs after retry exhaustion  

---

# 11. Convergence Rules

Execution must:

- Eventually reach terminal state
- Avoid infinite loops
- Avoid zombie execution

---

# 12. Safety Guarantees

- Atomic step claim
- Idempotent execution
- Optimistic concurrency
- Versioned updates

---

# 13. Helper Functions (Conceptual)

IsCompleted = all steps completed  
IsFailed = terminal failure exists  
CanContinue = not terminal  

---

# 14. Anti-Patterns (Must Avoid)

- Manual global state mutation
- Non-atomic updates
- Step re-execution without guard
- Blocking execution on Waiting

---

# 15. Future Extensions

- RAG integration
- LLM step execution
- Priority scheduling
- Distributed rate limiting

---

# 🚀 Next Steps

1. Implement convergence evaluation in engine
2. Add retry engine
3. Add failure propagation rules
4. Harden Redis Lua store
5. Introduce immutable ExecutionState V2
