from enum import Enum


class AiSdkExecutionWatchChannel(str, Enum):
    LIFECYCLE = "Lifecycle"
    STEPS = "Steps"
    POLICIES = "Policies"
    CHILDREN = "Children"
    EFFECTS = "Effects"
    RECOVERY = "Recovery"
