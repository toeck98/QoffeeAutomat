"""Tests of the command entities: button, switch, select, number and text (T505)."""

from typing import Any

import pytest
from homeassistant.config_entries import ConfigEntryState
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import HomeAssistantError, ServiceValidationError
from homeassistant.helpers import entity_registry as er
from homeassistant.helpers.entity_registry import RegistryEntryDisabler
from pytest_homeassistant_custom_component.common import MockConfigEntry

from custom_components.qoffeeautomat.client import (
    NotConnectedError,
    RequestError,
    RequestTimeoutError,
    UnexpectedResponseError,
)
from custom_components.qoffeeautomat.commands import translate_error
from tests_integration.fake_agent import FakeAgent
from tests_integration.helpers import (
    NEW_COMMAND,
    SECOND_REV,
    EntryFactory,
    attribute_of,
    changed_manifest,
    entity_id_of,
    find_entity_id,
    state_of,
)

SWITCH_COMMAND: dict[str, Any] = {
    "id": "set_mute",
    "name": "Mute",
    "vars": [{"name": "Muted", "type": "bool", "required": True}],
    "control": {"type": "switch", "var": "Muted"},
}
STATE_SWITCH_COMMAND: dict[str, Any] = {
    "id": "set_night_light",
    "name": "Night light",
    "vars": [{"name": "Enabled", "type": "bool", "required": True}],
    "control": {"type": "switch", "var": "Enabled", "state_metric": "night.light"},
}
STATE_SELECT_COMMAND: dict[str, Any] = {
    "id": "set_output",
    "name": "Output",
    "vars": [
        {
            "name": "Device",
            "type": "enum",
            "options": ["speakers", "headphones"],
            "required": True,
        }
    ],
    "control": {"type": "select", "var": "Device", "state_metric": "audio.output"},
}
FLOAT_COMMAND: dict[str, Any] = {
    "id": "set_gamma",
    "name": "Gamma",
    "vars": [
        {
            "name": "Factor",
            "type": "float",
            "required": True,
            "min": 0.5,
            "max": 2.0,
            "step": 0.1,
            "unit": "x",
        }
    ],
    "control": {"type": "number", "var": "Factor"},
}
# Commands that must not get an entity.
SCRIPT_ONLY_COMMAND: dict[str, Any] = {
    "id": "script_only",
    "name": "Script only",
    "control": {"type": "none"},
}
BROKEN_SWITCH_COMMAND: dict[str, Any] = {
    "id": "broken_switch",
    "name": "Broken switch",
    "vars": [{"name": "Value", "type": "string"}],
    "control": {"type": "switch", "var": "Value"},
}
NEEDS_VALUE_COMMAND: dict[str, Any] = {
    "id": "needs_value",
    "name": "Needs value",
    "vars": [{"name": "Value", "type": "string", "required": True}],
    "control": {"type": "button"},
}

# Metrics that only the controls use: their own entities are disabled by default, so
# the subscription holds them only because of the controls.
HIDDEN_METRICS: list[dict[str, Any]] = [
    {
        "id": "night.light",
        "name": "Night light",
        "kind": "binary_sensor",
        "value_type": "bool",
        "enabled_default": False,
    },
    {
        "id": "audio.output",
        "name": "Output device",
        "kind": "sensor",
        "value_type": "enum",
        "options": ["speakers", "headphones"],
        "enabled_default": False,
    },
]
SNAPSHOT: dict[str, Any] = {
    "audio.volume": 35,
    "night.light": False,
    "audio.output": "speakers",
}


@pytest.fixture(name="agent")
def agent_fixture(fake_agent: FakeAgent) -> FakeAgent:
    """The fake agent with a control of every kind and a snapshot of their metrics."""
    fake_agent.set_manifest(
        changed_manifest(
            fake_agent,
            add_metrics=HIDDEN_METRICS,
            add_commands=[
                SWITCH_COMMAND,
                STATE_SWITCH_COMMAND,
                STATE_SELECT_COMMAND,
                FLOAT_COMMAND,
                SCRIPT_ONLY_COMMAND,
                BROKEN_SWITCH_COMMAND,
                NEEDS_VALUE_COMMAND,
            ],
        )
    )
    fake_agent.snapshot = SNAPSHOT
    return fake_agent


def eid(hass: HomeAssistant, agent: FakeAgent, domain: str, key: str) -> str:
    """Return the entity id of a command."""
    return entity_id_of(hass, domain, agent.agent_id, key)


def has_value(hass: HomeAssistant, entity_id: str) -> bool:
    """True when the entity has a state other than unknown or unavailable."""
    state = hass.states.get(entity_id)
    return state is not None and state.state not in ("unknown", "unavailable")


@pytest.fixture(name="entry")
async def entry_fixture(
    hass: HomeAssistant, agent: FakeAgent, entry_factory: EntryFactory
) -> MockConfigEntry:
    """Set up the entry and wait for the values of the state metrics."""
    entry = await entry_factory()
    volume = eid(hass, agent, "number", "set_volume")
    night_light = eid(hass, agent, "switch", "set_night_light")

    def settled() -> bool:
        return has_value(hass, volume) and has_value(hass, night_light)

    await agent.wait_for(settled)
    return entry


async def call(
    hass: HomeAssistant, domain: str, service: str, entity_id: str, **data: Any
) -> None:
    """Call an entity service and wait until it was handled."""
    await hass.services.async_call(
        domain, service, {"entity_id": entity_id, **data}, blocking=True
    )


def last_subscription(agent: FakeAgent) -> list[str]:
    """Return the metrics of the last ``subscribe`` request."""
    sent = agent.subscribed_metrics()
    return list(sent[-1]) if sent else []


# -- which entities exist -------------------------------------------------------


async def test_entities_are_created_from_the_control_descriptors(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """Every control type has its platform; commands without a usable control not."""
    expected = {
        "button": ["lock_session"],
        "switch": ["set_mute", "set_night_light"],
        "select": ["set_display_profile", "set_output"],
        "number": ["set_volume", "set_gamma"],
        "text": ["say_text"],
    }
    for domain, keys in expected.items():
        for key in keys:
            assert find_entity_id(hass, domain, agent.agent_id, key) is not None, key

    registry = er.async_get(hass)
    for key in ("script_only", "broken_switch", "needs_value"):
        for domain in expected:
            assert find_entity_id(hass, domain, agent.agent_id, key) is None
    # Unique id: <agent id>_<command id>; the name comes from the manifest.
    button = registry.async_get(eid(hass, agent, "button", "lock_session"))
    assert button is not None
    assert button.unique_id == f"{agent.agent_id}_lock_session"
    friendly_name = attribute_of(hass, button.entity_id, "friendly_name")
    assert friendly_name.endswith("Lock session")
    assert attribute_of(hass, button.entity_id, "icon") == "mdi:lock"


# -- button -----------------------------------------------------------------------


async def test_button_press_runs_the_command_without_variables(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """A press sends ``run`` for the command with no variables and does not wait."""
    await call(hass, "button", "press", eid(hass, agent, "button", "lock_session"))

    frame = agent.runs[-1]
    assert frame["t"] == "run"
    assert frame["command"] == "lock_session"
    assert "vars" not in frame
    assert frame["wait"] is False


# -- switch -----------------------------------------------------------------------


async def test_optimistic_switch_sends_true_and_false_and_shows_it(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """Without a state metric the switch assumes its state."""
    switch = eid(hass, agent, "switch", "set_mute")
    assert attribute_of(hass, switch, "assumed_state") is True

    await call(hass, "switch", "turn_on", switch)
    assert agent.runs[-1]["command"] == "set_mute"
    assert agent.runs[-1]["vars"] == {"Muted": True}
    assert state_of(hass, switch) == "on"

    await call(hass, "switch", "turn_off", switch)
    assert agent.runs[-1]["vars"] == {"Muted": False}
    assert state_of(hass, switch) == "off"


async def test_switch_with_state_metric_follows_the_metric(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The state comes from the metric, not from what was sent."""
    switch = eid(hass, agent, "switch", "set_night_light")
    assert attribute_of(hass, switch, "assumed_state") is None
    assert state_of(hass, switch) == "off"

    await call(hass, "switch", "turn_on", switch)
    assert agent.runs[-1]["vars"] == {"Enabled": True}
    assert state_of(hass, switch) == "off"

    await agent.send_state({"night.light": True})
    await agent.wait_for(lambda: state_of(hass, switch) == "on")


# -- select -----------------------------------------------------------------------


async def test_select_takes_the_options_and_sends_the_choice(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The options are those of the enum variable; the choice is the variable."""
    select = eid(hass, agent, "select", "set_display_profile")
    assert attribute_of(hass, select, "options") == ["Normal-Mode", "TV-Mode"]
    assert state_of(hass, select) == "unknown"

    await call(hass, "select", "select_option", select, option="TV-Mode")

    assert agent.runs[-1]["command"] == "set_display_profile"
    assert agent.runs[-1]["vars"] == {"Profile": "TV-Mode"}
    assert state_of(hass, select) == "TV-Mode"


async def test_select_with_state_metric_follows_the_metric(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The option shown is the value of the metric."""
    select = eid(hass, agent, "select", "set_output")
    await agent.wait_for(lambda: state_of(hass, select) == "speakers")

    await call(hass, "select", "select_option", select, option="headphones")
    assert agent.runs[-1]["vars"] == {"Device": "headphones"}
    assert state_of(hass, select) == "speakers"

    await agent.send_state({"audio.output": "headphones"})
    await agent.wait_for(lambda: state_of(hass, select) == "headphones")


# -- number -----------------------------------------------------------------------


async def test_number_takes_min_max_step_and_unit_from_the_variable(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The limits and the unit of the parameter definition reach the entity."""
    volume = eid(hass, agent, "number", "set_volume")
    assert attribute_of(hass, volume, "min") == 0
    assert attribute_of(hass, volume, "max") == 100
    assert attribute_of(hass, volume, "step") == 1
    assert attribute_of(hass, volume, "unit_of_measurement") == "%"

    gamma = eid(hass, agent, "number", "set_gamma")
    assert attribute_of(hass, gamma, "min") == 0.5
    assert attribute_of(hass, gamma, "max") == 2.0
    assert attribute_of(hass, gamma, "step") == 0.1
    assert attribute_of(hass, gamma, "unit_of_measurement") == "x"


async def test_number_with_state_metric_sends_an_integer_and_follows_the_metric(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """An ``int`` variable gets a whole number; the state is the metric's."""
    volume = eid(hass, agent, "number", "set_volume")
    assert float(state_of(hass, volume)) == 35

    await call(hass, "number", "set_value", volume, value=40)

    sent = agent.runs[-1]["vars"]["Level"]
    assert sent == 40
    assert isinstance(sent, int)
    assert float(state_of(hass, volume)) == 35

    await agent.send_state({"audio.volume": 40})
    await agent.wait_for(lambda: float(state_of(hass, volume)) == 40)


async def test_optimistic_float_number_shows_what_it_sent(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """A ``float`` variable is sent as it is and shown without a metric."""
    gamma = eid(hass, agent, "number", "set_gamma")
    assert attribute_of(hass, gamma, "assumed_state") is True

    await call(hass, "number", "set_value", gamma, value=1.5)

    assert agent.runs[-1]["vars"] == {"Factor": 1.5}
    assert float(state_of(hass, gamma)) == 1.5


async def test_number_is_unavailable_while_its_metric_has_no_value(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """A ``null`` value of the state metric makes the entity unavailable."""
    volume = eid(hass, agent, "number", "set_volume")

    await agent.send_state({"audio.volume": None})

    await agent.wait_for(lambda: state_of(hass, volume) == "unavailable")


# -- text -------------------------------------------------------------------------


async def test_text_takes_limits_and_sends_the_value(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """Maximum length and pattern come from the variable; the value is sent."""
    text = eid(hass, agent, "text", "say_text")
    assert attribute_of(hass, text, "max") == 200
    assert attribute_of(hass, text, "pattern") == "^[A-Za-z0-9 .,!?-]+$"

    await call(hass, "text", "set_value", text, value="Hello world")

    assert agent.runs[-1]["command"] == "say_text"
    assert agent.runs[-1]["vars"] == {"Message": "Hello world"}
    assert state_of(hass, text) == "Hello world"


# -- state metric and subscription ------------------------------------------------


async def test_state_metrics_of_enabled_controls_are_subscribed(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The metric of an enabled control is subscribed even without a sensor.

    Disabling the control removes the metric from the subscription again.
    """
    await agent.wait_for(lambda: "night.light" in last_subscription(agent))
    assert "audio.output" in last_subscription(agent)

    er.async_get(hass).async_update_entity(
        eid(hass, agent, "switch", "set_night_light"),
        disabled_by=RegistryEntryDisabler.USER,
    )

    await agent.wait_for(lambda: "night.light" not in last_subscription(agent))
    # The number and the sensor of the volume are still enabled.
    assert "audio.volume" in last_subscription(agent)
    assert "audio.output" in last_subscription(agent)


# -- dynamic changes --------------------------------------------------------------


async def test_added_and_removed_commands_change_the_entities_without_a_reload(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """New commands get entities, vanished ones lose them; the entry stays loaded."""
    runtime = entry.runtime_data
    connections = len(agent.connections)
    say_text = eid(hass, agent, "text", "say_text")
    assert hass.states.get(say_text) is not None

    agent.set_manifest(
        changed_manifest(
            agent,
            rev=SECOND_REV,
            add_commands=[NEW_COMMAND],
            remove_commands=["say_text"],
        )
    )
    await agent.send_manifest_changed()

    def applied() -> bool:
        new = find_entity_id(hass, "button", agent.agent_id, "tidy_desktop")
        gone = find_entity_id(hass, "text", agent.agent_id, "say_text")
        return new is not None and gone is None

    await agent.wait_for(applied)
    await hass.async_block_till_done()
    assert hass.states.get(say_text) is None
    tidy = eid(hass, agent, "button", "tidy_desktop")
    assert state_of(hass, tidy) != "unavailable"
    assert entry.state is ConfigEntryState.LOADED
    assert entry.runtime_data is runtime
    assert len(agent.connections) == connections

    await call(hass, "button", "press", tidy)
    assert agent.runs[-1]["command"] == "tidy_desktop"


async def test_changed_command_updates_its_entity_and_keeps_the_entity_id(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """New options of a select appear on the same entity."""
    select = eid(hass, agent, "select", "set_display_profile")
    manifest = changed_manifest(agent, rev=SECOND_REV)
    for command in manifest["commands"]:
        if command["id"] == "set_display_profile":
            command["vars"][0]["options"].append("Study-Mode")
    agent.set_manifest(manifest)

    await agent.send_manifest_changed()

    await agent.wait_for(
        lambda: "Study-Mode" in (attribute_of(hass, select, "options") or [])
    )
    assert eid(hass, agent, "select", "set_display_profile") == select


async def test_command_with_another_control_type_moves_to_the_other_platform(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The entity of the old platform leaves the registry, the new one appears."""
    as_switch: dict[str, Any] = {
        "id": "lock_session",
        "name": "Lock session",
        "vars": [{"name": "Locked", "type": "bool", "required": True}],
        "control": {"type": "switch", "var": "Locked"},
    }
    agent.set_manifest(
        changed_manifest(
            agent,
            rev=SECOND_REV,
            remove_commands=["lock_session"],
            add_commands=[as_switch],
        )
    )

    await agent.send_manifest_changed()

    def moved() -> bool:
        new = find_entity_id(hass, "switch", agent.agent_id, "lock_session")
        old = find_entity_id(hass, "button", agent.agent_id, "lock_session")
        return new is not None and old is None

    await agent.wait_for(moved)


# -- errors -----------------------------------------------------------------------


async def test_failing_command_raises_an_error_with_the_message_of_the_agent(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """An error result becomes a translated Home Assistant error."""
    agent.run_errors["lock_session"] = ("no_session", "Nobody is logged in.")

    with pytest.raises(HomeAssistantError) as error:
        await call(hass, "button", "press", eid(hass, agent, "button", "lock_session"))

    assert error.value.translation_domain == "qoffeeautomat"
    assert error.value.translation_key == "command_failed"
    assert error.value.translation_placeholders == {
        "command": "lock_session",
        "message": "Nobody is logged in.",
    }


async def test_failing_optimistic_switch_keeps_its_state(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """When the agent refuses the command, nothing is assumed."""
    switch = eid(hass, agent, "switch", "set_mute")
    agent.run_errors["set_mute"] = ("busy", "Command is already running.")

    with pytest.raises(HomeAssistantError) as error:
        await call(hass, "switch", "turn_on", switch)

    assert error.value.translation_placeholders is not None
    assert error.value.translation_placeholders["message"] == (
        "Command is already running."
    )
    assert state_of(hass, switch) == "unknown"


async def test_rejected_variables_are_a_validation_error(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """``invalid_vars`` is the user's mistake: ``ServiceValidationError``."""
    number = eid(hass, agent, "number", "set_gamma")
    agent.run_errors["set_gamma"] = ("invalid_vars", "Factor must be at most 2.")

    with pytest.raises(ServiceValidationError) as error:
        await call(hass, "number", "set_value", number, value=1.9)

    assert error.value.translation_key == "command_rejected"
    placeholders = error.value.translation_placeholders
    assert placeholders is not None
    assert placeholders["message"] == "Factor must be at most 2."


def test_client_errors_are_translated() -> None:
    """Every kind of client error has a translated Home Assistant error.

    (Home Assistant does not call entities that are unavailable, so the offline case
    cannot be driven through an entity service; the action tests cover it end to end.)
    """
    offline = translate_error("lock_session", NotConnectedError("not connected"))
    assert type(offline) is HomeAssistantError
    assert offline.translation_key == "agent_offline"

    slow = translate_error("lock_session", RequestTimeoutError("too slow"))
    assert type(slow) is HomeAssistantError
    assert slow.translation_key == "command_timeout"

    refused = translate_error("lock_session", RequestError("unknown_command", "No."))
    assert type(refused) is ServiceValidationError
    assert refused.translation_key == "command_rejected"
    assert refused.translation_placeholders == {
        "command": "lock_session",
        "message": "No.",
    }

    busy = translate_error("lock_session", RequestError("busy", "Busy."))
    assert type(busy) is HomeAssistantError
    assert busy.translation_key == "command_failed"

    other = translate_error("lock_session", UnexpectedResponseError("bad result"))
    assert type(other) is HomeAssistantError
    assert other.translation_key == "command_failed"
