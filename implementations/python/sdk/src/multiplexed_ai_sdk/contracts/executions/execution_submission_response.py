from __future__ import annotations

from dataclasses import dataclass

from ...wire_model import AiSdkWireModel
from ..common.schema_versions import AiSdkSchemaVersions
from .execution_status import AiSdkExecutionStatus


@dataclass(frozen=True)
class AiSdkExecutionSubmissionResponse(AiSdkWireModel):
    execution_id: str
    publication_ref: str
    status: AiSdkExecutionStatus
    accepted_at_utc: str
    schema_version: int = AiSdkSchemaVersions.EXECUTION_SUBMISSION_RESPONSE
    idempotency_key: str | None = None
    correlation_id: str | None = None
