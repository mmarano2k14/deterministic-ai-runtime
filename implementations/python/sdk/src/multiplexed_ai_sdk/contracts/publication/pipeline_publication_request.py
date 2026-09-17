from __future__ import annotations

from dataclasses import dataclass

from ...wire_model import AiSdkWireModel
from ..common.schema_versions import AiSdkSchemaVersions
from ..pipelines.pipeline_definition import AiSdkPipelineDefinition
from .publication_function_upload import AiSdkPublicationFunctionUpload


@dataclass(frozen=True)
class AiSdkPipelinePublicationRequest(AiSdkWireModel):
    definition: AiSdkPipelineDefinition
    functions: tuple[AiSdkPublicationFunctionUpload, ...] = ()
    schema_version: int = AiSdkSchemaVersions.PIPELINE_PUBLICATION_REQUEST
