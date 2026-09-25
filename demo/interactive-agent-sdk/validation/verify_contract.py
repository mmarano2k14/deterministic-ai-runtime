from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CONTRACT = json.loads((Path(__file__).parent / "interactive-agent-scenario.json").read_text(encoding="utf-8"))

SOURCES = {
    "dotnet": ROOT / "dotnet" / "InteractiveAgentPipeline.cs",
    "typescript": ROOT / "typescript" / "src" / "pipeline.ts",
    "python": ROOT / "python" / "src" / "interactive_agent_sdk_demo" / "pipeline.py",
}

required_literals = {
    "root_step_names": [step["name"] for step in CONTRACT["rootPipeline"]["steps"]],
    "root_step_keys": [step["stepKey"] for step in CONTRACT["rootPipeline"]["steps"]],
    "child_step_names": [step["name"] for step in CONTRACT["childPipeline"]["steps"]],
    "child_step_keys": [step["stepKey"] for step in CONTRACT["childPipeline"]["steps"]],
    "waiting": [
        CONTRACT["humanReview"]["waitingStepName"],
        CONTRACT["humanReview"]["waitingKey"],
    ],
    "provider": [CONTRACT["provider"]["kind"]],
}

failures: list[str] = []
for language, path in SOURCES.items():
    text = path.read_text(encoding="utf-8")
    for group, literals in required_literals.items():
        for literal in literals:
            if literal not in text:
                failures.append(f"{language}: missing {group} literal {literal!r}")

if failures:
    print("Canonical interactive-agent contract: FAIL")
    for failure in failures:
        print(f"- {failure}")
    raise SystemExit(1)

print("Canonical interactive-agent contract: PASS")
for language in SOURCES:
    print(f"- {language}: semantic pipeline literals present")
