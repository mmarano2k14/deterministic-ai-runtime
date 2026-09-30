# Interactive validation replay readiness

- Added bounded audit-replay readiness to the .NET, TypeScript, and Python demo consumers.
- Retry is restricted to the exact public snapshot-missing response after a Completed observation and result.
- Preserved ExecutionId, replay request correlation, strict determinism, and diagnostic request flags across attempts.
- Added a 30-second stage deadline with cancellation and a 250-millisecond retry interval.
- Preserved nontransient failures, determinism failures, authentication/transport errors, and identity mismatches as validation failures.
- Added per-attempt readiness tracing and aggregate attempt/miss/elapsed diagnostics to existing evidence documents.
- Preserved all existing gate predicates and evidence schema/validator behavior.
- Added offline behavioral suites for all three consumers.
- Python and TypeScript suites passed; TypeScript compilation and existing trace/launcher checks passed.
- The .NET suite is supplied but remains unexecuted in the delivery environment.
- No production runtime, snapshot persistence, replay engine, SDK contract, topology, authorization, queue, provider credential, or PERF attribution changes.
