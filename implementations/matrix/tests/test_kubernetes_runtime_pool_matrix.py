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


if __name__ == "__main__":
    unittest.main()
