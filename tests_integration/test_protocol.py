"""Tests of the QoffeeLink models against the shared fixtures in protocol/ (T104)."""

import ast
import json
from pathlib import Path
from typing import Any

import pytest

from custom_components.qoffeeautomat import protocol
from custom_components.qoffeeautomat.protocol import (
    Bye,
    Event,
    Hello,
    ManifestChanged,
    ProtocolError,
    Result,
    State,
    UnknownMessage,
    build_client_hello,
    build_get_manifest,
    build_pair_request,
    build_run,
    build_subscribe,
    parse_frame,
    parse_manifest,
    parse_message,
    parse_pair_response,
    parse_run_result_data,
)

ROOT = Path(__file__).resolve().parents[1]
PROTOCOL_DIR = ROOT / "protocol"
FIXTURES = PROTOCOL_DIR / "fixtures"
SCHEMAS = PROTOCOL_DIR / "schema"

# Message types that an agent sends and that this module parses into typed objects.
RECEIVED_TYPES: dict[str, type] = {
    "hello": Hello,
    "manifest_changed": ManifestChanged,
    "state": State,
    "event": Event,
    "bye": Bye,
    "result": Result,
}
# Fixture prefixes that this module parses (messages sent by clients are not parsed).
PARSED_PREFIXES = {*RECEIVED_TYPES, "message", "manifest", "pair_response"}


def _load(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def _files(folder: Path, prefixes: set[str] | None = None) -> list[Path]:
    files = sorted(folder.glob("*.json"))
    if prefixes is None:
        return files
    return [path for path in files if path.name.split(".", 1)[0] in prefixes]


def _prefix(path: Path) -> str:
    return path.name.split(".", 1)[0]


def _name(path: Path) -> str:
    return path.name


VALID_MESSAGES = _files(FIXTURES, set(RECEIVED_TYPES)) + _files(
    FIXTURES / "forward_compat", {*RECEIVED_TYPES, "message"}
)
INVALID_PARSED = _files(FIXTURES / "invalid", PARSED_PREFIXES)


# ---------------------------------------------------------------------------
# Valid fixtures
# ---------------------------------------------------------------------------


def test_fixtures_exist_for_every_received_type() -> None:
    present = {_prefix(path) for path in VALID_MESSAGES}
    assert set(RECEIVED_TYPES) <= present


@pytest.mark.parametrize("path", VALID_MESSAGES, ids=_name)
def test_valid_message_fixtures_parse_into_typed_objects(path: Path) -> None:
    message = parse_message(_load(path))
    expected = RECEIVED_TYPES.get(_prefix(path), UnknownMessage)
    assert isinstance(message, expected)


@pytest.mark.parametrize("path", VALID_MESSAGES, ids=_name)
def test_frames_parse_from_text_and_bytes(path: Path) -> None:
    text = path.read_text(encoding="utf-8")
    assert parse_frame(text) == parse_frame(text.encode("utf-8"))


def test_hello_is_parsed_completely() -> None:
    hello = parse_message(_load(FIXTURES / "hello.features.json"))
    assert isinstance(hello, Hello)
    assert (hello.protocol.min, hello.protocol.max) == (1, 2)
    assert hello.agent.name == "Media-PC"
    assert hello.agent.os_version == "6.12.0"
    assert hello.agent.ui_url is None
    assert hello.agent.macs == ()
    assert hello.boot_id == "0f1e2d3c-4b5a-4968-8776-655443322110"
    assert hello.features == ("wol", "notify")


def test_hello_without_features_has_no_features() -> None:
    hello = parse_message(_load(FIXTURES / "hello.json"))
    assert isinstance(hello, Hello)
    assert hello.features == ()
    assert hello.agent.ui_url == "https://gaming-pc.local:8481/"


def test_state_keeps_unavailable_values() -> None:
    state = parse_message(_load(FIXTURES / "state.unavailable.json"))
    assert isinstance(state, State)
    assert state.values == {
        "session.locked": None,
        "window.title": "Visual Studio Code",
    }


def test_state_values_keep_their_types() -> None:
    state = parse_message(_load(FIXTURES / "state.json"))
    assert isinstance(state, State)
    assert state.ts == 1763126400000
    assert state.values["cpu.usage"] == 12
    assert state.values["session.locked"] is False
    assert state.values["audio.volume"] == 35.5


def test_event_data_is_optional() -> None:
    minimal = parse_message(_load(FIXTURES / "event.minimal.json"))
    assert isinstance(minimal, Event)
    assert minimal.data == {}
    full = parse_message(_load(FIXTURES / "event.json"))
    assert isinstance(full, Event)
    assert full.data == {"user": "alice"}


def test_result_success_and_failure() -> None:
    ok = parse_message(_load(FIXTURES / "result.ok.json"))
    assert isinstance(ok, Result)
    assert ok.ok is True
    assert ok.data == {}
    assert ok.error is None
    failed = parse_message(_load(FIXTURES / "result.error.json"))
    assert isinstance(failed, Result)
    assert failed.ok is False
    assert failed.error is not None
    assert failed.error.code == protocol.ERROR_UNKNOWN_COMMAND


def test_run_result_data() -> None:
    result = parse_message(_load(FIXTURES / "result.run.json"))
    assert isinstance(result, Result)
    assert result.data is not None
    data = parse_run_result_data(result.data)
    assert data.run_id == "01JD8Z3Q4V7N2K9M5X6C8B0A1E"
    assert data.exit_code == 0
    assert data.stdout == "Applied TV-Mode\n"
    assert data.duration_ms == 412
    assert data.timed_out is False


def test_run_result_data_requires_a_run_id() -> None:
    with pytest.raises(ProtocolError):
        parse_run_result_data({"exit_code": 0})


# ---------------------------------------------------------------------------
# Forward compatibility
# ---------------------------------------------------------------------------


def test_unknown_message_types_are_returned_not_raised() -> None:
    path = FIXTURES / "forward_compat" / "message.unknown_type.json"
    message = parse_message(_load(path))
    assert isinstance(message, UnknownMessage)
    assert message.type == "some_future_message"
    assert message.raw["payload"] == {"anything": [1, 2, 3]}


@pytest.mark.parametrize("name", ["client_hello", "get_manifest", "subscribe", "run"])
def test_client_message_types_are_unknown_to_a_client(name: str) -> None:
    message = parse_message({"t": name, "id": 1})
    assert isinstance(message, UnknownMessage)


def test_unknown_fields_and_features_are_tolerated() -> None:
    path = FIXTURES / "forward_compat" / "hello.future_fields.json"
    hello = parse_message(_load(path))
    assert isinstance(hello, Hello)
    assert hello.protocol.max == 3
    assert "some_future_feature" in hello.features


def test_unknown_error_codes_are_tolerated() -> None:
    path = FIXTURES / "forward_compat" / "result.future_fields.json"
    result = parse_message(_load(path))
    assert isinstance(result, Result)
    assert result.error is not None
    assert result.error.code == "some_future_error"
    assert result.error.code not in protocol.ERROR_CODES


def test_manifest_with_unknown_fields_parses() -> None:
    path = FIXTURES / "forward_compat" / "manifest.future_fields.json"
    manifest = parse_manifest(_load(path))
    assert manifest.metrics[0].enabled_default is True
    assert manifest.commands[0].control is not None
    assert manifest.commands[0].control.type == "button"
    assert manifest.events == ()
    assert manifest.features == ()


def test_null_is_treated_like_absent_for_optional_members() -> None:
    frame = _load(FIXTURES / "event.json")
    frame["data"] = None
    event = parse_message(frame)
    assert isinstance(event, Event)
    assert event.data == {}


# ---------------------------------------------------------------------------
# Manifest and pairing
# ---------------------------------------------------------------------------


def test_manifest_fixture_parses_completely() -> None:
    manifest = parse_manifest(_load(FIXTURES / "manifest.json"))
    assert manifest.rev == "143ffa0cd221"
    assert manifest.agent.name == "Gaming-PC"
    assert manifest.agent.os_version == "10.0.26200"
    assert [metric.id for metric in manifest.metrics] == [
        "cpu.usage",
        "session.locked",
        "audio.volume",
    ]
    cpu = manifest.metrics[0]
    assert (cpu.kind, cpu.value_type, cpu.unit, cpu.precision) == (
        "sensor",
        "number",
        "%",
        0,
    )
    assert manifest.metrics[1].context == "user"
    assert manifest.metrics[2].interval_seconds == 0
    assert [command.id for command in manifest.commands] == [
        "set_display_profile",
        "set_volume",
        "lock_session",
        "say_text",
    ]
    assert [event.type for event in manifest.events] == [
        "session_locked",
        "session_unlocked",
    ]
    assert manifest.features == ("wol", "notify")


def test_manifest_commands_expose_variables_and_controls() -> None:
    manifest = parse_manifest(_load(FIXTURES / "manifest.json"))
    display, volume, lock, say = manifest.commands
    assert display.returns_output is True
    profile, adapter_ids = display.vars
    assert (profile.type, profile.required) == ("enum", True)
    assert profile.options == ("Normal-Mode", "TV-Mode")
    assert (adapter_ids.type, adapter_ids.default) == ("bool", False)
    assert display.control is not None
    assert (display.control.type, display.control.var) == ("select", "Profile")
    assert display.control.state_metric is None
    level = volume.vars[0]
    assert (level.min, level.max, level.step) == (0, 100, 1)
    assert volume.control is not None
    assert volume.control.state_metric == "audio.volume"
    assert lock.vars == ()
    assert lock.control is not None
    assert lock.control.type == "button"
    assert say.vars[0].max_length == 200
    assert say.vars[0].pattern == "^[A-Za-z0-9 .,!?-]+$"


def test_pair_response_is_parsed() -> None:
    response = parse_pair_response(_load(FIXTURES / "pair_response.json"))
    assert response.agent_id == "5b0c6c1e-8e0a-4f6e-9a57-3d2f3a1c9b77"
    assert response.agent_name == "Gaming-PC"
    assert len(response.token) == 43


# ---------------------------------------------------------------------------
# Invalid fixtures
# ---------------------------------------------------------------------------


def test_invalid_fixtures_cover_the_parsed_types() -> None:
    assert {_prefix(path) for path in INVALID_PARSED} >= {
        "hello",
        "manifest_changed",
        "state",
        "event",
        "bye",
        "result",
        "manifest",
        "pair_response",
        "message",
    }


def _parse_by_prefix(prefix: str, data: Any) -> object:
    if prefix == "manifest":
        return parse_manifest(data)
    if prefix == "pair_response":
        return parse_pair_response(data)
    return parse_message(data)


@pytest.mark.parametrize("path", INVALID_PARSED, ids=_name)
def test_invalid_fixtures_raise_protocol_error(path: Path) -> None:
    with pytest.raises(ProtocolError):
        _parse_by_prefix(_prefix(path), _load(path))


@pytest.mark.parametrize("text", ["", "not json", "{", '{"t": "hello"'])
def test_undecodable_frames_raise_protocol_error(text: str) -> None:
    with pytest.raises(ProtocolError):
        parse_frame(text)


# ---------------------------------------------------------------------------
# Frames built by the client
# ---------------------------------------------------------------------------


def test_built_client_hello_equals_the_fixtures() -> None:
    plain = build_client_hello(
        1, kind="homeassistant", name="Home", version="2026.11.0"
    )
    assert plain == _load(FIXTURES / "client_hello.json")
    with_features = build_client_hello(
        1,
        kind="hub",
        name="QoffeeAutomat Hub",
        version="1.0.0",
        features=["fleet"],
    )
    assert with_features == _load(FIXTURES / "client_hello.features.json")


def test_built_requests_equal_the_fixtures() -> None:
    assert build_get_manifest(1) == _load(FIXTURES / "get_manifest.json")
    listed = build_subscribe(2, ["cpu.usage", "session.locked", "audio.volume"])
    assert listed == _load(FIXTURES / "subscribe.json")
    assert build_subscribe(3, "*") == _load(FIXTURES / "subscribe.all.json")
    intervals = {"cpu.usage": 5, "audio.volume": 0.5}
    limited = build_subscribe(4, ["cpu.usage", "audio.volume"], intervals)
    assert limited == _load(FIXTURES / "subscribe.intervals.json")


def test_built_run_requests_equal_the_fixtures() -> None:
    full = build_run(
        5,
        "set_display_profile",
        {"Profile": "TV-Mode", "UpdateAdapterIds": False},
        wait=True,
        user_id="8f1c2d3e4a5b6c7d8e9f0a1b2c3d4e5f",
    )
    assert full == _load(FIXTURES / "run.json")
    assert build_run(6, "lock_session") == _load(FIXTURES / "run.minimal.json")


def test_built_pair_request_equals_the_fixture() -> None:
    request = build_pair_request("123456", kind="homeassistant", name="Home")
    assert request == _load(FIXTURES / "pair_request.json")


def test_subscribe_rejects_a_plain_string_other_than_the_wildcard() -> None:
    with pytest.raises(ProtocolError):
        build_subscribe(1, "cpu.usage")


# ---------------------------------------------------------------------------
# Drift guards
# ---------------------------------------------------------------------------


def test_module_has_no_home_assistant_imports() -> None:
    tree = ast.parse(Path(protocol.__file__).read_text(encoding="utf-8"))
    modules: set[str] = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            modules.update(alias.name for alias in node.names)
        elif isinstance(node, ast.ImportFrom) and node.module:
            modules.add(node.module)
    assert not {name for name in modules if name.split(".")[0] == "homeassistant"}


def test_closed_enums_match_the_schemas() -> None:
    defs = _load(SCHEMAS / "defs.schema.json")["$defs"]
    bye = _load(SCHEMAS / "bye.schema.json")["properties"]["reason"]["enum"]
    assert set(bye) == protocol.BYE_REASONS
    assert set(defs["client_kind"]["enum"]) == protocol.CLIENT_KINDS
    metric = defs["metric_descriptor"]["properties"]
    assert set(metric["kind"]["enum"]) == protocol.METRIC_KINDS
    assert set(metric["value_type"]["enum"]) == protocol.VALUE_TYPES
    assert set(metric["context"]["enum"]) == protocol.METRIC_CONTEXTS
    variable = defs["variable_descriptor"]["properties"]
    assert set(variable["type"]["enum"]) == protocol.VARIABLE_TYPES
    control = defs["control_descriptor"]["properties"]
    assert set(control["type"]["enum"]) == protocol.CONTROL_TYPES


def test_error_codes_match_the_readme() -> None:
    readme = (PROTOCOL_DIR / "README.md").read_text(encoding="utf-8")
    for code in protocol.ERROR_CODES:
        assert f"| `{code}` |" in readme
