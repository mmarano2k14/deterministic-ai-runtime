from __future__ import annotations

from dataclasses import fields, is_dataclass
from enum import Enum
from types import UnionType
from typing import Any, TypeVar, Union, get_args, get_origin, get_type_hints

from .json_types import AiSdkJsonObject, AiSdkJsonValue

TWireModel = TypeVar("TWireModel", bound="AiSdkWireModel")


class AiSdkWireModel:
    def to_wire(self) -> AiSdkJsonObject:
        value = _encode(self)
        if not isinstance(value, dict):
            raise TypeError("SDK wire model must encode as a JSON object")
        return value

    @classmethod
    def from_wire(cls: type[TWireModel], value: AiSdkJsonObject) -> TWireModel:
        return _decode_dataclass(cls, value)


def _encode(value: Any) -> AiSdkJsonValue:
    if isinstance(value, Enum):
        return value.value
    if is_dataclass(value) and not isinstance(value, type):
        result: AiSdkJsonObject = {}
        for item in fields(value):
            field_value = getattr(value, item.name)
            if field_value is None:
                continue
            result[_to_camel(item.name)] = _encode(field_value)
        return result
    if isinstance(value, dict):
        return {str(key): _encode(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [_encode(item) for item in value]
    if value is None or isinstance(value, (bool, int, float, str)):
        return value
    raise TypeError(f"Unsupported SDK wire value type: {type(value).__name__}")


def _decode_dataclass(model_type: type[TWireModel], value: AiSdkJsonObject) -> TWireModel:
    if not isinstance(value, dict):
        raise TypeError(f"{model_type.__name__} requires a JSON object")

    type_hints = get_type_hints(model_type)
    kwargs: dict[str, Any] = {}
    for item in fields(model_type):
        wire_name = _to_camel(item.name)
        if wire_name not in value:
            continue
        raw = value[wire_name]
        if item.metadata.get("json") is True:
            kwargs[item.name] = raw
            continue
        kwargs[item.name] = _decode_value(type_hints[item.name], raw)
    return model_type(**kwargs)


def _decode_value(expected_type: Any, value: Any) -> Any:
    if expected_type is Any:
        return value

    origin = get_origin(expected_type)
    args = get_args(expected_type)

    if origin in (Union, UnionType):
        if value is None and type(None) in args:
            return None
        for candidate in args:
            if candidate is type(None):
                continue
            try:
                return _decode_value(candidate, value)
            except (TypeError, ValueError):
                continue
        return value

    if origin is list:
        if not isinstance(value, list):
            raise TypeError("Expected JSON array")
        item_type = args[0] if args else Any
        return [_decode_value(item_type, item) for item in value]

    if origin is tuple:
        if not isinstance(value, list):
            raise TypeError("Expected JSON array")
        item_type = args[0] if args else Any
        return tuple(_decode_value(item_type, item) for item in value)

    if origin is dict:
        if not isinstance(value, dict):
            raise TypeError("Expected JSON object")
        value_type = args[1] if len(args) > 1 else Any
        return {str(key): _decode_value(value_type, item) for key, item in value.items()}

    if isinstance(expected_type, type) and issubclass(expected_type, Enum):
        return expected_type(value)

    if isinstance(expected_type, type) and issubclass(expected_type, AiSdkWireModel):
        if not isinstance(value, dict):
            raise TypeError(f"{expected_type.__name__} requires a JSON object")
        return expected_type.from_wire(value)

    if expected_type in (str, int, float, bool):
        if type(value) is not expected_type:
            raise TypeError(f"Expected {expected_type.__name__}")
        return value

    return value


def _to_camel(name: str) -> str:
    head, *tail = name.split("_")
    return head + "".join(part[:1].upper() + part[1:] for part in tail)
