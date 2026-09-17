from __future__ import annotations

import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
SDK_ROOT = HERE.parent
SRC_ROOT = SDK_ROOT / "src"
MANIFEST = SDK_ROOT.parent.parent / "sdk" / "protocol" / "ai-sdk-protocol-v1.json"
sys.path.insert(0, str(SRC_ROOT))

from multiplexed_ai_sdk import (  # noqa: E402
    AI_SDK_OPERATION_RETRY,
    AI_SDK_OPERATIONS,
    AI_SDK_PROTOCOL_VERSION,
    AiSdkExecutionMode,
    AiSdkExecutionStatus,
    AiSdkInvocationKind,
    AiSdkPublicationDependencyPackageKind,
    AiSdkPublicationFunctionKind,
)

manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
expected_operations = [item["name"] for item in manifest["operations"]]
assert AI_SDK_PROTOCOL_VERSION == manifest["protocolVersion"]
assert list(AI_SDK_OPERATIONS.values()) == expected_operations
assert AI_SDK_OPERATION_RETRY["sdk.execution.observe"] == "safe-read"
assert AI_SDK_OPERATION_RETRY["sdk.execution.result"] == "safe-read"
for name in ("sdk.publish_pipeline", "sdk.execution.submit", "sdk.execution.cancel"):
    assert AI_SDK_OPERATION_RETRY[name] == "never"

assert [item.value for item in AiSdkExecutionMode] == ["Sequential", "Dag"]
assert [item.value for item in AiSdkInvocationKind] == ["Native", "Custom", "Mcp"]
assert [item.value for item in AiSdkExecutionStatus] == [
    "Pending", "Running", "Waiting", "Completed", "Failed", "Cancelled"
]
assert [item.value for item in AiSdkPublicationFunctionKind] == [
    "Step", "ConcurrencyPolicy", "RetryPolicy", "DelegationPolicy"
]
assert [item.value for item in AiSdkPublicationDependencyPackageKind] == [
    "PythonWheelBundle", "NodeLockedBundle", "DotNetAssemblyClosure"
]

for forbidden in (
    "Multiplexed.AI",
    "MongoDB",
    "StackExchange.Redis",
    "TenantId",
    "TenantGroupId",
    "SharedRunId",
    "LocalRunId",
    "RuntimeInstanceId",
    "WorkerId",
    "ClaimToken",
    "AssignmentEpoch",
    "ControlPlaneId",
):
    for path in SRC_ROOT.rglob("*.py"):
        assert forbidden not in path.read_text(encoding="utf-8"), f"Forbidden token {forbidden} in {path}"

print(f"Python external SDK validated: {len(expected_operations)} operations.")
