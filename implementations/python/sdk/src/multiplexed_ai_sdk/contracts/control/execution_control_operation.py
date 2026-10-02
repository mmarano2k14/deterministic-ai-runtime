from enum import Enum


class AiSdkExecutionControlOperation(str, Enum):
    PAUSE = "Pause"
    RESUME = "Resume"
    SUBMIT_INPUT = "SubmitInput"
