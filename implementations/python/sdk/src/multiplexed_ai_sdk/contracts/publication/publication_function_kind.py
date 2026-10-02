from enum import Enum


class AiSdkPublicationFunctionKind(str, Enum):
    STEP = "Step"
    CONCURRENCY_POLICY = "ConcurrencyPolicy"
    RETRY_POLICY = "RetryPolicy"
    DELEGATION_POLICY = "DelegationPolicy"
