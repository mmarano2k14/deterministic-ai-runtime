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


if __name__ == "__main__":
    unittest.main()
