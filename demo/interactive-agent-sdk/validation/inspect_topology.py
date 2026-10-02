from __future__ import annotations

import argparse
import json

from topology import TopologyRequest, diagnose_local_docker


def main() -> int:
    parser = argparse.ArgumentParser(description="Inspect the current local Docker runtime topology without exposing secrets.")
    parser.add_argument("--runtime-provider", choices=("ProcessHostPool", "KubernetesPool"), required=True)
    parser.add_argument("--runtime-transport", choices=("HTTP", "gRPC"), required=True)
    args = parser.parse_args()

    result = diagnose_local_docker(TopologyRequest(args.runtime_provider, args.runtime_transport))
    observed = result["observed"]
    safe = {
        "requested": result["requested"],
        "observed": {
            "source": observed.get("source"),
            "runtimeProvider": observed.get("runtimeProvider"),
            "runtimeTransport": observed.get("runtimeTransport"),
            "hostMode": observed.get("hostMode"),
            "providerName": observed.get("providerName"),
            "scaleOutMode": observed.get("scaleOutMode"),
            "hostCreationMode": observed.get("hostCreationMode"),
            "service": observed.get("service"),
            "containerId": observed.get("containerId"),
        },
        "verified": False,
        "note": "Control-plane configuration is diagnostic only; Runtime Pool topology requires machine-generated topology-specific attestation."
    }
    print(json.dumps(safe, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
