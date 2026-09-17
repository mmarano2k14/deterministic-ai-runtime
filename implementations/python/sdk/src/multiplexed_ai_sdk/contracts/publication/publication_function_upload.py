from __future__ import annotations

from dataclasses import dataclass

from ...wire_model import AiSdkWireModel
from .publication_call_site import AiSdkPublicationCallSite
from .publication_dependency_upload import AiSdkPublicationDependencyUpload
from .publication_file_upload import AiSdkPublicationFileUpload


@dataclass(frozen=True)
class AiSdkPublicationFunctionUpload(AiSdkWireModel):
    site: AiSdkPublicationCallSite
    environment_ref: str
    entry_point_path: str
    entry_point_symbol: str
    sources: tuple[AiSdkPublicationFileUpload, ...] = ()
    dependencies: tuple[AiSdkPublicationDependencyUpload, ...] = ()
