"""QoffeeLink v1 protocol models and parsers (PLAN.md S07, S10).

This module has no Home Assistant imports so that it can become part of a PyPI library
together with ``client.py``. It turns decoded JSON into typed, immutable objects:

* ``parse_frame`` and ``parse_message`` handle the frames that an agent sends
  (``hello``, ``manifest_changed``, ``state``, ``event``, ``bye``, ``result``). Unknown
  message types become :class:`UnknownMessage`; unknown fields are ignored.
* ``parse_manifest`` and ``parse_pair_response`` parse the manifest and the pairing
  response; ``parse_run_result_data`` parses the data of a successful ``run`` result.
* ``parse_fleet_frame`` and ``parse_fleet_message`` handle the frames that a hub sends
  on the fleet link (PLAN.md S20): the hub ``hello``, ``agent_added``,
  ``agent_removed``, ``agent_status`` and the agent-scoped variants of
  ``manifest_changed``, ``state`` and ``event`` that carry the agent id in the
  member ``a``. ``parse_get_agents_result_data`` parses the answer to ``get_agents``
  and ``parse_hub_pair_response`` the answer of ``POST /hub/v1/pair``.
* Data that violates the schemas in ``protocol/schema`` raises :class:`ProtocolError`.
* The ``build_*`` functions create the frames that a client sends; ``get_manifest``,
  ``subscribe`` and ``run`` take an optional ``agent_id`` for the fleet link.

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
ERROR_CONDITION_NOT_MET: Final = "condition_not_met"
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
        ERROR_CONDITION_NOT_MET,
    }
)

# Error codes that the hub adds (PLAN.md S20). They are kept apart from ERROR_CODES,
# which lists the codes of protocol v1. Receivers tolerate unknown codes. ``revoked``
# is only used in the HTTP 401 response of the agent link, never in a frame.
ERROR_UNKNOWN_AGENT: Final = "unknown_agent"
ERROR_AGENT_OFFLINE: Final = "agent_offline"
ERROR_FORBIDDEN: Final = "forbidden"
ERROR_REVOKED: Final = "revoked"
HUB_ERROR_CODES: Final = frozenset(
    {ERROR_UNKNOWN_AGENT, ERROR_AGENT_OFFLINE, ERROR_FORBIDDEN, ERROR_REVOKED}
)

# Paths and features of the hub (PLAN.md S20).
HUB_PAIR_PATH: Final = "/hub/v1/pair"
HUB_FLEET_PATH: Final = "/hub/v1/fleet"
FEATURE_FLEET: Final = "fleet"

# Close codes (PLAN.md S07, D-039).
CLOSE_BAD_REQUEST: Final = 4400
CLOSE_UNAUTHORIZED: Final = 4401
CLOSE_UNSUPPORTED_VERSION: Final = 4406

# Closed enums of protocol version 1.
BYE_REASONS: Final = frozenset(
    {"sleep", "hibernate", "shutdown", "restart", "service_stop", "update"}
)
UNKNOWN_POWER_STATE: Final = "unknown"
# The last ``bye`` reason of an agent as reported by the hub, or "unknown" (S20).
POWER_STATES: Final = BYE_REASONS | {UNKNOWN_POWER_STATE}
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
_AGENT_ID_MAX_LENGTH: Final = 64

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
# Fleet link models (PLAN.md S20)
# ---------------------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class HubInfo:
    """Identity of a hub (``hello.hub`` of the fleet link)."""

    id: str
    name: str
    version: str


@dataclass(frozen=True, slots=True)
class FleetHello:
    """First frame of the fleet link, sent by the hub in place of an agent ``hello``."""

    protocol: ProtocolRange
    hub: HubInfo
    features: tuple[str, ...] = ()


@dataclass(frozen=True, slots=True)
class HubPairResponse:
    """Response of a successful ``POST /hub/v1/pair`` (PLAN.md S07, S20).

    ``scope_type`` is ``all`` or ``tags``; ``scope_tags`` lists the tags of a tag scope.
    ``fingerprint`` is the SHA-256 fingerprint of the hub certificate; it is absent when
    the hub is served without TLS.
    """

    token: str
    hub_id: str
    hub_name: str
    scope_type: str
    scope_tags: tuple[str, ...] = ()
    fingerprint: str | None = None


@dataclass(frozen=True, slots=True)
class FleetAgent:
    """An agent in the scope of the fleet token, as listed by ``get_agents``.

    ``manifest_rev`` and ``agent`` are ``None`` for an agent that never connected.
    ``power_state`` is the last ``bye`` reason or ``"unknown"``.
    """

    id: str
    name: str
    online: bool
    power_state: str
    manifest_rev: str | None = None
    agent: AgentInfo | None = None


@dataclass(frozen=True, slots=True)
class AgentAdded:
    """An agent entered the scope of the token; ``agent_id`` is the member ``a``."""

    agent_id: str
    name: str
    online: bool
    power_state: str
    manifest_rev: str | None = None
    agent: AgentInfo | None = None

    def to_agent(self) -> FleetAgent:
        """Return the same agent in the form of a ``get_agents`` entry."""
        return FleetAgent(
            id=self.agent_id,
            name=self.name,
            online=self.online,
            power_state=self.power_state,
            manifest_rev=self.manifest_rev,
            agent=self.agent,
        )


@dataclass(frozen=True, slots=True)
class AgentRemoved:
    """An agent left the scope of the token or was removed from the hub."""

    agent_id: str


@dataclass(frozen=True, slots=True)
class AgentStatus:
    """An agent went online or offline."""

    agent_id: str
    online: bool
    power_state: str


@dataclass(frozen=True, slots=True)
class AgentManifestChanged:
    """Agent-scoped ``manifest_changed``."""

    agent_id: str
    rev: str


@dataclass(frozen=True, slots=True)
class AgentState:
    """Agent-scoped ``state``; ``None`` means unavailable."""

    agent_id: str
    ts: int
    values: Mapping[str, MetricValue]


@dataclass(frozen=True, slots=True)
class AgentEvent:
    """Agent-scoped ``event``."""

    agent_id: str
    type: str
    ts: int
    data: Mapping[str, Any] = field(default_factory=dict)


type FleetMessage = (
    FleetHello
    | AgentAdded
    | AgentRemoved
    | AgentStatus
    | AgentManifestChanged
    | AgentState
    | AgentEvent
    | Result
    | UnknownMessage
)


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
# Fleet link parsers (PLAN.md S20)
# ---------------------------------------------------------------------------


def _req_agent_id(obj: JsonObject, key: str, where: str) -> str:
    value = _req_str(obj, key, where)
    if len(value) > _AGENT_ID_MAX_LENGTH:
        _fail(
            f"{where}/{key}",
            f"must not be longer than {_AGENT_ID_MAX_LENGTH} characters",
        )
    return value


def _parse_fleet_agent(data: JsonObject, where: str, *, id_key: str) -> FleetAgent:
    """Parse a ``get_agents`` entry (``id_key="id"``) or ``agent_added`` (``"a"``)."""
    details: AgentInfo | None = None
    if _present(data, "agent"):
        agent_data = _req_object(data, "agent", where)
        details = _parse_agent_info(agent_data, f"{where}/agent")
    return FleetAgent(
        id=_req_agent_id(data, id_key, where),
        name=_req_str(data, "name", where),
        online=_req_bool(data, "online", where),
        power_state=_req_str(data, "power_state", where, allowed=POWER_STATES),
        manifest_rev=_opt_str(data, "manifest_rev", where, pattern=_REV),
        agent=details,
    )


def _parse_fleet_hello(data: JsonObject) -> FleetHello:
    where = "hello"
    protocol = _req_object(data, "protocol", where)
    hub = _req_object(data, "hub", where)
    return FleetHello(
        protocol=ProtocolRange(
            min=_req_int(protocol, "min", f"{where}/protocol", minimum=1),
            max=_req_int(protocol, "max", f"{where}/protocol", minimum=1),
        ),
        hub=HubInfo(
            id=_req_agent_id(hub, "id", f"{where}/hub"),
            name=_req_str(hub, "name", f"{where}/hub"),
            version=_req_str(hub, "version", f"{where}/hub"),
        ),
        features=_str_tuple(_req_list(data, "features", where), f"{where}/features"),
    )


def _parse_agent_added(data: JsonObject) -> AgentAdded:
    agent = _parse_fleet_agent(data, "agent_added", id_key="a")
    return AgentAdded(
        agent_id=agent.id,
        name=agent.name,
        online=agent.online,
        power_state=agent.power_state,
        manifest_rev=agent.manifest_rev,
        agent=agent.agent,
    )


def parse_fleet_message(data: Any) -> FleetMessage:
    """Parse a decoded frame that a hub sends on the fleet link.

    The frames of S07 (``manifest_changed``, ``state``, ``event``) carry the agent id in
    the member ``a`` and become the ``Agent*`` types. Frames with an unknown ``t``
    become :class:`UnknownMessage`. Raises :class:`ProtocolError` if the frame is not
    an object, has no ``t`` or violates the schema of its type.
    """
    frame = _object(data, "frame")
    message_type = _req_str(frame, "t", "frame")
    match message_type:
        case "hello":
            return _parse_fleet_hello(frame)
        case "agent_added":
            return _parse_agent_added(frame)
        case "agent_removed":
            return AgentRemoved(
                agent_id=_req_agent_id(frame, "a", "agent_removed"),
            )
        case "agent_status":
            return AgentStatus(
                agent_id=_req_agent_id(frame, "a", "agent_status"),
                online=_req_bool(frame, "online", "agent_status"),
                power_state=_req_str(
                    frame, "power_state", "agent_status", allowed=POWER_STATES
                ),
            )
        case "manifest_changed":
            return AgentManifestChanged(
                agent_id=_req_agent_id(frame, "a", "manifest_changed"),
                rev=_req_str(frame, "rev", "manifest_changed", pattern=_REV),
            )
        case "state":
            state = _parse_state(frame)
            return AgentState(
                agent_id=_req_agent_id(frame, "a", "state"),
                ts=state.ts,
                values=state.values,
            )
        case "event":
            event = _parse_event(frame)
            return AgentEvent(
                agent_id=_req_agent_id(frame, "a", "event"),
                type=event.type,
                ts=event.ts,
                data=event.data,
            )
        case "result":
            return _parse_result(frame)
        case _:
            return UnknownMessage(type=message_type, raw=frame)


def parse_fleet_frame(text: str | bytes) -> FleetMessage:
    """Decode the JSON text of one fleet link frame and parse it."""
    try:
        data = json.loads(text)
    except ValueError as error:
        raise ProtocolError(f"frame: not valid JSON ({error})") from error
    return parse_fleet_message(data)


def parse_get_agents_result_data(data: Any) -> tuple[FleetAgent, ...]:
    """Parse the ``data`` of a successful ``get_agents`` result."""
    where = "get_agents_result"
    result = _object(data, where)
    return tuple(
        _parse_fleet_agent(
            _object(entry, f"{where}/agents/{index}"),
            f"{where}/agents/{index}",
            id_key="id",
        )
        for index, entry in enumerate(_req_list(result, "agents", where))
    )


_SCOPE_TYPES: Final = frozenset({"all", "tags"})
_FINGERPRINT = re.compile(r"^([0-9A-F]{2}:){31}[0-9A-F]{2}$")


def parse_hub_pair_response(data: Any) -> HubPairResponse:
    """Parse the response of ``POST /hub/v1/pair`` (``pair_response.schema.json``)."""
    where = "hub_pair_response"
    scope_where = f"{where}/scope"
    response = _object(data, where)
    hub = _req_object(response, "hub", where)
    scope = _req_object(response, "scope", where)
    scope_type = _req_str(scope, "type", scope_where, allowed=_SCOPE_TYPES)
    tags = _str_tuple(_opt_list(scope, "tags", scope_where), f"{scope_where}/tags")
    if scope_type == "tags" and not tags:
        _fail(f"{scope_where}/tags", "a tag scope needs at least one tag")
    return HubPairResponse(
        token=_req_str(response, "token", where, pattern=_TOKEN),
        hub_id=_req_agent_id(hub, "id", f"{where}/hub"),
        hub_name=_req_str(hub, "name", f"{where}/hub"),
        scope_type=scope_type,
        scope_tags=tags,
        fingerprint=_opt_str(response, "fingerprint", where, pattern=_FINGERPRINT),
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


def build_get_agents(request_id: int) -> dict[str, Any]:
    """Build a ``get_agents`` request (fleet link)."""
    return {"t": "get_agents", "id": request_id}


def build_get_manifest(
    request_id: int, *, agent_id: str | None = None
) -> dict[str, Any]:
    """Build a ``get_manifest`` request; ``agent_id`` is ``a`` on the fleet link."""
    frame: dict[str, Any] = {"t": "get_manifest", "id": request_id}
    if agent_id is not None:
        frame["a"] = agent_id
    return frame


def build_subscribe(
    request_id: int,
    metrics: Sequence[str] | str,
    intervals: Mapping[str, float] | None = None,
    *,
    agent_id: str | None = None,
) -> dict[str, Any]:
    """Build a ``subscribe`` request; ``metrics`` is a list of ids or ``"*"``.

    ``agent_id`` is the member ``a`` (fleet link).
    """
    if isinstance(metrics, str) and metrics != "*":
        msg = 'metrics must be a list of metric ids or "*"'
        raise ProtocolError(msg)
    frame: dict[str, Any] = {
        "t": "subscribe",
        "id": request_id,
        "metrics": metrics if isinstance(metrics, str) else list(metrics),
    }
    if agent_id is not None:
        frame["a"] = agent_id
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
    agent_id: str | None = None,
) -> dict[str, Any]:
    """Build a ``run`` request. Members that are not given are omitted.

    ``agent_id`` is the member ``a`` (fleet link).
    """
    frame: dict[str, Any] = {"t": "run", "id": request_id, "command": command}
    if agent_id is not None:
        frame["a"] = agent_id
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
