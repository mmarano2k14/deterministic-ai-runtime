from __future__ import annotations

import json
import sys
import time
from pathlib import Path

ROOT = Path("/matrix/evidence")
CLIENTS = ("dotnet", "typescript", "python")
WORKERS = ("dotnet", "typescript", "python")
EXPECTED = [f"core-{client}-client-{worker}-worker" for client in CLIENTS for worker in WORKERS]


def main() -> int:
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline and any(not (ROOT / f"{scenario}.json").exists() for scenario in EXPECTED):
        time.sleep(0.25)
    failures: list[str] = []
    for scenario in EXPECTED:
        path = ROOT / f"{scenario}.json"
        if not path.exists():
            failures.append(f"{scenario}: missing evidence")
            continue
        document = json.loads(path.read_text(encoding="utf-8-sig"))
        required = {"publish", "submit", "observe", "terminal-result", "public-execution-id"}
        if document.get("scenarioId") != scenario or document.get("status") != "passed":
            failures.append(f"{scenario}: invalid status or identity")
        elif document.get("topology") != "docker" or document.get("provider") != "ProcessHostPool":
            failures.append(f"{scenario}: wrong topology/provider evidence")
        elif not required.issubset(set(document.get("evidence", []))):
            failures.append(f"{scenario}: missing required evidence")
        else:
            print(f"{scenario}: PASSED")
    if failures:
        for failure in failures:
            print(failure, file=sys.stderr)
        return 1
    print("9/9 production-like Docker ProcessHostPool scenarios passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
