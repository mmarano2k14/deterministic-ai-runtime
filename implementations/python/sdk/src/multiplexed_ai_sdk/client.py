from __future__ import annotations

from dataclasses import replace
from typing import AsyncIterator, TypeVar

from .contracts.common.schema_versions import AiSdkSchemaVersions
from .contracts.control.execution_cancellation_request import AiSdkExecutionCancellationRequest
from .contracts.control.execution_cancellation_response import AiSdkExecutionCancellationResponse
from .contracts.control.execution_control_request import AiSdkExecutionControlRequest
from .contracts.control.execution_control_response import AiSdkExecutionControlResponse
from .contracts.control.execution_input_submission_request import AiSdkExecutionInputSubmissionRequest
from .contracts.executions.execution_result import AiSdkExecutionResult
from .contracts.executions.execution_status import AiSdkExecutionStatus
from .contracts.executions.execution_submission_request import AiSdkExecutionSubmissionRequest
from .contracts.executions.execution_submission_response import AiSdkExecutionSubmissionResponse
from .contracts.observation.execution_observation import AiSdkExecutionObservation
from .contracts.publication.pipeline_publication_request import AiSdkPipelinePublicationRequest
from .contracts.publication.pipeline_publication_response import AiSdkPipelinePublicationResponse
from .contracts.replay.execution_replay_request import AiSdkExecutionReplayRequest
from .contracts.replay.execution_replay_response import AiSdkExecutionReplayResponse
from .contracts.watch.execution_watch_channel import AiSdkExecutionWatchChannel
from .contracts.watch.execution_watch_event import AiSdkExecutionWatchEvent
from .contracts.watch.execution_watch_event_kind import AiSdkExecutionWatchEventKind
from .contracts.watch.execution_watch_request import AiSdkExecutionWatchRequest
from .contracts.watch.execution_watch_resync_reason import AiSdkExecutionWatchResyncReason
from .contracts.watch.execution_watch_resync_required import AiSdkExecutionWatchResyncRequired
from .errors import AiSdkError, AiSdkException
from .json_types import AiSdkJsonObject
from .protocol import AI_SDK_OPERATIONS
from .transport import AiSdkTransport, AiSdkTransportRequest
from .wire_model import AiSdkWireModel

TResponse = TypeVar("TResponse", bound=AiSdkWireModel)
MAX_CONSECUTIVE_DUPLICATE_WATCH_ITEMS = 3


class AiSdkClient:
    def __init__(self, transport: AiSdkTransport) -> None:
        if transport is None:
            raise ValueError("transport is required")
        self._transport = transport

    async def publish_pipeline(
        self,
        request: AiSdkPipelinePublicationRequest,
    ) -> AiSdkPipelinePublicationResponse:
        if request is None:
            raise _invalid_request("publication_request_required", "A pipeline publication request is required.")
        _validate_schema(
            "pipeline publication request",
            request.schema_version,
            AiSdkSchemaVersions.PIPELINE_PUBLICATION_REQUEST,
        )
        _validate_schema(
            "pipeline definition",
            request.definition.schema_version,
            AiSdkSchemaVersions.PIPELINE_DEFINITION,
        )
        return await self._invoke(
            AI_SDK_OPERATIONS["publish_pipeline"],
            {"request": request.to_wire()},
            AiSdkPipelinePublicationResponse,
            AiSdkSchemaVersions.PIPELINE_PUBLICATION_RESPONSE,
        )

    async def submit_execution(
        self,
        request: AiSdkExecutionSubmissionRequest,
    ) -> AiSdkExecutionSubmissionResponse:
        if request is None:
            raise _invalid_request("submission_request_required", "An execution submission request is required.")
        _validate_schema(
            "execution submission request",
            request.schema_version,
            AiSdkSchemaVersions.EXECUTION_SUBMISSION_REQUEST,
        )
        return await self._invoke(
            AI_SDK_OPERATIONS["submit_execution"],
            {"request": request.to_wire()},
            AiSdkExecutionSubmissionResponse,
            AiSdkSchemaVersions.EXECUTION_SUBMISSION_RESPONSE,
        )

    async def observe_execution(self, execution_id: str) -> AiSdkExecutionObservation:
        _validate_execution_id(execution_id)
        return await self._invoke(
            AI_SDK_OPERATIONS["observe_execution"],
            {"executionId": execution_id},
            AiSdkExecutionObservation,
            AiSdkSchemaVersions.EXECUTION_OBSERVATION,
        )

    def watch_execution(
        self,
        request: AiSdkExecutionWatchRequest,
    ) -> AsyncIterator[AiSdkExecutionWatchEvent]:
        if request is None:
            raise _invalid_request("watch_request_required", "An execution Watch request is required.")
        _validate_schema(
            "execution watch request",
            request.schema_version,
            AiSdkSchemaVersions.EXECUTION_WATCH_REQUEST,
        )
        _validate_execution_id(request.execution_id)
        _validate_watch_channels(request.channels)
        return self._watch_execution_core(request)

    async def get_execution_result(self, execution_id: str) -> AiSdkExecutionResult:
        _validate_execution_id(execution_id)
        return await self._invoke(
            AI_SDK_OPERATIONS["get_execution_result"],
            {"executionId": execution_id},
            AiSdkExecutionResult,
            AiSdkSchemaVersions.EXECUTION_RESULT,
        )

    async def cancel_execution(
        self,
        execution_id: str,
        request: AiSdkExecutionCancellationRequest,
    ) -> AiSdkExecutionCancellationResponse:
        _validate_execution_id(execution_id)
        if request is None:
            raise _invalid_request("cancellation_request_required", "An execution cancellation request is required.")
        _validate_schema(
            "execution cancellation request",
            request.schema_version,
            AiSdkSchemaVersions.EXECUTION_CANCELLATION_REQUEST,
        )
        return await self._invoke(
            AI_SDK_OPERATIONS["cancel_execution"],
            {"executionId": execution_id, "request": request.to_wire()},
            AiSdkExecutionCancellationResponse,
            AiSdkSchemaVersions.EXECUTION_CANCELLATION_RESPONSE,
        )

    async def pause_execution(
        self,
        execution_id: str,
        request: AiSdkExecutionControlRequest | None = None,
    ) -> AiSdkExecutionControlResponse:
        _validate_execution_id(execution_id)
        effective = request or AiSdkExecutionControlRequest()
        _validate_schema(
            "execution control request",
            effective.schema_version,
            AiSdkSchemaVersions.EXECUTION_CONTROL_REQUEST,
        )
        return await self._invoke(
            AI_SDK_OPERATIONS["pause_execution"],
            {"executionId": execution_id, "request": effective.to_wire()},
            AiSdkExecutionControlResponse,
            AiSdkSchemaVersions.EXECUTION_CONTROL_RESPONSE,
        )

    async def resume_execution(
        self,
        execution_id: str,
        request: AiSdkExecutionControlRequest | None = None,
    ) -> AiSdkExecutionControlResponse:
        _validate_execution_id(execution_id)
        effective = request or AiSdkExecutionControlRequest()
        _validate_schema(
            "execution control request",
            effective.schema_version,
            AiSdkSchemaVersions.EXECUTION_CONTROL_REQUEST,
        )
        return await self._invoke(
            AI_SDK_OPERATIONS["resume_execution"],
            {"executionId": execution_id, "request": effective.to_wire()},
            AiSdkExecutionControlResponse,
            AiSdkSchemaVersions.EXECUTION_CONTROL_RESPONSE,
        )

    async def submit_execution_input(
        self,
        execution_id: str,
        request: AiSdkExecutionInputSubmissionRequest,
    ) -> AiSdkExecutionControlResponse:
        _validate_execution_id(execution_id)
        if request is None:
            raise _invalid_request(
                "input_submission_request_required",
                "An execution input submission request is required.",
            )
        _validate_schema(
            "execution input submission request",
            request.schema_version,
            AiSdkSchemaVersions.EXECUTION_INPUT_SUBMISSION_REQUEST,
        )
        if not isinstance(request.waiting_key, str) or not request.waiting_key.strip():
            raise _invalid_request("waiting_key_required", "A non-empty waitingKey is required.")
        if not isinstance(request.input, dict):
            raise _invalid_request("input_object_required", "Execution input must be a JSON object.")
        return await self._invoke(
            AI_SDK_OPERATIONS["submit_execution_input"],
            {"executionId": execution_id, "request": request.to_wire()},
            AiSdkExecutionControlResponse,
            AiSdkSchemaVersions.EXECUTION_CONTROL_RESPONSE,
        )

    async def replay_execution(
        self,
        execution_id: str,
        request: AiSdkExecutionReplayRequest | None = None,
    ) -> AiSdkExecutionReplayResponse:
        _validate_execution_id(execution_id)
        effective = request or AiSdkExecutionReplayRequest()
        _validate_schema(
            "execution replay request",
            effective.schema_version,
            AiSdkSchemaVersions.EXECUTION_REPLAY_REQUEST,
        )
        return await self._invoke(
            AI_SDK_OPERATIONS["replay_execution"],
            {"executionId": execution_id, "request": effective.to_wire()},
            AiSdkExecutionReplayResponse,
            AiSdkSchemaVersions.EXECUTION_REPLAY_RESPONSE,
        )

    async def _watch_execution_core(
        self,
        request: AiSdkExecutionWatchRequest,
    ) -> AsyncIterator[AiSdkExecutionWatchEvent]:
        after_sequence = request.after_sequence
        include_initial_snapshot = request.include_initial_snapshot
        awaiting_resync_snapshot = False
        consecutive_duplicates = 0

        while True:
            next_request = replace(
                request,
                after_sequence=after_sequence,
                include_initial_snapshot=include_initial_snapshot,
            )

            item = await self._invoke(
                AI_SDK_OPERATIONS["watch_execution"],
                {"request": next_request.to_wire()},
                AiSdkExecutionWatchEvent,
                AiSdkSchemaVersions.EXECUTION_WATCH_EVENT,
            )

            _validate_watch_event(next_request, item)

            if item.kind is AiSdkExecutionWatchEventKind.RESYNC_REQUIRED:
                if awaiting_resync_snapshot:
                    raise _invalid_watch_response(
                        "watch_resync_loop",
                        "The execution Watch server requested another resynchronization before returning the authoritative snapshot.",
                    )

                yield item
                after_sequence = None
                include_initial_snapshot = True
                awaiting_resync_snapshot = True
                consecutive_duplicates = 0
                continue

            if awaiting_resync_snapshot and item.kind is not AiSdkExecutionWatchEventKind.SNAPSHOT:
                raise _invalid_watch_response(
                    "watch_resync_snapshot_required",
                    "The execution Watch server did not return the authoritative snapshot required to complete resynchronization.",
                )

            if after_sequence is not None and item.sequence is not None:
                if item.sequence == after_sequence:
                    consecutive_duplicates += 1
                    if consecutive_duplicates > MAX_CONSECUTIVE_DUPLICATE_WATCH_ITEMS:
                        raise _invalid_watch_response(
                            "duplicate_watch_sequence_loop",
                            "The execution Watch server repeatedly returned the already-consumed public sequence.",
                        )
                    continue

                if not request.channels and item.sequence > after_sequence + 1:
                    yield _create_gap_detected_resync(
                        request.execution_id,
                        after_sequence,
                        item.sequence,
                        item.occurred_at_utc,
                    )
                    after_sequence = None
                    include_initial_snapshot = True
                    awaiting_resync_snapshot = True
                    consecutive_duplicates = 0
                    continue

            consecutive_duplicates = 0
            yield item

            if awaiting_resync_snapshot:
                awaiting_resync_snapshot = False

            if _is_terminal_watch_item(item):
                return

            after_sequence = item.sequence
            include_initial_snapshot = False

    async def _invoke(
        self,
        operation: str,
        arguments: AiSdkJsonObject,
        response_type: type[TResponse],
        expected_schema_version: int,
    ) -> TResponse:
        try:
            response = await self._transport.invoke(
                AiSdkTransportRequest(operation=operation, arguments=arguments)
            )
        except AiSdkException:
            raise
        except Exception as exc:
            raise AiSdkException(
                AiSdkError(
                    kind="transport",
                    code="transport_failure",
                    message="The SDK transport failed before a normalized response was returned.",
                )
            ) from exc

        if response.error is not None:
            raise AiSdkException(response.error)
        if not isinstance(response.result, dict):
            raise AiSdkException(
                AiSdkError(
                    kind="invalid_response",
                    code="missing_transport_result",
                    message="The SDK transport reported success without an object result document.",
                )
            )

        schema_version = response.result.get("schemaVersion")
        if type(schema_version) is not int:
            raise AiSdkException(
                AiSdkError(
                    kind="invalid_response",
                    code="missing_schema_version",
                    message=f"The '{operation}' response does not contain a numeric schemaVersion.",
                )
            )
        _validate_schema(f"{operation} response", schema_version, expected_schema_version)

        try:
            return response_type.from_wire(response.result)
        except (TypeError, ValueError) as exc:
            raise AiSdkException(
                AiSdkError(
                    kind="invalid_response",
                    code="invalid_response_json",
                    message=f"The '{operation}' response does not match the public SDK contract.",
                )
            ) from exc


def _validate_execution_id(execution_id: str) -> None:
    if isinstance(execution_id, str) and execution_id.strip():
        return
    raise _invalid_request("execution_id_required", "A non-empty executionId is required.")


def _validate_watch_channels(channels: tuple[AiSdkExecutionWatchChannel, ...]) -> None:
    if channels is None:
        raise _invalid_request("invalid_watch_channel", "Execution Watch channels are required to be a sequence.")
    for channel in channels:
        if not isinstance(channel, AiSdkExecutionWatchChannel):
            raise _invalid_request(
                "invalid_watch_channel",
                f"Unknown execution Watch channel '{channel}'.",
            )


def _validate_watch_event(
    request: AiSdkExecutionWatchRequest,
    item: AiSdkExecutionWatchEvent,
) -> None:
    if item.execution_id != request.execution_id:
        raise _invalid_watch_response(
            "watch_execution_mismatch",
            "The execution Watch response does not belong to the requested execution.",
        )

    if item.kind is AiSdkExecutionWatchEventKind.SNAPSHOT:
        if item.snapshot is None or item.sequence is None:
            raise _invalid_watch_response(
                "invalid_watch_snapshot",
                "An execution Watch snapshot requires both snapshot and sequence values.",
            )
        _validate_schema(
            "execution Watch snapshot",
            item.snapshot.schema_version,
            AiSdkSchemaVersions.EXECUTION_OBSERVATION,
        )
    elif item.kind is AiSdkExecutionWatchEventKind.EVENT:
        if (
            item.sequence is None
            or not isinstance(item.channel, AiSdkExecutionWatchChannel)
            or not isinstance(item.event_type, str)
            or not item.event_type.strip()
        ):
            raise _invalid_watch_response(
                "invalid_watch_event",
                "An execution Watch event requires sequence, known channel and eventType values.",
            )
    elif item.kind is AiSdkExecutionWatchEventKind.RESYNC_REQUIRED:
        if item.resync_required is None:
            raise _invalid_watch_response(
                "invalid_watch_resync",
                "A ResyncRequired Watch item requires a resyncRequired document.",
            )
        _validate_schema(
            "execution Watch resync document",
            item.resync_required.schema_version,
            AiSdkSchemaVersions.EXECUTION_WATCH_RESYNC_REQUIRED,
        )
        return
    else:
        raise _invalid_watch_response(
            "unknown_watch_kind",
            f"Unknown execution Watch item kind '{item.kind}'.",
        )

    if type(item.sequence) is not int or item.sequence < 0:
        raise _invalid_watch_response(
            "invalid_watch_sequence",
            "Execution Watch sequence values must be non-negative integers.",
        )

    if request.after_sequence is not None and item.sequence < request.after_sequence:
        raise _invalid_watch_response(
            "regressing_watch_sequence",
            "The execution Watch response regressed behind the requested public sequence.",
        )


def _create_gap_detected_resync(
    execution_id: str,
    requested_after_sequence: int,
    observed_sequence: int,
    occurred_at_utc: str,
) -> AiSdkExecutionWatchEvent:
    return AiSdkExecutionWatchEvent(
        execution_id=execution_id,
        kind=AiSdkExecutionWatchEventKind.RESYNC_REQUIRED,
        occurred_at_utc=occurred_at_utc,
        resync_required=AiSdkExecutionWatchResyncRequired(
            reason=AiSdkExecutionWatchResyncReason.GAP_DETECTED,
            requested_after_sequence=requested_after_sequence,
            earliest_available_sequence=requested_after_sequence + 1,
            latest_sequence=observed_sequence,
            message="A gap was detected in the unfiltered public execution Watch stream.",
        ),
    )


def _is_terminal_watch_item(item: AiSdkExecutionWatchEvent) -> bool:
    if item.snapshot is not None:
        return _is_terminal_status(item.snapshot.status)

    if (
        item.kind is not AiSdkExecutionWatchEventKind.EVENT
        or item.channel is not AiSdkExecutionWatchChannel.LIFECYCLE
        or not isinstance(item.payload, dict)
    ):
        return False

    return _is_terminal_status(item.payload.get("status"))


def _is_terminal_status(value: object) -> bool:
    if isinstance(value, AiSdkExecutionStatus):
        return value in (
            AiSdkExecutionStatus.COMPLETED,
            AiSdkExecutionStatus.FAILED,
            AiSdkExecutionStatus.CANCELLED,
        )
    return value in (
        AiSdkExecutionStatus.COMPLETED.value,
        AiSdkExecutionStatus.FAILED.value,
        AiSdkExecutionStatus.CANCELLED.value,
    )


def _invalid_watch_response(code: str, message: str) -> AiSdkException:
    return AiSdkException(AiSdkError(kind="invalid_response", code=code, message=message))


def _validate_schema(document: str, actual: int, expected: int) -> None:
    if type(actual) is int and actual == expected:
        return
    raise AiSdkException(
        AiSdkError(
            kind="unsupported_schema",
            code="unsupported_schema",
            message=f"Unsupported {document} schemaVersion '{actual}'. Expected '{expected}'.",
        )
    )


def _invalid_request(code: str, message: str) -> AiSdkException:
    return AiSdkException(AiSdkError(kind="invalid_request", code=code, message=message))
