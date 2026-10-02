from enum import Enum


class AiSdkInvocationKind(str, Enum):
    NATIVE = "Native"
    CUSTOM = "Custom"
    MCP = "Mcp"
