from __future__ import annotations

from dataclasses import dataclass

from ...wire_model import AiSdkWireModel
from ..common.schema_versions import AiSdkSchemaVersions
from .execution_watch_channel import AiSdkExecutionWatchChannel


@dataclass(frozen=True)
class AiSdkExecutionWatchRequest(AiSdkWireModel):
    execution_id: str
    schema_version: int = AiSdkSchemaVersions.EXECUTION_WATCH_REQUEST
    channels: tuple[AiSdkExecutionWatchChannel, ...] = ()
    after_sequence: int | None = None
    include_initial_snapshot: bool = True
