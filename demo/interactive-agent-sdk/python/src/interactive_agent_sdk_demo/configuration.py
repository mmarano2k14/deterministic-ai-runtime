from __future__ import annotations

import os
from dataclasses import dataclass
from urllib.parse import urljoin, urlparse


def optional(name: str) -> str | None:
    value = os.getenv(name)
    if value is None:
        return None
    value = value.strip()
    return value or None


def required(name: str) -> str:
    value = optional(name)
    if value is None:
        raise RuntimeError(f"Missing required Python demo configuration '{name}'.")
    return value


def flag(name: str) -> bool:
    value = (optional(name) or "").lower()
    return value in {"1", "true", "yes", "on"}


def _absolute_http_url(name: str, value: str) -> str:
    parsed = urlparse(value)
    if parsed.scheme not in {"http", "https"} or not parsed.netloc:
        raise RuntimeError(f"{name} must be an absolute HTTP or HTTPS URL.")
    return value


@dataclass(frozen=True)
class DemoConfiguration:
    endpoint: str
    token: str | None
    access_context: str | None
    access_context_header: str
    access_context_endpoint: str
    openai_model: str
    verbose: bool

    @staticmethod
    def load() -> "DemoConfiguration":
        endpoint = _absolute_http_url(
            "AI_RUNTIME_ENDPOINT",
            required("AI_RUNTIME_ENDPOINT"),
        )
        configured_context_endpoint = optional("AI_RUNTIME_ACCESS_CONTEXT_ENDPOINT")
        access_context_endpoint = (
            _absolute_http_url(
                "AI_RUNTIME_ACCESS_CONTEXT_ENDPOINT",
                configured_context_endpoint,
            )
            if configured_context_endpoint is not None
            else urljoin(endpoint, "/auth/access-context")
        )

        return DemoConfiguration(
            endpoint=endpoint,
            token=optional("AI_RUNTIME_TOKEN"),
            access_context=optional("AI_RUNTIME_ACCESS_CONTEXT"),
            access_context_header=(
                optional("AI_RUNTIME_ACCESS_CONTEXT_HEADER")
                or "X-Access-Context"
            ),
            access_context_endpoint=access_context_endpoint,
            openai_model=required("OPENAI_MODEL"),
            verbose=flag("AI_DEMO_VERBOSE"),
        )


def is_smoke_mode() -> bool:
    return os.getenv("AI_DEMO_SMOKE") == "1"
