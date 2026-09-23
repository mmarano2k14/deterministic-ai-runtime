from __future__ import annotations

import argparse
import asyncio
import ast
import base64
import json
import os
import sys
import tomllib
import uuid
import urllib.request
import urllib.parse
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[4]
SDK_SRC = REPO_ROOT / "implementations" / "python" / "sdk" / "src"
if str(SDK_SRC) not in sys.path:
    sys.path.insert(0, str(SDK_SRC))

from multiplexed_ai_sdk import (  # noqa: E402
    AiSdkClient,
    AiSdkCredential,
    AiSdkExecutionCancellationRequest,
    AiSdkExecutionControlRequest,
    AiSdkExecutionInputSubmissionRequest,
    AiSdkExecutionReplayRequest,
    AiSdkExecutionMode,
    AiSdkException,
    AiSdkExecutionStatus,
    AiSdkExecutionStepStatus,
    AiSdkExecutionSubmissionRequest,
    AiSdkExecutionWatchEventKind,
    AiSdkExecutionWatchRequest,
    AiSdkInvocationDefinition,
    AiSdkInvocationKind,
    AiSdkMcpHttpTransport,
    AiSdkPipelineDefinition,
    AiSdkPipelinePublicationRequest,
    AiSdkPipelineStepDefinition,
    AiSdkPublicationCallSite,
    AiSdkPublicationFileUpload,
    AiSdkPublicationFunctionKind,
    AiSdkPublicationFunctionUpload,
    AiSdkStaticCredentialProvider,
    AiSdkTransportOptions,
)


def _diagnostic(args: argparse.Namespace, message: str) -> None:
    line = f"[matrix-python-client] {message}"
    print(line, flush=True)
    diagnostic_log = getattr(args, "diagnostic_log", None)
    if diagnostic_log:
        path = Path(diagnostic_log).resolve()
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("a", encoding="utf-8") as stream:
            stream.write(line + "\n")


def _diagnostic_failure(args: argparse.Namespace, stage: str, exception: Exception) -> None:
    if isinstance(exception, AiSdkException):
        error = exception.error
        details = json.dumps(error.details, ensure_ascii=False, sort_keys=True, default=str)
        _diagnostic(
            args,
            f"{stage} FAILED AiSdkException kind={error.kind} code={error.code} "
            f"retryable={str(error.retryable).lower()} message={error.message} details={details}",
        )
        return
    _diagnostic(args, f"{stage} FAILED {type(exception).__name__}: {exception}")


async def main() -> int:
    args = _parse_args()
    _diagnostic(
        args,
        f"START scenario={args.scenario_id} worker={args.worker} topology={args.topology} "
        f"runtimeProvider={args.runtime_provider} environmentRef={args.environment_ref} endpoint={args.endpoint}",
    )
    if args.feature == "dependency-firewall":
        _run_dependency_firewall(args)
        return 0

    provider = (
        AiSdkStaticCredentialProvider(AiSdkCredential("Bearer", args.token))
        if args.token
        else None
    )
    client = AiSdkClient(
        AiSdkMcpHttpTransport(
            args.endpoint,
            AiSdkTransportOptions(
                credential_provider=provider,
                additional_headers=(
                    {args.access_context_header: args.access_context}
                    if args.access_context
                    else None
                ),
            ),
        )
    )
    if args.feature == "control":
        await _run_control(client, args)
        return 0
    source = _cancellation_worker_source(args.worker) if args.feature in {"cancellation", "watch"} else _worker_source(args.worker)
    marker = f"{args.scenario_id}-marker"

    _diagnostic(args, "PUBLISH start")
    try:
        publication = await client.publish_pipeline(
            AiSdkPipelinePublicationRequest(
                definition=AiSdkPipelineDefinition(
                    name=f"matrix-{args.scenario_id}",
                    version="1",
                    execution_language=args.worker,
                    execution_mode=AiSdkExecutionMode.DAG,
                    steps=(
                        AiSdkPipelineStepDefinition(
                            name="work",
                            step_key="custom",
                            order=0,
                            execution_language=args.worker,
                            invocation=AiSdkInvocationDefinition(kind=AiSdkInvocationKind.CUSTOM),
                            input={"marker": marker},
                        ),
                    ),
                ),
                functions=(
                    AiSdkPublicationFunctionUpload(
                        site=AiSdkPublicationCallSite(
                            kind=AiSdkPublicationFunctionKind.STEP,
                            step_name="work",
                        ),
                        environment_ref=args.environment_ref,
                        entry_point_path=source["entry_point_path"],
                        entry_point_symbol=source["entry_point_symbol"],
                        sources=(
                            AiSdkPublicationFileUpload(
                                path=source["entry_point_path"],
                                content_base64=base64.b64encode(source["bytes"]).decode("ascii"),
                            ),
                        ),
                    ),
                ),
            )
        )
    except Exception as exception:
        _diagnostic_failure(args, "PUBLISH", exception)
        raise
    _diagnostic(args, f"PUBLISH succeeded publicationRef={publication.publication_ref}")

    _diagnostic(args, "SUBMIT start")
    try:
        submitted = await client.submit_execution(
            AiSdkExecutionSubmissionRequest(
                publication_ref=publication.publication_ref,
                idempotency_key=f"{args.scenario_id}-{uuid.uuid4().hex}",
                input={"marker": marker},
                metadata={
                    "matrix.scenario": args.scenario_id,
                    "matrix.client": "python",
                    "matrix.worker": args.worker,
                },
            )
        )
    except Exception as exception:
        _diagnostic_failure(args, "SUBMIT", exception)
        raise
    _diagnostic(args, f"SUBMIT succeeded executionId={submitted.execution_id} status={submitted.status.value}")

    if args.feature == "cancellation":
        active = await _wait_for_active_step(client, submitted.execution_id, "work", 30.0)
        correlation_id = f"cancel-{uuid.uuid4().hex}"
        cancellation = await client.cancel_execution(
            submitted.execution_id,
            AiSdkExecutionCancellationRequest(
                reason="matrix-running-cancellation",
                correlation_id=correlation_id,
            ),
        )
        if cancellation.execution_id != submitted.execution_id or cancellation.cancellation_requested is not True:
            raise RuntimeError("Public cancellation operation did not acknowledge the submitted execution.")
        if cancellation.requested_at_utc is None or cancellation.correlation_id != correlation_id:
            raise RuntimeError("Public cancellation acknowledgement did not preserve durable request metadata.")

        observation = await _wait_for_terminal(client, submitted.execution_id, 45.0)
        result = await client.get_execution_result(submitted.execution_id)
        if observation.status != AiSdkExecutionStatus.CANCELLED or result.status != AiSdkExecutionStatus.CANCELLED:
            raise RuntimeError(
                f"Cancellation scenario ended as observation='{observation.status.value}', result='{result.status.value}', expected 'Cancelled'."
            )
        if result.output is not None or result.failure is not None:
            raise RuntimeError("Cancelled public result unexpectedly exposed completed output or failure payload.")
        active_step = next((step for step in active.steps if step.name == "work"), None)
        terminal_step = next((step for step in observation.steps if step.name == "work"), None)
        _write_evidence(
            Path(args.evidence),
            {
                "schemaVersion": 1,
                "scenarioId": args.scenario_id,
                "status": "passed",
                "coverageTarget": "cancellation",
                "coverageValues": [],
                "cancellationMode": "running-cooperative",
                "clientLanguage": "python",
                "workerLanguage": args.worker,
                "environmentRef": args.environment_ref,
                "endpoint": args.endpoint,
                "topology": args.topology,
                "provider": args.provider,
                "runtimeProvider": args.runtime_provider,
                "workerExecutionProvider": args.worker_execution_provider,
                "publicationRef": publication.publication_ref,
                "executionId": submitted.execution_id,
                "activeStatusBeforeCancel": active.status.value,
                "activeStepStatusBeforeCancel": active_step.status.value if active_step else None,
                "cancellationRequested": cancellation.cancellation_requested,
                "cancellationRequestedAtUtc": cancellation.requested_at_utc,
                "cancellationCorrelationId": cancellation.correlation_id,
                "terminalStatus": result.status.value,
                "terminalStepStatus": terminal_step.status.value if terminal_step else None,
                "evidence": [
                    "publish",
                    "submit",
                    "active-execution-observed",
                    "sdk-execution-cancel",
                    "durable-cancellation-acknowledged",
                    "terminal-cancelled-observed",
                    "terminal-result",
                ],
                "recordedAtUtc": observation.updated_at_utc or None,
            },
        )
    elif args.feature == "watch":
        public_sequences: list[int] = []
        public_event_types: list[str] = []
        saw_snapshot = False
        saw_event = False
        saw_resync = False

        async with asyncio.timeout(45.0):
            async for item in client.watch_execution(
                AiSdkExecutionWatchRequest(
                    execution_id=submitted.execution_id,
                    include_initial_snapshot=True,
                )
            ):
                if item.execution_id != submitted.execution_id:
                    raise RuntimeError("Watch returned an event for a different execution.")
                if item.sequence is not None:
                    public_sequences.append(item.sequence)
                if item.kind is AiSdkExecutionWatchEventKind.SNAPSHOT:
                    saw_snapshot = True
                elif item.kind is AiSdkExecutionWatchEventKind.EVENT:
                    saw_event = True
                    if item.event_type:
                        public_event_types.append(item.event_type)
                elif item.kind is AiSdkExecutionWatchEventKind.RESYNC_REQUIRED:
                    saw_resync = True

        if not saw_snapshot:
            raise RuntimeError("Watch E2E did not receive the authoritative initial snapshot.")
        if not saw_event:
            raise RuntimeError("Watch E2E did not receive any incremental public event after the snapshot.")
        if saw_resync:
            raise RuntimeError("Nominal Watch E2E unexpectedly required resynchronization.")
        if len(public_sequences) < 2 or any(
            right <= left for left, right in zip(public_sequences, public_sequences[1:])
        ):
            raise RuntimeError("Watch E2E did not observe a strictly increasing public sequence.")

        result = await client.get_execution_result(submitted.execution_id)
        if result.status is not AiSdkExecutionStatus.COMPLETED:
            raise RuntimeError(
                f"Watch E2E execution '{submitted.execution_id}' ended as '{result.status.value}', expected 'Completed'."
            )

        _write_evidence(
            Path(args.evidence),
            {
                "schemaVersion": 1,
                "scenarioId": args.scenario_id,
                "status": "passed",
                "coverageTarget": "execution-watch-e2e",
                "coverageValues": [],
                "clientLanguage": "python",
                "workerLanguage": args.worker,
                "environmentRef": args.environment_ref,
                "endpoint": args.endpoint,
                "topology": args.topology,
                "provider": args.provider,
                "runtimeProvider": args.runtime_provider,
                "workerExecutionProvider": args.worker_execution_provider,
                "publicationRef": publication.publication_ref,
                "executionId": submitted.execution_id,
                "initialSnapshotObserved": saw_snapshot,
                "incrementalEventObserved": saw_event,
                "resyncObserved": saw_resync,
                "publicSequences": public_sequences,
                "publicEventTypes": public_event_types,
                "terminalStatus": result.status.value,
                "evidence": [
                    "publish",
                    "submit",
                    "sdk-execution-watch",
                    "mcp-http-public-boundary",
                    "initial-snapshot",
                    "ordered-incremental-event",
                    "terminal-watch-convergence",
                    "terminal-result",
                    "public-execution-id",
                ],
                "recordedAtUtc": None,
            },
        )
    else:
        if args.feature:
            raise ValueError(f"Unsupported --feature '{args.feature}'.")
        _diagnostic(args, f"OBSERVE waiting executionId={submitted.execution_id}")
        try:
            observation = await _wait_for_terminal(client, submitted.execution_id, args.terminal_timeout_seconds)
        except Exception as exception:
            _diagnostic_failure(args, "OBSERVE", exception)
            raise
        _diagnostic(args, f"OBSERVE terminal executionId={submitted.execution_id} status={observation.status.value}")
        try:
            result = await client.get_execution_result(submitted.execution_id)
        except Exception as exception:
            _diagnostic_failure(args, "RESULT", exception)
            raise
        _diagnostic(args, f"RESULT status={result.status.value}")
        if result.status.value != "Completed":
            raise RuntimeError(f"Execution '{submitted.execution_id}' ended as '{result.status.value}'.")
        output_marker_verified = False
        if args.require_output_marker:
            output_marker_verified = _contains_json_value(result.output, marker)
            if not output_marker_verified:
                _diagnostic(args, "MARKER verification failed")
                raise RuntimeError(
                    "Completed execution did not expose the published function marker in the terminal result output."
                )
            _diagnostic(args, "MARKER verified")

        _write_evidence(
            Path(args.evidence),
            {
                "schemaVersion": 1,
                "scenarioId": args.scenario_id,
                "status": "passed",
                "clientLanguage": "python",
                "workerLanguage": args.worker,
                "environmentRef": args.environment_ref,
                "endpoint": args.endpoint,
                "topology": args.topology,
                "provider": args.provider,
                "runtimeProvider": args.runtime_provider,
                "workerExecutionProvider": args.worker_execution_provider,
                "publicationRef": publication.publication_ref,
                "executionId": submitted.execution_id,
                "terminalStatus": result.status.value,
                "outputMarkerVerified": output_marker_verified,
                "evidence": ["publish", "submit", "observe", "terminal-result", "public-execution-id"],
                "recordedAtUtc": observation.updated_at_utc or None,
            },
        )
    return 0



async def _run_control(client: AiSdkClient, args: argparse.Namespace) -> None:
    _diagnostic(args, "CONTROL start")

    pause_publication = await _publish_control_pipeline(client, args, "pause")
    pause_execution = await _submit_control_execution(client, args, pause_publication.publication_ref, "pause")
    pause_watch_task, pause_first_step_active = _start_control_watch(client, pause_execution.execution_id, 75.0)
    await _wait_for_watch_active_step(pause_first_step_active, pause_execution.execution_id, "first", 30.0)

    pause = await client.pause_execution(
        pause_execution.execution_id,
        AiSdkExecutionControlRequest(reason="matrix-control-e2e-pause"),
    )
    if not pause.accepted or pause.execution_id != pause_execution.execution_id:
        raise RuntimeError("Public pause operation was not accepted for the submitted execution.")
    pause_gate_verified = await _wait_for_pause_gate(client, pause_execution.execution_id, 20.0)
    if not pause_gate_verified:
        raise RuntimeError("Pause did not gate the dependent second step.")

    resume = await client.resume_execution(
        pause_execution.execution_id,
        AiSdkExecutionControlRequest(reason="matrix-control-e2e-resume"),
    )
    if not resume.accepted or resume.execution_id != pause_execution.execution_id:
        raise RuntimeError("Public resume operation was not accepted for the paused execution.")
    pause_result = await _wait_for_completed_result(client, pause_execution.execution_id, 45.0)
    pause_watch = await pause_watch_task
    if pause_watch["resyncObserved"]:
        raise RuntimeError("Pause/resume unexpectedly forced Watch resynchronization.")

    input_publication = await _publish_control_pipeline(client, args, "input")
    input_execution = await _submit_control_execution(client, args, input_publication.publication_ref, "input")
    input_watch_task, input_first_step_active = _start_control_watch(client, input_execution.execution_id, 75.0)
    await _wait_for_watch_active_step(input_first_step_active, input_execution.execution_id, "first", 30.0)
    waiting_key = f"approval:{args.scenario_id}:{uuid.uuid4().hex}"
    await _seed_waiting_for_input(args.endpoint, input_execution.execution_id, waiting_key, "second")
    input_gate_verified = await _wait_for_pause_gate(client, input_execution.execution_id, 20.0)
    if not input_gate_verified:
        raise RuntimeError("Waiting-for-input did not gate the dependent second step.")

    input_response = await client.submit_execution_input(
        input_execution.execution_id,
        AiSdkExecutionInputSubmissionRequest(
            waiting_key=waiting_key,
            waiting_step_name="second",
            reason="matrix-control-e2e-approval",
            input={"approved": True, "source": "matrix"},
        ),
    )
    if (
        not input_response.accepted
        or input_response.execution_id != input_execution.execution_id
        or input_response.state is None
        or input_response.state.input_received_at_utc is None
    ):
        raise RuntimeError("Public human-input submission was not durably acknowledged.")
    input_result = await _wait_for_completed_result(client, input_execution.execution_id, 45.0)
    input_watch = await input_watch_task
    if input_watch["resyncObserved"]:
        raise RuntimeError("Human-input flow unexpectedly forced Watch resynchronization.")

    replay_publication = await _publish_replay_pipeline(client, args)
    replay_execution = await _submit_control_execution(client, args, replay_publication.publication_ref, "replay")
    await _wait_for_completed_result(client, replay_execution.execution_id, 45.0)
    replay = await client.replay_execution(
        replay_execution.execution_id,
        AiSdkExecutionReplayRequest(include_diagnostics=True, reason="matrix-control-e2e-replay"),
    )
    if not replay.succeeded or replay.deterministic is False:
        raise RuntimeError(
            f"Public replay validation failed. Message='{replay.message}', FailureReason='{replay.failure_reason}'."
        )

    _write_evidence(
        Path(args.evidence),
        {
            "schemaVersion": 1,
            "scenarioId": args.scenario_id,
            "status": "passed",
            "coverageTarget": "execution-control-replay-e2e",
            "clientLanguage": "python",
            "workerLanguage": args.worker,
            "endpoint": args.endpoint,
            "topology": args.topology,
            "provider": args.provider,
            "runtimeProvider": args.runtime_provider,
            "workerExecutionProvider": args.worker_execution_provider,
            "pauseExecutionId": pause_execution.execution_id,
            "pauseAccepted": pause.accepted,
            "pauseGateVerified": pause_gate_verified,
            "resumeAccepted": resume.accepted,
            "pauseResumeTerminalStatus": pause_result.status.value,
            "inputExecutionId": input_execution.execution_id,
            "inputWaitSeeded": True,
            "inputAccepted": input_response.accepted,
            "inputGateVerified": input_gate_verified,
            "inputTerminalStatus": input_result.status.value,
            "replayExecutionId": replay_execution.execution_id,
            "replaySucceeded": replay.succeeded,
            "replayDeterministic": replay.deterministic,
            "watchResyncObserved": pause_watch["resyncObserved"] or input_watch["resyncObserved"],
            "watchSnapshotsObserved": pause_watch["snapshotObserved"] and input_watch["snapshotObserved"],
            "watchIncrementalEventsObserved": pause_watch["eventObserved"] and input_watch["eventObserved"],
            "evidence": [
                "publish", "submit", "sdk-execution-watch", "sdk-execution-pause",
                "pause-gated-next-step", "sdk-execution-resume", "resume-terminal-convergence",
                "matrix-wait-input-production-authority", "sdk-execution-input-submit",
                "input-terminal-convergence", "sdk-execution-replay", "replay-validation-succeeded",
                "mcp-http-public-boundary",
            ],
            "recordedAtUtc": None,
        },
    )


async def _publish_control_pipeline(client: AiSdkClient, args: argparse.Namespace, suffix: str):
    slow, fast = _control_worker_sources(args.worker)
    return await client.publish_pipeline(
        AiSdkPipelinePublicationRequest(
            definition=AiSdkPipelineDefinition(
                name=f"matrix-{args.scenario_id}-{suffix}",
                version="1",
                execution_language=args.worker,
                execution_mode=AiSdkExecutionMode.DAG,
                steps=(
                    AiSdkPipelineStepDefinition(
                        name="first", step_key="custom", order=0,
                        execution_language=args.worker,
                        invocation=AiSdkInvocationDefinition(kind=AiSdkInvocationKind.CUSTOM),
                        input={"marker": f"{args.scenario_id}-first"},
                    ),
                    AiSdkPipelineStepDefinition(
                        name="second", step_key="custom", order=1,
                        execution_language=args.worker, depends_on=("first",),
                        invocation=AiSdkInvocationDefinition(kind=AiSdkInvocationKind.CUSTOM),
                        input={"marker": f"{args.scenario_id}-second"},
                    ),
                ),
            ),
            functions=(
                _upload_for_step(slow, args.environment_ref, "first"),
                _upload_for_step(fast, args.environment_ref, "second"),
            ),
        )
    )


async def _publish_replay_pipeline(client: AiSdkClient, args: argparse.Namespace):
    source = _worker_source(args.worker)
    return await client.publish_pipeline(
        AiSdkPipelinePublicationRequest(
            definition=AiSdkPipelineDefinition(
                name=f"matrix-{args.scenario_id}-replay",
                version="1",
                execution_language=args.worker,
                execution_mode=AiSdkExecutionMode.DAG,
                steps=(
                    AiSdkPipelineStepDefinition(
                        name="work", step_key="custom", order=0,
                        execution_language=args.worker,
                        invocation=AiSdkInvocationDefinition(kind=AiSdkInvocationKind.CUSTOM),
                        input={"marker": f"{args.scenario_id}-replay"},
                    ),
                ),
            ),
            functions=(_upload_for_step(source, args.environment_ref, "work"),),
        )
    )


def _upload_for_step(source: dict[str, object], environment_ref: str, step_name: str) -> AiSdkPublicationFunctionUpload:
    return AiSdkPublicationFunctionUpload(
        site=AiSdkPublicationCallSite(kind=AiSdkPublicationFunctionKind.STEP, step_name=step_name),
        environment_ref=environment_ref,
        entry_point_path=str(source["entry_point_path"]),
        entry_point_symbol=str(source["entry_point_symbol"]),
        sources=(
            AiSdkPublicationFileUpload(
                path=str(source["entry_point_path"]),
                content_base64=base64.b64encode(source["bytes"]).decode("ascii"),
            ),
        ),
    )


async def _submit_control_execution(client: AiSdkClient, args: argparse.Namespace, publication_ref: str, suffix: str):
    return await client.submit_execution(
        AiSdkExecutionSubmissionRequest(
            publication_ref=publication_ref,
            idempotency_key=f"{args.scenario_id}-{suffix}-{uuid.uuid4().hex}",
            input={"scenario": args.scenario_id, "phase": suffix},
            metadata={
                "matrix.scenario": args.scenario_id,
                "matrix.client": "python",
                "matrix.worker": args.worker,
                "matrix.feature": "control",
                "matrix.phase": suffix,
            },
        )
    )


async def _wait_for_pause_gate(client: AiSdkClient, execution_id: str, timeout_seconds: float) -> bool:
    loop = asyncio.get_running_loop()
    deadline = loop.time() + timeout_seconds
    last_execution_status: AiSdkExecutionStatus | None = None
    last_first_status: AiSdkExecutionStepStatus | None = None
    last_second_status: AiSdkExecutionStepStatus | None = None

    while loop.time() < deadline:
        observation = await client.observe_execution(execution_id)
        first = next((step for step in observation.steps if step.name == "first"), None)
        second = next((step for step in observation.steps if step.name == "second"), None)

        last_execution_status = observation.status
        last_first_status = first.status if first is not None else None
        last_second_status = second.status if second is not None else None

        if second is not None and second.status in {
            AiSdkExecutionStepStatus.RUNNING,
            AiSdkExecutionStepStatus.COMPLETED,
            AiSdkExecutionStepStatus.FAILED,
        }:
            print(
                "[matrix-python-client] PAUSE GATE FAILED second advanced. "
                f"ExecutionStatus='{observation.status.value}' "
                f"FirstStatus='{first.status.value if first is not None else 'missing'}' "
                f"SecondStatus='{second.status.value}'.",
                flush=True,
            )
            return False

        if observation.status in {
            AiSdkExecutionStatus.COMPLETED,
            AiSdkExecutionStatus.FAILED,
            AiSdkExecutionStatus.CANCELLED,
        }:
            print(
                "[matrix-python-client] PAUSE GATE FAILED execution became terminal. "
                f"ExecutionStatus='{observation.status.value}' "
                f"FirstStatus='{first.status.value if first is not None else 'missing'}' "
                f"SecondStatus='{second.status.value if second is not None else 'missing'}'.",
                flush=True,
            )
            return False

        # Published custom functions use the durable invocation adapter. The DAG step starts,
        # parks as WaitingForExternal, and the completed invocation continuation later makes
        # the same step Ready again. While paused, that Ready continuation must NOT be claimed.
        if first is not None and first.status is AiSdkExecutionStepStatus.READY:
            await asyncio.sleep(0.75)
            confirm = await client.observe_execution(execution_id)
            confirm_first = next((step for step in confirm.steps if step.name == "first"), None)
            confirm_second = next((step for step in confirm.steps if step.name == "second"), None)
            gated = (
                confirm.status not in {
                    AiSdkExecutionStatus.COMPLETED,
                    AiSdkExecutionStatus.FAILED,
                    AiSdkExecutionStatus.CANCELLED,
                }
                and confirm_first is not None
                and confirm_first.status is AiSdkExecutionStepStatus.READY
                and (
                    confirm_second is None
                    or confirm_second.status not in {
                        AiSdkExecutionStepStatus.RUNNING,
                        AiSdkExecutionStepStatus.COMPLETED,
                        AiSdkExecutionStepStatus.FAILED,
                    }
                )
            )
            print(
                "[matrix-python-client] PAUSE GATE PROOF "
                f"executionStatus='{confirm.status.value}' "
                f"firstStatus='{confirm_first.status.value if confirm_first is not None else 'missing'}' "
                f"secondStatus='{confirm_second.status.value if confirm_second is not None else 'missing'}' "
                f"gated='{gated}'.",
                flush=True,
            )
            return gated

        if first is not None and first.status in {
            AiSdkExecutionStepStatus.COMPLETED,
            AiSdkExecutionStepStatus.FAILED,
        }:
            print(
                "[matrix-python-client] PAUSE GATE FAILED first continuation advanced while paused. "
                f"ExecutionStatus='{observation.status.value}' "
                f"FirstStatus='{first.status.value}' "
                f"SecondStatus='{second.status.value if second is not None else 'missing'}'.",
                flush=True,
            )
            return False

        await asyncio.sleep(0.1)

    print(
        "[matrix-python-client] PAUSE GATE TIMEOUT "
        f"executionId='{execution_id}' "
        f"ExecutionStatus='{last_execution_status.value if last_execution_status is not None else 'unknown'}' "
        f"FirstStatus='{last_first_status.value if last_first_status is not None else 'missing'}' "
        f"SecondStatus='{last_second_status.value if last_second_status is not None else 'missing'}'.",
        flush=True,
    )
    return False


async def _wait_for_completed_result(client: AiSdkClient, execution_id: str, timeout_seconds: float):
    observation = await _wait_for_terminal(client, execution_id, timeout_seconds)
    if observation.status is not AiSdkExecutionStatus.COMPLETED:
        raise RuntimeError(f"Execution '{execution_id}' ended as '{observation.status.value}'.")
    result = await client.get_execution_result(execution_id)
    if result.status is not AiSdkExecutionStatus.COMPLETED:
        raise RuntimeError(f"Execution '{execution_id}' result ended as '{result.status.value}'.")
    return result


def _start_control_watch(
    client: AiSdkClient,
    execution_id: str,
    timeout_seconds: float,
) -> tuple[asyncio.Task[dict[str, bool]], asyncio.Future[bool]]:
    loop = asyncio.get_running_loop()
    first_step_active: asyncio.Future[bool] = loop.create_future()
    task = asyncio.create_task(
        _collect_control_watch(client, execution_id, timeout_seconds, first_step_active)
    )
    return task, first_step_active


async def _collect_control_watch(
    client: AiSdkClient,
    execution_id: str,
    timeout_seconds: float,
    first_step_active: asyncio.Future[bool] | None = None,
) -> dict[str, bool]:
    snapshot = False
    event = False
    resync = False
    try:
        async with asyncio.timeout(timeout_seconds):
            async for item in client.watch_execution(AiSdkExecutionWatchRequest(execution_id=execution_id, include_initial_snapshot=True)):
                snapshot = snapshot or item.kind is AiSdkExecutionWatchEventKind.SNAPSHOT
                event = event or item.kind is AiSdkExecutionWatchEventKind.EVENT
                resync = resync or item.kind is AiSdkExecutionWatchEventKind.RESYNC_REQUIRED
                if first_step_active is not None and not first_step_active.done() and _watch_item_shows_active_step(item, "first"):
                    first_step_active.set_result(True)
    except BaseException as exc:
        if first_step_active is not None and not first_step_active.done():
            first_step_active.set_exception(exc)
        raise
    finally:
        if first_step_active is not None and not first_step_active.done():
            first_step_active.set_exception(
                RuntimeError(f"Execution '{execution_id}' Watch ended before step 'first' became active.")
            )
    return {"snapshotObserved": snapshot, "eventObserved": event, "resyncObserved": resync}


def _watch_item_shows_active_step(item, step_name: str) -> bool:
    if item.snapshot is not None:
        for step in item.snapshot.steps:
            if step.name == step_name and step.status in {
                AiSdkExecutionStepStatus.RUNNING,
                AiSdkExecutionStepStatus.WAITING_FOR_EXTERNAL,
            }:
                return True

    payload = item.payload
    if (
        item.kind is not AiSdkExecutionWatchEventKind.EVENT
        or item.channel is None
        or item.channel.value != "Steps"
        or not isinstance(payload, dict)
    ):
        return False

    return payload.get("name") == step_name and payload.get("status") in {"Running", "WaitingForExternal"}


async def _wait_for_watch_active_step(
    active_step: asyncio.Future[bool],
    execution_id: str,
    step_name: str,
    timeout_seconds: float,
) -> None:
    try:
        await asyncio.wait_for(asyncio.shield(active_step), timeout_seconds)
    except TimeoutError as exc:
        raise TimeoutError(
            f"Execution '{execution_id}' Watch did not expose active step '{step_name}' within {timeout_seconds} seconds."
        ) from exc


async def _seed_waiting_for_input(endpoint: str, execution_id: str, waiting_key: str, waiting_step_name: str) -> None:
    base = urllib.parse.urlsplit(endpoint)
    url = urllib.parse.urlunsplit((base.scheme, base.netloc, f"/matrix/execution-control/{urllib.parse.quote(execution_id, safe='')}/wait-for-input", "", ""))
    payload = json.dumps({
        "waitingKey": waiting_key,
        "waitingStepName": waiting_step_name,
        "reason": "matrix-control-e2e-await-approval",
    }).encode("utf-8")

    def send() -> None:
        request = urllib.request.Request(url, data=payload, method="POST", headers={"content-type": "application/json"})
        with urllib.request.urlopen(request, timeout=10) as response:
            if response.status < 200 or response.status >= 300:
                raise RuntimeError(f"Matrix wait-for-input setup failed with HTTP {response.status}.")
            response.read()

    await asyncio.to_thread(send)


def _control_worker_sources(worker: str) -> tuple[dict[str, object], dict[str, object]]:
    sample_root = _sample_root()
    if worker == "dotnet":
        data = (sample_root / "dotnet" / "Multiplexed.AI.Samples.PublishedFunctions.dll").read_bytes()
        return (
            {"entry_point_path": "control-slow.dll", "entry_point_symbol": "Multiplexed.AI.Samples.PublishedFunctions.Functions::PinStable", "bytes": data},
            {"entry_point_path": "control-fast.dll", "entry_point_symbol": "Multiplexed.AI.Samples.PublishedFunctions.Functions::Run", "bytes": data},
        )
    if worker == "typescript":
        return (
            {"entry_point_path": "control-slow.ts", "entry_point_symbol": "run", "bytes": b"export async function run(inputs, context) { await new Promise(resolve => setTimeout(resolve, 8000)); return { success: true, payload: { marker: inputs?.marker ?? null, phase: 'slow' } }; }\n"},
            {"entry_point_path": "control-fast.ts", "entry_point_symbol": "run", "bytes": b"export function run(inputs, context) { return { success: true, payload: { marker: inputs?.marker ?? null, phase: 'fast' } }; }\n"},
        )
    if worker == "python":
        return (
            {"entry_point_path": "control_slow.py", "entry_point_symbol": "run", "bytes": b"import time\ndef run(inputs, context):\n    time.sleep(8)\n    return {'success': True, 'payload': {'marker': inputs.get('marker'), 'phase': 'slow'}}\n"},
            {"entry_point_path": "control_fast.py", "entry_point_symbol": "run", "bytes": b"def run(inputs, context):\n    return {'success': True, 'payload': {'marker': inputs.get('marker'), 'phase': 'fast'}}\n"},
        )
    raise ValueError(f"Unsupported worker language '{worker}'.")


def _run_dependency_firewall(args: argparse.Namespace) -> None:
    sdk_root = REPO_ROOT / "implementations" / "python" / "sdk"
    project = tomllib.loads((sdk_root / "pyproject.toml").read_text(encoding="utf-8"))
    declared_dependencies = sorted(project.get("project", {}).get("dependencies", []))

    def dependency_name(requirement: str) -> str:
        value = requirement.split(";", 1)[0].strip()
        for separator in ("[", "<", ">", "=", "!", "~", " "):
            value = value.split(separator, 1)[0]
        return value.strip().lower().replace("_", "-")

    forbidden_declared_dependencies = sorted(
        requirement
        for requirement in declared_dependencies
        if dependency_name(requirement).startswith("multiplexed-")
    )

    absolute_import_roots: set[str] = set()
    for source in (sdk_root / "src" / "multiplexed_ai_sdk").rglob("*.py"):
        tree = ast.parse(source.read_text(encoding="utf-8"), filename=str(source))
        for node in ast.walk(tree):
            if isinstance(node, ast.Import):
                absolute_import_roots.update(alias.name.split(".", 1)[0] for alias in node.names)
            elif isinstance(node, ast.ImportFrom) and node.level == 0 and node.module:
                absolute_import_roots.add(node.module.split(".", 1)[0])

    forbidden_absolute_imports = sorted(
        name for name in absolute_import_roots
        if name.startswith("multiplexed") and name != "multiplexed_ai_sdk"
    )
    if forbidden_declared_dependencies or forbidden_absolute_imports:
        raise RuntimeError(
            "External Python SDK dependency firewall detected repository dependencies: "
            + ", ".join([*forbidden_declared_dependencies, *forbidden_absolute_imports])
        )

    _write_evidence(
        Path(args.evidence),
        {
            "schemaVersion": 1,
            "scenarioId": args.scenario_id,
            "status": "passed",
            "coverageTarget": "external-client-dependency-firewall",
            "coverageValues": [],
            "clientLanguage": "python",
            "workerLanguage": None,
            "topology": args.topology,
            "provider": args.provider,
            "runtimeProvider": args.runtime_provider,
            "workerExecutionProvider": args.worker_execution_provider,
            "artifactKind": "python-sdk-source-distribution",
            "declaredDependencies": declared_dependencies,
            "absoluteImportRoots": sorted(absolute_import_roots),
            "forbiddenDeclaredDependencies": forbidden_declared_dependencies,
            "forbiddenAbsoluteImports": forbidden_absolute_imports,
            "evidence": [
                "pyproject-dependencies-inspected",
                "sdk-source-imports-inspected",
                "no-engine-runtime-dependency",
            ],
            "recordedAtUtc": None,
        },
    )

async def _wait_for_active_step(client: AiSdkClient, execution_id: str, step_name: str, timeout_seconds: float):
    loop = asyncio.get_running_loop()
    deadline = loop.time() + timeout_seconds
    while loop.time() < deadline:
        observation = await client.observe_execution(execution_id)
        if observation.status in {AiSdkExecutionStatus.COMPLETED, AiSdkExecutionStatus.FAILED, AiSdkExecutionStatus.CANCELLED}:
            raise RuntimeError(
                f"Execution '{execution_id}' became terminal as '{observation.status.value}' before cancellation could be requested."
            )
        step = next((item for item in observation.steps if item.name == step_name), None)
        if step is not None and step.status in {AiSdkExecutionStepStatus.RUNNING, AiSdkExecutionStepStatus.WAITING_FOR_EXTERNAL}:
            return observation
        await asyncio.sleep(0.1)
    raise TimeoutError(f"Execution '{execution_id}' did not expose active step '{step_name}' within {timeout_seconds} seconds.")


async def _wait_for_terminal(client: AiSdkClient, execution_id: str, timeout_seconds: float):
    loop = asyncio.get_running_loop()
    deadline = loop.time() + timeout_seconds
    while loop.time() < deadline:
        observation = await client.observe_execution(execution_id)
        if observation.status.value in {"Completed", "Failed", "Cancelled"}:
            return observation
        await asyncio.sleep(0.25)
    raise TimeoutError(f"Execution '{execution_id}' did not become terminal within {timeout_seconds} seconds.")


def _sample_root() -> Path:
    configured = os.environ.get("MATRIX_SAMPLE_ROOT")
    if configured:
        return Path(configured).resolve()
    return REPO_ROOT / "implementations" / "sdk" / "samples" / "published-functions"


def _worker_source(worker: str) -> dict[str, object]:
    sample_root = _sample_root()
    if worker == "python":
        path = sample_root / "python" / "functions.py"
        return {"entry_point_path": "functions.py", "entry_point_symbol": "run", "bytes": path.read_bytes()}
    if worker == "typescript":
        path = sample_root / "typescript" / "functions.ts"
        return {"entry_point_path": "functions.ts", "entry_point_symbol": "run", "bytes": path.read_bytes()}
    if worker == "dotnet":
        path = sample_root / "dotnet" / "Multiplexed.AI.Samples.PublishedFunctions.dll"
        return {"entry_point_path": "functions.dll", "entry_point_symbol": "Multiplexed.AI.Samples.PublishedFunctions.Functions::Run", "bytes": path.read_bytes()}
    raise ValueError(f"Unsupported worker language '{worker}'.")


def _cancellation_worker_source(worker: str) -> dict[str, object]:
    sample_root = _sample_root()
    if worker == "python":
        return {
            "entry_point_path": "main.py",
            "entry_point_symbol": "run",
            "bytes": (
                "import time\n"
                "def run(inputs, context):\n"
                "    time.sleep(8)\n"
                "    return {'success': True, 'payload': {'cancellationSample': True}}\n"
            ).encode("utf-8"),
        }
    if worker == "typescript":
        return {
            "entry_point_path": "main.ts",
            "entry_point_symbol": "run",
            "bytes": (
                "export async function run(inputs: unknown, context: unknown) { "
                "await new Promise(resolve => setTimeout(resolve, 8000)); "
                "return { success: true, payload: { cancellationSample: true } }; }\n"
            ).encode("utf-8"),
        }
    if worker == "dotnet":
        path = sample_root / "dotnet" / "Multiplexed.AI.Samples.PublishedFunctions.dll"
        return {
            "entry_point_path": "functions.dll",
            "entry_point_symbol": "Multiplexed.AI.Samples.PublishedFunctions.Functions::PinStable",
            "bytes": path.read_bytes(),
        }
    raise ValueError(f"Unsupported worker language '{worker}'.")


def _contains_json_value(value: object, expected: object) -> bool:
    if value == expected:
        return True
    if isinstance(value, dict):
        return any(_contains_json_value(item, expected) for item in value.values())
    if isinstance(value, (list, tuple)):
        return any(_contains_json_value(item, expected) for item in value)
    return False


def _write_evidence(path: Path, document: dict[str, object]) -> None:
    path = path.resolve()
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--endpoint")
    parser.add_argument("--feature", choices=("cancellation", "dependency-firewall", "watch", "control"))
    parser.add_argument("--worker", required=True, choices=("dotnet", "typescript", "python"))
    parser.add_argument("--environment-ref")
    parser.add_argument("--scenario-id", required=True)
    parser.add_argument("--evidence", required=True)
    parser.add_argument("--token")
    parser.add_argument("--access-context")
    parser.add_argument("--access-context-header", default="X-Access-Context")
    parser.add_argument("--topology", default="local")
    parser.add_argument("--provider", default="ProcessHostPool")
    parser.add_argument("--runtime-provider", default="ProcessHostPool")
    parser.add_argument("--worker-execution-provider", default="TrustedProcess")
    parser.add_argument("--require-output-marker", action="store_true")
    parser.add_argument("--diagnostic-log")
    parser.add_argument("--terminal-timeout-seconds", type=float, default=90.0)
    parser.add_argument("--manifest")
    args = parser.parse_args()
    if args.manifest:
        manifest = json.loads(Path(args.manifest).resolve().read_text(encoding="utf-8-sig"))
        args.endpoint = args.endpoint or manifest["endpoint"]
        args.environment_ref = args.environment_ref or manifest["environmentRefs"][args.worker]
        args.token = args.token or manifest.get("bearerToken")
        args.access_context = args.access_context or manifest.get("accessContext")
        args.access_context_header = manifest.get("accessContextHeader", args.access_context_header)
        args.topology = manifest.get("topology", args.topology)
        args.provider = manifest.get("provider", args.provider)
        args.runtime_provider = manifest.get("runtimeProvider", args.runtime_provider)
        args.worker_execution_provider = manifest.get("workerExecutionProvider", args.worker_execution_provider)
    if args.terminal_timeout_seconds <= 0:
        parser.error("--terminal-timeout-seconds must be positive")
    if not args.endpoint or not args.environment_ref:
        parser.error("--endpoint and --environment-ref are required unless --manifest supplies them")
    return args



if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
