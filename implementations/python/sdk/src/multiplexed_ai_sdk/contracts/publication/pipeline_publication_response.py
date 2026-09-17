from dataclasses import dataclass

from ...wire_model import AiSdkWireModel
from ..common.schema_versions import AiSdkSchemaVersions


@dataclass(frozen=True)
class AiSdkPipelinePublicationResponse(AiSdkWireModel):
    publication_ref: str
    publication_sha256: str
    pipeline_name: str
    pipeline_version: str
    schema_version: int = AiSdkSchemaVersions.PIPELINE_PUBLICATION_RESPONSE
