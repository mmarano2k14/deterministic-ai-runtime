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
    AiSdkExecutionMode,
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
    source = _worker_source(args.worker)
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


def _write_evidence(path: Path, document: dict[str, object]) -> None:
    path = path.resolve()
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--endpoint")
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
