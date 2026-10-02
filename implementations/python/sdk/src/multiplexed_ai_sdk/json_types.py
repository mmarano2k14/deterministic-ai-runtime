from __future__ import annotations

from typing import TypeAlias

AiSdkJsonScalar: TypeAlias = None | bool | int | float | str
AiSdkJsonValue: TypeAlias = AiSdkJsonScalar | list["AiSdkJsonValue"] | dict[str, "AiSdkJsonValue"]
AiSdkJsonObject: TypeAlias = dict[str, AiSdkJsonValue]
