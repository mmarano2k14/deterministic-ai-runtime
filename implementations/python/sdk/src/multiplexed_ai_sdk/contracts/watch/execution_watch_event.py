from __future__ import annotations

from dataclasses import dataclass, field

from ...json_types import AiSdkJsonValue
from ...wire_model import AiSdkWireModel
from ..common.schema_versions import AiSdkSchemaVersions
from ..observation.execution_observation import AiSdkExecutionObservation
from .execution_watch_channel import AiSdkExecutionWatchChannel
from .execution_watch_event_kind import AiSdkExecutionWatchEventKind
from .execution_watch_resync_required import AiSdkExecutionWatchResyncRequired


@dataclass(frozen=True)
class AiSdkExecutionWatchEvent(AiSdkWireModel):
    execution_id: str
    kind: AiSdkExecutionWatchEventKind
    occurred_at_utc: str
    schema_version: int = AiSdkSchemaVersions.EXECUTION_WATCH_EVENT
    sequence: int | None = None
    channel: AiSdkExecutionWatchChannel | None = None
    event_type: str | None = None
    payload_schema_version: int | None = None
    payload: AiSdkJsonValue | None = field(default=None, metadata={"json": True})
    snapshot: AiSdkExecutionObservation | None = None
    resync_required: AiSdkExecutionWatchResyncRequired | None = None
