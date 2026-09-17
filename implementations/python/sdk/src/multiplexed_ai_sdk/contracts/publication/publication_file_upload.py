from dataclasses import dataclass

from ...wire_model import AiSdkWireModel


@dataclass(frozen=True)
class AiSdkPublicationFileUpload(AiSdkWireModel):
    path: str
    content_base64: str
