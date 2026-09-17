from __future__ import annotations

from dataclasses import dataclass

from ...wire_model import AiSdkWireModel
from .publication_function_kind import AiSdkPublicationFunctionKind


@dataclass(frozen=True)
class AiSdkPublicationCallSite(AiSdkWireModel):
    kind: AiSdkPublicationFunctionKind
    step_name: str | None = None
    policy_index: int | None = None
    definition_path: str | None = None
