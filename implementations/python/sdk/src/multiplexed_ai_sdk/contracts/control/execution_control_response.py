from dataclasses import dataclass

from ...wire_model import AiSdkWireModel
from ..common.schema_versions import AiSdkSchemaVersions
from .execution_control_operation import AiSdkExecutionControlOperation
from .execution_control_state import AiSdkExecutionControlState


@dataclass(frozen=True)
class AiSdkExecutionControlResponse(AiSdkWireModel):
    execution_id: str
    operation: AiSdkExecutionControlOperation
    accepted: bool
    accepted_at_utc: str
    schema_version: int = AiSdkSchemaVersions.EXECUTION_CONTROL_RESPONSE
    state: AiSdkExecutionControlState | None = None
    correlation_id: str | None = None
