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
import urllib.error
import urllib.parse
import urllib.request
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
    AiSdkPipelineStepExecutionDefinition,
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
    elif args.feature == "mcp-effect-evidence":
        document = await _run_mcp_effect_evidence(client, args)
    elif args.feature == "recovery":
        document = await _run_recovery(client, args)
    elif args.feature == "journal-result-acceptance":
        document = await _run_journal_result_acceptance(args)
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


async def _run_mcp_effect_evidence(client: AiSdkClient, args: argparse.Namespace) -> dict[str, object]:
    effect_case = args.effect_case
    if effect_case not in {"completed-local-replay", "uncertain-blocks-blind-resend"}:
        raise ValueError("mcp-effect-evidence requires a supported effect case.")
    if not args.effect_probe_state_endpoint or not args.effect_evidence_endpoint:
        raise RuntimeError("MCP effect evidence scenarios require matrix diagnostics endpoints from the runtime manifest.")

    tool = "probe.fail-count" if effect_case == "completed-local-replay" else "probe.slow-count"
    step_input: dict[str, object] = {"scenario": args.scenario_id}
    if effect_case == "uncertain-blocks-blind-resend":
        step_input["milliseconds"] = 10000

    definition = AiSdkPipelineDefinition(
        name=f"matrix-feature-mcp-effect-{effect_case}",
        version="v1",
        execution_mode=AiSdkExecutionMode.DAG,
        steps=(
            AiSdkPipelineStepDefinition(
                name="effect",
                step_key="mcp.tool",
                order=1,
                invocation=AiSdkInvocationDefinition(
                    kind=AiSdkInvocationKind.MCP,
                    connection_ref="matrix-effect-probe",
                    tool=tool,
                ),
                input=step_input,
                execution=AiSdkPipelineStepExecutionDefinition(
                    max_retries=1,
                    retry_delay_ms=50,
                ),
            ),
        ),
    )
    publication = await client.publish_pipeline(AiSdkPipelinePublicationRequest(definition=definition))
    submitted = await client.submit_execution(
        AiSdkExecutionSubmissionRequest(
            publication_ref=publication.publication_ref,
            idempotency_key=f"{args.scenario_id}-{uuid.uuid4().hex}",
            input={"feature": "mcp-effect-evidence", "case": effect_case},
            metadata=_metadata(args) | {"matrix.effectCase": effect_case},
        )
    )
    terminal = await _wait_for_terminal(client, submitted.execution_id, 45.0)
    result = await client.get_execution_result(submitted.execution_id)
    if result.status != AiSdkExecutionStatus.FAILED:
        raise RuntimeError(
            f"MCP effect evidence scenario '{effect_case}' ended as '{result.status.value}', expected 'Failed'."
        )

    step = next((item for item in terminal.steps if item.name == "effect"), None)
    if step is None or step.status != AiSdkExecutionStepStatus.FAILED:
        raise RuntimeError("MCP effect evidence scenario did not retain the failed effect step observation.")

    evidence_url = (
        f"{args.effect_evidence_endpoint.rstrip('/')}/"
        f"{urllib.parse.quote(submitted.execution_id, safe='')}/effect"
    )
    probe_url = (
        f"{args.effect_probe_state_endpoint.rstrip('/')}/"
        f"{urllib.parse.quote(args.scenario_id, safe='')}"
    )
    durable = await _read_json(evidence_url)
    probe = await _read_json(probe_url)

    expected_status = "Completed" if effect_case == "completed-local-replay" else "Uncertain"
    if durable.get("status") != expected_status:
        raise RuntimeError(
            f"Durable MCP effect evidence ended as '{durable.get('status')}', expected '{expected_status}'."
        )
    if durable.get("retryCount") != 1:
        raise RuntimeError(
            f"MCP effect step retry count was '{durable.get('retryCount')}', expected exactly one logical retry."
        )
    if probe.get("physicalCallCount") != 1:
        raise RuntimeError(
            f"MCP effect probe observed '{probe.get('physicalCallCount')}' physical calls; blind re-emission was not fenced."
        )

    if effect_case == "completed-local-replay":
        if durable.get("resultIsError") is not True or durable.get("uncertaintyReasonCode") is not None:
            raise RuntimeError("Completed MCP effect evidence did not preserve the confirmed remote tool-error result.")
        evidence = [
            "publish",
            "submit",
            "durable-effect-completed",
            "logical-retry-observed",
            "completed-result-replayed-locally",
            "single-physical-tools-call",
            "terminal-result",
        ]
    else:
        if durable.get("resultIsError") is not None or durable.get("uncertaintyReasonCode") != "transport-timeout":
            raise RuntimeError("Uncertain MCP effect evidence did not preserve the transport-timeout uncertainty proof.")
        evidence = [
            "publish",
            "submit",
            "durable-effect-uncertain",
            "logical-retry-observed",
            "blind-resend-blocked",
            "single-physical-tools-call",
            "terminal-result",
        ]

    return {
        "schemaVersion": 1,
        "scenarioId": args.scenario_id,
        "status": "passed",
        "coverageTarget": "mcp-effect-evidence",
        "coverageValues": [effect_case],
        "clientLanguage": "python",
        "workerLanguage": None,
        "endpoint": args.endpoint,
        "topology": args.topology,
        "provider": args.provider,
        "publicationRef": publication.publication_ref,
        "executionId": submitted.execution_id,
        "terminalStatus": result.status.value,
        "stepStatus": step.status.value,
        "durableEvidenceStatus": durable.get("status"),
        "durableEvidenceRevision": durable.get("revision"),
        "effectId": durable.get("effectId"),
        "retryCount": durable.get("retryCount"),
        "physicalCallCount": probe.get("physicalCallCount"),
        "durableResultIsError": durable.get("resultIsError"),
        "uncertaintyReasonCode": durable.get("uncertaintyReasonCode"),
        "evidence": evidence,
        "recordedAtUtc": terminal.updated_at_utc or None,
    }


async def _run_recovery(client: AiSdkClient, args: argparse.Namespace) -> dict[str, object]:
    recovery_case = args.recovery_case
    if not args.recovery_endpoint:
        raise RuntimeError("The runtime manifest did not expose the matrix recovery endpoint.")

    pipeline_name = f"matrix-feature-recovery-{recovery_case}-{uuid.uuid4().hex}"
    delay_ms = 15000 if recovery_case == "in-flight-resume" else 3000
    definition = AiSdkPipelineDefinition(
        name=pipeline_name,
        version="1",
        execution_mode=AiSdkExecutionMode.DAG,
        steps=(
            AiSdkPipelineStepDefinition(
                name="work",
                step_key="delay-step",
                order=0,
                config={"delayMs": delay_ms},
            ),
        ),
    )
    publication = await client.publish_pipeline(
        AiSdkPipelinePublicationRequest(definition=definition)
    )

    if recovery_case == "local-queued-redispatch":
        seed_identity = f"matrix-local-queued-{uuid.uuid4().hex}"
        recovery = await _post_json(
            f"{args.recovery_endpoint.rstrip('/')}/{urllib.parse.quote(recovery_case)}/{urllib.parse.quote(seed_identity)}",
            {
                "definition": definition.to_wire(),
                "input": {"feature": "recovery", "case": recovery_case},
                "metadata": _metadata(args),
            },
        )
        redispatched_execution_id = recovery.get("redispatchedExecutionId")
        if not isinstance(redispatched_execution_id, str) or not redispatched_execution_id:
            raise RuntimeError(f"Local-queued recovery did not produce a replacement execution: {recovery!r}")
        if recovery.get("preRecoveryExecutionId") is not None:
            raise RuntimeError("Local-queued recovery seed unexpectedly carried a durable execution identity before redispatch.")
        if recovery.get("indexStatus") != "requeued-for-recovery" or recovery.get("recoveryChanged") is not True:
            raise RuntimeError(f"Local-queued recovery did not apply the expected durable transition: {recovery!r}")
        if recovery.get("terminalStatus") != "Completed":
            raise RuntimeError(
                f"Redispatched local-queued execution ended as '{recovery.get('terminalStatus')}', expected 'Completed'."
            )
        if recovery.get("replacementRuntimeIndexStatus") != "completed":
            raise RuntimeError(
                "Redispatched local-queued runtime index did not converge to completed: "
                f"{recovery.get('replacementRuntimeIndexStatus')!r}."
            )

        return {
            "schemaVersion": 1,
            "scenarioId": args.scenario_id,
            "status": "passed",
            "coverageTarget": "recovery",
            "coverageValues": [recovery_case],
            "clientLanguage": "python",
            "workerLanguage": None,
            "endpoint": args.endpoint,
            "topology": args.topology,
            "provider": args.provider,
            "publicationRef": publication.publication_ref,
            "seedIdentity": seed_identity,
            "preRecoveryExecutionId": recovery.get("preRecoveryExecutionId"),
            "redispatchedExecutionId": redispatched_execution_id,
            "sharedRunId": recovery.get("sharedRunId"),
            "failedRuntimeInstanceId": recovery.get("failedRuntimeInstanceId"),
            "failedLocalRunId": recovery.get("failedLocalRunId"),
            "replacementRuntimeInstanceId": recovery.get("replacementRuntimeInstanceId"),
            "replacementLocalRunId": recovery.get("replacementLocalRunId"),
            "runtimeIndexStatus": recovery.get("indexStatus"),
            "replacementRuntimeIndexStatus": recovery.get("replacementRuntimeIndexStatus"),
            "recoveryAction": recovery.get("recoveryAction"),
            "recoveryReason": recovery.get("recoveryReason"),
            "recoveryObservedBy": recovery.get("observedBy"),
            "terminalStatus": recovery.get("terminalStatus"),
            "evidence": [
                "public-sdk-definition-seed",
                "local-queued-no-execution-id",
                "local-queued-ownership-seeded",
                "production-recovery-reconciler",
                "shared-run-requeued-for-recovery",
                "healthy-runtime-redispatch",
                "new-execution-id-created",
                "terminal-result",
            ],
            "recordedAtUtc": None,
        }

    submitted = await client.submit_execution(
        AiSdkExecutionSubmissionRequest(
            publication_ref=publication.publication_ref,
            idempotency_key=f"{args.scenario_id}-{uuid.uuid4().hex}",
            input={"feature": "recovery", "case": recovery_case},
            metadata=_metadata(args),
        )
    )
    active = await _wait_for_active_step(client, submitted.execution_id, "work", 30.0)
    recovery = await _post_json(
        f"{args.recovery_endpoint.rstrip('/')}/{urllib.parse.quote(recovery_case)}/{urllib.parse.quote(submitted.execution_id)}"
    )
    if recovery.get("executionId") != submitted.execution_id:
        raise RuntimeError("In-flight recovery did not preserve the original durable execution identity.")
    if recovery.get("indexStatus") != "requeued-for-recovery" or recovery.get("recoveryChanged") is not True:
        raise RuntimeError(f"In-flight recovery did not apply the expected durable transition: {recovery!r}")
    failed_runtime_instance_id = recovery.get("failedRuntimeInstanceId")
    replacement_runtime_instance_id = recovery.get("replacementRuntimeInstanceId")
    if not isinstance(replacement_runtime_instance_id, str) or not replacement_runtime_instance_id:
        raise RuntimeError(f"In-flight recovery did not expose replacement runtime ownership: {recovery!r}")
    if replacement_runtime_instance_id == failed_runtime_instance_id:
        raise RuntimeError("In-flight recovery reused the failed runtime instead of distinct replacement capacity.")

    observation = await _wait_for_terminal(client, submitted.execution_id, 120.0)
    result = await client.get_execution_result(submitted.execution_id)
    if observation.status != AiSdkExecutionStatus.COMPLETED or result.status != AiSdkExecutionStatus.COMPLETED:
        raise RuntimeError(
            f"Recovered in-flight execution ended as '{result.status.value}', expected 'Completed'."
        )
    active_step = next((candidate for candidate in active.steps if candidate.name == "work"), None)
    return {
        "schemaVersion": 1,
        "scenarioId": args.scenario_id,
        "status": "passed",
        "coverageTarget": "recovery",
        "coverageValues": [recovery_case],
        "clientLanguage": "python",
        "workerLanguage": None,
        "endpoint": args.endpoint,
        "topology": args.topology,
        "provider": args.provider,
        "publicationRef": publication.publication_ref,
        "executionId": submitted.execution_id,
        "originalExecutionId": submitted.execution_id,
        "recoveredExecutionId": submitted.execution_id,
        "activeStatusBeforeRecovery": active.status.value,
        "activeStepStatusBeforeRecovery": active_step.status.value if active_step else None,
        "sharedRunId": recovery.get("sharedRunId"),
        "failedRuntimeInstanceId": recovery.get("failedRuntimeInstanceId"),
        "failedLocalRunId": recovery.get("failedLocalRunId"),
        "replacementRuntimeInstanceId": replacement_runtime_instance_id,
        "replacementLocalRunId": recovery.get("replacementLocalRunId"),
        "runtimeIndexStatus": recovery.get("indexStatus"),
        "recoveryAction": recovery.get("recoveryAction"),
        "recoveryReason": recovery.get("recoveryReason"),
        "recoveryObservedBy": recovery.get("observedBy"),
        "terminalStatus": result.status.value,
        "evidence": [
            "publish",
            "submit",
            "active-execution-observed",
            "runtime-ownership-marked-unavailable",
            "production-recovery-reconciler",
            "shared-run-requeued-for-recovery",
            "same-execution-id-resumed",
            "terminal-result",
        ],
        "recordedAtUtc": observation.updated_at_utc or None,
    }


async def _run_journal_result_acceptance(args: argparse.Namespace) -> dict[str, object]:
    acceptance_case = args.journal_case
    if not args.journal_result_acceptance_endpoint:
        raise RuntimeError("The runtime manifest did not expose the matrix journal result-acceptance endpoint.")

    diagnostic = await _post_json(
        f"{args.journal_result_acceptance_endpoint.rstrip('/')}/{urllib.parse.quote(acceptance_case)}"
    )
    if diagnostic.get("acceptanceCase") != acceptance_case:
        raise RuntimeError("Journal result-acceptance diagnostics returned the wrong case identity.")
    if diagnostic.get("terminalStatus") != "Succeeded" or diagnostic.get("continuationStatus") != "Pending":
        raise RuntimeError(f"Journal result acceptance did not persist the expected terminal state: {diagnostic!r}")
    if diagnostic.get("leaseEpoch") != 1:
        raise RuntimeError("Journal result acceptance did not preserve the first lease epoch fence.")
    result_hash = diagnostic.get("resultSha256")
    if not isinstance(result_hash, str) or len(result_hash) != 64:
        raise RuntimeError("Journal result acceptance did not expose a durable result hash.")

    if acceptance_case == "accepted-result-replay":
        if diagnostic.get("firstCompletionStatus") != "Accepted" or diagnostic.get("replayCompletionStatus") != "AlreadyAccepted":
            raise RuntimeError(f"Accepted result replay was not idempotent: {diagnostic!r}")
        evidence = [
            "mongo-backed-journal-prepare",
            "lease-epoch-acquired",
            "result-accepted",
            "fresh-journal-reload",
            "identical-result-replay-already-accepted",
            "pending-continuation-preserved",
        ]
    else:
        if diagnostic.get("deliveryCount") != 8:
            raise RuntimeError("Duplicate-delivery convergence did not execute all eight deliveries.")
        if diagnostic.get("acceptedCount") != 1 or diagnostic.get("alreadyAcceptedCount") != 7:
            raise RuntimeError(f"Duplicate deliveries did not converge to one accepted result: {diagnostic!r}")
        if diagnostic.get("leaseRejectedCount") != 0:
            raise RuntimeError("Identical duplicate deliveries unexpectedly crossed the lease fence.")
        evidence = [
            "mongo-backed-journal-prepare",
            "lease-epoch-acquired",
            "concurrent-duplicate-deliveries",
            "single-result-accepted",
            "duplicates-converged-already-accepted",
            "pending-continuation-preserved",
        ]

    return {
        "schemaVersion": 1,
        "scenarioId": args.scenario_id,
        "status": "passed",
        "coverageTarget": "journal-result-acceptance",
        "coverageValues": [acceptance_case],
        "clientLanguage": "python",
        "workerLanguage": None,
        "endpoint": args.endpoint,
        "topology": args.topology,
        "provider": args.provider,
        "operationId": diagnostic.get("operationId"),
        "firstCompletionStatus": diagnostic.get("firstCompletionStatus"),
        "replayCompletionStatus": diagnostic.get("replayCompletionStatus"),
        "deliveryCount": diagnostic.get("deliveryCount"),
        "acceptedCount": diagnostic.get("acceptedCount"),
        "alreadyAcceptedCount": diagnostic.get("alreadyAcceptedCount"),
        "leaseRejectedCount": diagnostic.get("leaseRejectedCount"),
        "terminalStatus": diagnostic.get("terminalStatus"),
        "continuationStatus": diagnostic.get("continuationStatus"),
        "resultSha256": diagnostic.get("resultSha256"),
        "leaseEpoch": diagnostic.get("leaseEpoch"),
        "journalRevision": diagnostic.get("revision"),
        "evidence": evidence,
    }


async def _wait_for_active_step(
    client: AiSdkClient,
    execution_id: str,
    step_name: str,
    timeout_seconds: float,
):
    loop = asyncio.get_running_loop()
    deadline = loop.time() + timeout_seconds
    while loop.time() < deadline:
        observation = await client.observe_execution(execution_id)
        step = next((candidate for candidate in observation.steps if candidate.name == step_name), None)
        if observation.status not in TERMINAL and step is not None and step.status in {
            AiSdkExecutionStepStatus.RUNNING,
            AiSdkExecutionStepStatus.WAITING_FOR_EXTERNAL,
        }:
            return observation
        await asyncio.sleep(0.1)
    raise TimeoutError(
        f"Execution '{execution_id}' did not expose active step '{step_name}' within {timeout_seconds} seconds."
    )


async def _post_json(url: str, payload: dict[str, object] | None = None) -> dict[str, object]:
    def post() -> dict[str, object]:
        request = urllib.request.Request(
            url,
            data=json.dumps(payload or {}).encode("utf-8"),
            method="POST",
            headers={"Content-Type": "application/json"},
        )
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                document = json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as error:
            body = error.read().decode("utf-8", errors="replace")
            raise RuntimeError(
                f"Matrix diagnostics endpoint '{url}' returned HTTP {error.code}: {body}"
            ) from error
        if not isinstance(document, dict):
            raise RuntimeError(f"Matrix diagnostics endpoint '{url}' did not return a JSON object.")
        return document

    return await asyncio.to_thread(post)


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
        path = _sample_root() / "dotnet" / "Multiplexed.AI.Samples.PublishedFunctions.dll"
        return AiSdkPublicationFunctionUpload(
            site=site,
            environment_ref=environment_ref,
            entry_point_path="functions.dll",
            entry_point_symbol="Multiplexed.AI.Samples.PublishedFunctions.Functions::Run",
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
        path = _sample_root() / "dotnet" / "Multiplexed.AI.Samples.PublishedFunctions.dll"
        return AiSdkPublicationFunctionUpload(
            site=site,
            environment_ref=environment_ref,
            entry_point_path="functions.dll",
            entry_point_symbol="Multiplexed.AI.Samples.PublishedFunctions.Functions::DelegationDeny",
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
        path = _sample_root() / "dotnet" / "Multiplexed.AI.Samples.PublishedFunctions.dll"
        symbol = (
            "Multiplexed.AI.Samples.PublishedFunctions.Functions::PinPoison"
            if replacement
            else "Multiplexed.AI.Samples.PublishedFunctions.Functions::PinStable"
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
        sample = _sample_root() / "dotnet"
        function_bytes = (sample / "Multiplexed.AI.Samples.PublishedPackagedFunctions.dll").read_bytes()
        dependency_bytes = (sample / "Multiplexed.AI.Samples.PublishedDependency.dll").read_bytes()
        dependency_path = "Multiplexed.AI.Samples.PublishedDependency.dll"
        dependency_name = "sampledependency"
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
                        "assemblyName": "Multiplexed.AI.Samples.PublishedDependency",
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
                entry_point_symbol="Multiplexed.AI.Samples.PublishedPackagedFunctions.Functions::Run",
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
            "Wheel-Version: 1.0\nGenerator: multiplexed-ai-sdk-sample\nRoot-Is-Purelib: true\nTag: py3-none-any\n",
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


def _sample_root() -> Path:
    configured = os.environ.get("MATRIX_SAMPLE_ROOT")
    if configured:
        return Path(configured).resolve()
    return REPO_ROOT / "implementations" / "sdk" / "samples" / "published-functions"


async def _wait_for_terminal(client: AiSdkClient, execution_id: str, timeout_seconds: float):
    loop = asyncio.get_running_loop()
    deadline = loop.time() + timeout_seconds
    while loop.time() < deadline:
        observation = await client.observe_execution(execution_id)
        if observation.status in TERMINAL:
            return observation
        await asyncio.sleep(0.25)
    raise TimeoutError(f"Execution '{execution_id}' did not become terminal within {timeout_seconds} seconds.")


async def _read_json(url: str) -> dict[str, object]:
    def read() -> dict[str, object]:
        with urllib.request.urlopen(url, timeout=5) as response:
            document = json.loads(response.read().decode("utf-8"))
        if not isinstance(document, dict):
            raise RuntimeError(f"Matrix diagnostics endpoint '{url}' did not return a JSON object.")
        return document

    return await asyncio.to_thread(read)


def _metadata(args: argparse.Namespace) -> dict[str, str]:
    return {
        "matrix.scenario": args.scenario_id,
        "matrix.client": "python",
        "matrix.worker": args.worker or "none",
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
        choices=("publication-pinning", "deterministic-dependency-packaging", "custom-policy-family", "nested-child-dag", "mcp-effect-evidence", "recovery", "journal-result-acceptance"),
    )
    parser.add_argument("--worker", choices=("dotnet", "typescript", "python"))
    parser.add_argument("--policy-family", choices=("concurrency", "retry", "delegation"))
    parser.add_argument("--effect-case", choices=("completed-local-replay", "uncertain-blocks-blind-resend"))
    parser.add_argument("--recovery-case", choices=("in-flight-resume", "local-queued-redispatch"))
    parser.add_argument("--journal-case", choices=("accepted-result-replay", "duplicate-delivery-convergence"))
    parser.add_argument("--scenario-id", required=True)
    parser.add_argument("--evidence", required=True)
    parser.add_argument("--endpoint")
    parser.add_argument("--environment-ref")
    parser.add_argument("--token")
    parser.add_argument("--access-context")
    parser.add_argument("--access-context-header", default="X-Access-Context")
    parser.add_argument("--topology", default="local")
    parser.add_argument("--provider", default="ProcessHostPool")
    parser.add_argument("--effect-probe-state-endpoint")
    parser.add_argument("--effect-evidence-endpoint")
    parser.add_argument("--recovery-endpoint")
    parser.add_argument("--journal-result-acceptance-endpoint")
    parser.add_argument("--manifest")
    args = parser.parse_args()

    if args.manifest:
        manifest = json.loads(Path(args.manifest).resolve().read_text(encoding="utf-8-sig"))
        args.endpoint = args.endpoint or manifest["endpoint"]
        if args.worker:
            args.environment_ref = args.environment_ref or manifest["environmentRefs"][args.worker]
        args.token = args.token or manifest.get("bearerToken")
        args.access_context = args.access_context or manifest.get("accessContext")
        args.access_context_header = manifest.get("accessContextHeader", args.access_context_header)
        args.topology = manifest.get("topology", args.topology)
        args.provider = manifest.get("provider", args.provider)
        args.effect_probe_state_endpoint = args.effect_probe_state_endpoint or manifest.get("effectProbeStateEndpoint")
        args.effect_evidence_endpoint = args.effect_evidence_endpoint or manifest.get("effectEvidenceEndpoint")
        args.recovery_endpoint = args.recovery_endpoint or manifest.get("recoveryEndpoint")
        args.journal_result_acceptance_endpoint = args.journal_result_acceptance_endpoint or manifest.get("journalResultAcceptanceEndpoint")

    if not args.endpoint:
        parser.error("--endpoint is required unless --manifest supplies it")
    non_hosted_features = {"mcp-effect-evidence", "recovery", "journal-result-acceptance"}
    if args.feature not in non_hosted_features and (not args.worker or not args.environment_ref):
        parser.error("--worker and --environment-ref are required for hosted feature scenarios")
    if args.feature in non_hosted_features and args.worker:
        parser.error(f"--worker is not used by {args.feature}")
    if args.feature == "custom-policy-family" and not args.policy_family:
        parser.error("--policy-family is required for custom-policy-family")
    if args.feature != "custom-policy-family" and args.policy_family:
        parser.error("--policy-family is valid only for custom-policy-family")
    if args.feature == "mcp-effect-evidence" and not args.effect_case:
        parser.error("--effect-case is required for mcp-effect-evidence")
    if args.feature != "mcp-effect-evidence" and args.effect_case:
        parser.error("--effect-case is valid only for mcp-effect-evidence")
    if args.feature == "recovery" and not args.recovery_case:
        parser.error("--recovery-case is required for recovery")
    if args.feature != "recovery" and args.recovery_case:
        parser.error("--recovery-case is valid only for recovery")
    if args.feature == "journal-result-acceptance" and not args.journal_case:
        parser.error("--journal-case is required for journal-result-acceptance")
    if args.feature != "journal-result-acceptance" and args.journal_case:
        parser.error("--journal-case is valid only for journal-result-acceptance")
    return args


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
