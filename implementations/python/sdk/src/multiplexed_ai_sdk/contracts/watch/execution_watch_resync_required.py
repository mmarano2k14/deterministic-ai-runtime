from __future__ import annotations

from dataclasses import dataclass

from ...wire_model import AiSdkWireModel
from ..common.schema_versions import AiSdkSchemaVersions
from .execution_watch_resync_reason import AiSdkExecutionWatchResyncReason


@dataclass(frozen=True)
class AiSdkExecutionWatchResyncRequired(AiSdkWireModel):
    reason: AiSdkExecutionWatchResyncReason
    schema_version: int = AiSdkSchemaVersions.EXECUTION_WATCH_RESYNC_REQUIRED
    requested_after_sequence: int | None = None
    earliest_available_sequence: int | None = None
    latest_sequence: int | None = None
    message: str | None = None
