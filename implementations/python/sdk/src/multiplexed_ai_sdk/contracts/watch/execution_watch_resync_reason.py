from enum import Enum


class AiSdkExecutionWatchResyncReason(str, Enum):
    HISTORY_UNAVAILABLE = "HistoryUnavailable"
    GAP_DETECTED = "GapDetected"
    INVALID_CURSOR = "InvalidCursor"
