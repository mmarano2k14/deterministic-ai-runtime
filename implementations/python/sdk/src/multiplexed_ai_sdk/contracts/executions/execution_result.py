from __future__ import annotations

from dataclasses import dataclass, field

from ...json_types import AiSdkJsonValue
from ...wire_model import AiSdkWireModel
from ..common.schema_versions import AiSdkSchemaVersions
from .execution_failure import AiSdkExecutionFailure
from .execution_status import AiSdkExecutionStatus


@dataclass(frozen=True)
class AiSdkExecutionResult(AiSdkWireModel):
    execution_id: str
    status: AiSdkExecutionStatus
    completed_at_utc: str
    schema_version: int = AiSdkSchemaVersions.EXECUTION_RESULT
    output: AiSdkJsonValue | None = field(default=None, metadata={"json": True})
    failure: AiSdkExecutionFailure | None = None
