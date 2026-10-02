from enum import Enum


class AiSdkExecutionWatchEventKind(str, Enum):
    SNAPSHOT = "Snapshot"
    EVENT = "Event"
    RESYNC_REQUIRED = "ResyncRequired"
