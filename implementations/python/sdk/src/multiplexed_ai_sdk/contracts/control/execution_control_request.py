from dataclasses import dataclass

from ...wire_model import AiSdkWireModel
from ..common.schema_versions import AiSdkSchemaVersions


@dataclass(frozen=True)
class AiSdkExecutionControlRequest(AiSdkWireModel):
    schema_version: int = AiSdkSchemaVersions.EXECUTION_CONTROL_REQUEST
    reason: str | None = None
    correlation_id: str | None = None
