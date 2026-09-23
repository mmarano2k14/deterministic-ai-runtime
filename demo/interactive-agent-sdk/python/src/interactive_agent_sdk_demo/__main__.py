from __future__ import annotations

import os
from dataclasses import dataclass
from urllib.parse import urlparse

from multiplexed_ai_sdk import (
    AiSdkClient,
    AiSdkCredential,
    AiSdkMcpHttpTransport,
    AiSdkStaticCredentialProvider,
    AiSdkTransportOptions,
)


def _optional(name: str) -> str | None:
    value = os.getenv(name)
    if value is None:
        return None
    value = value.strip()
    return value or None


def _required(name: str) -> str:
    value = _optional(name)
    if value is None:
        raise RuntimeError(
            f"Missing required Python demo configuration '{name}'."
        )
    return value


@dataclass(frozen=True)
class DemoConfiguration:
    endpoint: str
    environment_ref: str
    token: str | None
    access_context: str | None
    access_context_header: str
    openai_key_configured: bool
    openai_model_configured: bool

    @staticmethod
    def load() -> "DemoConfiguration":
        endpoint = _required("AI_RUNTIME_ENDPOINT")
        parsed = urlparse(endpoint)
        if parsed.scheme not in {"http", "https"} or not parsed.netloc:
            raise RuntimeError(
                "AI_RUNTIME_ENDPOINT must be an absolute HTTP or HTTPS URL."
            )

        return DemoConfiguration(
            endpoint=endpoint,
            environment_ref=_required("AI_RUNTIME_PYTHON_ENVIRONMENT_REF"),
            token=_optional("AI_RUNTIME_TOKEN"),
            access_context=_optional("AI_RUNTIME_ACCESS_CONTEXT"),
            access_context_header=(
                _optional("AI_RUNTIME_ACCESS_CONTEXT_HEADER")
                or "X-Access-Context"
            ),
            openai_key_configured=_optional("OPENAI_API_KEY") is not None,
            openai_model_configured=_optional("OPENAI_MODEL") is not None,
        )


def main() -> int:
    config = DemoConfiguration.load()

    provider = (
        AiSdkStaticCredentialProvider(
            AiSdkCredential("Bearer", config.token)
        )
        if config.token
        else None
    )

    client = AiSdkClient(
        AiSdkMcpHttpTransport(
            config.endpoint,
            AiSdkTransportOptions(
                credential_provider=provider,
                additional_headers=(
                    {config.access_context_header: config.access_context}
                    if config.access_context
                    else None
                ),
            ),
        )
    )

    _ = client

    print("Interactive Agent SDK Demo")
    print("SDK: Python")
    print(f"Runtime endpoint: {config.endpoint}")
    print(f"Environment ref: {config.environment_ref}")
    print(f"Bearer token configured: {config.token is not None}")
    print(f"Access context configured: {config.access_context is not None}")
    print(f"OpenAI key configured: {config.openai_key_configured}")
    print(f"OpenAI model configured: {config.openai_model_configured}")
    print()
    print("External SDK consumer initialized.")
    print("No runtime request is sent by the scaffold increment.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
