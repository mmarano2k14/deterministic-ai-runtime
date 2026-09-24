from __future__ import annotations

import base64
import hashlib
import hmac
import json
import os
import sys
import time
import uuid


def required(name: str) -> str:
    value = os.getenv(name, "").strip()
    if not value:
        raise RuntimeError(f"Missing required environment variable: {name}")
    return value


def optional(name: str, default: str) -> str:
    value = os.getenv(name, "").strip()
    return value or default


def b64url(raw: bytes) -> str:
    return base64.urlsafe_b64encode(raw).rstrip(b"=").decode("ascii")


def encode_json(value: object) -> str:
    return b64url(
        json.dumps(
            value,
            separators=(",", ":"),
            sort_keys=True,
        ).encode("utf-8")
    )


def trn(project: str, namespace: str, resource: str, feature: str, action: str) -> str:
    return f"trn:{project}:{namespace}:{resource}:{feature}:{action}"


def main() -> int:
    issuer = required("AiMcpAuthentication__Issuer")
    audience = required("AiMcpAuthentication__Audience")
    signing_key = required("AiMcpAuthentication__SymmetricSigningKey")

    if len(signing_key.encode("utf-8")) < 32:
        raise RuntimeError(
            "AiMcpAuthentication__SymmetricSigningKey must contain at least 32 UTF-8 bytes."
        )

    ttl_seconds = int(optional("AI_DEMO_TOKEN_TTL_SECONDS", "3600"))
    if ttl_seconds <= 0 or ttl_seconds > 86400:
        raise RuntimeError("AI_DEMO_TOKEN_TTL_SECONDS must be between 1 and 86400.")

    project = optional("AI_DEMO_PROJECT", "rbac-demo")
    namespace = optional("AI_DEMO_NAMESPACE", "default")
    user_id = optional("AI_DEMO_USER_ID", "interactive-agent-demo")
    tenant_id = optional("AI_DEMO_TENANT_ID", "interactive-agent-tenant")
    tenant_group_id = optional(
        "AI_DEMO_TENANT_GROUP_ID",
        "interactive-agent-group",
    )

    capabilities = [
        trn(project, namespace, "code", "publication", "publish"),
        trn(project, namespace, "code", "publication", "read"),
        trn(project, namespace, "code", "publication", "execute"),
        trn(project, namespace, "shared-run", "execution", "submit"),
        trn(project, namespace, "execution", "control", "read"),
        trn(project, namespace, "execution", "control", "cancel"),
        trn(project, namespace, "execution", "control", "pause"),
        trn(project, namespace, "execution", "control", "resume"),
        trn(project, namespace, "execution", "control", "input"),
        trn(project, namespace, "replay", "execution", "run"),
    ]

    now = int(time.time())

    header = {
        "alg": "HS256",
        "typ": "JWT",
    }
    payload = {
        "iss": issuer,
        "aud": audience,
        "sub": user_id,
        "iat": now,
        "nbf": now - 1,
        "exp": now + ttl_seconds,
        "jti": uuid.uuid4().hex,
        "tenant_id": tenant_id,
        "tenant_group_id": tenant_group_id,
        "project": project,
        "namespace": namespace,
        "trn": capabilities,
    }

    encoded_header = encode_json(header)
    encoded_payload = encode_json(payload)
    signing_input = f"{encoded_header}.{encoded_payload}".encode("ascii")
    signature = hmac.new(
        signing_key.encode("utf-8"),
        signing_input,
        hashlib.sha256,
    ).digest()

    token = f"{encoded_header}.{encoded_payload}.{b64url(signature)}"
    print(token)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f"create-local-jwt: {exc}", file=sys.stderr)
        raise SystemExit(1)
