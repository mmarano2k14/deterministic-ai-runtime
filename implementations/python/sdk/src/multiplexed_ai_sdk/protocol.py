AI_SDK_PROTOCOL_VERSION = 1

AI_SDK_OPERATIONS = {
    "publish_pipeline": "sdk.publish_pipeline",
    "submit_execution": "sdk.execution.submit",
    "observe_execution": "sdk.execution.observe",
    "get_execution_result": "sdk.execution.result",
    "cancel_execution": "sdk.execution.cancel",
}

AI_SDK_OPERATION_RETRY = {
    AI_SDK_OPERATIONS["publish_pipeline"]: "never",
    AI_SDK_OPERATIONS["submit_execution"]: "never",
    AI_SDK_OPERATIONS["observe_execution"]: "safe-read",
    AI_SDK_OPERATIONS["get_execution_result"]: "safe-read",
    AI_SDK_OPERATIONS["cancel_execution"]: "never",
}
