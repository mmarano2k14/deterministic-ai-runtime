from __future__ import annotations

import argparse
import asyncio
import ast
import base64
import json
import os
import sys
import tomllib
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
    AiSdkException,
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


def _diagnostic(args: argparse.Namespace, message: str) -> None:
    line = f"[matrix-python-client] {message}"
    print(line, flush=True)
    diagnostic_log = getattr(args, "diagnostic_log", None)
    if diagnostic_log:
        path = Path(diagnostic_log).resolve()
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("a", encoding="utf-8") as stream:
            stream.write(line + "\n")


def _diagnostic_failure(args: argparse.Namespace, stage: str, exception: Exception) -> None:
    if isinstance(exception, AiSdkException):
        error = exception.error
        details = json.dumps(error.details, ensure_ascii=False, sort_keys=True, default=str)
        _diagnostic(
            args,
            f"{stage} FAILED AiSdkException kind={error.kind} code={error.code} "
            f"retryable={str(error.retryable).lower()} message={error.message} details={details}",
        )
        return
    _diagnostic(args, f"{stage} FAILED {type(exception).__name__}: {exception}")


async def main() -> int:
    args = _parse_args()
    _diagnostic(
        args,
        f"START scenario={args.scenario_id} worker={args.worker} topology={args.topology} "
        f"runtimeProvider={args.runtime_provider} environmentRef={args.environment_ref} endpoint={args.endpoint}",
    )
    if args.feature == "dependency-firewall":
        _run_dependency_firewall(args)
        return 0

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

    _diagnostic(args, "PUBLISH start")
    try:
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
    except Exception as exception:
        _diagnostic_failure(args, "PUBLISH", exception)
        raise
    _diagnostic(args, f"PUBLISH succeeded publicationRef={publication.publication_ref}")

    _diagnostic(args, "SUBMIT start")
    try:
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
    except Exception as exception:
        _diagnostic_failure(args, "SUBMIT", exception)
        raise
    _diagnostic(args, f"SUBMIT succeeded executionId={submitted.execution_id} status={submitted.status.value}")

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
                "environmentRef": args.environment_ref,
                "endpoint": args.endpoint,
                "topology": args.topology,
                "provider": args.provider,
                "runtimeProvider": args.runtime_provider,
                "workerExecutionProvider": args.worker_execution_provider,
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
        _diagnostic(args, f"OBSERVE waiting executionId={submitted.execution_id}")
        try:
            observation = await _wait_for_terminal(client, submitted.execution_id, args.terminal_timeout_seconds)
        except Exception as exception:
            _diagnostic_failure(args, "OBSERVE", exception)
            raise
        _diagnostic(args, f"OBSERVE terminal executionId={submitted.execution_id} status={observation.status.value}")
        try:
            result = await client.get_execution_result(submitted.execution_id)
        except Exception as exception:
            _diagnostic_failure(args, "RESULT", exception)
            raise
        _diagnostic(args, f"RESULT status={result.status.value}")
        if result.status.value != "Completed":
            raise RuntimeError(f"Execution '{submitted.execution_id}' ended as '{result.status.value}'.")
        output_marker_verified = False
        if args.require_output_marker:
            output_marker_verified = _contains_json_value(result.output, marker)
            if not output_marker_verified:
                _diagnostic(args, "MARKER verification failed")
                raise RuntimeError(
                    "Completed execution did not expose the published function marker in the terminal result output."
                )
            _diagnostic(args, "MARKER verified")

        _write_evidence(
            Path(args.evidence),
            {
                "schemaVersion": 1,
                "scenarioId": args.scenario_id,
                "status": "passed",
                "clientLanguage": "python",
                "workerLanguage": args.worker,
                "environmentRef": args.environment_ref,
                "endpoint": args.endpoint,
                "topology": args.topology,
                "provider": args.provider,
                "runtimeProvider": args.runtime_provider,
                "workerExecutionProvider": args.worker_execution_provider,
                "publicationRef": publication.publication_ref,
                "executionId": submitted.execution_id,
                "terminalStatus": result.status.value,
                "outputMarkerVerified": output_marker_verified,
                "evidence": ["publish", "submit", "observe", "terminal-result", "public-execution-id"],
                "recordedAtUtc": observation.updated_at_utc or None,
            },
        )
    return 0



def _run_dependency_firewall(args: argparse.Namespace) -> None:
    sdk_root = REPO_ROOT / "implementations" / "python" / "sdk"
    project = tomllib.loads((sdk_root / "pyproject.toml").read_text(encoding="utf-8"))
    declared_dependencies = sorted(project.get("project", {}).get("dependencies", []))

    def dependency_name(requirement: str) -> str:
        value = requirement.split(";", 1)[0].strip()
        for separator in ("[", "<", ">", "=", "!", "~", " "):
            value = value.split(separator, 1)[0]
        return value.strip().lower().replace("_", "-")

    forbidden_declared_dependencies = sorted(
        requirement
        for requirement in declared_dependencies
        if dependency_name(requirement).startswith("multiplexed-")
    )

    absolute_import_roots: set[str] = set()
    for source in (sdk_root / "src" / "multiplexed_ai_sdk").rglob("*.py"):
        tree = ast.parse(source.read_text(encoding="utf-8"), filename=str(source))
        for node in ast.walk(tree):
            if isinstance(node, ast.Import):
                absolute_import_roots.update(alias.name.split(".", 1)[0] for alias in node.names)
            elif isinstance(node, ast.ImportFrom) and node.level == 0 and node.module:
                absolute_import_roots.add(node.module.split(".", 1)[0])

    forbidden_absolute_imports = sorted(
        name for name in absolute_import_roots
        if name.startswith("multiplexed") and name != "multiplexed_ai_sdk"
    )
    if forbidden_declared_dependencies or forbidden_absolute_imports:
        raise RuntimeError(
            "External Python SDK dependency firewall detected repository dependencies: "
            + ", ".join([*forbidden_declared_dependencies, *forbidden_absolute_imports])
        )

    _write_evidence(
        Path(args.evidence),
        {
            "schemaVersion": 1,
            "scenarioId": args.scenario_id,
            "status": "passed",
            "coverageTarget": "external-client-dependency-firewall",
            "coverageValues": [],
            "clientLanguage": "python",
            "workerLanguage": None,
            "topology": args.topology,
            "provider": args.provider,
            "runtimeProvider": args.runtime_provider,
            "workerExecutionProvider": args.worker_execution_provider,
            "artifactKind": "python-sdk-source-distribution",
            "declaredDependencies": declared_dependencies,
            "absoluteImportRoots": sorted(absolute_import_roots),
            "forbiddenDeclaredDependencies": forbidden_declared_dependencies,
            "forbiddenAbsoluteImports": forbidden_absolute_imports,
            "evidence": [
                "pyproject-dependencies-inspected",
                "sdk-source-imports-inspected",
                "no-engine-runtime-dependency",
            ],
            "recordedAtUtc": None,
        },
    )

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


def _sample_root() -> Path:
    configured = os.environ.get("MATRIX_SAMPLE_ROOT")
    if configured:
        return Path(configured).resolve()
    return REPO_ROOT / "implementations" / "sdk" / "samples" / "published-functions"


def _worker_source(worker: str) -> dict[str, object]:
    sample_root = _sample_root()
    if worker == "python":
        path = sample_root / "python" / "functions.py"
        return {"entry_point_path": "functions.py", "entry_point_symbol": "run", "bytes": path.read_bytes()}
    if worker == "typescript":
        path = sample_root / "typescript" / "functions.ts"
        return {"entry_point_path": "functions.ts", "entry_point_symbol": "run", "bytes": path.read_bytes()}
    if worker == "dotnet":
        path = sample_root / "dotnet" / "Multiplexed.AI.Samples.PublishedFunctions.dll"
        return {"entry_point_path": "functions.dll", "entry_point_symbol": "Multiplexed.AI.Samples.PublishedFunctions.Functions::Run", "bytes": path.read_bytes()}
    raise ValueError(f"Unsupported worker language '{worker}'.")


def _cancellation_worker_source(worker: str) -> dict[str, object]:
    sample_root = _sample_root()
    if worker == "python":
        return {
            "entry_point_path": "main.py",
            "entry_point_symbol": "run",
            "bytes": (
                "import time\n"
                "def run(inputs, context):\n"
                "    time.sleep(8)\n"
                "    return {'success': True, 'payload': {'cancellationSample': True}}\n"
            ).encode("utf-8"),
        }
    if worker == "typescript":
        return {
            "entry_point_path": "main.ts",
            "entry_point_symbol": "run",
            "bytes": (
                "export async function run(inputs: unknown, context: unknown) { "
                "await new Promise(resolve => setTimeout(resolve, 8000)); "
                "return { success: true, payload: { cancellationSample: true } }; }\n"
            ).encode("utf-8"),
        }
    if worker == "dotnet":
        path = sample_root / "dotnet" / "Multiplexed.AI.Samples.PublishedFunctions.dll"
        return {
            "entry_point_path": "functions.dll",
            "entry_point_symbol": "Multiplexed.AI.Samples.PublishedFunctions.Functions::PinStable",
            "bytes": path.read_bytes(),
        }
    raise ValueError(f"Unsupported worker language '{worker}'.")


def _contains_json_value(value: object, expected: object) -> bool:
    if value == expected:
        return True
    if isinstance(value, dict):
        return any(_contains_json_value(item, expected) for item in value.values())
    if isinstance(value, (list, tuple)):
        return any(_contains_json_value(item, expected) for item in value)
    return False


def _write_evidence(path: Path, document: dict[str, object]) -> None:
    path = path.resolve()
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--endpoint")
    parser.add_argument("--feature", choices=("cancellation", "dependency-firewall"))
    parser.add_argument("--worker", required=True, choices=("dotnet", "typescript", "python"))
    parser.add_argument("--environment-ref")
    parser.add_argument("--scenario-id", required=True)
    parser.add_argument("--evidence", required=True)
    parser.add_argument("--token")
    parser.add_argument("--access-context")
    parser.add_argument("--access-context-header", default="X-Access-Context")
    parser.add_argument("--topology", default="local")
    parser.add_argument("--provider", default="ProcessHostPool")
    parser.add_argument("--runtime-provider", default="ProcessHostPool")
    parser.add_argument("--worker-execution-provider", default="TrustedProcess")
    parser.add_argument("--require-output-marker", action="store_true")
    parser.add_argument("--diagnostic-log")
    parser.add_argument("--terminal-timeout-seconds", type=float, default=90.0)
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
        args.runtime_provider = manifest.get("runtimeProvider", args.runtime_provider)
        args.worker_execution_provider = manifest.get("workerExecutionProvider", args.worker_execution_provider)
    if args.terminal_timeout_seconds <= 0:
        parser.error("--terminal-timeout-seconds must be positive")
    if not args.endpoint or not args.environment_ref:
        parser.error("--endpoint and --environment-ref are required unless --manifest supplies them")
    return args



if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
