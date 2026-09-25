from __future__ import annotations

import asyncio
import json
import os
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from uuid import uuid4

from multiplexed_ai_sdk import (
    AiSdkClient,
    AiSdkExecutionInputSubmissionRequest,
    AiSdkExecutionReplayRequest,
    AiSdkExecutionStepStatus,
)

from .configuration import DemoConfiguration
from .pipeline import WAITING_KEY, WAITING_STEP_NAME, create_publication, create_submission

_POLL_SECONDS = 0.4
_DEFAULT_TIMEOUT_SECONDS = 20 * 60
_DEFAULT_PROMPT = "Validate the interactive agent topology through the public SDK boundary."
_DEFAULT_FEEDBACK = "topology-validation"
_TERMINAL = {"Completed", "Failed", "Cancelled"}


def is_automated_validation_mode() -> bool:
    return os.getenv("AI_DEMO_AUTOMATED_VALIDATION", "").strip().lower() in {
        "1", "true", "yes", "on"
    }


async def run_automated_validation(
    client: AiSdkClient,
    config: DemoConfiguration,
) -> int:
    started = _utc_now()
    runtime_provider = _required("AI_VALIDATION_RUNTIME_PROVIDER")
    runtime_transport = _required("AI_VALIDATION_RUNTIME_TRANSPORT")
    evidence_path = Path(_required("AI_DEMO_EVIDENCE_PATH"))
    prompt = os.getenv("AI_DEMO_VALIDATION_PROMPT", _DEFAULT_PROMPT).strip() or _DEFAULT_PROMPT
    feedback = os.getenv("AI_DEMO_VALIDATION_FEEDBACK", _DEFAULT_FEEDBACK)
    timeout_seconds = int(os.getenv("AI_DEMO_VALIDATION_TIMEOUT_SECONDS", str(_DEFAULT_TIMEOUT_SECONDS)))

    publication = await client.publish_pipeline(create_publication(config.openai_model))
    submission = await client.submit_execution(
        create_submission(publication.publication_ref, prompt)
    )
    execution_id = submission.execution_id

    deadline = asyncio.get_running_loop().time() + max(timeout_seconds, 1)
    waiting_boundary_reached = False
    input_response = None
    final_observation = None

    while True:
        if asyncio.get_running_loop().time() >= deadline:
            raise TimeoutError(
                f"Automated validation timed out after {timeout_seconds}s for {execution_id}."
            )

        observation = await client.observe_execution(execution_id)
        final_observation = observation
        waiting_step = next(
            (step for step in observation.steps if step.name == WAITING_STEP_NAME),
            None,
        )

        if (
            input_response is None
            and waiting_step is not None
            and waiting_step.status is AiSdkExecutionStepStatus.WAITING_FOR_EXTERNAL
        ):
            waiting_boundary_reached = True
            input_response = await client.submit_execution_input(
                execution_id,
                AiSdkExecutionInputSubmissionRequest(
                    waiting_key=WAITING_KEY,
                    waiting_step_name=WAITING_STEP_NAME,
                    reason="interactive-agent-topology-validation",
                    correlation_id=f"interactive-agent-validation-input-{uuid4().hex}",
                    input={"approved": True, "feedback": feedback},
                ),
            )
            if not input_response.accepted:
                raise RuntimeError("Automated human input was not accepted.")
            if input_response.execution_id != execution_id:
                raise RuntimeError("Human input changed the durable ExecutionId.")

        if observation.status.value in _TERMINAL:
            break

        await asyncio.sleep(_POLL_SECONDS)

    if final_observation is None:
        raise RuntimeError("No terminal observation was produced.")
    if input_response is None or not waiting_boundary_reached:
        raise RuntimeError("Human-review waiting boundary was not reached.")

    result = await client.get_execution_result(execution_id)
    replay = await client.replay_execution(
        execution_id,
        AiSdkExecutionReplayRequest(
            strict_determinism=True,
            include_diagnostics=True,
            reason="interactive-agent-topology-validation",
            correlation_id=f"interactive-agent-validation-replay-{uuid4().hex}",
        ),
    )

    published = _published_result(result.output)
    provider = _read_string(published, "providerKey") or "openai"
    model = _read_string(published, "model") or config.openai_model
    request_id = _find_request_id(published)

    evidence = {
        "contractVersion": 1,
        "scenarioId": "interactive-agent-sdk-canonical-v1",
        "sdkClient": "python",
        "runtimeProvider": runtime_provider,
        "runtimeTransport": runtime_transport,
        "publication": {
            "accepted": True,
            "publicationRef": publication.publication_ref,
            "pipelineName": publication.pipeline_name,
            "pipelineVersion": publication.pipeline_version,
        },
        "execution": {
            "executionId": execution_id,
            "executionIdAfterHumanInput": input_response.execution_id,
            "sameExecutionIdAfterHumanInput": input_response.execution_id == execution_id,
            "status": result.status.value,
        },
        "humanReview": {
            "waitingBoundaryReached": waiting_boundary_reached,
            "waitingStepName": WAITING_STEP_NAME,
            "waitingKey": WAITING_KEY,
            "accepted": input_response.accepted,
            "approved": True,
            "feedback": feedback,
        },
        "provider": {
            "name": provider,
            "model": model,
            "responsePayloadPresent": published is not None,
        },
        "rootSteps": {
            step.name: step.status.value
            for step in final_observation.steps
            if step.name in {
                "plan", "delegate-analysis", "await-review", "final-answer", "publish-result"
            }
        },
        "result": {
            "available": result.output is not None,
            "requestId": request_id,
        },
        "replay": {
            "succeeded": replay.succeeded,
            "deterministic": replay.deterministic is True,
            "executionIdUnchanged": result.execution_id == execution_id,
        },
        "timing": {
            "startedAtUtc": started,
            "completedAtUtc": _utc_now(),
        },
        "diagnostics": {
            "replayMessage": replay.message,
            "replayFailureReason": replay.failure_reason,
            "terminalCompletedAtUtc": str(result.completed_at_utc),
        },
    }

    evidence_path.parent.mkdir(parents=True, exist_ok=True)
    evidence_path.write_text(json.dumps(evidence, indent=2, ensure_ascii=False), encoding="utf-8")

    return 0 if _evidence_passes(evidence) else 1


def _evidence_passes(evidence: dict[str, Any]) -> bool:
    return (
        evidence["execution"]["status"] == "Completed"
        and evidence["execution"]["sameExecutionIdAfterHumanInput"] is True
        and evidence["humanReview"]["accepted"] is True
        and all(
            evidence["rootSteps"].get(name) == "Completed"
            for name in ("plan", "delegate-analysis", "await-review", "final-answer", "publish-result")
        )
        and evidence["provider"]["name"] == "openai"
        and bool(evidence["provider"]["model"])
        and evidence["provider"]["responsePayloadPresent"] is True
        and evidence["result"]["available"] is True
        and evidence["replay"]["succeeded"] is True
        and evidence["replay"]["deterministic"] is True
        and evidence["replay"]["executionIdUnchanged"] is True
    )


def _published_result(output: Any) -> Any:
    if isinstance(output, dict) and "result" in output:
        return output["result"]
    return output


def _read_string(value: Any, name: str) -> str | None:
    if not isinstance(value, dict):
        return None
    candidate = value.get(name)
    return candidate if isinstance(candidate, str) else None


def _find_request_id(value: Any) -> str | None:
    if isinstance(value, dict):
        candidate = value.get("requestId")
        if isinstance(candidate, str):
            return candidate
        for child in value.values():
            found = _find_request_id(child)
            if found is not None:
                return found
    elif isinstance(value, list):
        for child in value:
            found = _find_request_id(child)
            if found is not None:
                return found
    return None


def _required(name: str) -> str:
    value = os.getenv(name, "").strip()
    if not value:
        raise RuntimeError(f"Missing required automated-validation configuration '{name}'.")
    return value


def _utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
