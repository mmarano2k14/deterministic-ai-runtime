from enum import Enum


class AiSdkExecutionControlStatus(str, Enum):
    NONE = "None"
    RUNNING = "Running"
    PAUSING = "Pausing"
    PAUSED = "Paused"
    RESUMING = "Resuming"
    CANCELLING = "Cancelling"
    CANCELLED = "Cancelled"
    WAITING_FOR_INPUT = "WaitingForInput"
