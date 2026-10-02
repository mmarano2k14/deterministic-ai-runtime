from dataclasses import dataclass, field

from ...wire_model import AiSdkWireModel
from ..common.schema_versions import AiSdkSchemaVersions


@dataclass(frozen=True)
class AiSdkExecutionReplayResponse(AiSdkWireModel):
    execution_id: str
    succeeded: bool
    started_at_utc: str
    completed_at_utc: str
    duration_ms: int
    schema_version: int = AiSdkSchemaVersions.EXECUTION_REPLAY_RESPONSE
    deterministic: bool | None = None
    message: str | None = None
    diagnostics: tuple[str, ...] = field(default_factory=tuple)
    failure_reason: str | None = None
    correlation_id: str | None = None
