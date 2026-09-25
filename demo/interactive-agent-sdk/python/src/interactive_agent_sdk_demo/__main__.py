from __future__ import annotations

import asyncio

from multiplexed_ai_sdk import (
    AiSdkAccessContextBootstrapOptions,
    AiSdkAccessContextBootstrapper,
    AiSdkClient,
    AiSdkCredential,
    AiSdkMcpHttpTransport,
    AiSdkStaticCredentialProvider,
    AiSdkTransportOptions,
)

from .configuration import DemoConfiguration, is_smoke_mode
from .interactive_console import (
    ConsoleInputPump,
    InteractiveExecutionConsole,
    print_terminal_result,
)
from .pipeline import (
    WAITING_KEY,
    WAITING_STEP_NAME,
    create_publication,
    create_submission,
)


async def _main() -> int:
    config = DemoConfiguration.load()

    if is_smoke_mode():
        print("Interactive Agent SDK Demo")
        print("SDK: Python")
        print(f"Runtime endpoint: {config.endpoint}")
        print(f"OpenAI model configured: {bool(config.openai_model.strip())}")
        print("External SDK consumer initialized.")
        print("Smoke mode: no authentication/bootstrap/runtime request was sent.")
        return 0

    _print_header(config)
    client = await _create_client(config)
    input_pump = ConsoleInputPump()

    try:
        user_prompt = (await input_pump.prompt("User request: ") or "").strip()
        if not user_prompt:
            print("A non-empty user request is required.")
            return 2

        print()
        if config.verbose:
            print("SDK command: sdk.publish_pipeline")
        else:
            print("[>] Publishing immutable pipeline")
        publication = await client.publish_pipeline(
            create_publication(config.openai_model)
        )
        if config.verbose:
            print(f"PublicationRef: {publication.publication_ref}")
            print(f"Pipeline: {publication.pipeline_name}@{publication.pipeline_version}")
        else:
            print(
                f"[OK] Published "
                f"{publication.pipeline_name}@{publication.pipeline_version}"
            )
        print()

        if config.verbose:
            print("SDK command: sdk.execution.submit")
        else:
            print("[>] Submitting durable execution")
        submission = await client.submit_execution(
            create_submission(publication.publication_ref, user_prompt)
        )
        print(f"ExecutionId: {submission.execution_id}")
        if config.verbose:
            print(f"Initial status: {submission.status.value}")
        print()

        console = InteractiveExecutionConsole(
            client,
            submission.execution_id,
            WAITING_KEY,
            WAITING_STEP_NAME,
            input_pump,
            config.verbose,
        )
        result = await console.run()
        if result is None:
            print()
            print("Local console detached. The durable execution was not cancelled.")
            return 0

        print_terminal_result(
            result,
            console.last_observation,
            config.openai_model,
            console.review_approved,
            console.review_feedback,
        )
        await console.run_post_terminal_commands(result.status)
        return 0 if result.status.value == "Completed" else 1
    finally:
        input_pump.close()


async def _create_client(config: DemoConfiguration) -> AiSdkClient:
    if not config.token:
        raise RuntimeError(
            "AI_RUNTIME_TOKEN is required for the standalone authenticated Python demo."
        )

    credentials = AiSdkStaticCredentialProvider(
        AiSdkCredential("Bearer", config.token)
    )

    access_context = config.access_context
    if access_context is None:
        if config.verbose:
            print("Authentication bootstrap:")
            print(f"  POST {config.access_context_endpoint}")
            print("  Authorization: Bearer <redacted>")
        else:
            print("[>] Creating RBAC access context from JWT claims")

        bootstrap = await AiSdkAccessContextBootstrapper.create(
            AiSdkAccessContextBootstrapOptions(
                endpoint=config.access_context_endpoint,
                credential_provider=credentials,
                access_context_header_name=config.access_context_header,
            )
        )
        access_context = bootstrap.access_context
        if config.verbose:
            print(
                f"  Access context created via '{bootstrap.header_name}'. "
                "Handle not displayed."
            )
            print("  Subsequent handle rotation is managed by the SDK transport.")
            print()
        else:
            print(
                f"[OK] RBAC access context created; "
                f"{bootstrap.header_name} rotation enabled"
            )
            print()
    elif config.verbose:
        print(
            "Using the pre-provisioned AI_RUNTIME_ACCESS_CONTEXT. "
            "Subsequent rotation is managed by the SDK transport."
        )
        print()
    else:
        print("[OK] Using pre-provisioned RBAC access context; rotation enabled")
        print()

    return AiSdkClient(
        AiSdkMcpHttpTransport(
            config.endpoint,
            AiSdkTransportOptions(
                credential_provider=credentials,
                access_context_header_name=config.access_context_header,
                additional_headers={
                    config.access_context_header: access_context,
                },
            ),
        )
    )


def _print_header(config: DemoConfiguration) -> None:
    print("==================================================")
    print(" Deterministic AI Runtime - Interactive SDK Agent")
    print("==================================================")
    print()
    print("SDK: Python")
    print(f"Runtime endpoint: {config.endpoint}")
    print(f"OpenAI model: {config.openai_model}")
    print(f"Console mode: {'verbose' if config.verbose else 'presentation'}")
    print()
    print(
        "Runtime authentication uses a Bearer JWT plus a server-created RBAC access context."
    )
    print(
        "OpenAI authentication stays on the runtime host. "
        "The external SDK does not send OPENAI_API_KEY."
    )
    print()


def main() -> int:
    return asyncio.run(_main())


if __name__ == "__main__":
    raise SystemExit(main())
