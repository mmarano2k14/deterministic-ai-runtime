AI_SDK_PROTOCOL_VERSION = 1

AI_SDK_OPERATIONS = {
    "publish_pipeline": "sdk.publish_pipeline",
    "submit_execution": "sdk.execution.submit",
    "observe_execution": "sdk.execution.observe",
    "watch_execution": "sdk.execution.watch",
    "get_execution_result": "sdk.execution.result",
    "cancel_execution": "sdk.execution.cancel",
    "pause_execution": "sdk.execution.pause",
    "resume_execution": "sdk.execution.resume",
    "submit_execution_input": "sdk.execution.input.submit",
    "replay_execution": "sdk.execution.replay",
}

AI_SDK_OPERATION_RETRY = {
    AI_SDK_OPERATIONS["publish_pipeline"]: "never",
    AI_SDK_OPERATIONS["submit_execution"]: "never",
    AI_SDK_OPERATIONS["observe_execution"]: "safe-read",
    AI_SDK_OPERATIONS["watch_execution"]: "safe-read",
    AI_SDK_OPERATIONS["get_execution_result"]: "safe-read",
    AI_SDK_OPERATIONS["cancel_execution"]: "never",
    AI_SDK_OPERATIONS["pause_execution"]: "never",
    AI_SDK_OPERATIONS["resume_execution"]: "never",
    AI_SDK_OPERATIONS["submit_execution_input"]: "never",
    AI_SDK_OPERATIONS["replay_execution"]: "never",
}
