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

    def test_nested_child_dag_feature_uses_exact_recursive_publication_path(self) -> None:
        client = (MATRIX_ROOT / "clients" / "python" / "feature.py").read_text(encoding="utf-8-sig")
        self.assertIn('definition_path="/invoke-child/invoke-grandchild"', client)
        self.assertIn('"childDagDefinition": grandchild_definition', client)
        self.assertIn('"childDagDefinition": child_definition', client)
        self.assertIn('"StepKey": "execution.child-dag"', client)
        self.assertIn('"Invocation": {"kind": "Custom"}', client)
        self.assertNotIn('"Invocation": {"Kind": "Custom"}', client)

    def test_docker_python_client_executes_all_three_nested_child_dag_workers(self) -> None:
        runner = (MATRIX_ROOT / "runtime" / "docker" / "run-client.sh").read_text(encoding="utf-8-sig")
        self.assertIn('nested-child-dag) scenario="feature-nested-child-dag-python-client-${worker}-worker"', runner)
        for worker in ("dotnet", "typescript", "python"):
            self.assertIn(f"run_feature nested-child-dag {worker}", runner)

    def test_docker_runtime_keeps_child_dag_composition_enabled(self) -> None:
        runtime = (MATRIX_ROOT / "runtime" / "docker" / "runtime-entrypoint.sh").read_text(encoding="utf-8-sig")
        self.assertIn('AiChildDagComposition__Enabled="true"', runtime)

    def test_verifier_requires_three_nested_child_dag_scenarios(self) -> None:
        verifier = (MATRIX_ROOT / "runtime" / "docker" / "verifier.py").read_text(encoding="utf-8-sig")
        self.assertIn("NESTED_CHILD_DAG_EXPECTED", verifier)
        self.assertIn('"nestedDepth"', verifier)
        self.assertIn('"/invoke-child/invoke-grandchild"', verifier)
        self.assertIn("3/3 nested Child DAG ProcessHostPool scenarios passed.", verifier)



    def test_matrix_harness_registers_durable_mcp_effect_fence_only_when_opted_in(self) -> None:
        registration = (
            MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI.McpServer.Host" /
            "Bootstrap" / "MatrixHarnessRegistration.cs"
        ).read_text(encoding="utf-8-sig")
        self.assertIn("services.AddAiDurableMcpEffectEvidence();", registration)
        self.assertIn("services.AddAiOutboundMcpToolExecution(", registration)
        self.assertIn('ConnectionRef = "matrix-effect-probe"', registration)
        self.assertIn('new AiOutboundMcpToolRegistration("probe.fail-count"', registration)
        self.assertIn('new AiOutboundMcpToolRegistration("probe.slow-count"', registration)
        self.assertLess(registration.index("if (!options.Enabled)"), registration.index("services.AddAiDurableMcpEffectEvidence();"))

    def test_mcp_effect_probe_is_matrix_only_and_has_no_runtime_project_reference(self) -> None:
        project = (
            MATRIX_ROOT / "fixtures" / "mcp-effect-probe" /
            "Multiplexed.AI.Matrix.McpEffectProbe" / "Multiplexed.AI.Matrix.McpEffectProbe.csproj"
        ).read_text(encoding="utf-8-sig")
        program = (
            MATRIX_ROOT / "fixtures" / "mcp-effect-probe" /
            "Multiplexed.AI.Matrix.McpEffectProbe" / "Program.cs"
        ).read_text(encoding="utf-8-sig")
        self.assertIn("ModelContextProtocol.AspNetCore", project)
        self.assertNotIn("ProjectReference", project)
        self.assertIn('Name = "probe.fail-count"', program)
        self.assertIn('Name = "probe.slow-count"', program)
        self.assertIn("physicalCallCount", program)

    def test_mcp_effect_feature_proves_retry_and_single_physical_emission(self) -> None:
        client = (MATRIX_ROOT / "clients" / "python" / "feature.py").read_text(encoding="utf-8-sig")
        verifier = (MATRIX_ROOT / "runtime" / "docker" / "verifier.py").read_text(encoding="utf-8-sig")
        self.assertIn('max_retries=1', client)
        self.assertIn('durable.get("retryCount") != 1', client)
        self.assertIn('probe.get("physicalCallCount") != 1', client)
        self.assertIn('"Completed" if effect_case == "completed-local-replay" else "Uncertain"', client)
        self.assertIn('"transport-timeout"', client)
        self.assertIn("MCP_EFFECT_EXPECTED", verifier)
        self.assertIn("2/2 durable MCP effect evidence ProcessHostPool scenarios passed.", verifier)

    def test_explicit_execution_retry_budget_is_materialized_into_durable_retry_definition(self) -> None:
        resolved_step = (
            MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.Abstractions" / "AI" / "Pipeline" /
            "ResolvedAiPipelineStep.cs"
        ).read_text(encoding="utf-8-sig")
        resolver = (
            MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI" / "Runtime" / "Pipeline" /
            "AiPipelineResolver.cs"
        ).read_text(encoding="utf-8-sig")
        retry_engine = (
            MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI" / "Runtime" / "AI" / "Retry" /
            "DefaultAiRetryEngine.cs"
        ).read_text(encoding="utf-8-sig")
        self.assertIn("public AiPipelineStepExecutionDefinition? Execution { get; init; }", resolved_step)
        self.assertIn("Execution = stepDefinition.Execution", resolver)
        self.assertIn("if (StepContext.Step.Execution is null)", retry_engine)
        self.assertIn("MaxRetries = StepContext.Step.MaxRetries", retry_engine)
        self.assertIn("BaseDelayMs = StepContext.Step.RetryDelayMs", retry_engine)

    def test_durable_mcp_timeout_finalization_is_not_cancelled_by_the_expired_request_token(self) -> None:
        adapter = (
            MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI" / "Runtime" / "Invocation" / "Mcp" /
            "AiMcpStepAdapter.cs"
        ).read_text(encoding="utf-8-sig")
        durable = (
            MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI" / "Runtime" / "Invocation" / "Mcp" /
            "Durable" / "AiDurableMcpToolTransport.cs"
        ).read_text(encoding="utf-8-sig")
        self.assertIn("executionCancellation.Token", adapter)
        self.assertIn("_transport is AiDurableMcpToolTransport", adapter)
        self.assertIn("CancellationToken.None", durable)
        self.assertNotIn("if (cancellationToken.IsCancellationRequested) return;", durable)

    def test_runtime_manifest_exposes_only_harness_diagnostics_for_mcp_effect_evidence(self) -> None:
        bootstrap = (
            MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI.McpServer.Host" /
            "Bootstrap" / "MatrixHarnessBootstrapHostedService.cs"
        ).read_text(encoding="utf-8-sig")
        diagnostics = (
            MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI.McpServer.Host" /
            "Bootstrap" / "ApplicationConfiguration.cs"
        ).read_text(encoding="utf-8-sig")
        self.assertIn("effectProbeStateEndpoint", bootstrap)
        self.assertIn("effectEvidenceEndpoint", bootstrap)
        self.assertIn('/matrix/mcp-effect-evidence/{executionId}/{stepName}', diagnostics)
        self.assertIn("retryCount = step?.RetryState?.RetryCount", diagnostics)
        start = diagnostics.index("private static void ConfigureMatrixMcpEffectEvidenceEndpoint")
        end = diagnostics.index("private static void ConfigureRuntimeInstanceEndpoints", start)
        diagnostic_block = diagnostics[start:end]
        self.assertNotIn("Endpoint =", diagnostic_block)
        self.assertNotIn("EffectProbeMcpEndpoint", diagnostic_block)


    def test_cancellation_target_is_executed_by_all_three_external_sdk_clients(self) -> None:
        plan = load_plan()
        scenarios = [item for item in plan["featureScenarios"] if item["coverageTarget"] == "cancellation"]
        self.assertEqual({item["clientLanguage"] for item in scenarios}, {"dotnet", "typescript", "python"})
        self.assertEqual(len(scenarios), 3)
        self.assertTrue(all(item["coverageValues"] == [] for item in scenarios))

    def test_each_external_client_uses_explicit_durable_cancellation_operation(self) -> None:
        dotnet = (MATRIX_ROOT / "clients" / "dotnet" / "Multiplexed.AI.Matrix.DotNetClient" / "Program.cs").read_text(encoding="utf-8-sig")
        typescript = (MATRIX_ROOT / "clients" / "typescript" / "run.mjs").read_text(encoding="utf-8-sig")
        python = (MATRIX_ROOT / "clients" / "python" / "run.py").read_text(encoding="utf-8-sig")
        self.assertIn("CancelExecutionAsync", dotnet)
        self.assertIn("cancelExecution", typescript)
        self.assertIn("cancel_execution", python)
        for text in (dotnet, typescript, python):
            self.assertIn("matrix-running-cancellation", text)
            self.assertIn("running-cooperative", text)
            self.assertIn("active-execution-observed", text)

    def test_docker_runner_assigns_cancellation_to_each_native_sdk_client_container(self) -> None:
        runner = (MATRIX_ROOT / "runtime" / "docker" / "run-client.sh").read_text(encoding="utf-8-sig")
        self.assertIn('scenario="feature-cancellation-${LANGUAGE}-client-${LANGUAGE}-worker"', runner)
        self.assertIn("dotnet /app/client/Multiplexed.AI.Matrix.DotNetClient.dll", runner)
        self.assertIn("node /app/implementations/matrix/clients/typescript/run.mjs", runner)
        self.assertIn("python /app/implementations/matrix/clients/python/run.py", runner)
        self.assertIn("run_cancellation_feature", runner)

    def test_verifier_requires_three_client_language_cancellation_scenarios(self) -> None:
        verifier = (MATRIX_ROOT / "runtime" / "docker" / "verifier.py").read_text(encoding="utf-8-sig")
        self.assertIn("CANCELLATION_EXPECTED", verifier)
        self.assertIn('"activeStepStatusBeforeCancel"', verifier)
        self.assertIn('"terminalStatus") != "Cancelled"', verifier)
        self.assertIn("3/3 durable cancellation SDK-client scenarios passed.", verifier)

    def test_public_cancellation_finalizes_parked_dag_through_terminal_store_authority(self) -> None:
        coordinator = (
            MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI" / "Runtime" / "Execution" / "Control" /
            "AiDagExecutionCancellationCoordinator.cs"
        ).read_text(encoding="utf-8-sig")
        boundary = (
            MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI.McpServer" / "PublicSdk" /
            "AiPublicSdkBoundary.cs"
        ).read_text(encoding="utf-8-sig")
        continuation = (
            MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI" / "Runtime" / "Invocation" / "Durable" / "Dag" /
            "AiDurableInvocationDagContinuationCoordinator.cs"
        ).read_text(encoding="utf-8-sig")
        self.assertIn("TryFinalizeExecutionAsync", coordinator)
        self.assertIn("ExpectedExecutionStepKey = record.ExecutionStepKey", coordinator)
        self.assertIn("Status = AiExecutionStatus.Cancelled", coordinator)
        self.assertIn("MarkCancelledAsync", coordinator)
        self.assertIn("AiDagExecutionCancellationCoordinator", boundary)
        self.assertIn("_cancellation.CancelAsync", boundary)
        self.assertIn("if (parent.IsTerminal)", continuation)
        self.assertIn("AiDurableInvocationContinuationStatus.Suppressed", continuation)


    def test_increment_six_recovery_harness_is_opt_in_and_uses_production_authorities(self) -> None:
        registration = (
            MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI.McpServer.Host" /
            "Bootstrap" / "MatrixHarnessRegistration.cs"
        ).read_text(encoding="utf-8-sig")
        endpoints = (
            MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI.McpServer.Host" /
            "Bootstrap" / "ApplicationConfiguration.cs"
        ).read_text(encoding="utf-8-sig")
        recovery = (
            MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI.McpServer.Host" /
            "Bootstrap" / "MatrixRecoveryProbe.cs"
        ).read_text(encoding="utf-8-sig")

        self.assertIn("if (!matrix.Enabled)", endpoints)
        self.assertIn('/matrix/recovery/{recoveryCase}/{executionId}', endpoints)
        self.assertIn("MatrixRecoverySeedRequest seed", endpoints)
        self.assertIn("services.AddSingleton<MatrixRecoveryProbe>()", registration)
        self.assertIn("IAiRuntimeExecutionRecoveryReconciler", recovery)
        self.assertIn("IAiRuntimeInstanceRegistry", recovery)
        self.assertIn("IAiRuntimeRunExecutionIndex", recovery)
        self.assertIn("IAiSharedRunStore", recovery)
        self.assertIn("IAiSharedQueue", recovery)
        self.assertIn(".ReconcileAsync(cancellationToken)", recovery)
        self.assertIn(".MarkUnhealthyAsync", recovery)
        self.assertNotIn("RedisAi", recovery)
        self.assertNotIn("MongoAi", recovery)

        feature_client = (MATRIX_ROOT / "clients" / "python" / "feature.py").read_text(encoding="utf-8-sig")
        self.assertIn("except urllib.error.HTTPError as error", feature_client)
        self.assertIn("returned HTTP {error.code}", feature_client)


    def test_increment_six_recovery_matrix_preprovisions_replacement_runtime_capacity(self) -> None:
        docker_entrypoint = (MATRIX_ROOT / "runtime" / "docker" / "runtime-entrypoint.sh").read_text()
        local_runner = (MATRIX_ROOT / "runtime" / "local" / "run.ps1").read_text()
        recovery = (
            MATRIX_ROOT.parent
            / "dotnet"
            / "src"
            / "Multiplexed.AI.McpServer.Host"
            / "Bootstrap"
            / "MatrixRecoveryProbe.cs"
        ).read_text()

        self.assertIn('AiLocalRuntimeInstancePool__InstanceCount="2"', docker_entrypoint)
        self.assertIn('AiLocalRuntimeInstancePool__InstanceCount = "2"', local_runner)
        self.assertIn("healthyReplacement", recovery)
        self.assertIn("runtime.Status == AiRuntimeInstanceStatus.Ready", recovery)
        self.assertIn("runtime.CanAcceptRun", recovery)
        self.assertIn("availableReplacementRuntimeInstanceId", recovery)
        self.assertIn("replacementRuntimeInstanceId = reassigned.AssignedRuntimeInstanceId", recovery)
        self.assertIn("was not reassigned to distinct healthy runtime capacity", recovery)

        feature_client = (MATRIX_ROOT / "clients" / "python" / "feature.py").read_text()
        self.assertIn('replacement_runtime_instance_id = recovery.get("replacementRuntimeInstanceId")', feature_client)
        self.assertIn("In-flight recovery reused the failed runtime", feature_client)

    def test_increment_six_recovery_proves_identity_preserving_resume_and_new_identity_redispatch(self) -> None:
        client = (MATRIX_ROOT / "clients" / "python" / "feature.py").read_text(encoding="utf-8-sig")
        recovery = (
            MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI.McpServer.Host" /
            "Bootstrap" / "MatrixRecoveryProbe.cs"
        ).read_text(encoding="utf-8-sig")

        self.assertIn('step_key="delay-step"', client)
        self.assertNotIn('The local-queued recovery template execution did not complete.', client)
        self.assertIn('"public-sdk-definition-seed"', client)
        self.assertIn('"local-queued-no-execution-id"', client)
        self.assertIn('"same-execution-id-resumed"', client)
        self.assertIn('"new-execution-id-created"', client)
        self.assertIn('recovery.get("preRecoveryExecutionId") is not None', client)
        self.assertIn('"in-flight-resume" => RunInFlightResumeAsync', recovery)
        self.assertIn('"local-queued-redispatch" => RunLocalQueuedRedispatchAsync', recovery)
        self.assertIn("ExecutionId = null", recovery)
        self.assertIn("RequestedExecutionId = null", recovery)
        self.assertIn("PipelineDefinitionSnapshot = null", recovery)
        self.assertIn("AiPublicSdkContractMapper.ToInternal(seed.Definition)", recovery)
        self.assertIn("CreateMatrixExecutionContextSnapshot", recovery)
        self.assertNotIn("No template shared run exists", recovery)
        self.assertLess(
            recovery.index("RegisterQueuedAsync"),
            recovery.index("// The recovery loop runs every second."),
        )
        self.assertIn("AiRunMetadataKeys.CamelCaseSharedRunId", recovery)
        self.assertNotIn("redispatchedExecutionId = replacement.ExecutionId", recovery)
        self.assertIn("replacementIndex = await this.runtimeRunIndex", recovery)
        self.assertIn(".GetAsync(replacement.LocalRunId, cancellationToken)", recovery)
        self.assertIn("var redispatchedExecutionId = replacementIndex.ExecutionId", recovery)
        self.assertIn("replacementRuntimeIndexStatus = completedIndex.Status", recovery)
        self.assertIn("preRecoveryExecutionId = (string?)null", recovery)
        self.assertIn("this.dagExecutions", recovery)

    def test_increment_six_repairs_redis_lua_empty_execution_context_collections(self) -> None:
        helper = (
            MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI" /
            "Stores" / "Cache" / "Redis" / "Serialization" / "JsonSerializationHelpers.cs"
        ).read_text(encoding="utf-8-sig")
        recovery = (
            MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI.McpServer.Host" /
            "Bootstrap" / "MatrixRecoveryProbe.cs"
        ).read_text(encoding="utf-8-sig")
        feature_client = (MATRIX_ROOT / "clients" / "python" / "feature.py").read_text(encoding="utf-8-sig")
        verifier = (MATRIX_ROOT / "runtime" / "docker" / "verifier.py").read_text(encoding="utf-8-sig")

        self.assertIn("WriteRepairedExecutionContextSnapshotJson", helper)
        self.assertIn('string.Equals(property.Name, "Namespaces", StringComparison.OrdinalIgnoreCase)', helper)
        self.assertIn("WriteRepairedNamespaceEntryJson", helper)
        self.assertIn('string.Equals(property.Name, "Trns", StringComparison.OrdinalIgnoreCase)', helper)
        self.assertIn("writer.WriteStartArray();", helper)
        self.assertIn("replacementRuntimeIndexStatus = completedIndex.Status", recovery)
        self.assertIn('recovery.get("replacementRuntimeIndexStatus") != "completed"', feature_client)
        self.assertIn('document.get("replacementRuntimeIndexStatus") != "completed"', verifier)

    def test_increment_six_local_queued_seed_is_not_visible_to_the_live_pending_pump(self) -> None:
        recovery = (
            MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI.McpServer.Host" /
            "Bootstrap" / "MatrixRecoveryProbe.cs"
        ).read_text(encoding="utf-8-sig")
        app = (
            MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI.McpServer.Host" /
            "Bootstrap" / "ApplicationConfiguration.cs"
        ).read_text(encoding="utf-8-sig")

        self.assertIn("Status = AiSharedQueueItemStatus.Dispatched", recovery)
        self.assertIn("ClaimToken = seedClaimToken", recovery)
        self.assertIn("ClaimedByRuntimeInstanceId = failedRuntimeInstanceId", recovery)
        self.assertNotIn(".ClaimAsync(", recovery)
        self.assertNotIn("AiSharedQueueItemStatus.Pending", recovery)
        self.assertIn('error = "matrix-recovery-probe-failed"', app)
        self.assertIn("exceptionType = exception.GetType().Name", app)
        self.assertIn("message = exception.Message", app)
        self.assertNotIn("exception.StackTrace", app)

    def test_increment_six_journal_probe_uses_real_store_lease_epoch_and_idempotent_completion(self) -> None:
        journal = (
            MATRIX_ROOT.parent / "dotnet" / "src" / "Multiplexed.AI.McpServer.Host" /
            "Bootstrap" / "MatrixJournalResultAcceptanceProbe.cs"
        ).read_text(encoding="utf-8-sig")
        self.assertIn("IAiDurableInvocationStore", journal)
        self.assertIn("new AiDurableInvocationJournal(this.store)", journal)
        self.assertIn("TryAcquireLeaseAsync", journal)
        self.assertIn("AiDurableInvocationCompletionStatus.Accepted", journal)
        self.assertIn("AiDurableInvocationCompletionStatus.AlreadyAccepted", journal)
        self.assertIn("Enumerable.Range(0, 8)", journal)
        self.assertIn("Task.WhenAll(deliveries)", journal)
        self.assertIn("accepted != 1 || alreadyAccepted != 7 || rejected != 0", journal)

    def test_increment_six_runner_executes_all_four_recovery_and_journal_cases(self) -> None:
        runner = (MATRIX_ROOT / "runtime" / "docker" / "run-client.sh").read_text(encoding="utf-8-sig")
        for command in (
            "run_recovery_feature in-flight-resume",
            "run_recovery_feature local-queued-redispatch",
            "run_journal_feature accepted-result-replay",
            "run_journal_feature duplicate-delivery-convergence",
        ):
            self.assertIn(command, runner)

    def test_increment_six_verifier_requires_recovery_and_journal_result_acceptance(self) -> None:
        verifier = (MATRIX_ROOT / "runtime" / "docker" / "verifier.py").read_text(encoding="utf-8-sig")
        self.assertIn("RECOVERY_EXPECTED", verifier)
        self.assertIn("JOURNAL_RESULT_ACCEPTANCE_EXPECTED", verifier)
        self.assertIn("2/2 runtime recovery ProcessHostPool scenarios passed.", verifier)
        self.assertIn("2/2 durable journal result-acceptance scenarios passed.", verifier)
        self.assertIn('document.get("runtimeIndexStatus") != "requeued-for-recovery"', verifier)
        self.assertIn('document.get("acceptedCount") != 1', verifier)
        self.assertIn('document.get("alreadyAcceptedCount") != 7', verifier)

    def test_increment_six_plan_raises_executed_gate_to_thirty(self) -> None:
        plan = load_plan()
        recovery = [item for item in plan["featureScenarios"] if item["coverageTarget"] == "recovery"]
        journal = [item for item in plan["featureScenarios"] if item["coverageTarget"] == "journal-result-acceptance"]
        self.assertEqual(9, len(plan["coreScenarios"]))
        self.assertEqual(21, len(plan["featureScenarios"]))
        self.assertEqual({"in-flight-resume", "local-queued-redispatch"}, {item["coverageValues"][0] for item in recovery})
        self.assertEqual({"accepted-result-replay", "duplicate-delivery-convergence"}, {item["coverageValues"][0] for item in journal})
        self.assertEqual(30, len(plan["coreScenarios"]) + len(plan["featureScenarios"]))


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
