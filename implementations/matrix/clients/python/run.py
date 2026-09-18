from __future__ import annotations

import argparse
import asyncio
import base64
import json
import os
import sys
import uuid
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[4]
SDK_SRC = REPO_ROOT / "implementations" / "python" / "sdk" / "src"
if str(SDK_SRC) not in sys.path:
    sys.path.insert(0, str(SDK_SRC))

from multiplexed_ai_sdk import (  # noqa: E402
    AiSdkClient,
    AiSdkCredential,
    AiSdkExecutionCancellationRequest,
    AiSdkExecutionMode,
    AiSdkExecutionStatus,
    AiSdkExecutionStepStatus,
    AiSdkExecutionSubmissionRequest,
    AiSdkInvocationDefinition,
    AiSdkInvocationKind,
    AiSdkMcpHttpTransport,
    AiSdkPipelineDefinition,
    AiSdkPipelinePublicationRequest,
    AiSdkPipelineStepDefinition,
    AiSdkPublicationCallSite,
    AiSdkPublicationFileUpload,
    AiSdkPublicationFunctionKind,
    AiSdkPublicationFunctionUpload,
    AiSdkStaticCredentialProvider,
    AiSdkTransportOptions,
)


async def main() -> int:
    args = _parse_args()
    provider = (
        AiSdkStaticCredentialProvider(AiSdkCredential("Bearer", args.token))
        if args.token
        else None
    )
    client = AiSdkClient(
        AiSdkMcpHttpTransport(
            args.endpoint,
            AiSdkTransportOptions(
                credential_provider=provider,
                additional_headers=(
                    {args.access_context_header: args.access_context}
                    if args.access_context
                    else None
                ),
            ),
        )
    )
    source = _cancellation_worker_source(args.worker) if args.feature == "cancellation" else _worker_source(args.worker)
    marker = f"{args.scenario_id}-marker"

    publication = await client.publish_pipeline(
        AiSdkPipelinePublicationRequest(
            definition=AiSdkPipelineDefinition(
                name=f"matrix-{args.scenario_id}",
                version="1",
                execution_language=args.worker,
                execution_mode=AiSdkExecutionMode.DAG,
                steps=(
                    AiSdkPipelineStepDefinition(
                        name="work",
                        step_key="custom",
                        order=0,
                        execution_language=args.worker,
                        invocation=AiSdkInvocationDefinition(kind=AiSdkInvocationKind.CUSTOM),
                        input={"marker": marker},
                    ),
                ),
            ),
            functions=(
                AiSdkPublicationFunctionUpload(
                    site=AiSdkPublicationCallSite(
                        kind=AiSdkPublicationFunctionKind.STEP,
                        step_name="work",
                    ),
                    environment_ref=args.environment_ref,
                    entry_point_path=source["entry_point_path"],
                    entry_point_symbol=source["entry_point_symbol"],
                    sources=(
                        AiSdkPublicationFileUpload(
                            path=source["entry_point_path"],
                            content_base64=base64.b64encode(source["bytes"]).decode("ascii"),
                        ),
                    ),
                ),
            ),
        )
    )

    submitted = await client.submit_execution(
        AiSdkExecutionSubmissionRequest(
            publication_ref=publication.publication_ref,
            idempotency_key=f"{args.scenario_id}-{uuid.uuid4().hex}",
            input={"marker": marker},
            metadata={
                "matrix.scenario": args.scenario_id,
                "matrix.client": "python",
                "matrix.worker": args.worker,
            },
        )
    )

    if args.feature == "cancellation":
        active = await _wait_for_active_step(client, submitted.execution_id, "work", 30.0)
        correlation_id = f"cancel-{uuid.uuid4().hex}"
        cancellation = await client.cancel_execution(
            submitted.execution_id,
            AiSdkExecutionCancellationRequest(
                reason="matrix-running-cancellation",
                correlation_id=correlation_id,
            ),
        )
        if cancellation.execution_id != submitted.execution_id or cancellation.cancellation_requested is not True:
            raise RuntimeError("Public cancellation operation did not acknowledge the submitted execution.")
        if cancellation.requested_at_utc is None or cancellation.correlation_id != correlation_id:
            raise RuntimeError("Public cancellation acknowledgement did not preserve durable request metadata.")

        observation = await _wait_for_terminal(client, submitted.execution_id, 45.0)
        result = await client.get_execution_result(submitted.execution_id)
        if observation.status != AiSdkExecutionStatus.CANCELLED or result.status != AiSdkExecutionStatus.CANCELLED:
            raise RuntimeError(
                f"Cancellation scenario ended as observation='{observation.status.value}', result='{result.status.value}', expected 'Cancelled'."
            )
        if result.output is not None or result.failure is not None:
            raise RuntimeError("Cancelled public result unexpectedly exposed completed output or failure payload.")
        active_step = next((step for step in active.steps if step.name == "work"), None)
        terminal_step = next((step for step in observation.steps if step.name == "work"), None)
        _write_evidence(
            Path(args.evidence),
            {
                "schemaVersion": 1,
                "scenarioId": args.scenario_id,
                "status": "passed",
                "coverageTarget": "cancellation",
                "coverageValues": [],
                "cancellationMode": "running-cooperative",
                "clientLanguage": "python",
                "workerLanguage": args.worker,
                "endpoint": args.endpoint,
                "topology": args.topology,
                "provider": args.provider,
                "publicationRef": publication.publication_ref,
                "executionId": submitted.execution_id,
                "activeStatusBeforeCancel": active.status.value,
                "activeStepStatusBeforeCancel": active_step.status.value if active_step else None,
                "cancellationRequested": cancellation.cancellation_requested,
                "cancellationRequestedAtUtc": cancellation.requested_at_utc,
                "cancellationCorrelationId": cancellation.correlation_id,
                "terminalStatus": result.status.value,
                "terminalStepStatus": terminal_step.status.value if terminal_step else None,
                "evidence": [
                    "publish",
                    "submit",
                    "active-execution-observed",
                    "sdk-execution-cancel",
                    "durable-cancellation-acknowledged",
                    "terminal-cancelled-observed",
                    "terminal-result",
                ],
                "recordedAtUtc": observation.updated_at_utc or None,
            },
        )
    else:
        if args.feature:
            raise ValueError(f"Unsupported --feature '{args.feature}'.")
        observation = await _wait_for_terminal(client, submitted.execution_id, 90.0)
        result = await client.get_execution_result(submitted.execution_id)
        if result.status.value != "Completed":
            raise RuntimeError(f"Execution '{submitted.execution_id}' ended as '{result.status.value}'.")

        _write_evidence(
            Path(args.evidence),
            {
                "schemaVersion": 1,
                "scenarioId": args.scenario_id,
                "status": "passed",
                "clientLanguage": "python",
                "workerLanguage": args.worker,
                "endpoint": args.endpoint,
                "topology": args.topology,
                "provider": args.provider,
                "publicationRef": publication.publication_ref,
                "executionId": submitted.execution_id,
                "terminalStatus": result.status.value,
                "evidence": ["publish", "submit", "observe", "terminal-result", "public-execution-id"],
                "recordedAtUtc": observation.updated_at_utc or None,
            },
        )
    return 0


async def _wait_for_active_step(client: AiSdkClient, execution_id: str, step_name: str, timeout_seconds: float):
    loop = asyncio.get_running_loop()
    deadline = loop.time() + timeout_seconds
    while loop.time() < deadline:
        observation = await client.observe_execution(execution_id)
        if observation.status in {AiSdkExecutionStatus.COMPLETED, AiSdkExecutionStatus.FAILED, AiSdkExecutionStatus.CANCELLED}:
            raise RuntimeError(
                f"Execution '{execution_id}' became terminal as '{observation.status.value}' before cancellation could be requested."
            )
        step = next((item for item in observation.steps if item.name == step_name), None)
        if step is not None and step.status in {AiSdkExecutionStepStatus.RUNNING, AiSdkExecutionStepStatus.WAITING_FOR_EXTERNAL}:
            return observation
        await asyncio.sleep(0.1)
    raise TimeoutError(f"Execution '{execution_id}' did not expose active step '{step_name}' within {timeout_seconds} seconds.")


async def _wait_for_terminal(client: AiSdkClient, execution_id: str, timeout_seconds: float):
    loop = asyncio.get_running_loop()
    deadline = loop.time() + timeout_seconds
    while loop.time() < deadline:
        observation = await client.observe_execution(execution_id)
        if observation.status.value in {"Completed", "Failed", "Cancelled"}:
            return observation
        await asyncio.sleep(0.25)
    raise TimeoutError(f"Execution '{execution_id}' did not become terminal within {timeout_seconds} seconds.")


def _worker_source(worker: str) -> dict[str, object]:
    configured = os.environ.get("MATRIX_FIXTURE_ROOT")
    fixture_root = Path(configured).resolve() if configured else REPO_ROOT / "implementations" / "matrix" / "fixtures"
    if worker == "python":
        path = fixture_root / "python-worker" / "main.py"
        return {"entry_point_path": "main.py", "entry_point_symbol": "run", "bytes": path.read_bytes()}
    if worker == "typescript":
        path = fixture_root / "typescript-worker" / "main.ts"
        return {"entry_point_path": "main.ts", "entry_point_symbol": "run", "bytes": path.read_bytes()}
    if worker == "dotnet":
        path = fixture_root / "dotnet-worker" / "Multiplexed.AI.Matrix.Worker.dll"
        return {"entry_point_path": "functions.dll", "entry_point_symbol": "Multiplexed.AI.Matrix.Worker.Functions::Run", "bytes": path.read_bytes()}
    raise ValueError(f"Unsupported worker language '{worker}'.")


def _cancellation_worker_source(worker: str) -> dict[str, object]:
    configured = os.environ.get("MATRIX_FIXTURE_ROOT")
    fixture_root = Path(configured).resolve() if configured else REPO_ROOT / "implementations" / "matrix" / "fixtures"
    if worker == "python":
        return {
            "entry_point_path": "main.py",
            "entry_point_symbol": "run",
            "bytes": (
                "import time\n"
                "def run(inputs, context):\n"
                "    time.sleep(8)\n"
                "    return {'success': True, 'payload': {'cancellationFixture': True}}\n"
            ).encode("utf-8"),
        }
    if worker == "typescript":
        return {
            "entry_point_path": "main.ts",
            "entry_point_symbol": "run",
            "bytes": (
                "export async function run(inputs: unknown, context: unknown) { "
                "await new Promise(resolve => setTimeout(resolve, 8000)); "
                "return { success: true, payload: { cancellationFixture: true } }; }\n"
            ).encode("utf-8"),
        }
    if worker == "dotnet":
        path = fixture_root / "dotnet-worker" / "Multiplexed.AI.Matrix.Worker.dll"
        return {
            "entry_point_path": "functions.dll",
            "entry_point_symbol": "Multiplexed.AI.Matrix.Worker.Functions::PinStable",
            "bytes": path.read_bytes(),
        }
    raise ValueError(f"Unsupported worker language '{worker}'.")


def _write_evidence(path: Path, document: dict[str, object]) -> None:
    path = path.resolve()
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--endpoint")
    parser.add_argument("--feature", choices=("cancellation",))
    parser.add_argument("--worker", required=True, choices=("dotnet", "typescript", "python"))
    parser.add_argument("--environment-ref")
    parser.add_argument("--scenario-id", required=True)
    parser.add_argument("--evidence", required=True)
    parser.add_argument("--token")
    parser.add_argument("--access-context")
    parser.add_argument("--access-context-header", default="X-Access-Context")
    parser.add_argument("--topology", default="local")
    parser.add_argument("--provider", default="ProcessHostPool")
    parser.add_argument("--manifest")
    args = parser.parse_args()
    if args.manifest:
        manifest = json.loads(Path(args.manifest).resolve().read_text(encoding="utf-8-sig"))
        args.endpoint = args.endpoint or manifest["endpoint"]
        args.environment_ref = args.environment_ref or manifest["environmentRefs"][args.worker]
        args.token = args.token or manifest.get("bearerToken")
        args.access_context = args.access_context or manifest.get("accessContext")
        args.access_context_header = manifest.get("accessContextHeader", args.access_context_header)
        args.topology = manifest.get("topology", args.topology)
        args.provider = manifest.get("provider", args.provider)
    if not args.endpoint or not args.environment_ref:
        parser.error("--endpoint and --environment-ref are required unless --manifest supplies them")
    return args



if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
