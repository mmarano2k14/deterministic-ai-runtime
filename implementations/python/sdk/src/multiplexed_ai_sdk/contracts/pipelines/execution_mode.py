from enum import Enum


class AiSdkExecutionMode(str, Enum):
    SEQUENTIAL = "Sequential"
    DAG = "Dag"
