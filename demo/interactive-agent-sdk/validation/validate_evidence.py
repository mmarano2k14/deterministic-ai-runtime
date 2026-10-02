from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

ROOT_STEPS = ("plan", "delegate-analysis", "await-review", "final-answer", "publish-result")
SDK_CLIENTS = {"dotnet", "typescript", "python"}
PROVIDERS = {"ProcessHostPool", "KubernetesPool"}
TRANSPORTS = {"HTTP", "gRPC"}


def validate(data: dict[str, Any]) -> list[str]:
    errors: list[str] = []
    expect(errors, data.get("contractVersion") == 1, "contractVersion must be 1")
    expect(errors, data.get("scenarioId") == "interactive-agent-sdk-canonical-v1", "unexpected scenarioId")
    expect(errors, data.get("sdkClient") in SDK_CLIENTS, "unexpected sdkClient")
    expect(errors, data.get("runtimeProvider") in PROVIDERS, "unexpected runtimeProvider")
    expect(errors, data.get("runtimeTransport") in TRANSPORTS, "unexpected runtimeTransport")

    topology = object_value(data, "topology", errors)
    expect(errors, topology.get("schemaVersion") == 1, "topology.schemaVersion must be 1")
    requested_topology = object_value(topology, "requested", errors)
    observed_topology = object_value(topology, "observed", errors)
    expect(errors, topology.get("verified") is True, "topology.verified must be true")
    expect(errors, requested_topology.get("runtimeProvider") == data.get("runtimeProvider"), "topology requested provider mismatch")
    expect(errors, requested_topology.get("runtimeTransport") == data.get("runtimeTransport"), "topology requested transport mismatch")
    expect(errors, observed_topology.get("runtimeProvider") == data.get("runtimeProvider"), "topology observed provider mismatch")
    expect(errors, observed_topology.get("runtimeTransport") == data.get("runtimeTransport"), "topology observed transport mismatch")
    checks = topology.get("checks")
    expect(errors, isinstance(checks, list) and bool(checks), "topology.checks must be a non-empty array")
    if isinstance(checks, list):
        expect(errors, all(isinstance(item, dict) and item.get("passed") is True for item in checks), "all topology checks must pass")
    expect_nonempty(errors, topology.get("verifiedAtUtc"), "topology.verifiedAtUtc")

    publication = object_value(data, "publication", errors)
    expect(errors, publication.get("accepted") is True, "publication.accepted must be true")
    expect_nonempty(errors, publication.get("publicationRef"), "publication.publicationRef")
    expect_nonempty(errors, publication.get("pipelineName"), "publication.pipelineName")
    expect(errors, publication.get("pipelineVersion") == "1", "publication.pipelineVersion must be 1")

    execution = object_value(data, "execution", errors)
    expect_nonempty(errors, execution.get("executionId"), "execution.executionId")
    expect_nonempty(errors, execution.get("executionIdAfterHumanInput"), "execution.executionIdAfterHumanInput")
    expect(errors, execution.get("sameExecutionIdAfterHumanInput") is True, "ExecutionId must remain unchanged")
    expect(errors, execution.get("status") == "Completed", "execution.status must be Completed")

    review = object_value(data, "humanReview", errors)
    expect(errors, review.get("waitingBoundaryReached") is True, "human review boundary not reached")
    expect(errors, review.get("waitingStepName") == "await-review", "unexpected waitingStepName")
    expect(errors, review.get("waitingKey") == "interactive-agent-review", "unexpected waitingKey")
    expect(errors, review.get("accepted") is True, "human input not accepted")
    expect(errors, review.get("approved") is True, "human review must be approved")

    provider = object_value(data, "provider", errors)
    expect(errors, provider.get("name") == "openai", "provider.name must be openai")
    expect_nonempty(errors, provider.get("model"), "provider.model")
    expect(errors, provider.get("responsePayloadPresent") is True, "provider response payload missing")

    steps = object_value(data, "rootSteps", errors)
    for name in ROOT_STEPS:
        expect(errors, steps.get(name) == "Completed", f"rootSteps.{name} must be Completed")

    result = object_value(data, "result", errors)
    expect(errors, result.get("available") is True, "public result missing")

    replay = object_value(data, "replay", errors)
    expect(errors, replay.get("succeeded") is True, "replay did not succeed")
    expect(errors, replay.get("deterministic") is True, "replay is not deterministic")
    expect(errors, replay.get("executionIdUnchanged") is True, "replay changed execution identity")

    timing = object_value(data, "timing", errors)
    expect_nonempty(errors, timing.get("startedAtUtc"), "timing.startedAtUtc")
    expect_nonempty(errors, timing.get("completedAtUtc"), "timing.completedAtUtc")
    return errors


def object_value(data: dict[str, Any], name: str, errors: list[str]) -> dict[str, Any]:
    value = data.get(name)
    if not isinstance(value, dict):
        errors.append(f"{name} must be an object")
        return {}
    return value


def expect(errors: list[str], condition: bool, message: str) -> None:
    if not condition:
        errors.append(message)


def expect_nonempty(errors: list[str], value: Any, name: str) -> None:
    expect(errors, isinstance(value, str) and bool(value.strip()), f"{name} must be a non-empty string")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("evidence", type=Path)
    args = parser.parse_args()
    data = json.loads(args.evidence.read_text(encoding="utf-8"))
    errors = validate(data)
    if errors:
        print(f"Evidence validation FAILED: {args.evidence}")
        for error in errors:
            print(f"- {error}")
        return 1
    print(f"Evidence validation PASS: {args.evidence}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
