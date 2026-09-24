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
        print("SDK command: sdk.publish_pipeline")
        publication = await client.publish_pipeline(
            create_publication(config.openai_model)
        )
        print(f"PublicationRef: {publication.publication_ref}")
        print(f"Pipeline: {publication.pipeline_name}@{publication.pipeline_version}")
        print()

        print("SDK command: sdk.execution.submit")
        submission = await client.submit_execution(
            create_submission(publication.publication_ref, user_prompt)
        )
        print(f"ExecutionId: {submission.execution_id}")
        print(f"Initial status: {submission.status.value}")
        print()

        console = InteractiveExecutionConsole(
            client,
            submission.execution_id,
            WAITING_KEY,
            WAITING_STEP_NAME,
            input_pump,
        )
        result = await console.run()
        if result is None:
            print()
            print("Local console detached. The durable execution was not cancelled.")
            return 0

        print_terminal_result(result)
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
        print("Authentication bootstrap:")
        print(f"  POST {config.access_context_endpoint}")
        print("  Authorization: Bearer <redacted>")

        bootstrap = await AiSdkAccessContextBootstrapper.create(
            AiSdkAccessContextBootstrapOptions(
                endpoint=config.access_context_endpoint,
                credential_provider=credentials,
                access_context_header_name=config.access_context_header,
            )
        )
        access_context = bootstrap.access_context
        print(
            f"  Access context created via '{bootstrap.header_name}'. "
            "Handle not displayed."
        )
        print("  Subsequent handle rotation is managed by the SDK transport.")
        print()
    else:
        print(
            "Using the pre-provisioned AI_RUNTIME_ACCESS_CONTEXT. "
            "Subsequent rotation is managed by the SDK transport."
        )
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
