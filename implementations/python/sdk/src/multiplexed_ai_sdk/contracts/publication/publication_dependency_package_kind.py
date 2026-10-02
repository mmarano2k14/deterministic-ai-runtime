from enum import Enum


class AiSdkPublicationDependencyPackageKind(str, Enum):
    PYTHON_WHEEL_BUNDLE = "PythonWheelBundle"
    NODE_LOCKED_BUNDLE = "NodeLockedBundle"
    DOTNET_ASSEMBLY_CLOSURE = "DotNetAssemblyClosure"
