"""Tests of the run_command action, the event entity and the bus event (T506)."""

from typing import Any

import pytest
import voluptuous as vol
from homeassistant.core import Context, HomeAssistant
from homeassistant.exceptions import HomeAssistantError, ServiceValidationError
from homeassistant.helpers import device_registry as dr
from pytest_homeassistant_custom_component.common import (
    MockConfigEntry,
    async_capture_events,
)

from custom_components.qoffeeautomat.const import (
    DOMAIN,
    EVENT_QOFFEEAUTOMAT,
    MAX_OUTPUT_CHARS,
)
from tests_integration.fake_agent import FakeAgent, load_fixture
from tests_integration.helpers import (
    SECOND_REV,
    EntryFactory,
    attribute_of,
    changed_manifest,
    entity_id_of,
    reconnect,
    state_of,
)

USER_ID = "8f1c2d3e4a5b6c7d8e9f0a1b2c3d4e5f"
RUN_ID = "01JD8Z3Q4V7N2K9M5X6C8B0A1E"


@pytest.fixture(name="entry")
async def entry_fixture(entry_factory: EntryFactory) -> MockConfigEntry:
    """Set up the entry of the fake agent."""
    return await entry_factory()


def device_id_of(hass: HomeAssistant, agent: FakeAgent) -> str:
    """Return the id of the device of the agent in the device registry."""
    registry = dr.async_get(hass)
    for config_entry in hass.config_entries.async_entries(DOMAIN):
        device = registry.async_get_device_by_identifier(
            (DOMAIN, agent.agent_id), config_entry.entry_id
        )
        if device is not None:
            return device.id
    raise AssertionError("device of the agent not found")


async def run_action(
    hass: HomeAssistant,
    device_id: str | list[str],
    command: str,
    *,
    context: Context | None = None,
    **fields: Any,
) -> dict[str, Any]:
    """Call ``qoffeeautomat.run_command`` and return its response data."""
    response = await hass.services.async_call(
        DOMAIN,
        "run_command",
        {"device_id": device_id, "command": command, **fields},
        blocking=True,
        return_response=True,
        context=context,
    )
    assert response is not None
    return dict(response)


def event_entity(hass: HomeAssistant, agent: FakeAgent) -> str:
    """Return the entity id of the event entity of the agent."""
    return entity_id_of(hass, "event", agent.agent_id, "_events")


# -- run_command ------------------------------------------------------------------


async def test_action_is_registered(
    hass: HomeAssistant, entry: MockConfigEntry
) -> None:
    """The action exists once the integration is set up."""
    assert hass.services.has_service(DOMAIN, "run_command")


async def test_run_command_with_variables_returns_the_result_as_response(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """Variables go to the agent as values; exit code and output come back."""
    response = await run_action(
        hass,
        device_id_of(hass, fake_agent),
        "set_display_profile",
        variables={"Profile": "TV-Mode", "UpdateAdapterIds": False},
    )

    frame = fake_agent.runs[-1]
    assert frame["command"] == "set_display_profile"
    assert frame["vars"] == {"Profile": "TV-Mode", "UpdateAdapterIds": False}
    assert frame["wait"] is True
    assert response == {
        "run_id": RUN_ID,
        "exit_code": 0,
        "stdout": "Applied TV-Mode\n",
        "stderr": "",
        "duration_ms": 412,
        "timed_out": False,
        "truncated": False,
    }


async def test_run_command_without_variables_sends_none(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The default variables of the action are an empty object."""
    await run_action(hass, device_id_of(hass, fake_agent), "lock_session")

    assert fake_agent.runs[-1]["command"] == "lock_session"
    assert fake_agent.runs[-1].get("vars", {}) == {}


async def test_run_command_passes_the_id_of_the_calling_user(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The user of the call is in the run context (the agent audits it)."""
    device_id = device_id_of(hass, fake_agent)

    await run_action(hass, device_id, "lock_session", context=Context(user_id=USER_ID))
    assert fake_agent.runs[-1]["context"] == {"user_id": USER_ID}

    await run_action(hass, device_id, "lock_session")
    assert "context" not in fake_agent.runs[-1]


async def test_run_command_without_wait_returns_only_the_run_id(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """A command that is not waited for has no exit code yet."""
    response = await run_action(
        hass, device_id_of(hass, fake_agent), "lock_session", wait=False
    )

    assert fake_agent.runs[-1]["wait"] is False
    assert response == {"run_id": RUN_ID}


async def test_run_command_caps_long_output_and_reports_it(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """Output beyond the response limit is cut and ``truncated`` says so."""
    fake_agent.run_result = {
        **load_fixture("result.run.json")["data"],
        "stdout": "x" * (MAX_OUTPUT_CHARS + 10),
    }

    response = await run_action(hass, device_id_of(hass, fake_agent), "lock_session")

    assert response["stdout"] == "x" * MAX_OUTPUT_CHARS
    assert response["truncated"] is True


async def test_run_command_times_out_with_the_given_timeout(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """An agent that does not answer within ``timeout`` raises an error."""
    device_id = device_id_of(hass, fake_agent)
    fake_agent.silent = True

    with pytest.raises(HomeAssistantError) as error:
        await run_action(hass, device_id, "lock_session", timeout=1)

    assert not isinstance(error.value, ServiceValidationError)
    assert error.value.translation_key == "command_timeout"


# -- errors -----------------------------------------------------------------------


async def test_unknown_command_is_a_validation_error(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """A command that the agent does not know raises ``ServiceValidationError``."""
    with pytest.raises(ServiceValidationError) as error:
        await run_action(hass, device_id_of(hass, fake_agent), "nope")

    assert error.value.translation_domain == DOMAIN
    assert error.value.translation_key == "command_rejected"
    assert error.value.translation_placeholders == {
        "command": "nope",
        "message": "Command 'nope' does not exist.",
    }


async def test_invalid_variables_are_a_validation_error(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """Variables that the agent rejects raise ``ServiceValidationError``."""
    fake_agent.run_errors["set_display_profile"] = (
        "invalid_vars",
        "Profile must be one of Normal-Mode, TV-Mode.",
    )

    with pytest.raises(ServiceValidationError) as error:
        await run_action(
            hass,
            device_id_of(hass, fake_agent),
            "set_display_profile",
            variables={"Profile": "Cinema"},
        )

    placeholders = error.value.translation_placeholders
    assert placeholders is not None
    assert placeholders["message"] == "Profile must be one of Normal-Mode, TV-Mode."


async def test_other_agent_errors_are_home_assistant_errors(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """A failure of the run itself is not the caller's mistake."""
    fake_agent.run_errors["lock_session"] = ("no_session", "Nobody is logged in.")

    with pytest.raises(HomeAssistantError) as error:
        await run_action(hass, device_id_of(hass, fake_agent), "lock_session")

    assert not isinstance(error.value, ServiceValidationError)
    assert error.value.translation_key == "command_failed"
    placeholders = error.value.translation_placeholders
    assert placeholders is not None
    assert placeholders["message"] == "Nobody is logged in."


async def test_offline_agent_raises_a_home_assistant_error(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """With the PC switched off the action fails with ``HomeAssistantError``."""
    device_id = device_id_of(hass, fake_agent)
    client = entry.runtime_data.client
    await fake_agent.stop()
    await fake_agent.wait_for(lambda: not client.connected)

    with pytest.raises(HomeAssistantError) as error:
        await run_action(hass, device_id, "lock_session")

    assert not isinstance(error.value, ServiceValidationError)
    assert error.value.translation_key == "agent_offline"


async def test_agent_of_an_entry_that_is_not_loaded_is_offline(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """A device whose entry is not loaded (for example retrying) has no connection."""
    device_id = device_id_of(hass, fake_agent)
    await hass.config_entries.async_unload(entry.entry_id)

    with pytest.raises(HomeAssistantError) as error:
        await run_action(hass, device_id, "lock_session")

    assert error.value.translation_key == "agent_offline"


async def test_unknown_device_is_a_validation_error(
    hass: HomeAssistant, entry: MockConfigEntry
) -> None:
    """A device id that is not a QoffeeAutomat device is refused."""
    with pytest.raises(ServiceValidationError) as error:
        await run_action(hass, "no-such-device", "lock_session")

    assert error.value.translation_key == "device_not_found"


async def test_exactly_one_device_is_required(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The response of one action belongs to one PC."""
    device_id = device_id_of(hass, fake_agent)

    with pytest.raises(ServiceValidationError) as error:
        await run_action(hass, [device_id, device_id], "lock_session")

    assert error.value.translation_key == "single_device_required"
    assert fake_agent.runs == []


async def test_schema_requires_a_command(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The fields are validated before anything is sent."""
    with pytest.raises(vol.Invalid):
        await hass.services.async_call(
            DOMAIN,
            "run_command",
            {"device_id": device_id_of(hass, fake_agent)},
            blocking=True,
            return_response=True,
        )

    assert fake_agent.runs == []


# -- agent events -----------------------------------------------------------------


async def test_agent_event_triggers_the_entity_and_the_bus_event(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """An ``event`` frame fires ``qoffeeautomat_event`` and the event entity."""
    events = async_capture_events(hass, EVENT_QOFFEEAUTOMAT)
    entity_id = event_entity(hass, fake_agent)
    assert state_of(hass, entity_id) == "unknown"
    assert "session_locked" in attribute_of(hass, entity_id, "event_types")

    await fake_agent.send_event("session_locked", {"user": "alice"})

    await fake_agent.wait_for(lambda: len(events) == 1)
    assert events[0].data == {
        "device_id": device_id_of(hass, fake_agent),
        "type": "session_locked",
        "data": {"user": "alice"},
    }
    await fake_agent.wait_for(
        lambda: attribute_of(hass, entity_id, "event_type") == "session_locked"
    )
    assert attribute_of(hass, entity_id, "user") == "alice"


async def test_agent_event_without_data_has_empty_data(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The ``data`` of the bus event is always an object."""
    events = async_capture_events(hass, EVENT_QOFFEEAUTOMAT)

    await fake_agent.send_event("session_unlocked")

    await fake_agent.wait_for(lambda: len(events) == 1)
    assert events[0].data["type"] == "session_unlocked"
    assert events[0].data["data"] == {}


async def test_event_type_that_is_not_in_the_manifest_only_fires_on_the_bus(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The event entity knows only the announced types; the bus event has them all."""
    events = async_capture_events(hass, EVENT_QOFFEEAUTOMAT)
    entity_id = event_entity(hass, fake_agent)

    await fake_agent.send_event("mystery", {"n": 1})
    await fake_agent.send_event("session_locked")

    await fake_agent.wait_for(lambda: len(events) == 2)
    assert [event.data["type"] for event in events] == ["mystery", "session_locked"]
    await fake_agent.wait_for(
        lambda: attribute_of(hass, entity_id, "event_type") == "session_locked"
    )


async def test_event_types_follow_the_manifest(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """A new event type of the agent is offered by the event entity."""
    entity_id = event_entity(hass, fake_agent)
    manifest = changed_manifest(fake_agent, rev=SECOND_REV)
    manifest["events"].append({"type": "session_ready", "name": "Session ready"})
    fake_agent.set_manifest(manifest)

    await fake_agent.send_manifest_changed()

    await fake_agent.wait_for(
        lambda: "session_ready" in (attribute_of(hass, entity_id, "event_types") or [])
    )
    assert attribute_of(hass, entity_id, "event_types")[0] == "booted"


# -- booted -----------------------------------------------------------------------


async def test_booted_fires_when_the_boot_id_changed_before_the_setup(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """The first connection already sees another boot id: ``booted`` is delivered."""
    events = async_capture_events(hass, EVENT_QOFFEEAUTOMAT)

    await entry_factory(last_boot_id="the-boot-before")

    booted = [event for event in events if event.data["type"] == "booted"]
    assert len(booted) == 1
    assert booted[0].data["device_id"] == device_id_of(hass, fake_agent)
    assert booted[0].data["data"] == {}
    entity_id = event_entity(hass, fake_agent)
    assert attribute_of(hass, entity_id, "event_type") == "booted"


async def test_booted_fires_once_per_new_boot_id(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """A reconnect with a new boot id is a PC start; with the same id it is not."""
    events = async_capture_events(hass, EVENT_QOFFEEAUTOMAT)
    entity_id = event_entity(hass, fake_agent)
    client = entry.runtime_data.client

    await reconnect(fake_agent, client)
    assert events == []

    fake_agent.boot_id = "the-next-boot"
    await reconnect(fake_agent, client)
    await fake_agent.wait_for(lambda: len(events) == 1)
    await reconnect(fake_agent, client)

    assert [event.data["type"] for event in events] == ["booted"]
    assert events[0].data["device_id"] == device_id_of(hass, fake_agent)
    await fake_agent.wait_for(
        lambda: attribute_of(hass, entity_id, "event_type") == "booted"
    )
