from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
MATRIX = ROOT / "implementations" / "matrix"
KUBERNETES = MATRIX / "runtime" / "kubernetes"
DOTNET_TESTS = ROOT / "implementations" / "dotnet" / "Tests" / "Multiplexed.AI.McpServer.Tests.Integration" / "Scenarios" / "Production" / "Providers"


class KubernetesRuntimePoolMatrixTests(unittest.TestCase):
    def test_sidecar_plan_preserves_37_baseline_and_declares_kubernetes_pool(self) -> None:
        plan = json.loads((KUBERNETES / "kubernetes-pool-execution-v1.json").read_text(encoding="utf-8-sig"))
        self.assertEqual(1, plan["schemaVersion"])
        self.assertEqual("kubernetes-pool-runtime-execution-v1", plan["matrixId"])
        self.assertEqual(37, plan["baselineExecutedCoverage"])
        scenario = plan["scenario"]
        self.assertEqual("feature-runtime-provider-kubernetes-pool-http-command-routing", scenario["id"])
        self.assertEqual("kubernetes", scenario["topology"])
        self.assertEqual("KubernetesPool", scenario["runtimeProvider"])
        self.assertEqual("http", scenario["transport"])
        self.assertEqual(3, scenario["minimumRuntimeInstanceCount"])
        self.assertEqual(3, scenario["minimumSuccessfulCommandCount"])

    def test_live_execution_reuses_existing_kubernetes_pool_test_path(self) -> None:
        scenario = (DOTNET_TESTS / "Http" / "KubernetesPool" / "HttpKubernetesPoolMcpCommandScenarioTests.cs").read_text(encoding="utf-8-sig")
        self.assertIn("KubernetesSdkAiKubernetesRuntimePoolHostClient", scenario)
        self.assertIn("CreateRuntimePoolHostAsync", scenario)
        self.assertIn("WaitUntilHostReadyAsync", scenario)
        self.assertIn("KubernetesServicePortForward", scenario)
        self.assertIn("HttpRuntimePoolCommandClient", scenario)
        self.assertIn("DeleteRuntimePoolHostAsync", scenario)
        self.assertIn("KubernetesRuntimePoolMatrixEvidenceWriter", scenario)
        self.assertNotIn("IAiWorkerInvocationTransport", scenario)

    def test_matrix_image_override_is_test_only_and_uses_pack1_authority(self) -> None:
        helper = (DOTNET_TESTS / "Base" / "KubernetesPool" / "KubernetesRuntimePoolMatrixImageProfile.cs").read_text(encoding="utf-8-sig")
        self.assertIn("RuntimeImageRepository", helper)
        self.assertIn("RuntimeImageDigest", helper)
        self.assertIn("RequireImmutableRuntimeImage = true", helper)
        self.assertIn("RuntimeImage = string.Empty", helper)
        self.assertNotIn("KubernetesSdkAiKubernetesRuntimePoolHostClient", helper)

    def test_runner_invokes_only_existing_live_kubernetes_pool_scenario_and_verifier(self) -> None:
        runner = (KUBERNETES / "run-kubernetes-pool-execution.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("HttpKubernetesPoolMcpCommandScenarioTests.Http_KubernetesPool_Should_Route_Exact_Commands_To_All_InPod_Children", runner)
        self.assertIn("MULTIPLEXED_AI_MATRIX_KUBERNETES_EVIDENCE_PATH", runner)
        self.assertIn("MULTIPLEXED_AI_MATRIX_KUBERNETES_RUNTIME_IMAGE_REPOSITORY", runner)
        self.assertIn("MULTIPLEXED_AI_MATRIX_KUBERNETES_RUNTIME_IMAGE_DIGEST", runner)
        self.assertIn("verifier.py", runner)
        self.assertNotIn("docker compose", runner.lower())

    def test_verifier_accepts_valid_live_kubernetes_pool_evidence(self) -> None:
        import importlib.util

        spec = importlib.util.spec_from_file_location("kubernetes_pool_verifier", KUBERNETES / "verifier.py")
        self.assertIsNotNone(spec)
        self.assertIsNotNone(spec.loader)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)

        plan_path = KUBERNETES / "kubernetes-pool-execution-v1.json"
        with tempfile.TemporaryDirectory() as directory:
            evidence_path = Path(directory) / "evidence.json"
            evidence_path.write_text(
                json.dumps(
                    {
                        "schemaVersion": 1,
                        "scenarioId": "feature-runtime-provider-kubernetes-pool-http-command-routing",
                        "status": "passed",
                        "topology": "kubernetes",
                        "runtimeProvider": "KubernetesPool",
                        "transport": "http",
                        "runtimeImage": "registry.example/runtime@sha256:" + "a" * 64,
                        "immutableRuntimeImage": True,
                        "kubernetes": {
                            "namespaceName": "ai-runtime",
                            "podName": "runtime-pool-test",
                            "serviceName": "runtime-pool-test",
                            "runtimeInstanceIds": ["rt-1", "rt-2", "rt-3"],
                            "runtimeInstanceCount": 3,
                            "commandCount": 3,
                            "cleanupSucceeded": True,
                        },
                        "evidenceKinds": [
                            "kubernetes-sdk-pod-created",
                            "runtime-pool-pod-ready",
                            "stable-service-command-routing",
                            "exact-inpod-runtime-identities",
                            "kubernetes-sdk-delete-accepted",
                        ],
                    }
                ),
                encoding="utf-8",
            )
            module.verify(plan_path, evidence_path, require_immutable_image=True)

    def test_recovery_sidecar_plan_preserves_baseline_and_requires_existing_full_failure_proof(self) -> None:
        plan = json.loads((KUBERNETES / "kubernetes-pool-recovery-v1.json").read_text(encoding="utf-8-sig"))
        self.assertEqual(1, plan["schemaVersion"])
        self.assertEqual("kubernetes-pool-runtime-recovery-v1", plan["matrixId"])
        self.assertEqual(37, plan["baselineExecutedCoverage"])
        scenario = plan["scenario"]
        self.assertEqual("feature-runtime-provider-kubernetes-pool-hierarchical-recovery", scenario["id"])
        self.assertEqual("KubernetesPool", scenario["runtimeProvider"])
        self.assertEqual("EventDriven", scenario["requiredObservationMode"])
        self.assertGreaterEqual(scenario["minimumExecutionCycleCount"], 2)
        self.assertGreaterEqual(scenario["minimumChildDepth"], 3)

    def test_recovery_evidence_is_emitted_by_existing_kubernetes_pool_full_failure_harness(self) -> None:
        base = (DOTNET_TESTS / "Base" / "KubernetesPool" / "KubernetesRuntimePoolProductionScenarioTestsBase.cs").read_text(encoding="utf-8-sig")
        writer = (DOTNET_TESTS / "Base" / "KubernetesPool" / "KubernetesRuntimePoolMatrixEvidenceWriter.cs").read_text(encoding="utf-8-sig")
        self.assertIn("WriteRecoveryPassedAsync", base)
        self.assertIn("feature-runtime-provider-kubernetes-pool-hierarchical-recovery", base)
        self.assertIn("MULTIPLEXED_AI_MATRIX_KUBERNETES_RECOVERY_EVIDENCE_PATH", writer)
        self.assertIn("process-kill-execution-identity-continuity", writer)
        self.assertIn("runtime-ownership-convergence", writer)
        self.assertNotIn("KubernetesSdkAiKubernetesRuntimePoolHostClient", writer)

    def test_recovery_runner_reuses_existing_eventdriven_full_failure_canary(self) -> None:
        runner = (KUBERNETES / "run-kubernetes-pool-recovery.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("HttpKubernetesRuntimePoolFullFailureProductionScenarioTests.Http_KubernetesPool_EventDriven_Canary_Should_Reuse_The_Same_FullFailure_Scenario", runner)
        self.assertIn("MULTIPLEXED_AI_MATRIX_KUBERNETES_RECOVERY_EVIDENCE_PATH", runner)
        self.assertIn("recovery_verifier.py", runner)
        self.assertNotIn("docker compose", runner.lower())
        self.assertNotIn("KubernetesSdkAiKubernetesRuntimePoolHostClient", runner)

    def test_recovery_verifier_accepts_exact_existing_engine_convergence_evidence(self) -> None:
        import importlib.util

        spec = importlib.util.spec_from_file_location("kubernetes_pool_recovery_verifier", KUBERNETES / "recovery_verifier.py")
        self.assertIsNotNone(spec)
        self.assertIsNotNone(spec.loader)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)

        plan_path = KUBERNETES / "kubernetes-pool-recovery-v1.json"
        with tempfile.TemporaryDirectory() as directory:
            evidence_path = Path(directory) / "recovery.json"
            evidence_path.write_text(
                json.dumps(
                    {
                        "schemaVersion": 1,
                        "scenarioId": "feature-runtime-provider-kubernetes-pool-hierarchical-recovery",
                        "status": "passed",
                        "topology": "kubernetes",
                        "runtimeProvider": "KubernetesPool",
                        "transport": "http",
                        "recovery": {
                            "observationMode": "EventDriven",
                            "executionCycleCount": 2,
                            "childDepth": 3,
                            "runtimeProcessFailureCount": 2,
                            "podFailureCount": 2,
                            "recoveredSharedRunCount": 8,
                            "recoveryForensicsProofCount": 8,
                            "runtimeOwnershipTransitionCount": 8,
                            "runtimeOwnershipTransitionViolationCount": 0,
                            "parentReplayExpectedExecutionCount": 54,
                            "parentReplayProvenExecutionCount": 54,
                            "missingRecursiveChildLogicalStepCount": 0,
                            "unexpectedDuplicateRecursiveChildLogicalStepCount": 0,
                            "lostRunCount": 0,
                            "duplicateDurableDispatchCount": 0,
                            "warmReuseProven": True,
                        },
                        "evidenceKinds": [
                            "exact-inpod-runtime-process-failure",
                            "process-kill-execution-identity-continuity",
                            "busy-pod-failure",
                            "replacement-pod-capacity",
                            "recovery-forensics",
                            "runtime-ownership-convergence",
                            "terminal-dag-convergence",
                            "parent-replay",
                            "ledger-trace-lifecycle-proof",
                            "warm-pool-reuse",
                        ],
                    }
                ),
                encoding="utf-8",
            )
            module.verify(plan_path, evidence_path)


    def test_external_sdk_plan_preserves_baseline_and_declares_public_kubernetes_execution(self) -> None:
        plan = json.loads((KUBERNETES / "kubernetes-pool-external-sdk-v1.json").read_text(encoding="utf-8-sig"))
        self.assertEqual(1, plan["schemaVersion"])
        self.assertEqual("kubernetes-pool-external-sdk-execution-v1", plan["matrixId"])
        self.assertEqual(37, plan["baselineExecutedCoverage"])
        scenario = plan["scenario"]
        self.assertEqual("feature-runtime-provider-kubernetes-pool-external-sdk-python-worker", scenario["id"])
        self.assertEqual("kubernetes", scenario["topology"])
        self.assertEqual("KubernetesPool", scenario["runtimeProvider"])
        self.assertEqual("TrustedProcess", scenario["workerExecutionProvider"])
        self.assertEqual("python", scenario["clientLanguage"])
        self.assertEqual("python", scenario["workerLanguage"])
        self.assertEqual("Completed", scenario["requiredTerminalStatus"])

    def test_external_sdk_profile_reuses_existing_kubernetes_pool_settings_and_splits_authority(self) -> None:
        profile = (
            DOTNET_TESTS
            / "Http"
            / "KubernetesPool"
            / "HttpKubernetesPoolExternalSdkMatrixProfileTests.cs"
        ).read_text(encoding="utf-8-sig")
        self.assertIn("HttpKubernetesRuntimePoolProductionScenarioSettingsBuilder.Build", profile)
        self.assertIn('AiHostedInvocation:EnableWorkerPolling"] = "false"', profile)
        self.assertIn('AiHostedInvocation:EnableDagReconciliation"] = "true"', profile)
        self.assertIn('AiHostedInvocation:EnableLocalWorkerProfiles"] = "false"', profile)
        self.assertNotIn('MULTIPLEXED_AI_MATRIX_LOCAL_PYTHON_EXECUTABLE', profile)
        self.assertIn('AiHostedInvocation__EnableWorkerPolling", "true"', profile)
        self.assertIn('AiHostedInvocation__EnableLocalWorkerProfiles", "true"', profile)
        self.assertIn('AiKubernetesRuntimePoolHost:DeleteResourcesOnFailure"] = "false"', profile)
        self.assertIn('AiHostedInvocation__EnableDagReconciliation", "false"', profile)
        self.assertIn("PublicationOnlyRuntimes:0:Reference", profile)
        self.assertIn("matrix-kubernetes-python", profile)
        self.assertIn("SubmitMode = ProductionRuntimeSubmitMode.QueueFirst", profile)
        self.assertIn("submitMode = scenario.SubmitMode.ToString()", profile)
        self.assertIn("ConfigureReplaySafePayloadStore(settings)", profile)
        self.assertIn('AiEngine:PayloadStore:Provider"] = "mongo-redis"', profile)
        self.assertIn('AiEngine:PayloadStore:RequireReplaySafePayloads"] = "true"', profile)
        self.assertIn('AiEngine:PayloadStore:Mongo:ConnectionString"] = mongoConnectionString', profile)
        self.assertIn('AiEngine__PayloadStore__Mongo__ConnectionString", KubernetesSdkScenarioConstants.MongoConnectionString', profile)
        self.assertIn("IDictionary<string, string?> settings", profile)
        self.assertNotIn("IReadOnlyDictionary<string, string?> settings", profile)
        self.assertIn('AiKubernetesRuntimePoolHost:RuntimeHostAssemblyPath"] =', profile)
        self.assertNotIn("KubernetesSdkAiKubernetesRuntimePoolHostClient", profile)

    def test_hosted_invocation_registration_supports_publication_only_remote_runtime_without_new_transport(self) -> None:
        host = ROOT / "implementations" / "dotnet" / "src" / "Multiplexed.AI.McpServer.Host"
        registration = (host / "Bootstrap" / "HostedInvocationHostRegistration.cs").read_text(encoding="utf-8-sig")
        options = (host / "Configuration" / "AiHostedInvocationHostOptions.cs").read_text(encoding="utf-8-sig")
        self.assertIn("EnableWorkerPolling", options)
        self.assertIn("EnableDagReconciliation", options)
        self.assertIn("EnableLocalWorkerProfiles", options)
        self.assertIn("PublicationOnlyRuntimes", options)
        self.assertIn("CreatePublicationOnlyRuntimes", registration)
        self.assertIn("options.EnableLocalWorkerProfiles", registration)
        self.assertIn("Array.Empty<AiWorkerProcessProfile>()", registration)
        self.assertIn("if (options.EnableWorkerPolling)", registration)
        self.assertIn("if (options.EnableDagReconciliation)", registration)
        self.assertIn("AiPublicationEnvironmentArtifactKind.HostRuntime", registration)
        self.assertNotIn("KubernetesPool", registration)

    def test_matrix_scaleout_diagnostics_exposes_existing_watcher_and_store_only(self) -> None:
        host = ROOT / "implementations" / "dotnet" / "src" / "Multiplexed.AI.McpServer.Host"
        application = (host / "Bootstrap" / "ApplicationConfiguration.cs").read_text(encoding="utf-8-sig")
        self.assertIn('"/matrix/scaleout"', application)
        self.assertIn('"/matrix/publication-environment/{reference}"', application)
        self.assertIn("IAiPublicationEnvironmentCatalog", application)
        self.assertIn("IAiPublicationExecutionEnvironmentCatalog", application)
        self.assertIn("AiRuntimeScaleOutRequestWatcherHostedService", application)
        self.assertIn("IAiRuntimeScaleOutRequestStore", application)
        self.assertIn("watcherReady", application)
        self.assertIn("rejectionReason", application)
        self.assertNotIn("CreateRuntimePoolHostAsync", application)

    def test_kubernetes_runtime_pool_image_contains_existing_host_and_production_workers(self) -> None:
        dockerfile = (KUBERNETES / "runtime-pool.Dockerfile").read_text(encoding="utf-8-sig")
        self.assertIn("Multiplexed.AI.McpServer.Host.csproj", dockerfile)
        self.assertIn("Multiplexed.AI.HostedInvocation.DotNetWorker", dockerfile)
        self.assertIn("implementations/node/workers/hosted_invocation", dockerfile)
        self.assertIn("implementations/python/workers/hosted_invocation", dockerfile)
        self.assertIn('ENTRYPOINT ["dotnet", "/app/Multiplexed.AI.McpServer.Host.dll"]', dockerfile)
        self.assertNotIn("docker-cli", dockerfile)
        self.assertNotIn("docker.sock", dockerfile)

    def test_external_sdk_runner_requires_published_function_output_and_final_closure(self) -> None:
        runner = (KUBERNETES / "run-kubernetes-pool-sdk-execution.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("--require-output-marker", runner)
        self.assertIn("sdk_verifier.py", runner)
        self.assertIn("closure_verifier.py", runner)
        self.assertIn("runtime-pool.Dockerfile", runner)
        self.assertIn("minikube image load", runner)
        self.assertIn("run-kubernetes-pool-execution.ps1", runner)
        self.assertIn("refreshing the lightweight KubernetesPool execution sidecar automatically", runner)
        self.assertIn("FINAL CLOSURE PENDING", runner)
        self.assertNotIn("Prior KubernetesPool execution evidence is missing. Run run-kubernetes-pool-execution.ps1 before final closure.", runner)
        self.assertIn("HttpKubernetesPoolExternalSdkMatrixProfileTests.Http_KubernetesPool_Should_Write_ExternalSdk_Matrix_Profile", runner)
        self.assertNotIn("docker compose", runner.lower())
        self.assertNotIn("command -v", runner)
        self.assertNotIn('"--entrypoint", "sh"', runner)
        self.assertIn('"runtime_image_probe.py"', runner)
        self.assertIn('$imageDotnetExecutable = [string]$runtimeIdentity.dotnet.executablePath', runner)
        self.assertIn('$imageNodeExecutable = [string]$runtimeIdentity.typescript.executablePath', runner)
        self.assertIn('$imagePythonExecutable = [string]$runtimeIdentity.python.executablePath', runner)
        self.assertIn('$imagePythonSha256 = [string]$runtimeIdentity.python.sha256', runner)
        self.assertNotIn('$imagePythonExecutable = "/usr/local/bin/python3"', runner)
        self.assertNotIn(".ArgumentList", runner)
        self.assertNotIn('$startInfo.Environment["ASPNETCORE_URLS"]', runner)
        self.assertIn("ConvertTo-NativeProcessArgument", runner)
        self.assertIn("$startInfo.Arguments =", runner)
        self.assertIn('$startInfo.EnvironmentVariables["ASPNETCORE_URLS"]', runner)
        self.assertIn("Publishing publication-only control-plane host", runner)
        self.assertIn("Wait-ScaleOutWatcherReady", runner)
        self.assertIn("/matrix/scaleout", runner)
        self.assertIn("Start-NativeProcess", runner)
        self.assertIn("Verifying publication-only runtime catalog entry", runner)
        self.assertIn("/matrix/publication-environment/", runner)
        self.assertIn("--diagnostic-log", runner)
        self.assertIn("--terminal-timeout-seconds", runner)
        self.assertIn('"300"', runner)
        self.assertIn("AddSeconds(180)", runner)
        self.assertIn("AddSeconds(360)", runner)
        self.assertIn("Scale-out request:", runner)
        self.assertIn("EXTERNAL SDK CLIENT", runner)
        self.assertIn("if (@($failureScaleOutRequests).Count -eq 0)", runner)
        self.assertIn("No KubernetesPool Pod was created for current pool", runner)
        self.assertIn("Write-KubernetesPoolDiagnostics", runner)
        self.assertIn("Stop-ProcessTree", runner)
        self.assertNotIn("Kill($true)", runner)
        self.assertNotIn("MULTIPLEXED_AI_MATRIX_LOCAL_PYTHON_EXECUTABLE", runner)
        self.assertNotIn("MULTIPLEXED_AI_MATRIX_LOCAL_NODE_EXECUTABLE", runner)

    def test_readiness_diagnostics_are_scoped_and_collected_before_runner_cleanup(self) -> None:
        runner = (KUBERNETES / "run-kubernetes-pool-sdk-execution.ps1").read_text(encoding="utf-8-sig")
        diagnostics = runner.split("function Write-KubernetesPoolDiagnostics {", 1)[1].split("function Remove-PoolResources {", 1)[0]
        self.assertIn("Get-PoolResources -Namespace $Namespace -PoolId $PoolId", diagnostics)
        self.assertIn('"describe", "pod", $podName', diagnostics)
        self.assertIn('"logs", $podName', diagnostics)
        self.assertIn('"--all-containers=true"', diagnostics)
        self.assertIn('"scaleout.json"', diagnostics)
        self.assertIn('"resources.json"', diagnostics)
        self.assertNotIn('get events -n $Namespace', diagnostics)
        self.assertNotIn('get pods -n $Namespace', diagnostics)
        self.assertIn("RedirectStandardOutput = $true", runner)
        self.assertIn("RedirectStandardError = $true", runner)
        self.assertIn("WaitForExit(15000)", runner)
        self.assertIn("Remove-PoolResources -Namespace ([string]$profile.namespaceName)", runner)
        rejection = runner.split('if ($rejected.Count -gt 0)', 1)[-1]
        self.assertLess(rejection.index("Write-KubernetesPoolDiagnostics"), rejection.index('throw "KubernetesPool scale-out'))

    def test_matrix_context_seed_supplies_validated_ttl_before_persistence(self) -> None:
        host = ROOT / "implementations" / "dotnet" / "src" / "Multiplexed.AI.McpServer.Host"
        options = (host / "Configuration" / "AiMatrixHarnessOptions.cs").read_text(encoding="utf-8-sig")
        bootstrap = (host / "Bootstrap" / "MatrixHarnessBootstrapHostedService.cs").read_text(encoding="utf-8-sig")
        self.assertIn("ExecutionContextTtlSeconds { get; set; } = 3600;", options)
        self.assertIn("TtlSeconds = options.ExecutionContextTtlSeconds", bootstrap)
        self.assertIn("executionContextTtlSeconds = context.TtlSeconds", bootstrap)
        self.assertLess(bootstrap.index("if (!options.Enabled)"), bootstrap.index("if (options.ExecutionContextTtlSeconds <= 0)"))
        self.assertLess(bootstrap.index("if (options.ExecutionContextTtlSeconds <= 0)"), bootstrap.index("_contexts.StoreAsync(context)"))
        self.assertLess(bootstrap.index("TtlSeconds = options.ExecutionContextTtlSeconds"), bootstrap.index("_contexts.StoreAsync(context)"))

    def test_kubernetes_profile_sets_explicit_snapshot_ttl(self) -> None:
        profile = (DOTNET_TESTS / "Http" / "KubernetesPool" / "HttpKubernetesPoolExternalSdkMatrixProfileTests.cs").read_text(encoding="utf-8-sig")
        self.assertIn('settings["AiMatrixHarness:ExecutionContextTtlSeconds"] = "3600";', profile)
        self.assertNotIn('settings["AiKubernetesRuntimePoolInPod:SnapshotTtlSeconds"]', profile)

    def test_sdk_runner_requires_manifest_ttl_before_starting_client(self) -> None:
        runner = (KUBERNETES / "run-kubernetes-pool-sdk-execution.ps1").read_text(encoding="utf-8-sig")
        self.assertIn('$manifest.PSObject.Properties["executionContextTtlSeconds"]', runner)
        self.assertIn("$contextTtlSeconds -le 0", runner)
        self.assertIn("[int]::TryParse", runner)
        self.assertLess(runner.index("$contextTtlProperty ="), runner.index("$clientProcess = Start-NativeProcess"))
        self.assertIn("Execution context snapshot TTL=$($contextTtlSeconds)s", runner)

    def test_sdk_runner_keeps_checking_terminal_pods_after_first_observation(self) -> None:
        runner = (KUBERNETES / "run-kubernetes-pool-sdk-execution.ps1").read_text(encoding="utf-8-sig")
        loop = runner.split("while (-not $clientProcess.HasExited) {", 1)[1].split("if ($clientProcess.ExitCode -ne 0)", 1)[0]
        self.assertLess(loop.index("$currentResources = Get-PoolResources"), loop.index("if (-not $poolObserved)"))
        self.assertIn('$_.status.phase -eq "Failed" -or $_.status.phase -eq "Succeeded"', loop)
        terminal = loop.split("if ($terminalPods.Count -gt 0)", 1)[1]
        self.assertLess(terminal.index("Write-KubernetesPoolDiagnostics"), terminal.index('throw "KubernetesPool Pod terminated'))
        self.assertIn("-PoolId $poolId", terminal)

    def test_production_snapshot_ttl_guard_and_argument_projection_remain_strict(self) -> None:
        inpod = ROOT / "implementations" / "dotnet" / "src" / "Multiplexed.AI" / "Runtime" / "ControlPlane" / "RuntimeInstances" / "HostManager" / "Pool" / "Kubernetes" / "InPod"
        validator = (inpod / "AiKubernetesRuntimePoolInPodOptionsValidator.cs").read_text(encoding="utf-8-sig")
        factory = (inpod / "AiKubernetesRuntimePoolInPodCommandLineFactory.cs").read_text(encoding="utf-8-sig")
        self.assertIn("options.SnapshotTtlSeconds <= 0", validator)
        self.assertIn("request.ExecutionContextSnapshot.TtlSeconds", factory)
        self.assertNotIn("Math.Max", factory)

    def test_dotnet_regression_covers_seed_to_actual_kubernetes_validator(self) -> None:
        tests = ROOT / "implementations" / "dotnet" / "Tests" / "Multiplexed.AI.McpServer.Tests.Integration" / "Bootstrap" / "MatrixHarnessBootstrapHostedServiceTests.cs"
        source = tests.read_text(encoding="utf-8-sig")
        for required in (
            "MatrixHarnessBootstrapHostedService(",
            "McpRuntimeExecutionContextAccessor()",
            "AiKubernetesRuntimePoolInPodCommandLineFactory(host).Create(spec, request)",
            "AddCommandLine(arguments.ToArray())",
            "AiKubernetesRuntimePoolInPodOptionsValidator.Validate(options, requirePodUidFile: false)",
            "StartAsync_Should_Reject_Invalid_Context_Ttl_Before_Store_Or_Manifest",
            "StartAsync_Should_Not_Seed_Or_Validate_Disabled_Harness",
            "Kubernetes_Validator_Should_Still_Reject_Nonpositive_Snapshot_Ttl",
        ):
            self.assertIn(required, source)
        self.assertNotIn("KubernetesSdkAiKubernetesRuntimePoolHostClient", source)

    def test_external_python_sdk_client_emits_pre_engine_stage_diagnostics(self) -> None:
        client = (MATRIX / "clients" / "python" / "run.py").read_text(encoding="utf-8-sig")
        self.assertIn('parser.add_argument("--diagnostic-log")', client)
        self.assertIn('parser.add_argument("--terminal-timeout-seconds", type=float, default=90.0)', client)
        self.assertIn("args.terminal_timeout_seconds", client)
        self.assertIn('_diagnostic(args, "PUBLISH start")', client)
        self.assertIn('_diagnostic_failure(args, "PUBLISH", exception)', client)
        self.assertIn('_diagnostic(args, "SUBMIT start")', client)
        self.assertIn('_diagnostic_failure(args, "SUBMIT", exception)', client)
        self.assertIn('_diagnostic_failure(args, "OBSERVE", exception)', client)
        self.assertIn('_diagnostic_failure(args, "RESULT", exception)', client)

    def test_external_sdk_verifier_accepts_real_publication_marker_and_live_pool_evidence(self) -> None:
        import importlib.util

        spec = importlib.util.spec_from_file_location("kubernetes_pool_sdk_verifier", KUBERNETES / "sdk_verifier.py")
        self.assertIsNotNone(spec)
        self.assertIsNotNone(spec.loader)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)

        exact_image = "registry.example/runtime@sha256:" + "b" * 64
        with tempfile.TemporaryDirectory() as directory:
            directory_path = Path(directory)
            profile_path = directory_path / "profile.json"
            sdk_path = directory_path / "sdk.json"
            kube_path = directory_path / "kube.json"
            profile_path.write_text(
                json.dumps(
                    {
                        "schemaVersion": 1,
                        "submitMode": "QueueFirst",
                        "runtimeImage": exact_image,
                        "immutableRuntimeImage": True,
                        "poolId": "matrix-sdk-pool",
                        "namespaceName": "ai-runtime",
                        "publicationRuntime": {
                            "reference": "matrix-kubernetes-python",
                            "language": "python",
                            "version": "3.12.0",
                            "sha256": "c" * 64,
                        },
                        "authority": {
                            "controlPlaneWorkerPolling": False,
                            "controlPlaneDagReconciliation": True,
                            "runtimeWorkerPolling": True,
                            "runtimeDagReconciliation": False,
                        },
                    }
                ),
                encoding="utf-8",
            )
            sdk_path.write_text(
                json.dumps(
                    {
                        "schemaVersion": 1,
                        "scenarioId": "feature-runtime-provider-kubernetes-pool-external-sdk-python-worker",
                        "status": "passed",
                        "clientLanguage": "python",
                        "workerLanguage": "python",
                        "environmentRef": "matrix-kubernetes-python",
                        "topology": "kubernetes",
                        "runtimeProvider": "KubernetesPool",
                        "workerExecutionProvider": "TrustedProcess",
                        "terminalStatus": "Completed",
                        "outputMarkerVerified": True,
                        "evidence": ["publish", "submit", "observe", "terminal-result", "public-execution-id"],
                    }
                ),
                encoding="utf-8",
            )
            kube_path.write_text(
                json.dumps(
                    {
                        "schemaVersion": 1,
                        "scenarioId": "feature-runtime-provider-kubernetes-pool-external-sdk-python-worker",
                        "status": "passed",
                        "topology": "kubernetes",
                        "runtimeProvider": "KubernetesPool",
                        "scaleOutWatcherReady": True,
                        "runtimeImage": exact_image,
                        "poolId": "matrix-sdk-pool",
                        "namespaceName": "ai-runtime",
                        "podNames": ["pool-1"],
                        "readyPodNames": ["pool-1"],
                        "serviceNames": ["pool-1"],
                        "runtimeContainerImages": [exact_image],
                        "evidenceKinds": [
                            "external-sdk-publication",
                            "publication-only-runtime-identity",
                            "control-plane-worker-polling-disabled",
                            "runtime-worker-polling-enabled",
                            "scale-out-watcher-ready",
                            "kubernetes-runtime-pool-ready",
                            "published-function-terminal-result",
                        ],
                    }
                ),
                encoding="utf-8",
            )
            module.verify(
                KUBERNETES / "kubernetes-pool-external-sdk-v1.json",
                profile_path,
                sdk_path,
                kube_path,
                require_immutable_image=True,
            )

    def test_external_sdk_verifier_rejects_completed_result_without_uploaded_function_marker(self) -> None:
        verifier = (KUBERNETES / "sdk_verifier.py").read_text(encoding="utf-8-sig")
        client = (MATRIX / "clients" / "python" / "run.py").read_text(encoding="utf-8-sig")
        self.assertIn('sdk.get("outputMarkerVerified") is True', verifier)
        self.assertIn("--require-output-marker", client)
        self.assertIn("_contains_json_value(result.output, marker)", client)

    def test_final_closure_plan_is_explicitly_cross_topology_not_homogeneous(self) -> None:
        plan = json.loads((KUBERNETES / "kubernetes-pool-closure-v1.json").read_text(encoding="utf-8-sig"))
        self.assertEqual("kubernetes-pool-cross-topology-closure-v1", plan["matrixId"])
        self.assertEqual(37, plan["baseline"]["executedScenarioCount"])
        self.assertEqual(3, plan["kubernetes"]["executedScenarioCount"])
        self.assertEqual(40, plan["crossTopologyExecutedScenarioCount"])
        self.assertEqual(3, len(plan["kubernetes"]["scenarioIds"]))
        self.assertIn("does not represent a 40-scenario homogeneous topology matrix", plan["claimBoundary"])

    def test_final_closure_verifier_delegates_to_each_existing_kubernetes_evidence_authority(self) -> None:
        verifier = (KUBERNETES / "closure_verifier.py").read_text(encoding="utf-8-sig")
        self.assertIn('root / "verifier.py"', verifier)
        self.assertIn('root / "recovery_verifier.py"', verifier)
        self.assertIn('root / "sdk_verifier.py"', verifier)
        self.assertIn("len(baseline_ids) == 37", verifier)
        self.assertIn("3/3 KubernetesPool branch scenarios passed", verifier)
        self.assertIn("This is not a homogeneous 40-scenario topology matrix", verifier)


if __name__ == "__main__":
    unittest.main()
