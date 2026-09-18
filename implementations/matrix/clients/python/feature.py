from __future__ import annotations

import argparse
import asyncio
import base64
import hashlib
import io
import json
import os
import sys
import uuid
import zipfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[4]
SDK_SRC = REPO_ROOT / "implementations" / "python" / "sdk" / "src"
if str(SDK_SRC) not in sys.path:
    sys.path.insert(0, str(SDK_SRC))

from multiplexed_ai_sdk import (  # noqa: E402
    AiSdkClient,
    AiSdkCredential,
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
    AiSdkPublicationDependencyPackage,
    AiSdkPublicationDependencyPackageKind,
    AiSdkPublicationDependencyUpload,
    AiSdkPublicationFileUpload,
    AiSdkPublicationFunctionKind,
    AiSdkPublicationFunctionUpload,
    AiSdkStaticCredentialProvider,
    AiSdkTransportOptions,
)

TERMINAL = {
    AiSdkExecutionStatus.COMPLETED,
    AiSdkExecutionStatus.FAILED,
    AiSdkExecutionStatus.CANCELLED,
}


async def main() -> int:
    args = _parse_args()
    client = _client(args)

    if args.feature == "publication-pinning":
        document = await _run_publication_pinning(client, args)
    elif args.feature == "deterministic-dependency-packaging":
        document = await _run_dependency_packaging(client, args)
    elif args.feature == "custom-policy-family":
        document = await _run_custom_policy_family(client, args)
    elif args.feature == "nested-child-dag":
        document = await _run_nested_child_dag(client, args)
    else:
        raise ValueError(f"Unsupported feature '{args.feature}'.")

    _write_evidence(Path(args.evidence), document)
    return 0


def _client(args: argparse.Namespace) -> AiSdkClient:
    provider = (
        AiSdkStaticCredentialProvider(AiSdkCredential("Bearer", args.token))
        if args.token
        else None
    )
    return AiSdkClient(
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


async def _run_publication_pinning(client: AiSdkClient, args: argparse.Namespace) -> dict[str, object]:
    stable = _pinning_function(args.worker, replacement=False)
    poison = _pinning_function(args.worker, replacement=True)
    pipeline_name = f"matrix-feature-publication-pinning-{args.worker}"

    original = await client.publish_pipeline(
        _publication_request(pipeline_name, args.worker, args.environment_ref, stable)
    )
    submitted = await client.submit_execution(
        AiSdkExecutionSubmissionRequest(
            publication_ref=original.publication_ref,
            idempotency_key=f"{args.scenario_id}-{uuid.uuid4().hex}",
            input={"feature": "publication-pinning"},
            metadata=_metadata(args),
        )
    )

    replacement = await client.publish_pipeline(
        _publication_request(pipeline_name, args.worker, args.environment_ref, poison)
    )

    if replacement.publication_ref == original.publication_ref:
        raise RuntimeError("Replacement publication did not produce a distinct immutable publication reference.")

    after_republication = await client.observe_execution(submitted.execution_id)
    if after_republication.publication_ref != original.publication_ref:
        raise RuntimeError(
            "The submitted execution changed publication identity after a later publication was created."
        )
    if after_republication.status in TERMINAL:
        raise RuntimeError(
            "The execution reached a terminal state before the post-republication pinning proof could be observed."
        )

    terminal = await _wait_for_terminal(client, submitted.execution_id, 90.0)
    result = await client.get_execution_result(submitted.execution_id)
    if result.status != AiSdkExecutionStatus.COMPLETED:
        raise RuntimeError(
            f"Execution '{submitted.execution_id}' ended as '{result.status.value}'. "
            "The replacement publication intentionally fails if it is ever executed."
        )
    if terminal.publication_ref != original.publication_ref:
        raise RuntimeError("Terminal observation no longer references the original immutable publication.")

    return {
        "schemaVersion": 1,
        "scenarioId": args.scenario_id,
        "status": "passed",
        "coverageTarget": "publication-pinning",
        "coverageValues": [],
        "clientLanguage": "python",
        "workerLanguage": args.worker,
        "endpoint": args.endpoint,
        "topology": args.topology,
        "provider": args.provider,
        "originalPublicationRef": original.publication_ref,
        "replacementPublicationRef": replacement.publication_ref,
        "submittedPublicationRef": submitted.publication_ref,
        "postRepublishObservedPublicationRef": after_republication.publication_ref,
        "postRepublishObservedStatus": after_republication.status.value,
        "terminalObservedPublicationRef": terminal.publication_ref,
        "executionId": submitted.execution_id,
        "terminalStatus": result.status.value,
        "evidence": [
            "publish-original",
            "submit-original",
            "publish-replacement",
            "observe-original-pin-after-republication",
            "replacement-poison-not-executed",
            "terminal-result",
        ],
        "recordedAtUtc": terminal.updated_at_utc or None,
    }


async def _run_dependency_packaging(client: AiSdkClient, args: argparse.Namespace) -> dict[str, object]:
    function, package_kind = _packaged_function(args.worker)
    publication = await client.publish_pipeline(
        _publication_request(
            f"matrix-feature-dependency-package-{args.worker}",
            args.worker,
            args.environment_ref,
            function,
        )
    )
    submitted = await client.submit_execution(
        AiSdkExecutionSubmissionRequest(
            publication_ref=publication.publication_ref,
            idempotency_key=f"{args.scenario_id}-{uuid.uuid4().hex}",
            input={"feature": "deterministic-dependency-packaging"},
            metadata=_metadata(args),
        )
    )
    terminal = await _wait_for_terminal(client, submitted.execution_id, 90.0)
    result = await client.get_execution_result(submitted.execution_id)
    if result.status != AiSdkExecutionStatus.COMPLETED:
        raise RuntimeError(
            f"Execution '{submitted.execution_id}' ended as '{result.status.value}'. "
            f"The {package_kind.value} package was not successfully materialized and executed."
        )
    if terminal.publication_ref != publication.publication_ref:
        raise RuntimeError("Packaged execution no longer references its submitted publication.")

    return {
        "schemaVersion": 1,
        "scenarioId": args.scenario_id,
        "status": "passed",
        "coverageTarget": "deterministic-dependency-packaging",
        "coverageValues": [package_kind.value],
        "clientLanguage": "python",
        "workerLanguage": args.worker,
        "endpoint": args.endpoint,
        "topology": args.topology,
        "provider": args.provider,
        "publicationRef": publication.publication_ref,
        "executionId": submitted.execution_id,
        "terminalStatus": result.status.value,
        "packageKind": package_kind.value,
        "evidence": [
            "publish",
            "submit",
            "packaged-dependency",
            "observe",
            "terminal-result",
        ],
        "recordedAtUtc": terminal.updated_at_utc or None,
    }



async def _run_custom_policy_family(client: AiSdkClient, args: argparse.Namespace) -> dict[str, object]:
    family = args.policy_family
    if family not in {"concurrency", "retry", "delegation"}:
        raise ValueError("custom-policy-family requires concurrency, retry or delegation.")

    definition = _custom_policy_definition(family, args.worker)
    function = _custom_policy_function(family, args.worker, args.environment_ref)
    publication = await client.publish_pipeline(
        AiSdkPipelinePublicationRequest(definition=definition, functions=(function,))
    )
    submitted = await client.submit_execution(
        AiSdkExecutionSubmissionRequest(
            publication_ref=publication.publication_ref,
            idempotency_key=f"{args.scenario_id}-{uuid.uuid4().hex}",
            input={"feature": "custom-policy-family", "family": family},
            metadata=_metadata(args) | {"matrix.policyFamily": family},
        )
    )
    terminal = await _wait_for_terminal(client, submitted.execution_id, 90.0)
    result = await client.get_execution_result(submitted.execution_id)

    expected_status = (
        AiSdkExecutionStatus.COMPLETED
        if family == "concurrency"
        else AiSdkExecutionStatus.FAILED
    )
    if result.status != expected_status:
        raise RuntimeError(
            f"Custom {family} policy scenario ended as '{result.status.value}', "
            f"expected '{expected_status.value}'."
        )
    if terminal.publication_ref != publication.publication_ref:
        raise RuntimeError("Custom policy execution no longer references its submitted immutable publication.")

    decision = {"concurrency": "allow", "retry": "stop", "delegation": "deny"}[family]
    evidence = ["publish", "submit", "hosted-custom-policy", f"policy-{decision}-enforced", "observe", "terminal-result"]
    return {
        "schemaVersion": 1,
        "scenarioId": args.scenario_id,
        "status": "passed",
        "coverageTarget": "custom-policy-family",
        "coverageValues": [family],
        "clientLanguage": "python",
        "workerLanguage": args.worker,
        "endpoint": args.endpoint,
        "topology": args.topology,
        "provider": args.provider,
        "publicationRef": publication.publication_ref,
        "executionId": submitted.execution_id,
        "policyFamily": family,
        "policyDecision": decision,
        "expectedTerminalStatus": expected_status.value,
        "terminalStatus": result.status.value,
        "failureCode": result.failure.code if result.failure else None,
        "evidence": evidence,
        "recordedAtUtc": terminal.updated_at_utc or None,
    }


async def _run_nested_child_dag(client: AiSdkClient, args: argparse.Namespace) -> dict[str, object]:
    definition = _nested_child_definition(args.worker)
    function = _nested_child_function(args.worker, args.environment_ref)
    publication = await client.publish_pipeline(
        AiSdkPipelinePublicationRequest(definition=definition, functions=(function,))
    )
    submitted = await client.submit_execution(
        AiSdkExecutionSubmissionRequest(
            publication_ref=publication.publication_ref,
            idempotency_key=f"{args.scenario_id}-{uuid.uuid4().hex}",
            input={"feature": "nested-child-dag", "worker": args.worker},
            metadata=_metadata(args) | {
                "matrix.childDagDepth": "2",
                "matrix.definitionPath": "/invoke-child/invoke-grandchild",
            },
        )
    )
    terminal = await _wait_for_terminal(client, submitted.execution_id, 120.0)
    result = await client.get_execution_result(submitted.execution_id)

    if result.status != AiSdkExecutionStatus.COMPLETED:
        raise RuntimeError(
            f"Nested Child DAG scenario ended as '{result.status.value}', expected 'Completed'."
        )
    if terminal.publication_ref != publication.publication_ref:
        raise RuntimeError("Nested Child DAG execution no longer references its submitted immutable publication.")

    root_step = next((step for step in terminal.steps if step.name == "invoke-child"), None)
    if root_step is None or root_step.status != AiSdkExecutionStepStatus.COMPLETED:
        raise RuntimeError("Nested Child DAG root continuation did not converge to a completed parent step.")

    return {
        "schemaVersion": 1,
        "scenarioId": args.scenario_id,
        "status": "passed",
        "coverageTarget": "nested-child-dag",
        "coverageValues": [],
        "clientLanguage": "python",
        "workerLanguage": args.worker,
        "endpoint": args.endpoint,
        "topology": args.topology,
        "provider": args.provider,
        "publicationRef": publication.publication_ref,
        "executionId": submitted.execution_id,
        "terminalStatus": result.status.value,
        "nestedDepth": 2,
        "definitionPath": "/invoke-child/invoke-grandchild",
        "rootChildStep": "invoke-child",
        "nestedChildStep": "invoke-grandchild",
        "leafStep": "leaf",
        "rootStepStatus": root_step.status.value,
        "evidence": [
            "publish-nested-definition",
            "submit-root",
            "nested-child-dispatch",
            "nested-grandchild-custom-declaration",
            "parent-continuation",
            "observe",
            "terminal-result",
        ],
        "recordedAtUtc": terminal.updated_at_utc or None,
    }


def _nested_child_definition(worker: str) -> AiSdkPipelineDefinition:
    grandchild_name = f"matrix-feature-nested-grandchild-{worker}"
    child_name = f"matrix-feature-nested-child-{worker}"

    grandchild_definition = {
        "Name": grandchild_name,
        "Version": "1",
        "ExecutionLanguage": worker,
        "ExecutionMode": "Dag",
        "Steps": [
            {
                "Name": "leaf",
                "StepKey": "matrix-nested-leaf",
                "Order": 0,
                "ExecutionLanguage": worker,
                "Invocation": {"kind": "Custom"},
                "DependsOn": [],
                "Input": {"marker": f"nested-child-dag-{worker}"},
                "Config": {},
            }
        ],
        "Config": {},
    }
    child_definition = {
        "Name": child_name,
        "Version": "1",
        "ExecutionLanguage": "typescript",
        "ExecutionMode": "Dag",
        "Steps": [
            {
                "Name": "invoke-grandchild",
                "StepKey": "execution.child-dag",
                "Order": 0,
                "DependsOn": [],
                "Input": {},
                "Config": {
                    "childDagId": grandchild_name,
                    "childDagVersion": "1",
                    "logicalInvocationKey": "matrix-nested-grandchild",
                    "childDagDefinition": grandchild_definition,
                },
            }
        ],
        "Config": {},
    }
    return AiSdkPipelineDefinition(
        name=f"matrix-feature-nested-child-dag-{worker}",
        version="1",
        execution_language="python",
        execution_mode=AiSdkExecutionMode.DAG,
        steps=(
            AiSdkPipelineStepDefinition(
                name="invoke-child",
                step_key="execution.child-dag",
                order=0,
                config={
                    "childDagId": child_name,
                    "childDagVersion": "1",
                    "logicalInvocationKey": "matrix-nested-child",
                    "childDagDefinition": child_definition,
                },
            ),
        ),
    )


def _nested_child_function(
    worker: str,
    environment_ref: str,
) -> AiSdkPublicationFunctionUpload:
    site = AiSdkPublicationCallSite(
        kind=AiSdkPublicationFunctionKind.STEP,
        step_name="leaf",
        definition_path="/invoke-child/invoke-grandchild",
    )

    if worker == "dotnet":
        path = _fixture_root() / "dotnet-worker" / "Multiplexed.AI.Matrix.Worker.dll"
        return AiSdkPublicationFunctionUpload(
            site=site,
            environment_ref=environment_ref,
            entry_point_path="functions.dll",
            entry_point_symbol="Multiplexed.AI.Matrix.Worker.Functions::Run",
            sources=(_file("functions.dll", path.read_bytes()),),
        )

    if worker == "typescript":
        source = (
            "export function run(inputs: { marker?: string }, context: unknown) { "
            "return { success: true, payload: { workerLanguage: 'typescript', marker: inputs.marker ?? null } }; }\n"
        ).encode("utf-8")
        return AiSdkPublicationFunctionUpload(
            site=site,
            environment_ref=environment_ref,
            entry_point_path="main.ts",
            entry_point_symbol="run",
            sources=(_file("main.ts", source),),
        )

    if worker == "python":
        source = (
            "def run(inputs, context):\n"
            "    return {'success': True, 'payload': {'workerLanguage': 'python', 'marker': inputs.get('marker')}}\n"
        ).encode("utf-8")
        return AiSdkPublicationFunctionUpload(
            site=site,
            environment_ref=environment_ref,
            entry_point_path="main.py",
            entry_point_symbol="run",
            sources=(_file("main.py", source),),
        )

    raise ValueError(f"Unsupported nested Child DAG worker language '{worker}'.")


def _custom_policy_definition(family: str, worker: str) -> AiSdkPipelineDefinition:
    policy = {
        "name": f"matrix.{family}.hosted",
        "kind": family.capitalize(),
        "executionLanguage": worker,
        "invocation": {"kind": "Custom"},
        "config": {},
    }

    if family == "concurrency":
        return AiSdkPipelineDefinition(
            name="matrix-feature-custom-policy-concurrency",
            version="1",
            execution_language=worker,
            execution_mode=AiSdkExecutionMode.DAG,
            config={
                "concurrency": {
                    "Enabled": True,
                    "Policies": [policy],
                    "LeaseSeconds": 30,
                }
            },
            steps=(
                AiSdkPipelineStepDefinition(
                    name="work",
                    step_key="hello-world",
                    order=0,
                    input={"text": "custom-concurrency-policy"},
                ),
            ),
        )

    if family == "retry":
        return AiSdkPipelineDefinition(
            name="matrix-feature-custom-policy-retry",
            version="1",
            execution_language=worker,
            execution_mode=AiSdkExecutionMode.DAG,
            steps=(
                AiSdkPipelineStepDefinition(
                    name="work",
                    step_key="fail-once-then-succeed",
                    order=0,
                    config={
                        "retry": {
                            "Policies": [policy],
                            "MaxRetries": 3,
                        }
                    },
                ),
            ),
        )

    child_name = "matrix-feature-delegation-child"
    child_definition = {
        "Name": child_name,
        "Version": "1",
        "ExecutionMode": "Dag",
        "Steps": [
            {
                "Name": "child-work",
                "StepKey": "hello-world",
                "Order": 0,
                "DependsOn": [],
                "Input": {"text": "delegation-child-must-not-run"},
                "Config": {},
            }
        ],
        "Config": {},
    }
    return AiSdkPipelineDefinition(
        name="matrix-feature-custom-policy-delegation",
        version="1",
        execution_language=worker,
        execution_mode=AiSdkExecutionMode.DAG,
        steps=(
            AiSdkPipelineStepDefinition(
                name="invoke-child",
                step_key="execution.child-dag",
                order=0,
                config={
                    "childDagId": child_name,
                    "childDagVersion": "1",
                    "logicalInvocationKey": "matrix-policy-delegation",
                    "childDagDefinition": child_definition,
                    "delegation": {"Policies": [policy]},
                },
            ),
        ),
    )


def _custom_policy_function(
    family: str,
    worker: str,
    environment_ref: str,
) -> AiSdkPublicationFunctionUpload:
    kind = {
        "concurrency": AiSdkPublicationFunctionKind.CONCURRENCY_POLICY,
        "retry": AiSdkPublicationFunctionKind.RETRY_POLICY,
        "delegation": AiSdkPublicationFunctionKind.DELEGATION_POLICY,
    }[family]
    step_name = {
        "concurrency": None,
        "retry": "work",
        "delegation": "invoke-child",
    }[family]
    site = AiSdkPublicationCallSite(
        kind=kind,
        step_name=step_name,
        policy_index=0,
    )

    if worker == "python":
        decision = "allow" if family == "concurrency" else ("stop" if family == "retry" else "deny")
        source = (
            "def run(inputs, context):\n"
            f"    return {{'success': True, 'payload': {{'schemaVersion': 1, 'requestId': inputs['requestId'], "
            f"'policyKind': '{family}', 'decision': '{decision}', 'reason': 'matrix-{family}-{decision}'}}}}\n"
        ).encode("utf-8")
        return AiSdkPublicationFunctionUpload(
            site=site,
            environment_ref=environment_ref,
            entry_point_path="policy.py",
            entry_point_symbol="run",
            sources=(_file("policy.py", source),),
        )

    if worker == "typescript":
        decision = "allow" if family == "concurrency" else ("stop" if family == "retry" else "deny")
        source = (
            "export function run(inputs: { requestId: string }, context: unknown) { "
            f"return {{ success: true, payload: {{ schemaVersion: 1, requestId: inputs.requestId, policyKind: '{family}', "
            f"decision: '{decision}', reason: 'matrix-{family}-{decision}' }} }}; }}\n"
        ).encode("utf-8")
        return AiSdkPublicationFunctionUpload(
            site=site,
            environment_ref=environment_ref,
            entry_point_path="policy.ts",
            entry_point_symbol="run",
            sources=(_file("policy.ts", source),),
        )

    if worker == "dotnet" and family == "delegation":
        path = _fixture_root() / "dotnet-worker" / "Multiplexed.AI.Matrix.Worker.dll"
        return AiSdkPublicationFunctionUpload(
            site=site,
            environment_ref=environment_ref,
            entry_point_path="functions.dll",
            entry_point_symbol="Multiplexed.AI.Matrix.Worker.Functions::DelegationDeny",
            sources=(_file("functions.dll", path.read_bytes()),),
        )

    raise ValueError(f"Unsupported custom policy worker/family combination '{worker}/{family}'.")

def _publication_request(
    pipeline_name: str,
    worker: str,
    environment_ref: str,
    function: AiSdkPublicationFunctionUpload,
) -> AiSdkPipelinePublicationRequest:
    return AiSdkPipelinePublicationRequest(
        definition=AiSdkPipelineDefinition(
            name=pipeline_name,
            version="1",
            execution_language=worker,
            execution_mode=AiSdkExecutionMode.DAG,
            steps=(
                AiSdkPipelineStepDefinition(
                    name="work",
                    step_key="custom",
                    order=0,
                    execution_language=worker,
                    invocation=AiSdkInvocationDefinition(kind=AiSdkInvocationKind.CUSTOM),
                    input={"feature": pipeline_name},
                ),
            ),
        ),
        functions=(
            AiSdkPublicationFunctionUpload(
                site=function.site,
                environment_ref=environment_ref,
                entry_point_path=function.entry_point_path,
                entry_point_symbol=function.entry_point_symbol,
                sources=function.sources,
                dependencies=function.dependencies,
            ),
        ),
    )


def _pinning_function(worker: str, replacement: bool) -> AiSdkPublicationFunctionUpload:
    site = AiSdkPublicationCallSite(
        kind=AiSdkPublicationFunctionKind.STEP,
        step_name="work",
    )

    if worker == "dotnet":
        path = _fixture_root() / "dotnet-worker" / "Multiplexed.AI.Matrix.Worker.dll"
        symbol = (
            "Multiplexed.AI.Matrix.Worker.Functions::PinPoison"
            if replacement
            else "Multiplexed.AI.Matrix.Worker.Functions::PinStable"
        )
        return AiSdkPublicationFunctionUpload(
            site=site,
            environment_ref="placeholder",
            entry_point_path="functions.dll",
            entry_point_symbol=symbol,
            sources=(_file("functions.dll", path.read_bytes()),),
        )

    if worker == "typescript":
        source = (
            "export function run(inputs: unknown, context: unknown) { "
            "throw new Error('replacement publication must not execute'); }\n"
            if replacement
            else
            "export async function run(inputs: unknown, context: unknown) { "
            "await new Promise(resolve => setTimeout(resolve, 8000)); "
            "return { success: true, payload: { workerLanguage: 'typescript', revision: 1 } }; }\n"
        )
        return AiSdkPublicationFunctionUpload(
            site=site,
            environment_ref="placeholder",
            entry_point_path="main.ts",
            entry_point_symbol="run",
            sources=(_file("main.ts", source.encode("utf-8")),),
        )

    if worker == "python":
        source = (
            "def run(inputs, context):\n"
            "    raise RuntimeError('replacement publication must not execute')\n"
            if replacement
            else
            "import time\n"
            "def run(inputs, context):\n"
            "    time.sleep(8)\n"
            "    return {'success': True, 'payload': {'workerLanguage': 'python', 'revision': 1}}\n"
        )
        return AiSdkPublicationFunctionUpload(
            site=site,
            environment_ref="placeholder",
            entry_point_path="main.py",
            entry_point_symbol="run",
            sources=(_file("main.py", source.encode("utf-8")),),
        )

    raise ValueError(f"Unsupported worker language '{worker}'.")


def _packaged_function(
    worker: str,
) -> tuple[AiSdkPublicationFunctionUpload, AiSdkPublicationDependencyPackageKind]:
    site = AiSdkPublicationCallSite(
        kind=AiSdkPublicationFunctionKind.STEP,
        step_name="work",
    )

    if worker == "dotnet":
        fixture = _fixture_root() / "dotnet-packaged-worker"
        function_bytes = (fixture / "Multiplexed.AI.Matrix.PackagedWorker.dll").read_bytes()
        dependency_bytes = (fixture / "Multiplexed.AI.Matrix.Dependency.dll").read_bytes()
        dependency_path = "Multiplexed.AI.Matrix.Dependency.dll"
        dependency_name = "matrixdependency"
        dependency_version = "1.0.0"
        manifest = _json_bytes(
            {
                "schemaVersion": 1,
                "packageName": dependency_name,
                "version": dependency_version,
                "assemblies": [
                    {
                        "path": dependency_path,
                        "sha256": hashlib.sha256(dependency_bytes).hexdigest(),
                        "assemblyName": "Multiplexed.AI.Matrix.Dependency",
                        "assemblyVersion": "1.0.0.0",
                    }
                ],
            }
        )
        dependency = AiSdkPublicationDependencyUpload(
            name=dependency_name,
            version=dependency_version,
            files=(
                _file("bundle.manifest.json", manifest),
                _file(dependency_path, dependency_bytes),
            ),
            package=AiSdkPublicationDependencyPackage(
                schema_version=1,
                kind=AiSdkPublicationDependencyPackageKind.DOTNET_ASSEMBLY_CLOSURE,
                manifest_path="bundle.manifest.json",
            ),
        )
        return (
            AiSdkPublicationFunctionUpload(
                site=site,
                environment_ref="placeholder",
                entry_point_path="functions.dll",
                entry_point_symbol="Multiplexed.AI.Matrix.PackagedWorker.Functions::Run",
                sources=(_file("functions.dll", function_bytes),),
                dependencies=(dependency,),
            ),
            AiSdkPublicationDependencyPackageKind.DOTNET_ASSEMBLY_CLOSURE,
        )

    if worker == "typescript":
        index = b"export { scale } from './math.ts';\n"
        math = b"export function scale(value: number): number { return value * 4; }\n"
        manifest = _json_bytes(
            {
                "schemaVersion": 1,
                "packageName": "rules",
                "version": "2.0.1",
                "entryPoint": "index.ts",
                "files": [
                    {"path": "index.ts", "sha256": hashlib.sha256(index).hexdigest()},
                    {"path": "math.ts", "sha256": hashlib.sha256(math).hexdigest()},
                ],
            }
        )
        dependency = AiSdkPublicationDependencyUpload(
            name="rules",
            version="2.0.1",
            files=(
                _file("bundle.manifest.json", manifest),
                _file("index.ts", index),
                _file("math.ts", math),
            ),
            package=AiSdkPublicationDependencyPackage(
                schema_version=1,
                kind=AiSdkPublicationDependencyPackageKind.NODE_LOCKED_BUNDLE,
                manifest_path="bundle.manifest.json",
            ),
        )
        source = (
            "import { scale } from '#rules'; "
            "export function run(inputs: unknown, context: unknown) { "
            "return { success: true, payload: { workerLanguage: 'typescript', dependencyValue: scale(21) } }; }\n"
        ).encode("utf-8")
        return (
            AiSdkPublicationFunctionUpload(
                site=site,
                environment_ref="placeholder",
                entry_point_path="main.ts",
                entry_point_symbol="run",
                sources=(_file("main.ts", source),),
                dependencies=(dependency,),
            ),
            AiSdkPublicationDependencyPackageKind.NODE_LOCKED_BUNDLE,
        )

    if worker == "python":
        wheel = _python_wheel_bytes()
        wheel_path = "rules-2.0.1-py3-none-any.whl"
        manifest = _json_bytes(
            {
                "schemaVersion": 1,
                "wheelPath": wheel_path,
                "wheelSha256": hashlib.sha256(wheel).hexdigest(),
                "distribution": "rules",
                "version": "2.0.1",
                "importRoots": ["wheel_rules"],
            }
        )
        dependency = AiSdkPublicationDependencyUpload(
            name="rules",
            version="2.0.1",
            files=(
                _file("bundle.manifest.json", manifest),
                _file(wheel_path, wheel),
            ),
            package=AiSdkPublicationDependencyPackage(
                schema_version=1,
                kind=AiSdkPublicationDependencyPackageKind.PYTHON_WHEEL_BUNDLE,
                manifest_path="bundle.manifest.json",
            ),
        )
        source = (
            "from wheel_rules import transform\n"
            "def run(inputs, context):\n"
            "    return {'success': True, 'payload': {'workerLanguage': 'python', 'dependencyValue': transform(21)}}\n"
        ).encode("utf-8")
        return (
            AiSdkPublicationFunctionUpload(
                site=site,
                environment_ref="placeholder",
                entry_point_path="main.py",
                entry_point_symbol="run",
                sources=(_file("main.py", source),),
                dependencies=(dependency,),
            ),
            AiSdkPublicationDependencyPackageKind.PYTHON_WHEEL_BUNDLE,
        )

    raise ValueError(f"Unsupported worker language '{worker}'.")


def _python_wheel_bytes() -> bytes:
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("wheel_rules/__init__.py", "def transform(value):\n    return value * 4\n")
        info = "rules-2.0.1.dist-info/"
        archive.writestr(
            info + "WHEEL",
            "Wheel-Version: 1.0\nGenerator: matrix-fixture\nRoot-Is-Purelib: true\nTag: py3-none-any\n",
        )
        archive.writestr(
            info + "METADATA",
            "Metadata-Version: 2.1\nName: rules\nVersion: 2.0.1\n",
        )
        archive.writestr(info + "RECORD", "")
    return buffer.getvalue()


def _file(path: str, content: bytes) -> AiSdkPublicationFileUpload:
    return AiSdkPublicationFileUpload(
        path=path,
        content_base64=base64.b64encode(content).decode("ascii"),
    )


def _json_bytes(value: object) -> bytes:
    return json.dumps(value, separators=(",", ":"), ensure_ascii=True).encode("utf-8")


def _fixture_root() -> Path:
    configured = os.environ.get("MATRIX_FIXTURE_ROOT")
    if configured:
        return Path(configured).resolve()
    return REPO_ROOT / "implementations" / "matrix" / "fixtures"


async def _wait_for_terminal(client: AiSdkClient, execution_id: str, timeout_seconds: float):
    loop = asyncio.get_running_loop()
    deadline = loop.time() + timeout_seconds
    while loop.time() < deadline:
        observation = await client.observe_execution(execution_id)
        if observation.status in TERMINAL:
            return observation
        await asyncio.sleep(0.25)
    raise TimeoutError(f"Execution '{execution_id}' did not become terminal within {timeout_seconds} seconds.")


def _metadata(args: argparse.Namespace) -> dict[str, str]:
    return {
        "matrix.scenario": args.scenario_id,
        "matrix.client": "python",
        "matrix.worker": args.worker,
        "matrix.feature": args.feature,
    }


def _write_evidence(path: Path, document: dict[str, object]) -> None:
    path = path.resolve()
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--feature",
        required=True,
        choices=("publication-pinning", "deterministic-dependency-packaging", "custom-policy-family", "nested-child-dag"),
    )
    parser.add_argument("--worker", required=True, choices=("dotnet", "typescript", "python"))
    parser.add_argument("--policy-family", choices=("concurrency", "retry", "delegation"))
    parser.add_argument("--scenario-id", required=True)
    parser.add_argument("--evidence", required=True)
    parser.add_argument("--endpoint")
    parser.add_argument("--environment-ref")
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
    if args.feature == "custom-policy-family" and not args.policy_family:
        parser.error("--policy-family is required for custom-policy-family")
    if args.feature != "custom-policy-family" and args.policy_family:
        parser.error("--policy-family is valid only for custom-policy-family")
    return args


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
