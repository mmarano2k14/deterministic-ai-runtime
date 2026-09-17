from enum import Enum


class AiSdkExecutionStepStatus(str, Enum):
    PENDING = "Pending"
    READY = "Ready"
    RUNNING = "Running"
    WAITING_FOR_RETRY = "WaitingForRetry"
    WAITING_FOR_EXTERNAL = "WaitingForExternal"
    COMPLETED = "Completed"
    FAILED = "Failed"
