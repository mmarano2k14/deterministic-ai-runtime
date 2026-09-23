from dataclasses import dataclass

from ...wire_model import AiSdkWireModel
from ..common.schema_versions import AiSdkSchemaVersions


@dataclass(frozen=True)
class AiSdkExecutionReplayRequest(AiSdkWireModel):
    schema_version: int = AiSdkSchemaVersions.EXECUTION_REPLAY_REQUEST
    strict_determinism: bool = True
    include_diagnostics: bool = True
    reason: str | None = None
    correlation_id: str | None = None
