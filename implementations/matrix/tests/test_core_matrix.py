from __future__ import annotations

import json
import sys
import unittest
from pathlib import Path
from unittest import mock

MATRIX_ROOT = Path(__file__).resolve().parents[1]
if str(MATRIX_ROOT) not in sys.path:
    sys.path.insert(0, str(MATRIX_ROOT))

import core_matrix
from matrix_plan import load_plan


class CoreMatrixTests(unittest.TestCase):
    def test_all_nine_core_scenarios_are_bound(self) -> None:
        plan = load_plan()
        self.assertEqual(9, len(plan["coreScenarios"]))
        self.assertTrue(all(item["executor"]["kind"] in {"dotnet", "typescript", "python"} for item in plan["coreScenarios"]))

    def test_dotnet_sdk_project_reference_resolves(self) -> None:
        project = MATRIX_ROOT / "clients" / "dotnet" / "Multiplexed.AI.Matrix.DotNetClient" / "Multiplexed.AI.Matrix.DotNetClient.csproj"
        text = project.read_text(encoding="utf-8-sig")
        marker = 'ProjectReference Include="'
        relative = text.split(marker, 1)[1].split('"', 1)[0].replace("\\", "/")
        resolved = (project.parent / relative).resolve()
        expected = (MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI.Sdk" / "Multiplexed.AI.Sdk.csproj").resolve()
        self.assertEqual(expected, resolved)
        self.assertTrue(resolved.is_file())

    def test_windows_npm_wrapper_resolution_is_explicit(self) -> None:
        with mock.patch("core_matrix.shutil.which", side_effect=lambda candidate: "C:/node/npm.cmd" if candidate == "npm.cmd" else None):
            self.assertEqual("C:/node/npm.cmd", core_matrix._resolve_tool("npm", windows=True))

    def test_commands_use_runtime_manifest_not_manual_environment_variables(self) -> None:
        scenario = load_plan()["coreScenarios"][0]
        command = core_matrix._command_for(scenario, Path("runtime-manifest.json"))
        self.assertIn("--manifest", command)
        self.assertNotIn("--environment-ref", command)

    def test_docker_compose_contains_real_infrastructure_and_three_clients(self) -> None:
        compose = (MATRIX_ROOT / "runtime" / "docker" / "docker-compose.yml").read_text(encoding="utf-8-sig")
        for service in ("mongo:", "redis:", "runtime:", "client-dotnet:", "client-typescript:", "client-python:", "matrix-verifier:"):
            self.assertIn(service, compose)

    def test_docker_verifier_requires_nine_scenarios(self) -> None:
        verifier = (MATRIX_ROOT / "runtime" / "docker" / "verifier.py").read_text(encoding="utf-8-sig")
        self.assertIn('CLIENTS = ("dotnet", "typescript", "python")', verifier)
        self.assertIn('WORKERS = ("dotnet", "typescript", "python")', verifier)
        self.assertIn('ProcessHostPool', verifier)

    def test_runtime_registration_uses_existing_worker_and_publication_authorities(self) -> None:
        registration = (MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI.McpServer.Host" / "Bootstrap" / "HostedInvocationHostRegistration.cs").read_text(encoding="utf-8-sig")
        self.assertIn("AddAiImmutablePublications", registration)
        self.assertIn("AddAiHostedInvocationWorkers", registration)
        self.assertIn("AddAiHostedInvocationWorkerPolling", registration)
        self.assertNotIn("new scheduler", registration.lower())


    def test_matrix_context_bootstrap_uses_real_context_store_path(self) -> None:
        bootstrap = (MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI.McpServer.Host" / "Bootstrap" / "MatrixHarnessBootstrapHostedService.cs").read_text(encoding="utf-8-sig")
        self.assertIn("using Multiplexed.Abstractions.Core.ExecutionContext;", bootstrap)
        self.assertIn("ContextKey = string.Empty", bootstrap)
        self.assertIn("_contexts.StoreAsync(context)", bootstrap)
        self.assertNotIn("_contexts.SeedAsync(context)", bootstrap)

    def test_docker_runtime_healthcheck_uses_runtime_available_python(self) -> None:
        compose = (MATRIX_ROOT / "runtime" / "docker" / "docker-compose.yml").read_text(encoding="utf-8-sig")
        self.assertIn("python3 -c", compose)
        self.assertNotIn("wget -q", compose)

    def test_local_topology_stages_same_worker_fixture_layout(self) -> None:
        runner = (MATRIX_ROOT / "runtime" / "local" / "run.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("$env:MATRIX_FIXTURE_ROOT = $fixtureRoot", runner)
        self.assertIn("Multiplexed.AI.Matrix.Worker.dll", runner)
        self.assertIn('Resolve-Tool "npm"', runner)

    def test_access_context_transport_header_is_supported_in_all_external_sdks(self) -> None:
        dotnet = (MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI.Sdk" / "Transport" / "AiSdkMcpHttpTransport.cs").read_text(encoding="utf-8-sig")
        typescript = (MATRIX_ROOT.parent / "node" / "sdk" / "src" / "transport" / "mcp-http-transport.ts").read_text(encoding="utf-8-sig")
        python = (MATRIX_ROOT.parent / "python" / "sdk" / "src" / "multiplexed_ai_sdk" / "mcp_http_transport.py").read_text(encoding="utf-8-sig")
        python_options = (MATRIX_ROOT.parent / "python" / "sdk" / "src" / "multiplexed_ai_sdk" / "transport.py").read_text(encoding="utf-8-sig")
        self.assertIn("AdditionalHeaders", dotnet)
        self.assertIn("additionalHeaders", typescript)
        self.assertIn("additional_headers", python)
        self.assertIn("cannot override Authorization", dotnet)
        self.assertIn("cannot override Authorization", typescript)
        self.assertIn("cannot override Authorization", python_options)

    def test_runtime_uses_matching_rbac_project_configuration(self) -> None:
        docker = (MATRIX_ROOT / "runtime" / "docker" / "runtime-entrypoint.sh").read_text(encoding="utf-8-sig")
        local = (MATRIX_ROOT / "runtime" / "local" / "run.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("--Multiplexed.Rbac.Core:Project=matrix", docker)
        self.assertIn("--Multiplexed.Rbac.Core:Project=matrix", local)
        self.assertNotIn("Multiplexed_Rbac_Core__Project", docker)
        self.assertNotIn("Multiplexed_Rbac_Core__Project", local)

    def test_runtime_requires_exact_dotnet_10_runtime_version(self) -> None:
        docker = (MATRIX_ROOT / "runtime" / "docker" / "runtime-entrypoint.sh").read_text(encoding="utf-8-sig")
        local = (MATRIX_ROOT / "runtime" / "local" / "run.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("10\\.0", docker)
        self.assertIn("10\\.0", local)
        self.assertIn(".NET 10.0.x runtime was not found", docker)
        self.assertIn(".NET 10.0.x runtime was not found", local)

    def test_matrix_harness_is_opt_in(self) -> None:
        registration = (MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI.McpServer.Host" / "Bootstrap" / "MatrixHarnessRegistration.cs").read_text(encoding="utf-8-sig")
        self.assertIn('if (!options.Enabled)', registration)
        self.assertIn('runtime.EnableRotation = false', registration)

    def test_docker_dotnet_publish_uses_explicit_publish_dir_without_output_alias(self) -> None:
        docker_dir = MATRIX_ROOT / "runtime" / "docker"
        for name in ("runtime.Dockerfile", "dotnet-client.Dockerfile", "typescript-client.Dockerfile", "python-client.Dockerfile"):
            text = (docker_dir / name).read_text(encoding="utf-8-sig")
            self.assertIn("-p:PublishDir=", text)
            self.assertNotIn(" -o /out/", text)

    def test_production_host_does_not_reference_test_project(self) -> None:
        project = MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI.McpServer.Host" / "Multiplexed.AI.McpServer.Host.csproj"
        text = project.read_text(encoding="utf-8-sig")
        self.assertNotIn("Multiplexed.AI.Tests.csproj", text)

    def test_matrix_authentication_scheme_does_not_hide_base_member(self) -> None:
        handler = MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI.McpServer.Host" / "Bootstrap" / "MatrixStaticBearerAuthenticationHandler.cs"
        text = handler.read_text(encoding="utf-8-sig")
        self.assertIn('public const string SchemeName = "MatrixBearer";', text)
        self.assertNotIn("public const string Scheme =", text)


    def test_custom_worker_publications_use_explicit_dag_in_all_clients(self) -> None:
        dotnet = (MATRIX_ROOT / "clients" / "dotnet" / "Multiplexed.AI.Matrix.DotNetClient" / "Program.cs").read_text(encoding="utf-8-sig")
        typescript = (MATRIX_ROOT / "clients" / "typescript" / "run.mjs").read_text(encoding="utf-8-sig")
        python = (MATRIX_ROOT / "clients" / "python" / "run.py").read_text(encoding="utf-8-sig")
        self.assertIn("AiSdkExecutionMode.Dag", dotnet)
        self.assertIn('executionMode: "Dag"', typescript)
        self.assertIn("AiSdkExecutionMode.DAG", python)
        self.assertNotIn("AiSdkExecutionMode.Sequential", dotnet)
        self.assertNotIn('executionMode: "Sequential"', typescript)
        self.assertNotIn("AiSdkExecutionMode.SEQUENTIAL", python)

    def test_host_registers_existing_durable_custom_invocation_adapters(self) -> None:
        registration = (MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI.McpServer.Host" / "Bootstrap" / "HostedInvocationHostRegistration.cs").read_text(encoding="utf-8-sig")
        self.assertIn("AddAiDurableInvocationDagReconciliation", registration)
        self.assertIn("AiDurableInvocationDagReconciliationOptions", registration)
        self.assertIn("AddAiHostedInvocationWorkers", registration)

    def test_control_plane_owns_durable_custom_continuation_reconciliation(self) -> None:
        registration = (MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI.McpServer.Host" / "Bootstrap" / "HostedInvocationHostRegistration.cs").read_text(encoding="utf-8-sig")
        self.assertIn("var invocationScope = new AiDurableInvocationScope", registration)
        self.assertIn("Scopes = new[] { invocationScope }", registration)
        self.assertIn("AddAiDurableInvocationDagReconciliation", registration)
        self.assertIn("new[] { invocationScope }", registration)
        self.assertNotIn("services.AddAiDurableInvocationDag();", registration)

    def test_process_topologies_align_control_plane_identity_and_discovery_key(self) -> None:
        docker = (MATRIX_ROOT / "runtime" / "docker" / "runtime-entrypoint.sh").read_text(encoding="utf-8-sig")
        local = (MATRIX_ROOT / "runtime" / "local" / "run.ps1").read_text(encoding="utf-8-sig")
        for text in (docker, local):
            self.assertIn("AiEngine__ControlPlane__ControlPlaneId", text)
            self.assertIn("matrix-control", text)
            self.assertIn("multiplexed-ai:matrix-control", text)

    def test_anonymous_health_probe_does_not_emit_invalid_bearer_failure(self) -> None:
        handler = MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI.McpServer.Host" / "Bootstrap" / "MatrixStaticBearerAuthenticationHandler.cs"
        text = handler.read_text(encoding="utf-8-sig")
        self.assertIn("AuthenticateResult.NoResult()", text)
        self.assertIn("string.IsNullOrWhiteSpace(authorization)", text)

    def test_process_topologies_use_replay_safe_payload_storage(self) -> None:
        docker = (MATRIX_ROOT / "runtime" / "docker" / "runtime-entrypoint.sh").read_text(encoding="utf-8-sig")
        local = (MATRIX_ROOT / "runtime" / "local" / "run.ps1").read_text(encoding="utf-8-sig")
        for text in (docker, local):
            self.assertIn("AiEngine__PayloadStore__Enabled", text)
            self.assertIn("AiEngine__PayloadStore__Provider", text)
            self.assertIn("mongo-redis", text)
            self.assertIn("AiEngine__PayloadStore__RequireReplaySafePayloads", text)
            self.assertIn("AiEngine__PayloadStore__Mongo__Enabled", text)
            self.assertIn("AiEngine__PayloadStore__Mongo__ConnectionString", text)
            self.assertIn("AiEngine__PayloadStore__Mongo__DatabaseName", text)
            self.assertIn("AiEngine__PayloadStore__RedisCache__Enabled", text)


    def test_process_topologies_enable_shared_queue_continuation_pump(self) -> None:
        docker = (MATRIX_ROOT / "runtime" / "docker" / "runtime-entrypoint.sh").read_text(encoding="utf-8-sig")
        local = (MATRIX_ROOT / "runtime" / "local" / "run.ps1").read_text(encoding="utf-8-sig")
        for text in (docker, local):
            self.assertIn("AiMcpHost__EnableSharedQueuePump", text)
            self.assertIn("AiSharedQueueBackgroundService__Enabled", text)
            self.assertIn("AiSharedQueuePump__Enabled", text)
            self.assertIn('"true"', text.lower())

    def test_public_sdk_submit_propagates_frozen_definition_to_runtime_request(self) -> None:
        boundary = (MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI.McpServer" / "PublicSdk" / "AiPublicSdkBoundary.cs").read_text(encoding="utf-8-sig")
        submit = boundary.split("public async Task<AiSdkExecutionSubmissionResponse> SubmitAsync", 1)[1].split(
            "public async Task<AiSdkExecutionObservation> ObserveAsync", 1
        )[0]
        self.assertIn("_runs.CreateWithDefinitionAsync", submit)
        self.assertIn("PipelineDefinitionSnapshot = record.PipelineDefinitionSnapshot", submit)
        self.assertIn("PipelineDefinition = admission.Definition", submit)
        self.assertNotIn("_publications.ReadDefinitionAsync", submit)

    def test_local_pool_runtime_advertises_routable_local_provider(self) -> None:
        factory = (MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI" / "Runtime" / "ControlPlane" / "RuntimeInstances" / "Pool" / "AiLocalRuntimeInstanceHostFactory.cs").read_text(encoding="utf-8-sig")
        self.assertIn("[AiRuntimeInstanceProviderMetadataKeys.LegacyProviderName] = AiRuntimeInstanceProviderNames.Local,", factory)
        self.assertIn("ProviderName = AiRuntimeInstanceProviderNames.Local,", factory)
        self.assertNotIn("AiRuntimeInstanceProviderNames.LocalPool", factory)

    def test_claimed_executor_projects_authoritative_claim_without_extra_store_read(self) -> None:
        executor = (MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI" / "Runtime" / "Execution" / "Engine" / "Steps" / "AiDagClaimedStepExecutor.cs").read_text(encoding="utf-8-sig")
        adapter = (MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI" / "Runtime" / "Invocation" / "Durable" / "Dag" / "AiDurableInvocationStepAdapter.cs").read_text(encoding="utf-8-sig")
        self.assertIn("ProjectAuthoritativeClaimIntoExecutionView", executor)
        self.assertIn("stepState.Status = AiStepExecutionStatus.Running", executor)
        self.assertIn("stepState.ClaimedBy = runtimeInstanceId", executor)
        self.assertIn("stepState.ClaimToken = claimedStep.ClaimToken", executor)
        self.assertNotIn("DagStore.GetStateAsync", executor)
        self.assertIn("currentStep.Status != AiStepExecutionStatus.Running", adapter)

    def test_pooled_runtime_child_does_not_inherit_control_plane_scaler(self) -> None:
        factory = (MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI" / "Runtime" / "ControlPlane" / "RuntimeInstances" / "Pool" / "AiLocalRuntimeInstanceHostFactory.cs").read_text(encoding="utf-8-sig")
        copy_loop = factory.split("foreach (var descriptor in servicesProvider.Services)", 1)[1].split("services.RemoveAll<IAiRuntimeObservability>", 1)[0]
        self.assertIn("typeof(IAiLocalRuntimeInstanceHostFactory)", copy_loop)
        self.assertIn("typeof(IAiLocalRuntimeInstanceScaler)", copy_loop)
        self.assertIn("typeof(IAiLocalRuntimeInstanceServiceCollectionProvider)", copy_loop)

    def test_docker_worker_executables_are_canonical_physical_paths(self) -> None:
        entrypoint = (MATRIX_ROOT / "runtime" / "docker" / "runtime-entrypoint.sh").read_text(encoding="utf-8-sig")
        self.assertIn("resolve_executable()", entrypoint)
        self.assertIn('readlink -f "$candidate"', entrypoint)
        self.assertIn('DOTNET_EXE="$(resolve_executable dotnet)"', entrypoint)
        self.assertIn('NODE_EXE="$(resolve_executable node)"', entrypoint)
        self.assertIn('PYTHON_EXE="$(resolve_executable python3)"', entrypoint)
        self.assertNotIn('DOTNET_EXE="$(command -v dotnet)"', entrypoint)

    def test_hosted_worker_profiles_fail_fast_before_polling(self) -> None:
        registration = (MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI.McpServer.Host" / "Bootstrap" / "HostedInvocationHostRegistration.cs").read_text(encoding="utf-8-sig")
        preflight = registration.index("AiWorkerLaunchPaths.ValidateProfile(profile)")
        polling = registration.index("AddAiHostedInvocationWorkerPolling")
        self.assertLess(preflight, polling)
        self.assertIn("foreach (var profile in profiles)", registration)

    def test_worker_dispatch_failure_diagnostics_keep_a_bounded_phase(self) -> None:
        supervisor = (MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI" / "Runtime" / "Invocation" / "Workers" / "AiWorkerInvocationSupervisor.cs").read_text(encoding="utf-8-sig")
        self.assertIn('failurePhase = "prepare-worker-code"', supervisor)
        self.assertIn('failurePhase = "invoke-worker-transport"', supervisor)
        self.assertIn('failurePhase = "complete-journal-result"', supervisor)
        self.assertIn("Phase={Phase}", supervisor)
        self.assertNotIn("exception.Message", supervisor)

    def test_python_matrix_evidence_preserves_sdk_wire_timestamp_string(self) -> None:
        client = (MATRIX_ROOT / "clients" / "python" / "run.py").read_text(encoding="utf-8-sig")
        observation_contract = (MATRIX_ROOT.parent / "python" / "sdk" / "src" / "multiplexed_ai_sdk" / "contracts" / "observation" / "execution_observation.py").read_text(encoding="utf-8-sig")
        self.assertIn('"recordedAtUtc": observation.updated_at_utc or None', client)
        self.assertNotIn("observation.updated_at_utc.isoformat()", client)
        self.assertIn("updated_at_utc: str", observation_contract)

    def test_strict_worker_launch_path_link_rejection_remains_enabled(self) -> None:
        launch_paths = (MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI" / "Runtime" / "Invocation" / "Workers" / "AiWorkerLaunchPaths.cs").read_text(encoding="utf-8-sig")
        self.assertIn("FileAttributes.ReparsePoint", launch_paths)
        self.assertIn("Symbolic links and reparse points are not accepted", launch_paths)
        self.assertIn("A linked launch path is not accepted", launch_paths)

    def test_initial_feature_matrix_binds_six_real_scenarios(self) -> None:
        plan = load_plan()
        bound = [
            item for item in plan["featureScenarios"]
            if item["coverageTarget"] in {"publication-pinning", "deterministic-dependency-packaging"}
        ]
        self.assertEqual(6, len(bound))
        self.assertEqual({"python"}, {item["clientLanguage"] for item in bound})
        for target in ("publication-pinning", "deterministic-dependency-packaging"):
            workers = {item["workerLanguage"] for item in bound if item["coverageTarget"] == target}
            self.assertEqual({"dotnet", "typescript", "python"}, workers)

    def test_feature_client_uses_only_public_python_sdk_contracts(self) -> None:
        client = (MATRIX_ROOT / "clients" / "python" / "feature.py").read_text(encoding="utf-8-sig")
        self.assertIn("from multiplexed_ai_sdk import", client)
        self.assertIn("AiSdkPublicationDependencyPackageKind", client)
        self.assertIn("postRepublishObservedPublicationRef", client)
        self.assertNotIn("Multiplexed.AI.Runtime", client)
        self.assertNotIn("IAiPublicSdkBoundary", client)

    def test_custom_policy_upload_sites_match_declared_policy_scope(self) -> None:
        client = (MATRIX_ROOT / "clients" / "python" / "feature.py").read_text(encoding="utf-8-sig")
        self.assertIn('"concurrency": None', client)
        self.assertIn('"retry": "work"', client)
        self.assertIn('"delegation": "invoke-child"', client)
        self.assertIn("step_name=step_name", client)

    def test_fail_once_retry_fixture_treats_missing_retry_state_as_first_attempt(self) -> None:
        fixture = (
            MATRIX_ROOT.parent
            / "dotnet"
            / "src"
            / "Multiplexed.AI"
            / "Runtime"
            / "Pipeline"
            / "Steps"
            / "Test"
            / "FailOnceThenSucceedStep.cs"
        ).read_text(encoding="utf-8-sig")
        self.assertIn("(stepState.RetryState?.RetryCount ?? 0) == 0", fixture)
        self.assertNotIn("stepState.RetryState?.RetryCount == 0", fixture)

    def test_python_docker_client_carries_dotnet_dependency_fixture_and_feature_runner(self) -> None:
        dockerfile = (MATRIX_ROOT / "runtime" / "docker" / "python-client.Dockerfile").read_text(encoding="utf-8-sig")
        runner = (MATRIX_ROOT / "runtime" / "docker" / "run-client.sh").read_text(encoding="utf-8-sig")
        self.assertIn("Multiplexed.AI.Matrix.PackagedWorker.csproj", dockerfile)
        self.assertIn("Multiplexed.AI.Matrix.Dependency.dll", dockerfile)
        self.assertIn("feature.py", runner)
        self.assertIn("publication-pinning", runner)
        self.assertIn("deterministic-dependency-packaging", runner)

    def test_verifier_preserves_core_nine_and_requires_six_feature_scenarios(self) -> None:
        verifier = (MATRIX_ROOT / "runtime" / "docker" / "verifier.py").read_text(encoding="utf-8-sig")
        self.assertIn("CORE_EXPECTED", verifier)
        self.assertIn("FEATURE_EXPECTED", verifier)
        self.assertIn("9/9 production-like Docker ProcessHostPool scenarios passed.", verifier)
        self.assertIn("6/6 publication-pinning/dependency-package ProcessHostPool feature scenarios passed.", verifier)
        for kind in ("DotNetAssemblyClosure", "NodeLockedBundle", "PythonWheelBundle"):
            self.assertIn(kind, verifier)

    def test_local_process_runner_stages_packaged_fixture_and_executes_feature_matrix(self) -> None:
        runner = (MATRIX_ROOT / "runtime" / "local" / "run.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("$fixtureDotNetPackaged", runner)
        self.assertIn("Multiplexed.AI.Matrix.PackagedWorker.dll", runner)
        self.assertIn("Multiplexed.AI.Matrix.Dependency.dll", runner)
        self.assertIn("feature_matrix.py", runner)

    def test_dotnet_dependency_fixture_has_exact_stable_identity(self) -> None:
        project = MATRIX_ROOT / "fixtures" / "dotnet-dependency" / "Multiplexed.AI.Matrix.Dependency" / "Multiplexed.AI.Matrix.Dependency.csproj"
        text = project.read_text(encoding="utf-8-sig")
        self.assertIn("<AssemblyName>Multiplexed.AI.Matrix.Dependency</AssemblyName>", text)
        self.assertIn("<Version>1.0.0</Version>", text)
        self.assertIn("<AssemblyVersion>1.0.0.0</AssemblyVersion>", text)
        self.assertIn("<Deterministic>true</Deterministic>", text)


if __name__ == "__main__":
    unittest.main()

def test_distributed_failure_path_preserves_custom_retry_policy_authority() -> None:
    root = MATRIX_ROOT.parents[1]
    helpers = (
        root
        / "implementations"
        / "dotnet"
        / "src"
        / "Multiplexed.AI"
        / "Runtime"
        / "Execution"
        / "Engine"
        / "Helpers"
        / "AiDagExecutionHelpers.cs"
    ).read_text(encoding="utf-8-sig")
    distributed = (
        root
        / "implementations"
        / "dotnet"
        / "src"
        / "Multiplexed.AI"
        / "Runtime"
        / "Execution"
        / "Engine"
        / "Distributed"
        / "AiDagDistributedExecutionRunner.cs"
    ).read_text(encoding="utf-8-sig")
    batch = (
        root
        / "implementations"
        / "dotnet"
        / "src"
        / "Multiplexed.AI"
        / "Runtime"
        / "Execution"
        / "Engine"
        / "Batch"
        / "AiDagBatchExecutionRunner.cs"
    ).read_text(encoding="utf-8-sig")
    store = (
        root
        / "implementations"
        / "dotnet"
        / "src"
        / "Multiplexed.AI"
        / "Stores"
        / "Cache"
        / "Redis"
        / "Dag"
        / "RedisDagStoreTransitionService.cs"
    ).read_text(encoding="utf-8-sig")

    assert "RetryPolicyBindings.Any" in helpers
    assert "AiInvocationKind.Custom" in helpers
    assert "HandleFailureAsync(" in helpers
    assert "TryFailStepWithDecisionAsync(" in helpers
    assert "TryPersistClaimedStepFailureAsync(" in distributed
    assert "TryPersistClaimedStepFailureAsync(" in batch
    assert "shouldRetry = decision.Disposition == AiDagStepFailureDisposition.Retry" in store


def test_redis_failure_transition_accepts_explicit_retry_decision_without_changing_native_failure_script() -> None:
    root = MATRIX_ROOT.parents[1]
    transition = (
        root
        / "implementations"
        / "dotnet"
        / "src"
        / "Multiplexed.AI"
        / "Stores"
        / "Cache"
        / "Redis"
        / "Dag"
        / "RedisDagStoreTransitionService.cs"
    ).read_text(encoding="utf-8-sig")
    lua = (
        root
        / "implementations"
        / "dotnet"
        / "src"
        / "Multiplexed.AI"
        / "Stores"
        / "Cache"
        / "Redis"
        / "Lua"
        / "RedisDagLuaScripts.cs"
    ).read_text(encoding="utf-8-sig")

    assert "FailWithDecisionPreparedScript" in transition
    assert "ExecuteFailWithDecisionAsync" in transition
    assert "public static readonly LuaScript FailWithDecisionPreparedScript" in lua
    assert "local shouldRetry = tonumber(@shouldRetry) == 1" in lua
    assert "if retryCount < maxRetries then" in lua
    assert "@decisionMode" not in lua
