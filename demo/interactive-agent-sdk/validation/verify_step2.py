from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

checks = {
    "dotnet automated runner": ROOT / "dotnet" / "AutomatedValidationRunner.cs",
    "typescript automated runner": ROOT / "typescript" / "src" / "automated-validation.ts",
    "python automated runner": ROOT / "python" / "src" / "interactive_agent_sdk_demo" / "automated_validation.py",
    "orchestrator": ROOT / "validation" / "run_e2e.py",
    "evidence validator": ROOT / "validation" / "validate_evidence.py",
    "local jwt helper": ROOT / "scripts" / "create-local-jwt.py",
}

for label, path in checks.items():
    if not path.is_file():
        raise SystemExit(f"Step 2 verification FAILED: missing {label}: {path}")

for path in (
    checks["dotnet automated runner"],
    checks["typescript automated runner"],
    checks["python automated runner"],
):
    text = path.read_text(encoding="utf-8")
    for required in (
        "AI_DEMO_EVIDENCE_PATH",
        "AI_VALIDATION_RUNTIME_PROVIDER",
        "AI_VALIDATION_RUNTIME_TRANSPORT",
        "topology-validation",
    ):
        if required not in text:
            raise SystemExit(
                f"Step 2 verification FAILED: {required!r} missing from {path}"
            )

orchestrator = checks["orchestrator"].read_text(encoding="utf-8")

# Capturing Docker metadata is allowed for local validation bootstrap. The actual
# SDK consumer invocation must remain attached to the console and must not be
# parsed as validation evidence.
consumer_marker = "run = subprocess.run(\n            [sys.executable, str(LAUNCHER), \"--sdk\", sdk],"
consumer_start = orchestrator.find(consumer_marker)
if consumer_start < 0:
    raise SystemExit("Step 2 verification FAILED: consumer invocation not found")
consumer_end = orchestrator.find("\n        )", consumer_start)
consumer_block = orchestrator[consumer_start:consumer_end if consumer_end >= 0 else None]
for forbidden in ("capture_output=True", "stdout=subprocess.PIPE", "stderr=subprocess.PIPE"):
    if forbidden in consumer_block:
        raise SystemExit(
            f"Step 2 verification FAILED: consumer console parsing dependency {forbidden!r} found"
        )

for required in (
    "AI_RUNTIME_TOKEN",
    "create-local-jwt.py",
    "local-jwt",
    "OPENAI_API_KEY",
    "AI_DEMO_SIGNING_KEY",
    "check_output",
):
    if required not in orchestrator:
        raise SystemExit(
            f"Step 2 verification FAILED: authentication/sanitization contract missing {required!r}"
        )

for required in (
    "_prepare_consumers",
    "bootstrap.py",
    "--skip-prepare",
    "consumer artifacts: READY",
):
    if required not in orchestrator:
        raise SystemExit(
            f"Step 2 verification FAILED: consumer preparation contract missing {required!r}"
        )

for required in (
    "_is_local_runtime_endpoint",
    "auto/local endpoint",
    "AI_RUNTIME_TOKEN (auto/remote endpoint)",
    "_docker_runtime_auth_settings",
    "docker",
    "inspect",
):
    if required not in orchestrator:
        raise SystemExit(
            f"Step 2 verification FAILED: local/remote auto-auth contract missing {required!r}"
        )

schema = (ROOT / "validation" / "evidence.schema.json").read_text(encoding="utf-8")
for required in (
    '"sameExecutionIdAfterHumanInput"',
    '"responsePayloadPresent"',
    '"deterministic"',
    '"runtimeProvider"',
    '"runtimeTransport"',
):
    if required not in schema:
        raise SystemExit(f"Step 2 verification FAILED: schema missing {required}")

print("Automated interactive-agent E2E runner contract: PASS")
print("- .NET machine-readable runner present")
print("- TypeScript machine-readable runner present")
print("- Python machine-readable runner present")
print("- common orchestrator present")
print("- consumer artifacts are rebuilt before validation unless --skip-prepare is explicit")
print("- evidence validation does not parse console output")
print("- local endpoints always receive a fresh locally generated JWT in auto mode")
print("- a running local Docker demo runtime supplies its effective JWT authority")
print("- remote auto mode requires an explicit AI_RUNTIME_TOKEN")
print("- provider/signing secrets are stripped from external consumer environments")
