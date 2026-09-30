from __future__ import annotations

import unittest

from attest_kubernetespool import build_attestation_from_documents


class KubernetesPoolAttestationTests(unittest.TestCase):
    def _profile(self) -> dict[str, object]:
        return {
            "poolId": "pool-123",
            "namespaceName": "ai-runtime",
            "runtimeImage": "multiplexed-ai-runtime:matrix-sdk",
        }

    def _pods(self, *, ready: bool = True, image: str = "multiplexed-ai-runtime:matrix-sdk") -> dict[str, object]:
        return {
            "items": [
                {
                    "metadata": {
                        "name": "runtime-pool-pod",
                        "annotations": {
                            "multiplexed.ai/pool-id": "pool-123",
                            "multiplexed.ai/runtime-instance-count": "3",
                        },
                    },
                    "spec": {"containers": [{"name": "runtime-pool", "image": image}]},
                    "status": {
                        "phase": "Running",
                        "conditions": [{"type": "Ready", "status": "True" if ready else "False"}],
                    },
                }
            ]
        }

    def _services(self) -> dict[str, object]:
        return {
            "items": [
                {
                    "metadata": {
                        "name": "runtime-pool-service",
                        "annotations": {"multiplexed.ai/pool-id": "pool-123"},
                    }
                }
            ]
        }

    def test_verified_live_pool(self) -> None:
        value = build_attestation_from_documents(self._profile(), self._pods(), self._services(), "HTTP")
        self.assertTrue(value["verified"])
        self.assertEqual("kubernetes-cluster-inspection", value["observed"]["source"])
        self.assertEqual(3, value["observed"]["runtimeInstanceCount"])

    def test_not_ready_fails_closed(self) -> None:
        value = build_attestation_from_documents(self._profile(), self._pods(ready=False), self._services(), "HTTP")
        self.assertFalse(value["verified"])

    def test_wrong_image_fails_closed(self) -> None:
        value = build_attestation_from_documents(self._profile(), self._pods(image="wrong:image"), self._services(), "HTTP")
        self.assertFalse(value["verified"])

    def test_missing_service_fails_closed(self) -> None:
        value = build_attestation_from_documents(self._profile(), self._pods(), {"items": []}, "HTTP")
        self.assertFalse(value["verified"])


if __name__ == "__main__":
    unittest.main()
