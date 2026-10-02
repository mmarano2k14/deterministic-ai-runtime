# Child snapshot trace upgrade

- Updated existing generated child trace predicates instead of treating gate presence as proof of current installation.
- Added Mongo snapshot acknowledgement and terminal snapshot lifecycle markers to the gated trace.
- Added tee-observation UTC timestamps without claiming database commit timestamps.
- Qualified the environment lookup with System.Environment for fresh and existing installations.
- Rejected missing, ambiguous, or unrecognized generated trace blocks before writing files.
- Preserved non-diagnostic drains, authorization, queue behavior, snapshot/replay semantics, provider settings, and PERF behavior.
- Added upgrade/idempotency regression tests and recorded source-level verification.

Validation: Python regression suite, actual-source transformation, staged full-installer idempotency, and existing launcher verifier passed. .NET compilation and live runtime validation were not performed.
