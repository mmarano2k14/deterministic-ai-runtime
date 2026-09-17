from __future__ import annotations

from dataclasses import dataclass

from ...wire_model import AiSdkWireModel
from .publication_dependency_package import AiSdkPublicationDependencyPackage
from .publication_file_upload import AiSdkPublicationFileUpload


@dataclass(frozen=True)
class AiSdkPublicationDependencyUpload(AiSdkWireModel):
    name: str
    version: str
    files: tuple[AiSdkPublicationFileUpload, ...] = ()
    package: AiSdkPublicationDependencyPackage | None = None
