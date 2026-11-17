"""Tests of the notifications from Home Assistant on the PC (T702).

The notify entity exists only while the manifest lists the feature ``notify``. It and
the action ``qoffeeautomat.send_notification`` run the command ``agent_notify`` with the
message, the title, the picture and up to three buttons as variables; a press of a
button comes back as the agent event ``notification_action``. The toast itself is shown
by Windows and checked by hand.
"""

from typing import Any

import pytest
import voluptuous as vol
from homeassistant.core import Context, HomeAssistant
from homeassistant.exceptions import HomeAssistantError, ServiceValidationError
from homeassistant.helpers import device_registry as dr
from homeassistant.helpers import entity_registry as er
from pytest_homeassistant_custom_component.common import (
    MockConfigEntry,
    async_capture_events,
)

from custom_components.qoffeeautomat.const import (
    COMMAND_NOTIFY,
    DOMAIN,
    EVENT_QOFFEEAUTOMAT,
    EVENT_TYPE_NOTIFICATION_ACTION,
    FEATURE_NOTIFY,
    NOTIFY_KEY,
)
from custom_components.qoffeeautomat.notifications import build_notification_variables
from tests_integration.fake_agent import FakeAgent
from tests_integration.helpers import (
    NEW_REV,
    SECOND_REV,
    EntryFactory,
    attribute_of,
    changed_manifest,
    entity_id_of,
    find_entity_id,
    state_of,
)

USER_ID = "8f1c2d3e4a5b6c7d8e9f0a1b2c3d4e5f"

# The command that an agent with the feature notify provides (D-044): a message, an
# optional title, picture and tag, and three buttons with an id and a label each.
NOTIFY_COMMAND: dict[str, Any] = {
    "id": COMMAND_NOTIFY,
    "name": "Show notification",
    "icon": "mdi:message-badge",
    "vars": [
        {"name": "Title", "type": "string", "max_length": 128},
        {"name": "Message", "type": "string", "required": True, "max_length": 1024},
        {"name": "ImageUrl", "type": "string", "max_length": 2048},
        {"name": "Tag", "type": "string", "max_length": 64},
        {"name": "Action1Id", "type": "string", "max_length": 64},
        {"name": "Action1Title", "type": "string", "max_length": 64},
        {"name": "Action2Id", "type": "string", "max_length": 64},
        {"name": "Action2Title", "type": "string", "max_length": 64},
        {"name": "Action3Id", "type": "string", "max_length": 64},
        {"name": "Action3Title", "type": "string", "max_length": 64},
    ],
}
ACTION_EVENT: dict[str, str] = {
    "type": EVENT_TYPE_NOTIFICATION_ACTION,
    "name": "Notification button pressed",
}


def notifying_manifest(agent: FakeAgent, **kwargs: Any) -> dict[str, Any]:
    """Return the manifest of an agent that provides notifications."""
    manifest = changed_manifest(agent, add_commands=[NOTIFY_COMMAND], **kwargs)
    manifest["features"] = [FEATURE_NOTIFY]
    manifest["events"] = [*manifest["events"], ACTION_EVENT]
    return manifest


@pytest.fixture(name="agent")
def agent_fixture(fake_agent: FakeAgent) -> FakeAgent:
    """The fake agent with the feature notify and its command."""
    fake_agent.set_manifest(
        notifying_manifest(fake_agent, rev=fake_agent.manifest["rev"])
    )
    return fake_agent


@pytest.fixture(name="entry")
async def entry_fixture(
    agent: FakeAgent, entry_factory: EntryFactory
) -> MockConfigEntry:
    """Set up the entry of the notifying agent."""
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


def notify_entity(hass: HomeAssistant, agent: FakeAgent) -> str:
    """Return the entity id of the notify entity."""
    return entity_id_of(hass, "notify", agent.agent_id, NOTIFY_KEY)


async def send_message(hass: HomeAssistant, entity_id: str, **data: Any) -> None:
    """Call ``notify.send_message`` the way an automation does."""
    await hass.services.async_call(
        "notify", "send_message", {"entity_id": entity_id, **data}, blocking=True
    )


async def send_notification(
    hass: HomeAssistant,
    device_id: str,
    *,
    context: Context | None = None,
    **fields: Any,
) -> None:
    """Call ``qoffeeautomat.send_notification`` for a device."""
    await hass.services.async_call(
        DOMAIN,
        "send_notification",
        {"device_id": device_id, **fields},
        blocking=True,
        context=context,
    )


# -- the entity exists only with the feature ------------------------------------------


async def test_the_notify_entity_exists_when_the_manifest_lists_the_feature(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The unique id is the reserved key; the name comes from the translation."""
    entity_id = notify_entity(hass, agent)

    registered = er.async_get(hass).async_get(entity_id)
    assert registered is not None
    assert registered.unique_id == f"{agent.agent_id}_{NOTIFY_KEY}"
    assert registered.translation_key == "notification"
    assert attribute_of(hass, entity_id, "friendly_name").endswith("Notification")


async def test_without_the_feature_there_is_no_notify_entity(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """A manifest that does not list the feature creates no entity of the platform."""
    manifest = notifying_manifest(fake_agent)
    manifest["features"] = ["wol"]
    fake_agent.set_manifest(manifest)

    await entry_factory()

    assert find_entity_id(hass, "notify", fake_agent.agent_id, NOTIFY_KEY) is None
    assert not hass.states.async_entity_ids("notify")


async def test_a_manifest_without_any_features_creates_no_notify_entity(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """An agent that announces nothing has no notifications."""
    manifest = changed_manifest(fake_agent)
    manifest["features"] = []
    fake_agent.set_manifest(manifest)

    await entry_factory()

    assert find_entity_id(hass, "notify", fake_agent.agent_id, NOTIFY_KEY) is None


async def test_the_entity_follows_the_feature_without_a_reload(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The feature goes away and comes back: entity and registry follow."""
    assert find_entity_id(hass, "notify", agent.agent_id, NOTIFY_KEY) is not None
    connections = len(agent.connections)

    without = notifying_manifest(agent, rev=NEW_REV)
    without["features"] = []
    agent.set_manifest(without)
    await agent.send_manifest_changed()
    await agent.wait_for(
        lambda: find_entity_id(hass, "notify", agent.agent_id, NOTIFY_KEY) is None
    )
    await hass.async_block_till_done()
    assert not hass.states.async_entity_ids("notify")

    agent.set_manifest(notifying_manifest(agent, rev=SECOND_REV))
    await agent.send_manifest_changed()
    await agent.wait_for(
        lambda: find_entity_id(hass, "notify", agent.agent_id, NOTIFY_KEY) is not None
    )
    await hass.async_block_till_done()

    assert hass.states.async_entity_ids("notify")
    assert len(agent.connections) == connections


async def test_an_entry_that_an_earlier_run_left_behind_is_removed_without_the_feature(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """The agent was updated and no longer has the feature: no unavailable leftover."""
    manifest = changed_manifest(fake_agent)
    manifest["features"] = []
    fake_agent.set_manifest(manifest)
    entry = await entry_factory(setup=False)
    er.async_get(hass).async_get_or_create(
        "notify",
        DOMAIN,
        f"{fake_agent.agent_id}_{NOTIFY_KEY}",
        config_entry=entry,
    )

    await hass.config_entries.async_setup(entry.entry_id)
    await hass.async_block_till_done()

    assert find_entity_id(hass, "notify", fake_agent.agent_id, NOTIFY_KEY) is None


# -- notify.send_message -------------------------------------------------------------


async def test_send_message_runs_the_command_with_message_and_title(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The values travel as variables and the call waits for the result."""
    await send_message(
        hass, notify_entity(hass, agent), message="It is ready.", title="Dinner"
    )

    frame = agent.runs[-1]
    assert frame["command"] == COMMAND_NOTIFY
    assert frame["vars"] == {"Message": "It is ready.", "Title": "Dinner"}
    assert frame["wait"] is True


async def test_send_message_without_title_sends_only_the_message(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    await send_message(hass, notify_entity(hass, agent), message="Hello")

    assert agent.runs[-1]["vars"] == {"Message": "Hello"}


async def test_the_notify_entity_shows_the_time_of_the_last_notification(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The state of a notify entity is the time of its last use."""
    entity_id = notify_entity(hass, agent)
    assert state_of(hass, entity_id) == "unknown"

    await send_message(hass, entity_id, message="Hello")

    assert state_of(hass, entity_id) != "unknown"


async def test_markup_and_quotes_in_the_text_are_values_and_nothing_else(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """Nothing is pasted into a script (D-012): the text arrives as it is."""
    text = "<b>\"quoted\"</b> & 'single' $(rm -rf /) {{ not a template }}"

    await send_message(hass, notify_entity(hass, agent), message=text)

    assert agent.runs[-1]["vars"]["Message"] == text


# -- qoffeeautomat.send_notification -------------------------------------------------


async def test_send_notification_passes_picture_tag_and_buttons_as_variables(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """Every button becomes a pair of variables, in order."""
    await send_notification(
        hass,
        device_id_of(hass, agent),
        title="Doorbell",
        message="Somebody is at the door.",
        image_url="http://homeassistant.local:8123/local/door.png",
        tag="door",
        actions=[
            {"action": "open_door", "title": "Open the door"},
            {"action": "ignore", "title": "Ignore"},
        ],
    )

    frame = agent.runs[-1]
    assert frame["command"] == COMMAND_NOTIFY
    assert frame["vars"] == {
        "Message": "Somebody is at the door.",
        "Title": "Doorbell",
        "ImageUrl": "http://homeassistant.local:8123/local/door.png",
        "Tag": "door",
        "Action1Id": "open_door",
        "Action1Title": "Open the door",
        "Action2Id": "ignore",
        "Action2Title": "Ignore",
    }
    assert frame["wait"] is True


async def test_three_buttons_fit(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    await send_notification(
        hass,
        device_id_of(hass, agent),
        message="Pick one",
        actions=[{"action": name, "title": name.upper()} for name in ("a", "b", "c")],
    )

    variables = agent.runs[-1]["vars"]
    assert [variables[f"Action{n}Id"] for n in (1, 2, 3)] == ["a", "b", "c"]
    assert [variables[f"Action{n}Title"] for n in (1, 2, 3)] == ["A", "B", "C"]


async def test_the_user_of_the_call_goes_to_the_agent(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The agent records who triggered the run."""
    await send_notification(
        hass,
        device_id_of(hass, agent),
        message="Hello",
        context=Context(user_id=USER_ID),
    )

    assert agent.runs[-1]["context"] == {"user_id": USER_ID}


async def test_more_than_three_buttons_are_refused_before_anything_is_sent(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    runs = len(agent.runs)

    with pytest.raises(ServiceValidationError, match="at most 3 buttons"):
        await send_notification(
            hass,
            device_id_of(hass, agent),
            message="Too many",
            actions=[{"action": f"a{n}", "title": "x"} for n in range(4)],
        )

    assert len(agent.runs) == runs


@pytest.mark.parametrize(
    "action_id", ["bad id", "semi;colon", "ümlaut", "", "x" * 65, "new\nline"]
)
async def test_an_id_that_is_not_a_plain_id_is_refused(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry, action_id: str
) -> None:
    """The id comes back in an event and travels in the arguments of a toast."""
    runs = len(agent.runs)

    with pytest.raises(ServiceValidationError, match="not valid"):
        await send_notification(
            hass,
            device_id_of(hass, agent),
            message="Hello",
            actions=[{"action": action_id, "title": "Go"}],
        )

    assert len(agent.runs) == runs


async def test_a_tag_that_is_not_a_plain_id_is_refused(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    with pytest.raises(ServiceValidationError, match="tag"):
        await send_notification(
            hass, device_id_of(hass, agent), message="Hello", tag="bad tag"
        )


@pytest.mark.parametrize("label", ["", "   "])
async def test_a_button_needs_a_label(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry, label: str
) -> None:
    with pytest.raises(ServiceValidationError, match="needs a title"):
        await send_notification(
            hass,
            device_id_of(hass, agent),
            message="Hello",
            actions=[{"action": "go", "title": label}],
        )


async def test_the_id_of_a_button_is_used_once(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    with pytest.raises(ServiceValidationError, match="used twice"):
        await send_notification(
            hass,
            device_id_of(hass, agent),
            message="Hello",
            actions=[
                {"action": "go", "title": "One"},
                {"action": "go", "title": "Two"},
            ],
        )


async def test_the_schema_requires_a_message_and_a_picture_address(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    device_id = device_id_of(hass, agent)

    with pytest.raises(vol.Invalid):
        await send_notification(hass, device_id)
    with pytest.raises(vol.Invalid):
        await send_notification(hass, device_id, message="Hello", image_url="no url")
    with pytest.raises(vol.Invalid):
        await send_notification(
            hass, device_id, message="Hello", actions=[{"action": "go"}]
        )


# -- what the agent answers ----------------------------------------------------------


async def test_a_notification_that_windows_does_not_show_is_an_error(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The exit code and the message of the agent reach the automation."""
    agent.run_result = {
        "run_id": "01JD8Z3Q4V7N2K9M5X6C8B0A1E",
        "exit_code": 1,
        "stdout": "",
        "stderr": "Windows does not show notifications from QoffeeAutomat.",
        "duration_ms": 12,
        "timed_out": False,
    }

    with pytest.raises(HomeAssistantError, match="does not show notifications"):
        await send_message(hass, notify_entity(hass, agent), message="Hello")


async def test_a_notification_that_timed_out_is_an_error(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    agent.run_result = {
        "run_id": "01JD8Z3Q4V7N2K9M5X6C8B0A1E",
        "exit_code": None,
        "timed_out": True,
    }

    with pytest.raises(HomeAssistantError, match="timed out"):
        await send_message(hass, notify_entity(hass, agent), message="Hello")


async def test_a_run_without_exit_code_data_counts_as_shown(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """An answer that carries no exit code is not a failure."""
    agent.run_result = {"run_id": "01JD8Z3Q4V7N2K9M5X6C8B0A1E"}

    await send_message(hass, notify_entity(hass, agent), message="Hello")


async def test_nobody_logged_in_is_an_error_with_the_message_of_the_agent(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    agent.run_errors[COMMAND_NOTIFY] = ("no_session", "Nobody is logged in.")

    with pytest.raises(HomeAssistantError, match="Nobody is logged in"):
        await send_message(hass, notify_entity(hass, agent), message="Hello")


async def test_values_that_the_agent_refuses_are_a_validation_error(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    agent.run_errors[COMMAND_NOTIFY] = ("invalid_vars", "Message is too long.")

    with pytest.raises(ServiceValidationError, match="Message is too long"):
        await send_notification(
            hass, device_id_of(hass, agent), message="Hello " * 1000
        )


async def test_an_agent_that_has_the_feature_but_not_the_command_is_an_error(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """Without the command nothing can be sent, and the error says so."""
    # The manifest of the fixture lists the feature notify but has no such command.
    await entry_factory()

    with pytest.raises(HomeAssistantError, match="does not offer notifications"):
        await send_message(
            hass,
            entity_id_of(hass, "notify", fake_agent.agent_id, NOTIFY_KEY),
            message="Hello",
        )


async def test_a_pc_that_is_off_cannot_show_a_notification(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    await agent.send_bye("shutdown")
    await hass.async_block_till_done()

    with pytest.raises(HomeAssistantError):
        await send_notification(hass, device_id_of(hass, agent), message="Hello")


# -- the way back: a press of a button -----------------------------------------------


async def test_a_press_of_a_button_fires_the_bus_event_with_the_action(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """Automations trigger on the bus event; ``data`` holds the action and the tag."""
    events = async_capture_events(hass, EVENT_QOFFEEAUTOMAT)

    await agent.send_event(
        EVENT_TYPE_NOTIFICATION_ACTION, {"action": "open_door", "tag": "door"}
    )
    await agent.wait_for(lambda: bool(events))
    await hass.async_block_till_done()

    assert events[-1].data["type"] == EVENT_TYPE_NOTIFICATION_ACTION
    assert events[-1].data["data"] == {"action": "open_door", "tag": "door"}
    assert events[-1].data["device_id"] == device_id_of(hass, agent)


async def test_the_event_entity_offers_the_type_of_the_press(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The agent lists the event, so the event entity of the device has the type."""
    entity_id = entity_id_of(hass, "event", agent.agent_id, "_events")

    assert EVENT_TYPE_NOTIFICATION_ACTION in attribute_of(
        hass, entity_id, "event_types"
    )

    await agent.send_event(EVENT_TYPE_NOTIFICATION_ACTION, {"action": "ignore"})
    await agent.wait_for(
        lambda: (
            attribute_of(hass, entity_id, "event_type")
            == EVENT_TYPE_NOTIFICATION_ACTION
        )
    )

    assert attribute_of(hass, entity_id, "action") == "ignore"


# -- the variables on their own ------------------------------------------------------


def test_the_variables_of_a_plain_message_are_the_message() -> None:
    assert build_notification_variables(message="Hello") == {"Message": "Hello"}


def test_empty_optional_values_are_left_out() -> None:
    assert build_notification_variables(
        message="Hello", title="", image_url=None, tag="", actions=[]
    ) == {"Message": "Hello"}


def test_the_names_of_the_variables_are_those_of_the_action() -> None:
    variables = build_notification_variables(
        message="m",
        title="t",
        image_url="https://example.com/x.png",
        tag="tag",
        actions=[{"action": "a", "title": "A"}],
    )

    assert set(variables) == {
        "Message",
        "Title",
        "ImageUrl",
        "Tag",
        "Action1Id",
        "Action1Title",
    }
