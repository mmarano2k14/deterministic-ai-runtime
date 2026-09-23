from dataclasses import dataclass, field

from ...json_types import AiSdkJsonObject, AiSdkJsonValue
from ...wire_model import AiSdkWireModel
from ..common.schema_versions import AiSdkSchemaVersions


@dataclass(frozen=True)
class AiSdkExecutionInputSubmissionRequest(AiSdkWireModel):
    waiting_key: str
    input: AiSdkJsonObject = field(metadata={"json": True})
    schema_version: int = AiSdkSchemaVersions.EXECUTION_INPUT_SUBMISSION_REQUEST
    waiting_step_name: str | None = None
    reason: str | None = None
    correlation_id: str | None = None
