from __future__ import annotations

from typing import TypeVar

from .contracts.common.schema_versions import AiSdkSchemaVersions
from .contracts.control.execution_cancellation_request import AiSdkExecutionCancellationRequest
from .contracts.control.execution_cancellation_response import AiSdkExecutionCancellationResponse
from .contracts.executions.execution_result import AiSdkExecutionResult
from .contracts.executions.execution_submission_request import AiSdkExecutionSubmissionRequest
from .contracts.executions.execution_submission_response import AiSdkExecutionSubmissionResponse
from .contracts.observation.execution_observation import AiSdkExecutionObservation
from .contracts.publication.pipeline_publication_request import AiSdkPipelinePublicationRequest
from .contracts.publication.pipeline_publication_response import AiSdkPipelinePublicationResponse
from .errors import AiSdkError, AiSdkException
from .json_types import AiSdkJsonObject
from .protocol import AI_SDK_OPERATIONS
from .transport import AiSdkTransport, AiSdkTransportRequest
from .wire_model import AiSdkWireModel

TResponse = TypeVar("TResponse", bound=AiSdkWireModel)


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
