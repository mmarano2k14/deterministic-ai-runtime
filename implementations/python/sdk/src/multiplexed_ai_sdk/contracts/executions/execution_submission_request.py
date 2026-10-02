from __future__ import annotations

from dataclasses import dataclass, field

from ...json_types import AiSdkJsonValue
from ...wire_model import AiSdkWireModel
from ..common.schema_versions import AiSdkSchemaVersions


@dataclass(frozen=True)
class AiSdkExecutionSubmissionRequest(AiSdkWireModel):
    publication_ref: str
    schema_version: int = AiSdkSchemaVersions.EXECUTION_SUBMISSION_REQUEST
    idempotency_key: str | None = None
    input: AiSdkJsonValue | None = field(default=None, metadata={"json": True})
    metadata: dict[str, str] = field(default_factory=dict)
    correlation_id: str | None = None
