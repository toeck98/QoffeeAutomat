"""QoffeeLink v1 protocol models and parsers (PLAN.md S07, S10).

This module has no Home Assistant imports so that it can become part of a PyPI library
together with ``client.py``. It turns decoded JSON into typed, immutable objects:

* ``parse_frame`` and ``parse_message`` handle the frames that an agent sends
  (``hello``, ``manifest_changed``, ``state``, ``event``, ``bye``, ``result``). Unknown
  message types become :class:`UnknownMessage`; unknown fields are ignored.
* ``parse_manifest`` and ``parse_pair_response`` parse the manifest and the pairing
  response; ``parse_run_result_data`` parses the data of a successful ``run`` result.
* Data that violates the schemas in ``protocol/schema`` raises :class:`ProtocolError`.
* The ``build_*`` functions create the frames that a client sends.

The schemas and fixtures in ``protocol/`` are normative; both this module and the C#
contracts are tested against the same fixtures.
"""

import json
import re
from collections.abc import Mapping, Sequence
from dataclasses import dataclass, field
from typing import Any, Final, NoReturn

PROTOCOL_VERSION: Final = 1
SUBPROTOCOL: Final = "qoffeelink.v1"

# Error codes (PLAN.md S07). Receivers tolerate unknown codes.
ERROR_UNAUTHORIZED: Final = "unauthorized"
ERROR_UNSUPPORTED_VERSION: Final = "unsupported_version"
ERROR_BAD_REQUEST: Final = "bad_request"
ERROR_UNKNOWN_COMMAND: Final = "unknown_command"
ERROR_COMMAND_DISABLED: Final = "command_disabled"
ERROR_INVALID_VARS: Final = "invalid_vars"
ERROR_BUSY: Final = "busy"
ERROR_COOLDOWN: Final = "cooldown"
ERROR_NO_SESSION: Final = "no_session"
ERROR_ELEVATION_UNAVAILABLE: Final = "elevation_unavailable"
ERROR_CANCELLED: Final = "cancelled"
ERROR_TIMEOUT: Final = "timeout"
ERROR_PAUSED: Final = "paused"
ERROR_INTERNAL: Final = "internal"
ERROR_CODES: Final = frozenset(
    {
        ERROR_UNAUTHORIZED,
        ERROR_UNSUPPORTED_VERSION,
        ERROR_BAD_REQUEST,
        ERROR_UNKNOWN_COMMAND,
        ERROR_COMMAND_DISABLED,
        ERROR_INVALID_VARS,
        ERROR_BUSY,
        ERROR_COOLDOWN,
        ERROR_NO_SESSION,
        ERROR_ELEVATION_UNAVAILABLE,
        ERROR_CANCELLED,
        ERROR_TIMEOUT,
        ERROR_PAUSED,
        ERROR_INTERNAL,
    }
)

# Close codes (PLAN.md S07, D-039).
CLOSE_BAD_REQUEST: Final = 4400
CLOSE_UNAUTHORIZED: Final = 4401
CLOSE_UNSUPPORTED_VERSION: Final = 4406

# Closed enums of protocol version 1.
BYE_REASONS: Final = frozenset(
    {"sleep", "hibernate", "shutdown", "restart", "service_stop", "update"}
)
CLIENT_KINDS: Final = frozenset({"homeassistant", "hub", "tool"})
METRIC_KINDS: Final = frozenset({"sensor", "binary_sensor"})
VALUE_TYPES: Final = frozenset({"number", "string", "bool", "timestamp", "enum"})
METRIC_CONTEXTS: Final = frozenset({"system", "user"})
VARIABLE_TYPES: Final = frozenset({"string", "int", "float", "bool", "enum"})
CONTROL_TYPES: Final = frozenset(
    {"none", "button", "switch", "select", "number", "text"}
)

_REV = re.compile(r"^[0-9a-f]{12}$")
_METRIC_ID = re.compile(r"^[a-z][a-z0-9_]*(\.[a-z0-9_-]+)*$")
_COMMAND_ID = re.compile(r"^[a-z][a-z0-9_]{0,62}$")
_VARIABLE_NAME = re.compile(r"^[A-Za-z][A-Za-z0-9_]{0,31}$")
_MAC = re.compile(r"^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$")
_TOKEN = re.compile(r"^[A-Za-z0-9_-]{43}$")
_ERROR_CODE = re.compile(r"^[a-z][a-z0-9_]*$")

type JsonObject = Mapping[str, Any]
type Scalar = str | int | float | bool
type MetricValue = str | int | float | bool | None


class ProtocolError(Exception):
    """Raised when data does not conform to QoffeeLink v1."""


# ---------------------------------------------------------------------------
# Models
# ---------------------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class AgentInfo:
    """Identity of an agent (``hello.agent`` and ``manifest.agent``)."""

    id: str
    name: str
    version: str
    os: str
    arch: str
    hostname: str
    macs: tuple[str, ...]
    os_version: str | None = None
    ui_url: str | None = None


@dataclass(frozen=True, slots=True)
class ProtocolRange:
    """The protocol versions an agent supports."""

    min: int
    max: int


@dataclass(frozen=True, slots=True)
class Hello:
    """First frame of every connection, sent by the agent."""

    protocol: ProtocolRange
    agent: AgentInfo
    manifest_rev: str
    boot_id: str
    features: tuple[str, ...] = ()


@dataclass(frozen=True, slots=True)
class ManifestChanged:
    """The manifest revision changed."""

    rev: str


@dataclass(frozen=True, slots=True)
class State:
    """Changed metric values; ``None`` means unavailable."""

    ts: int
    values: Mapping[str, MetricValue]


@dataclass(frozen=True, slots=True)
class Event:
    """Something happened on the PC."""

    type: str
    ts: int
    data: Mapping[str, Any] = field(default_factory=dict)


@dataclass(frozen=True, slots=True)
class Bye:
    """The agent goes away deliberately."""

    reason: str


@dataclass(frozen=True, slots=True)
class ErrorInfo:
    """The error of a failed request."""

    code: str
    message: str


@dataclass(frozen=True, slots=True)
class Result:
    """Answer to a request: ``data`` when ``ok``, ``error`` otherwise."""

    id: int
    ok: bool
    data: Mapping[str, Any] | None = None
    error: ErrorInfo | None = None


@dataclass(frozen=True, slots=True)
class UnknownMessage:
    """A frame with a message type this version does not know; it is to be ignored."""

    type: str
    raw: Mapping[str, Any]


type Message = Hello | ManifestChanged | State | Event | Bye | Result | UnknownMessage


@dataclass(frozen=True, slots=True)
class MetricDescriptor:
    """A metric announced in the manifest."""

    id: str
    name: str
    kind: str
    value_type: str
    translation_key: str | None = None
    unit: str | None = None
    device_class: str | None = None
    state_class: str | None = None
    entity_category: str | None = None
    icon: str | None = None
    precision: int | None = None
    options: tuple[str, ...] | None = None
    enabled_default: bool = True
    interval_seconds: float | None = None
    deadband: float | None = None
    context: str | None = None


@dataclass(frozen=True, slots=True)
class VariableDescriptor:
    """A variable of a command."""

    name: str
    type: str
    required: bool = False
    default: Scalar | None = None
    min: float | None = None
    max: float | None = None
    step: float | None = None
    pattern: str | None = None
    max_length: int | None = None
    options: tuple[str, ...] | None = None
    secret: bool = False
    unit: str | None = None
    description: str | None = None


@dataclass(frozen=True, slots=True)
class ControlDescriptor:
    """The Home Assistant control of a command."""

    type: str
    var: str | None = None
    state_metric: str | None = None


@dataclass(frozen=True, slots=True)
class CommandDescriptor:
    """A command announced in the manifest."""

    id: str
    name: str
    icon: str | None = None
    description: str | None = None
    returns_output: bool = False
    vars: tuple[VariableDescriptor, ...] = ()
    control: ControlDescriptor | None = None


@dataclass(frozen=True, slots=True)
class EventDescriptor:
    """An event type that the agent can raise."""

    type: str
    name: str


@dataclass(frozen=True, slots=True)
class Manifest:
    """What the agent offers."""

    rev: str
    agent: AgentInfo
    metrics: tuple[MetricDescriptor, ...]
    commands: tuple[CommandDescriptor, ...]
    events: tuple[EventDescriptor, ...] = ()
    features: tuple[str, ...] = ()


@dataclass(frozen=True, slots=True)
class PairResponse:
    """Response of a successful pairing request."""

    token: str
    agent_id: str
    agent_name: str


@dataclass(frozen=True, slots=True)
class RunResultData:
    """The data of a successful ``run`` result."""

    run_id: str
    exit_code: int | None = None
    stdout: str | None = None
    stderr: str | None = None
    duration_ms: int | None = None
    timed_out: bool | None = None


# ---------------------------------------------------------------------------
# Field helpers
# ---------------------------------------------------------------------------


def _fail(where: str, problem: str) -> NoReturn:
    raise ProtocolError(f"{where}: {problem}")


def _object(value: Any, where: str) -> JsonObject:
    if not isinstance(value, Mapping):
        _fail(where, "expected an object")
    return value


def _present(obj: JsonObject, key: str) -> bool:
    """A member counts as absent when it is missing or null."""
    return obj.get(key) is not None


def _str_value(
    value: Any,
    where: str,
    *,
    pattern: re.Pattern[str] | None = None,
    allowed: frozenset[str] | None = None,
    non_empty: bool = True,
) -> str:
    if not isinstance(value, str):
        _fail(where, "expected a string")
    if non_empty and not value:
        _fail(where, "must not be empty")
    if pattern is not None and not pattern.search(value):
        _fail(where, f"does not match {pattern.pattern}")
    if allowed is not None and value not in allowed:
        _fail(where, f"must be one of {sorted(allowed)}")
    return value


def _int_value(value: Any, where: str, *, minimum: int | None = None) -> int:
    if isinstance(value, bool) or not isinstance(value, int):
        _fail(where, "expected an integer")
    if minimum is not None and value < minimum:
        _fail(where, f"must be at least {minimum}")
    return value


def _number_value(value: Any, where: str, *, minimum: float | None = None) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        _fail(where, "expected a number")
    if minimum is not None and value < minimum:
        _fail(where, f"must be at least {minimum}")
    return value


def _bool_value(value: Any, where: str) -> bool:
    if not isinstance(value, bool):
        _fail(where, "expected a boolean")
    return value


def _require(obj: JsonObject, key: str, where: str) -> Any:
    if obj.get(key) is None:
        _fail(where, f"missing required field {key!r}")
    return obj[key]


def _req_str(obj: JsonObject, key: str, where: str, **options: Any) -> str:
    return _str_value(_require(obj, key, where), f"{where}/{key}", **options)


def _opt_str(obj: JsonObject, key: str, where: str, **options: Any) -> str | None:
    if not _present(obj, key):
        return None
    return _str_value(obj[key], f"{where}/{key}", **options)


def _req_int(obj: JsonObject, key: str, where: str, **options: Any) -> int:
    return _int_value(_require(obj, key, where), f"{where}/{key}", **options)


def _opt_int(obj: JsonObject, key: str, where: str, **options: Any) -> int | None:
    if not _present(obj, key):
        return None
    return _int_value(obj[key], f"{where}/{key}", **options)


def _opt_number(obj: JsonObject, key: str, where: str, **options: Any) -> float | None:
    if not _present(obj, key):
        return None
    return _number_value(obj[key], f"{where}/{key}", **options)


def _req_bool(obj: JsonObject, key: str, where: str) -> bool:
    return _bool_value(_require(obj, key, where), f"{where}/{key}")


def _opt_bool(obj: JsonObject, key: str, where: str, *, default: bool) -> bool:
    if not _present(obj, key):
        return default
    return _bool_value(obj[key], f"{where}/{key}")


def _req_object(obj: JsonObject, key: str, where: str) -> JsonObject:
    return _object(_require(obj, key, where), f"{where}/{key}")


def _list_value(value: Any, where: str) -> Sequence[Any]:
    if not isinstance(value, list):
        _fail(where, "expected an array")
    return value


def _req_list(obj: JsonObject, key: str, where: str) -> Sequence[Any]:
    return _list_value(_require(obj, key, where), f"{where}/{key}")


def _opt_list(obj: JsonObject, key: str, where: str) -> Sequence[Any]:
    if not _present(obj, key):
        return ()
    return _list_value(obj[key], f"{where}/{key}")


def _str_tuple(values: Sequence[Any], where: str, **options: Any) -> tuple[str, ...]:
    return tuple(
        _str_value(item, f"{where}/{index}", **options)
        for index, item in enumerate(values)
    )


def _features(obj: JsonObject, where: str) -> tuple[str, ...]:
    return _str_tuple(_opt_list(obj, "features", where), f"{where}/features")


# ---------------------------------------------------------------------------
# Parsers
# ---------------------------------------------------------------------------


def _parse_agent_info(data: JsonObject, where: str) -> AgentInfo:
    macs = _str_tuple(_req_list(data, "macs", where), f"{where}/macs", pattern=_MAC)
    ui_url = _opt_str(data, "ui_url", where)
    if ui_url is not None and not re.match(r"^https?://", ui_url):
        _fail(f"{where}/ui_url", "must be an http or https URL")
    return AgentInfo(
        id=_req_str(data, "id", where),
        name=_req_str(data, "name", where),
        version=_req_str(data, "version", where),
        os=_req_str(data, "os", where),
        arch=_req_str(data, "arch", where),
        hostname=_req_str(data, "hostname", where),
        macs=macs,
        os_version=_opt_str(data, "os_version", where, non_empty=False),
        ui_url=ui_url,
    )


def _parse_hello(data: JsonObject) -> Hello:
    where = "hello"
    protocol = _req_object(data, "protocol", where)
    return Hello(
        protocol=ProtocolRange(
            min=_req_int(protocol, "min", f"{where}/protocol", minimum=1),
            max=_req_int(protocol, "max", f"{where}/protocol", minimum=1),
        ),
        agent=_parse_agent_info(_req_object(data, "agent", where), f"{where}/agent"),
        manifest_rev=_req_str(data, "manifest_rev", where, pattern=_REV),
        boot_id=_req_str(data, "boot_id", where),
        features=_features(data, where),
    )


def _parse_state(data: JsonObject) -> State:
    where = "state"
    values: dict[str, MetricValue] = {}
    for key, value in _req_object(data, "v", where).items():
        item_where = f"{where}/v/{key}"
        if not _METRIC_ID.search(key):
            _fail(item_where, "is not a valid metric id")
        if value is not None and not isinstance(value, (str, int, float, bool)):
            _fail(item_where, "expected a number, string, boolean or null")
        values[key] = value
    return State(ts=_req_int(data, "ts", where, minimum=0), values=values)


def _parse_event(data: JsonObject) -> Event:
    where = "event"
    payload: JsonObject = {}
    if _present(data, "data"):
        payload = _req_object(data, "data", where)
    return Event(
        type=_req_str(data, "type", where),
        ts=_req_int(data, "ts", where, minimum=0),
        data=payload,
    )


def _parse_error_info(data: JsonObject, where: str) -> ErrorInfo:
    return ErrorInfo(
        code=_req_str(data, "code", where, pattern=_ERROR_CODE),
        message=_req_str(data, "message", where, non_empty=False),
    )


def _parse_result(data: JsonObject) -> Result:
    where = "result"
    ok = _req_bool(data, "ok", where)
    request_id = _req_int(data, "id", where, minimum=0)
    if ok:
        return Result(id=request_id, ok=True, data=_req_object(data, "data", where))
    error = _parse_error_info(_req_object(data, "error", where), f"{where}/error")
    return Result(id=request_id, ok=False, error=error)


def parse_message(data: Any) -> Message:
    """Parse a decoded frame sent by an agent.

    Frames with an unknown ``t`` (including the types that only clients send) become
    :class:`UnknownMessage`. Raises :class:`ProtocolError` if the frame is not an
    object, has no ``t`` or violates the schema of its type.
    """
    frame = _object(data, "frame")
    message_type = _req_str(frame, "t", "frame")
    match message_type:
        case "hello":
            return _parse_hello(frame)
        case "manifest_changed":
            return ManifestChanged(
                rev=_req_str(frame, "rev", "manifest_changed", pattern=_REV)
            )
        case "state":
            return _parse_state(frame)
        case "event":
            return _parse_event(frame)
        case "bye":
            return Bye(
                reason=_req_str(frame, "reason", "bye", allowed=BYE_REASONS),
            )
        case "result":
            return _parse_result(frame)
        case _:
            return UnknownMessage(type=message_type, raw=frame)


def parse_frame(text: str | bytes) -> Message:
    """Decode the JSON text of one WebSocket frame and parse it."""
    try:
        data = json.loads(text)
    except ValueError as error:
        raise ProtocolError(f"frame: not valid JSON ({error})") from error
    return parse_message(data)


def _parse_variable(data: Any, where: str) -> VariableDescriptor:
    item = _object(data, where)
    default: Scalar | None = None
    if _present(item, "default"):
        raw_default = item["default"]
        if not isinstance(raw_default, (str, int, float, bool)):
            _fail(f"{where}/default", "expected a string, number or boolean")
        default = raw_default
    options = None
    if _present(item, "options"):
        options = _str_tuple(
            _req_list(item, "options", where), f"{where}/options", non_empty=False
        )
    return VariableDescriptor(
        name=_req_str(item, "name", where, pattern=_VARIABLE_NAME),
        type=_req_str(item, "type", where, allowed=VARIABLE_TYPES),
        required=_opt_bool(item, "required", where, default=False),
        default=default,
        min=_opt_number(item, "min", where),
        max=_opt_number(item, "max", where),
        step=_opt_number(item, "step", where),
        pattern=_opt_str(item, "pattern", where, non_empty=False),
        max_length=_opt_int(item, "max_length", where, minimum=1),
        options=options,
        secret=_opt_bool(item, "secret", where, default=False),
        unit=_opt_str(item, "unit", where, non_empty=False),
        description=_opt_str(item, "description", where, non_empty=False),
    )


def _parse_control(data: JsonObject, where: str) -> ControlDescriptor:
    return ControlDescriptor(
        type=_req_str(data, "type", where, allowed=CONTROL_TYPES),
        var=_opt_str(data, "var", where, pattern=_VARIABLE_NAME),
        state_metric=_opt_str(data, "state_metric", where, pattern=_METRIC_ID),
    )


def _parse_command(data: Any, where: str) -> CommandDescriptor:
    item = _object(data, where)
    control = None
    if _present(item, "control"):
        control = _parse_control(
            _req_object(item, "control", where), f"{where}/control"
        )
    variables = tuple(
        _parse_variable(entry, f"{where}/vars/{index}")
        for index, entry in enumerate(_opt_list(item, "vars", where))
    )
    return CommandDescriptor(
        id=_req_str(item, "id", where, pattern=_COMMAND_ID),
        name=_req_str(item, "name", where),
        icon=_opt_str(item, "icon", where, non_empty=False),
        description=_opt_str(item, "description", where, non_empty=False),
        returns_output=_opt_bool(item, "returns_output", where, default=False),
        vars=variables,
        control=control,
    )


def _parse_metric(data: Any, where: str) -> MetricDescriptor:
    item = _object(data, where)
    options = None
    if _present(item, "options"):
        options = _str_tuple(
            _req_list(item, "options", where), f"{where}/options", non_empty=False
        )
    context = _opt_str(item, "context", where, allowed=METRIC_CONTEXTS)
    return MetricDescriptor(
        id=_req_str(item, "id", where, pattern=_METRIC_ID),
        name=_req_str(item, "name", where),
        kind=_req_str(item, "kind", where, allowed=METRIC_KINDS),
        value_type=_req_str(item, "value_type", where, allowed=VALUE_TYPES),
        translation_key=_opt_str(item, "translation_key", where, non_empty=False),
        unit=_opt_str(item, "unit", where, non_empty=False),
        device_class=_opt_str(item, "device_class", where, non_empty=False),
        state_class=_opt_str(item, "state_class", where, non_empty=False),
        entity_category=_opt_str(item, "entity_category", where, non_empty=False),
        icon=_opt_str(item, "icon", where, non_empty=False),
        precision=_opt_int(item, "precision", where, minimum=0),
        options=options,
        enabled_default=_opt_bool(item, "enabled_default", where, default=True),
        interval_seconds=_opt_number(item, "interval_seconds", where, minimum=0),
        deadband=_opt_number(item, "deadband", where, minimum=0),
        context=context,
    )


def _parse_event_descriptor(data: Any, where: str) -> EventDescriptor:
    item = _object(data, where)
    return EventDescriptor(
        type=_req_str(item, "type", where),
        name=_req_str(item, "name", where),
    )


def parse_manifest(data: Any) -> Manifest:
    """Parse the manifest (the ``data`` of the result of ``get_manifest``)."""
    where = "manifest"
    manifest = _object(data, where)
    return Manifest(
        rev=_req_str(manifest, "rev", where, pattern=_REV),
        agent=_parse_agent_info(
            _req_object(manifest, "agent", where), f"{where}/agent"
        ),
        metrics=tuple(
            _parse_metric(entry, f"{where}/metrics/{index}")
            for index, entry in enumerate(_req_list(manifest, "metrics", where))
        ),
        commands=tuple(
            _parse_command(entry, f"{where}/commands/{index}")
            for index, entry in enumerate(_req_list(manifest, "commands", where))
        ),
        events=tuple(
            _parse_event_descriptor(entry, f"{where}/events/{index}")
            for index, entry in enumerate(_opt_list(manifest, "events", where))
        ),
        features=_features(manifest, where),
    )


def parse_pair_response(data: Any) -> PairResponse:
    """Parse the response of ``POST /link/v1/pair``."""
    where = "pair_response"
    response = _object(data, where)
    agent = _req_object(response, "agent", where)
    return PairResponse(
        token=_req_str(response, "token", where, pattern=_TOKEN),
        agent_id=_req_str(agent, "id", f"{where}/agent"),
        agent_name=_req_str(agent, "name", f"{where}/agent"),
    )


def parse_run_result_data(data: Mapping[str, Any]) -> RunResultData:
    """Parse the ``data`` of a successful ``run`` result."""
    where = "run_result"
    timed_out = data.get("timed_out")
    if timed_out is not None:
        timed_out = _bool_value(timed_out, f"{where}/timed_out")
    return RunResultData(
        run_id=_req_str(data, "run_id", where),
        exit_code=_opt_int(data, "exit_code", where),
        stdout=_opt_str(data, "stdout", where, non_empty=False),
        stderr=_opt_str(data, "stderr", where, non_empty=False),
        duration_ms=_opt_int(data, "duration_ms", where, minimum=0),
        timed_out=timed_out,
    )


# ---------------------------------------------------------------------------
# Frames sent by a client
# ---------------------------------------------------------------------------


def build_client_hello(
    protocol: int,
    *,
    kind: str,
    name: str,
    version: str,
    features: Sequence[str] = (),
) -> dict[str, Any]:
    """Build a ``client_hello`` frame."""
    frame: dict[str, Any] = {
        "t": "client_hello",
        "protocol": protocol,
        "client": {"kind": kind, "name": name, "version": version},
    }
    if features:
        frame["features"] = list(features)
    return frame


def build_get_manifest(request_id: int) -> dict[str, Any]:
    """Build a ``get_manifest`` request."""
    return {"t": "get_manifest", "id": request_id}


def build_subscribe(
    request_id: int,
    metrics: Sequence[str] | str,
    intervals: Mapping[str, float] | None = None,
) -> dict[str, Any]:
    """Build a ``subscribe`` request; ``metrics`` is a list of ids or ``"*"``."""
    if isinstance(metrics, str) and metrics != "*":
        msg = 'metrics must be a list of metric ids or "*"'
        raise ProtocolError(msg)
    frame: dict[str, Any] = {
        "t": "subscribe",
        "id": request_id,
        "metrics": metrics if isinstance(metrics, str) else list(metrics),
    }
    if intervals:
        frame["intervals"] = dict(intervals)
    return frame


def build_run(
    request_id: int,
    command: str,
    variables: Mapping[str, Scalar] | None = None,
    *,
    wait: bool | None = None,
    user_id: str | None = None,
) -> dict[str, Any]:
    """Build a ``run`` request. Members that are not given are omitted."""
    frame: dict[str, Any] = {"t": "run", "id": request_id, "command": command}
    if variables is not None:
        frame["vars"] = dict(variables)
    if wait is not None:
        frame["wait"] = wait
    if user_id is not None:
        frame["context"] = {"user_id": user_id}
    return frame


def build_pair_request(code: str, *, kind: str, name: str) -> dict[str, Any]:
    """Build the body of ``POST /link/v1/pair``."""
    return {"code": code, "client": {"kind": kind, "name": name}}
