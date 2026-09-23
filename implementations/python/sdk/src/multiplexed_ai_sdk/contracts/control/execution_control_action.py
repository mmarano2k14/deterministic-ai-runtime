from enum import Enum


class AiSdkExecutionControlAction(str, Enum):
    NONE = "None"
    PAUSE = "Pause"
    RESUME = "Resume"
    CANCEL = "Cancel"
    WAIT_FOR_INPUT = "WaitForInput"
    SUBMIT_INPUT = "SubmitInput"
