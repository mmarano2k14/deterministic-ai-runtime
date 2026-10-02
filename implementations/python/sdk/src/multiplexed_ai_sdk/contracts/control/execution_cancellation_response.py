from __future__ import annotations

from dataclasses import dataclass

from ...wire_model import AiSdkWireModel
from ..common.schema_versions import AiSdkSchemaVersions
from ..executions.execution_status import AiSdkExecutionStatus


@dataclass(frozen=True)
class AiSdkExecutionCancellationResponse(AiSdkWireModel):
    execution_id: str
    cancellation_requested: bool
    status: AiSdkExecutionStatus
    schema_version: int = AiSdkSchemaVersions.EXECUTION_CANCELLATION_RESPONSE
    requested_at_utc: str | None = None
    correlation_id: str | None = None
