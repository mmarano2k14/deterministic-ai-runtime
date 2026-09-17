from dataclasses import dataclass

from ...wire_model import AiSdkWireModel
from .publication_dependency_package_kind import AiSdkPublicationDependencyPackageKind


@dataclass(frozen=True)
class AiSdkPublicationDependencyPackage(AiSdkWireModel):
    schema_version: int
    kind: AiSdkPublicationDependencyPackageKind
    manifest_path: str
