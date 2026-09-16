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
    AiSdkError,
    AiSdkTransportRequest,
    AiSdkTransportResponse,
)

manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
expected_operations = [item["name"] for item in manifest["operations"]]
assert AI_SDK_PROTOCOL_VERSION == manifest["protocolVersion"]
assert list(AI_SDK_OPERATIONS.values()) == expected_operations
assert AI_SDK_OPERATION_RETRY["sdk.execution.observe"] == "safe-read"
assert AI_SDK_OPERATION_RETRY["sdk.execution.result"] == "safe-read"
for name in ("sdk.publish_pipeline", "sdk.execution.submit", "sdk.execution.cancel"):
    assert AI_SDK_OPERATION_RETRY[name] == "never"

request = AiSdkTransportRequest(operation="sdk.execution.observe", arguments={"executionId": "execution-1"})
assert request.protocol_version == 1
success = AiSdkTransportResponse(result={"status": "Running"})
assert success.is_success
failure = AiSdkTransportResponse(error=AiSdkError(kind="transport", code="transport_failure", message="failed"))
assert not failure.is_success

print(f"Python SDK foundation validated: {len(expected_operations)} operations.")
