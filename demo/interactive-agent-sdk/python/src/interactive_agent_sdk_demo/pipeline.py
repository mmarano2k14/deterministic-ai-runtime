from __future__ import annotations

from uuid import uuid4

from multiplexed_ai_sdk import (
    AiSdkExecutionMode,
    AiSdkExecutionSubmissionRequest,
    AiSdkPipelineDefinition,
    AiSdkPipelinePublicationRequest,
    AiSdkPipelineStepDefinition,
)

WAITING_KEY = "interactive-agent-review"
WAITING_STEP_NAME = "await-review"

_ROOT_PIPELINE_NAME = "interactive-agent-sdk-python"
_ROOT_PIPELINE_VERSION = "1"
_CHILD_PIPELINE_NAME = "interactive-agent-sdk-python-analysis"
_CHILD_PIPELINE_VERSION = "1"


def create_publication(openai_model: str) -> AiSdkPipelinePublicationRequest:
    if not openai_model.strip():
        raise ValueError("OPENAI_MODEL must not be empty.")

    return AiSdkPipelinePublicationRequest(
        definition=AiSdkPipelineDefinition(
            name=_ROOT_PIPELINE_NAME,
            version=_ROOT_PIPELINE_VERSION,
            execution_mode=AiSdkExecutionMode.DAG,
            steps=(
                _prompt_step(
                    name="plan",
                    order=0,
                    depends_on=(),
                    model=openai_model,
                    template="""You are the root planning agent in a deterministic AI runtime.

The public execution request is supplied as JSON:
{{requestJson}}

Read the \"userPrompt\" field from that JSON.

Produce a concise planning note containing:
1. the user's actual objective,
2. a short plan,
3. one bounded analysis task to delegate to a child agent,
4. important assumptions or risks.

Do not answer the user's request yet.""",
                    input_bindings={"requestJson": "state.input"},
                ),
                AiSdkPipelineStepDefinition(
                    name="delegate-analysis",
                    step_key="execution.child-dag",
                    order=1,
                    depends_on=("plan",),
                    input={
                        "requestJson": "state.input",
                        "rootPlan": "steps.plan.result.data.value",
                    },
                    config={
                        "childDagId": _CHILD_PIPELINE_NAME,
                        "childDagVersion": _CHILD_PIPELINE_VERSION,
                        "logicalInvocationKey": "interactive-agent-analysis",
                        "childDagDefinition": _create_embedded_child_definition(openai_model),
                    },
                ),
                AiSdkPipelineStepDefinition(
                    name=WAITING_STEP_NAME,
                    step_key="execution.await-input",
                    order=2,
                    depends_on=("delegate-analysis",),
                    config={
                        "waitingKey": WAITING_KEY,
                        "reason": (
                            "Review the root plan and delegated child analysis before "
                            "final response generation."
                        ),
                    },
                ),
                _prompt_step(
                    name="final-answer",
                    order=3,
                    depends_on=(WAITING_STEP_NAME,),
                    model=openai_model,
                    template="""You are the root agent producing the final response.

The original public execution request is JSON:
{{requestJson}}

Root planning note:
{{rootPlan}}

Delegated child analysis:
{{childAnalysis}}

Human approval:
{{approved}}

Human feedback:
{{feedback}}

Read the \"userPrompt\" field from the request JSON.

If approval is false, do not present the proposed work as approved. Briefly explain
that the proposal was rejected and incorporate the feedback.

If approval is true, answer the user's original request directly. Use the root plan,
the independent child analysis, and the human feedback. Keep the response focused.""",
                    input_bindings={
                        "requestJson": "state.input",
                        "rootPlan": "steps.plan.result.data.value",
                        "childAnalysis": "steps.delegate-analysis.result.payload.data.result",
                        "approved": f"steps.{WAITING_STEP_NAME}.result.value.approved",
                        "feedback": f"steps.{WAITING_STEP_NAME}.result.value.feedback",
                    },
                ),
                AiSdkPipelineStepDefinition(
                    name="publish-result",
                    step_key="execution.publish-result",
                    order=4,
                    depends_on=("final-answer",),
                    config={"source": "steps.final-answer.result.data.value"},
                ),
            ),
        ),
        functions=(),
    )


def create_submission(
    publication_ref: str,
    user_prompt: str,
) -> AiSdkExecutionSubmissionRequest:
    if not publication_ref.strip():
        raise ValueError("publication_ref must not be empty.")
    if not user_prompt.strip():
        raise ValueError("user_prompt must not be empty.")

    request_id = uuid4().hex
    return AiSdkExecutionSubmissionRequest(
        publication_ref=publication_ref,
        idempotency_key=f"interactive-agent-python-{request_id}",
        correlation_id=f"interactive-agent-python-{request_id}",
        input={"userPrompt": user_prompt, "requestId": request_id},
        metadata={
            "demo": "interactive-agent-sdk",
            "sdk.language": "python",
        },
    )


def _prompt_step(
    *,
    name: str,
    order: int,
    depends_on: tuple[str, ...],
    model: str,
    template: str,
    input_bindings: dict[str, str],
) -> AiSdkPipelineStepDefinition:
    return AiSdkPipelineStepDefinition(
        name=name,
        step_key="ai.prompt",
        order=order,
        depends_on=depends_on,
        input=input_bindings,
        config={
            "provider": "openai",
            "model": model,
            "template": template,
            "promptVersion": "interactive-agent-sdk-v1",
        },
    )


def _create_embedded_child_definition(openai_model: str) -> dict[str, object]:
    # Child DAG definitions are native runtime payloads carried through the public
    # pipeline contract, so preserve the runtime's canonical property casing here.
    return {
        "Name": _CHILD_PIPELINE_NAME,
        "Version": _CHILD_PIPELINE_VERSION,
        "ExecutionMode": "Dag",
        "Steps": [
            {
                "Name": "child-analysis",
                "StepKey": "ai.prompt",
                "Order": 0,
                "DependsOn": [],
                "Input": {
                    "requestJson": "state.requestJson",
                    "rootPlan": "state.rootPlan",
                },
                "Config": {
                    "provider": "openai",
                    "model": openai_model,
                    "template": """You are a bounded delegated child agent.

The original public execution request is JSON:
{{requestJson}}

Root planning note:
{{rootPlan}}

Read the \"userPrompt\" field from the request JSON.

Independently analyze the bounded delegated task implied by the root plan.
Check assumptions, identify a useful correction or confirmation, and return
concise evidence or reasoning that the parent should consider.

Do not delegate again and do not ask for human input.""",
                    "promptVersion": "interactive-agent-sdk-child-v1",
                },
            },
            {
                "Name": "publish-child-result",
                "StepKey": "execution.publish-result",
                "Order": 1,
                "DependsOn": ["child-analysis"],
                "Input": {},
                "Config": {
                    "source": "steps.child-analysis.result.data.value",
                },
            },
        ],
        "Config": {},
    }
