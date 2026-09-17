from __future__ import annotations

from dataclasses import dataclass
from typing import Protocol


@dataclass(frozen=True)
class AiSdkCredential:
    scheme: str
    value: str


class AiSdkCredentialProvider(Protocol):
    async def get_credential(self) -> AiSdkCredential | None:
        ...


@dataclass(frozen=True)
class AiSdkStaticCredentialProvider:
    credential: AiSdkCredential

    async def get_credential(self) -> AiSdkCredential:
        return self.credential
