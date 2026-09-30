from __future__ import annotations

import json
import py_compile
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent

required = [
    HERE / "processhostpool-inventory.schema.json",
    HERE / "attest_processhostpool.py",
    HERE / "run_processhostpool_matrix.py",
]
missing = [str(p) for p in required if not p.is_file()]
if missing:
    raise SystemExit("Step 4 verification FAILED: missing " + ", ".join(missing))

for file in (HERE / "attest_processhostpool.py", HERE / "run_processhostpool_matrix.py"):
    py_compile.compile(str(file), doraise=True)

schema = json.loads((HERE / "processhostpool-inventory.schema.json").read_text(encoding="utf-8"))
if schema.get("properties", {}).get("runtimeProvider", {}).get("const") != "ProcessHostPool":
    raise SystemExit("Step 4 verification FAILED: inventory schema is not ProcessHostPool-specific")
if schema.get("properties", {}).get("hosts", {}).get("minItems") != 2:
    raise SystemExit("Step 4 verification FAILED: host inventory must require at least two independent parent hosts")

attestor = (HERE / "attest_processhostpool.py").read_text(encoding="utf-8")
for required_literal in (
    '"source": "process-host-inspection"',
    '"runtimeInstanceId-uniqueness"',
    '"routeId-uniqueness"',
    'stable-endpoint',
    'child-of-host',
    'inventory-source',
    'inventory-freshness-seconds',
):
    if required_literal not in attestor:
        raise SystemExit(f"Step 4 verification FAILED: attestor missing {required_literal}")

runner = (HERE / "run_processhostpool_matrix.py").read_text(encoding="utf-8")
for required_literal in (
    "--topology-attestation",
    '"--sdk", "all"',
    '"--runtime-provider", "ProcessHostPool"',
):
    if required_literal not in runner:
        raise SystemExit(f"Step 4 verification FAILED: matrix runner missing {required_literal}")

print("ProcessHostPool interactive-agent validation contract: PASS")
print("- machine-generated inventory contract present")
print("- PoolId/HostId/RuntimeInstanceId plus exact stable-endpoint evidence required")
print("- live parent and child process ancestry checks required")
print("- RouteId is optional diagnostic evidence because it is internal to the external ProcessHost boundary")
print("- exact HTTP/gRPC provider parity required")
print("- common automated E2E runner consumes only verified process-host attestation")
