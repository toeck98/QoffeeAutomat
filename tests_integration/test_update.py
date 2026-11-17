"""Tests of the update entity (T704, D-046).

The entity is composed of four metrics (``agent.version``, ``agent.latest_version``,
``agent.release_url`` and ``agent.release_notes``) and the command ``agent_update``; it
exists only while the manifest lists the feature ``update``. What the installer does on
a real PC, and whether the agent comes back with the new version, is checked by hand.
"""

from typing import Any

import pytest
from homeassistant.components.update import UpdateEntityFeature
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import HomeAssistantError
from homeassistant.helpers import entity_registry as er
from pytest_homeassistant_custom_component.common import MockConfigEntry
from pytest_homeassistant_custom_component.typing import WebSocketGenerator

from custom_components.qoffeeautomat.const import (
    COMMAND_AGENT_UPDATE,
    DOMAIN,
    FEATURE_UPDATE,
    UPDATE_KEY,
)
from custom_components.qoffeeautomat.update import UPDATE_METRICS, summarize
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

NOTES = (
    "## What is new\n\n- The update entity.\n- Fixes.\n\n"
    "Full list: https://github.com/example/QoffeeAutomat/releases"
)
RELEASE_URL = "https://github.com/example/QoffeeAutomat/releases/tag/v1.2.0"
RUN_ID = "01JD8Z3Q4V7N2K9M5X6C8B0A1E"


def _metric(metric_id: str, name: str) -> dict[str, Any]:
    """Return a version metric: text, off by default, diagnostic, of the system."""
    return {
        "id": metric_id,
        "name": name,
        "kind": "sensor",
        "value_type": "string",
        "entity_category": "diagnostic",
        "enabled_default": False,
        "context": "system",
    }


UPDATE_METRIC_DESCRIPTORS: list[dict[str, Any]] = [
    _metric("agent.version", "Agent version"),
    _metric("agent.latest_version", "Latest agent version"),
    _metric("agent.release_url", "Release page"),
    _metric("agent.release_notes", "Release notes"),
]

UPDATE_COMMAND: dict[str, Any] = {
    "id": COMMAND_AGENT_UPDATE,
    "name": "Update the agent",
    "vars": [],
}

NEWER: dict[str, Any] = {
    "agent.version": "1.1.0",
    "agent.latest_version": "1.2.0",
    "agent.release_url": RELEASE_URL,
    "agent.release_notes": NOTES,
}


def update_manifest(
    agent: FakeAgent,
    *,
    commands: tuple[dict[str, Any], ...] | None = None,
    remove_metrics: tuple[str, ...] = (),
    **kwargs: Any,
) -> dict[str, Any]:
    """Return the manifest of an agent that provides the update entity."""
    if commands is None:
        commands = (UPDATE_COMMAND,)
    dropped = set(remove_metrics)
    # What an earlier call put into the manifest of the agent is replaced, not doubled.
    manifest = changed_manifest(
        agent,
        add_metrics=tuple(
            m for m in UPDATE_METRIC_DESCRIPTORS if m["id"] not in dropped
        ),
        remove_metrics=(*(m["id"] for m in UPDATE_METRIC_DESCRIPTORS), *dropped),
        add_commands=commands,
        remove_commands=(COMMAND_AGENT_UPDATE,),
        **kwargs,
    )
    manifest["features"] = [FEATURE_UPDATE]
    return manifest


def update_entity(hass: HomeAssistant, agent: FakeAgent) -> str:
    """Return the entity id of the update entity."""
    return entity_id_of(hass, "update", agent.agent_id, UPDATE_KEY)


def subscribed(agent: FakeAgent) -> set[str]:
    """Return the metrics of the last ``subscribe`` request."""
    metrics = agent.subscribed_metrics()[-1]
    assert isinstance(metrics, list)
    return set(metrics)


async def wait_for_subscription(agent: FakeAgent) -> None:
    """Wait until the update entity has told the agent which metrics it follows."""
    await agent.wait_for(
        lambda: any(
            isinstance(metrics, list) and "agent.version" in metrics
            for metrics in agent.subscribed_metrics()
        )
    )


async def install(hass: HomeAssistant, entity_id: str) -> None:
    """Call ``update.install`` the way the dialog and an automation do."""
    await hass.services.async_call(
        "update", "install", {"entity_id": entity_id}, blocking=True
    )


@pytest.fixture(name="agent")
def agent_fixture(fake_agent: FakeAgent) -> FakeAgent:
    """The fake agent with the feature update, its metrics and command, behind."""
    fake_agent.set_manifest(update_manifest(fake_agent, rev=fake_agent.manifest["rev"]))
    fake_agent.snapshot = dict(NEWER)
    return fake_agent


@pytest.fixture(name="entry")
async def entry_fixture(
    hass: HomeAssistant, agent: FakeAgent, entry_factory: EntryFactory
) -> MockConfigEntry:
    """Set up the entry and wait for the first values of the update entity."""
    entry = await entry_factory()
    await wait_for_subscription(agent)
    entity_id = update_entity(hass, agent)
    await agent.wait_for(lambda: state_of(hass, entity_id) == "on")
    return entry


# -- the entity exists only with the feature ------------------------------------------


async def test_the_update_entity_exists_when_the_manifest_lists_the_feature(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The unique id is the reserved key; the name comes from the translation."""
    entity_id = update_entity(hass, agent)

    registered = er.async_get(hass).async_get(entity_id)
    assert registered is not None
    assert registered.unique_id == f"{agent.agent_id}_{UPDATE_KEY}"
    assert registered.translation_key == "agent"
    assert registered.platform == DOMAIN
    assert attribute_of(hass, entity_id, "friendly_name").endswith("Agent")
    assert attribute_of(hass, entity_id, "title") == "QoffeeAutomat"


async def test_without_the_feature_there_is_no_update_entity(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """The metrics and the command are there, the feature is not: no entity."""
    manifest = update_manifest(fake_agent)
    manifest["features"] = ["notify", "wol"]
    fake_agent.set_manifest(manifest)

    await entry_factory()

    assert find_entity_id(hass, "update", fake_agent.agent_id, UPDATE_KEY) is None
    assert not hass.states.async_entity_ids("update")


async def test_a_manifest_without_any_features_creates_no_update_entity(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """An agent that announces nothing (an older one) has no update entity."""
    manifest = changed_manifest(fake_agent)
    manifest["features"] = []
    fake_agent.set_manifest(manifest)

    await entry_factory()

    assert not hass.states.async_entity_ids("update")


async def test_the_entity_follows_the_feature_without_a_reload(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The feature goes away and comes back; entity and registry entry follow."""
    assert find_entity_id(hass, "update", agent.agent_id, UPDATE_KEY)
    connections = len(agent.connections)

    without = update_manifest(agent, rev=NEW_REV)
    without["features"] = []
    agent.set_manifest(without)
    await agent.send_manifest_changed()
    await agent.wait_for(
        lambda: find_entity_id(hass, "update", agent.agent_id, UPDATE_KEY) is None
    )
    await hass.async_block_till_done()
    assert not hass.states.async_entity_ids("update")

    agent.set_manifest(update_manifest(agent, rev=SECOND_REV))
    await agent.send_manifest_changed()
    await agent.wait_for(
        lambda: find_entity_id(hass, "update", agent.agent_id, UPDATE_KEY) is not None
    )
    await hass.async_block_till_done()

    assert hass.states.async_entity_ids("update")
    assert len(agent.connections) == connections


async def test_an_entry_that_an_earlier_run_left_behind_is_removed_without_the_feature(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """The agent no longer has the feature: no unavailable leftover remains."""
    manifest = changed_manifest(fake_agent)
    manifest["features"] = []
    fake_agent.set_manifest(manifest)
    entry = await entry_factory(setup=False)
    er.async_get(hass).async_get_or_create(
        "update",
        DOMAIN,
        f"{fake_agent.agent_id}_{UPDATE_KEY}",
        config_entry=entry,
    )

    await hass.config_entries.async_setup(entry.entry_id)
    await hass.async_block_till_done()

    assert find_entity_id(hass, "update", fake_agent.agent_id, UPDATE_KEY) is None


# -- the subscription ------------------------------------------------------------------


async def test_the_entity_subscribes_to_the_version_metrics(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The metric sensors are disabled by default; the entity adds what it follows."""
    assert set(UPDATE_METRICS) <= subscribed(agent)


async def test_the_entity_asks_only_for_the_metrics_the_agent_announces(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """An agent without the release notes does not get a subscription to them."""
    fake_agent.set_manifest(
        update_manifest(fake_agent, remove_metrics=("agent.release_notes",))
    )
    fake_agent.snapshot = dict(NEWER)

    await entry_factory()
    await wait_for_subscription(fake_agent)

    assert "agent.release_notes" not in subscribed(fake_agent)
    assert "agent.latest_version" in subscribed(fake_agent)
    entity_id = update_entity(hass, fake_agent)
    await fake_agent.wait_for(lambda: state_of(hass, entity_id) == "on")
    assert attribute_of(hass, entity_id, "release_summary") is None


async def test_a_disabled_entity_costs_nothing(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """Disabled in the entity registry: nothing is subscribed (D-007)."""
    fake_agent.set_manifest(update_manifest(fake_agent, rev=fake_agent.manifest["rev"]))
    fake_agent.snapshot = dict(NEWER)
    entry = await entry_factory(setup=False)
    er.async_get(hass).async_get_or_create(
        "update",
        DOMAIN,
        f"{fake_agent.agent_id}_{UPDATE_KEY}",
        config_entry=entry,
        disabled_by=er.RegistryEntryDisabler.USER,
    )

    await hass.config_entries.async_setup(entry.entry_id)
    await hass.async_block_till_done()
    await fake_agent.wait_ready()
    # The subscription is sent after a short delay; wait for the first one.
    await fake_agent.wait_for(lambda: bool(fake_agent.subscribed_metrics()))

    assert not hass.states.async_entity_ids("update")
    assert not set(UPDATE_METRICS) & subscribed(fake_agent)


# -- state and attributes --------------------------------------------------------------


async def test_the_entity_shows_the_installed_and_the_latest_version(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """A newer release means an update is available."""
    entity_id = update_entity(hass, agent)

    assert state_of(hass, entity_id) == "on"
    assert attribute_of(hass, entity_id, "installed_version") == "1.1.0"
    assert attribute_of(hass, entity_id, "latest_version") == "1.2.0"
    assert attribute_of(hass, entity_id, "release_url") == RELEASE_URL
    assert attribute_of(hass, entity_id, "release_summary") == "What is new"


async def test_the_entity_is_up_to_date_when_both_versions_are_equal(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The agent reports the new release as installed: no update."""
    entity_id = update_entity(hass, agent)

    await agent.send_state({"agent.version": "1.2.0"})
    await agent.wait_for(lambda: state_of(hass, entity_id) == "off")

    assert attribute_of(hass, entity_id, "installed_version") == "1.2.0"
    assert attribute_of(hass, entity_id, "latest_version") == "1.2.0"


async def test_a_new_release_shows_up_without_polling(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The agent pushes the release it found; the entity follows it."""
    entity_id = update_entity(hass, agent)
    await agent.send_state({"agent.version": "1.2.0"})
    await agent.wait_for(lambda: state_of(hass, entity_id) == "off")

    await agent.send_state(
        {
            "agent.latest_version": "1.3.0",
            "agent.release_url": RELEASE_URL.replace("1.2.0", "1.3.0"),
            "agent.release_notes": "Another one.",
        }
    )
    await agent.wait_for(lambda: state_of(hass, entity_id) == "on")

    assert attribute_of(hass, entity_id, "latest_version") == "1.3.0"
    assert attribute_of(hass, entity_id, "release_summary") == "Another one."
    assert attribute_of(hass, entity_id, "release_url") == (
        "https://github.com/example/QoffeeAutomat/releases/tag/v1.3.0"
    )


async def test_without_a_known_release_the_state_is_unknown(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The check is switched off (or failed): the agent has no latest version.

    The entity does not claim that the agent is up to date; nobody checked.
    """
    entity_id = update_entity(hass, agent)

    await agent.send_state(
        {
            "agent.latest_version": None,
            "agent.release_url": None,
            "agent.release_notes": None,
        }
    )
    await agent.wait_for(lambda: state_of(hass, entity_id) == "unknown")

    assert attribute_of(hass, entity_id, "installed_version") == "1.1.0"
    assert attribute_of(hass, entity_id, "latest_version") is None
    assert attribute_of(hass, entity_id, "release_url") is None
    assert attribute_of(hass, entity_id, "release_summary") is None


async def test_an_older_release_than_the_installed_version_is_no_update(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """A newer build than the newest release (a development build) is not downgraded."""
    entity_id = update_entity(hass, agent)

    await agent.send_state({"agent.version": "1.3.0"})
    await agent.wait_for(lambda: state_of(hass, entity_id) == "off")


def test_the_summary_is_the_first_line_without_markdown() -> None:
    """Heading marks, bullets and quotes are dropped; empty lines are skipped."""
    assert summarize("\n\n## What is new\n- more") == "What is new"
    assert summarize("- **Fixes** for the `tray`") == "Fixes for the tray"
    assert summarize("> quoted") == "quoted"
    assert summarize("plain text\nsecond line") == "plain text"


def test_the_summary_is_cut_to_what_home_assistant_keeps() -> None:
    """A long first line ends with an ellipsis at 255 characters."""
    summary = summarize("word " * 200)

    assert summary is not None
    assert len(summary) <= 255
    assert summary.endswith("…")


@pytest.mark.parametrize("notes", [None, "", "   \n\n", "###", "- * -"])
def test_notes_without_text_have_no_summary(notes: str | None) -> None:
    assert summarize(notes) is None


async def test_the_full_release_notes_are_available(
    hass: HomeAssistant,
    agent: FakeAgent,
    entry: MockConfigEntry,
    hass_ws_client: WebSocketGenerator,
) -> None:
    """The dialog asks for the Markdown notes with ``update/release_notes``."""
    entity_id = update_entity(hass, agent)
    client = await hass_ws_client(hass)

    await client.send_json_auto_id(
        {"type": "update/release_notes", "entity_id": entity_id}
    )
    response = await client.receive_json()

    assert response["success"]
    assert response["result"] == NOTES


async def test_the_supported_features_follow_the_commands_of_the_manifest(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """With the command the entity can install; without it only show."""
    entity_id = update_entity(hass, agent)
    features = attribute_of(hass, entity_id, "supported_features")
    assert features == UpdateEntityFeature.INSTALL | UpdateEntityFeature.RELEASE_NOTES

    agent.set_manifest(update_manifest(agent, rev=NEW_REV, commands=()))
    await agent.send_manifest_changed()
    await agent.wait_for(
        lambda: (
            attribute_of(hass, entity_id, "supported_features")
            == UpdateEntityFeature.RELEASE_NOTES
        )
    )

    with pytest.raises(HomeAssistantError):
        await install(hass, entity_id)
    assert not agent.runs


# -- install ---------------------------------------------------------------------------


async def test_install_runs_the_update_command_and_waits_for_the_answer(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The command has no variables; nothing of the entity goes into it (D-012)."""
    await install(hass, update_entity(hass, agent))

    frame = agent.runs[-1]
    assert frame["command"] == COMMAND_AGENT_UPDATE
    assert not frame.get("vars")
    assert frame["wait"] is True
    assert len(agent.runs) == 1


async def test_a_refused_installer_is_an_error_with_the_reason_of_the_agent(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """A wrong checksum: the agent refuses, and the dialog shows why."""
    agent.run_result = {
        "run_id": RUN_ID,
        "exit_code": -1,
        "stdout": "",
        "stderr": "The checksum of QoffeeAutomat-Setup-1.2.0-x64.exe does not match.",
        "duration_ms": 1200,
        "timed_out": False,
    }
    entity_id = update_entity(hass, agent)

    with pytest.raises(HomeAssistantError, match=r"checksum .* does not match"):
        await install(hass, entity_id)

    # The update did not start: the entity still offers it and is not stuck.
    assert state_of(hass, entity_id) == "on"
    assert attribute_of(hass, entity_id, "in_progress") is False


async def test_an_update_that_timed_out_is_an_error(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    agent.run_result = {"run_id": RUN_ID, "exit_code": None, "timed_out": True}

    with pytest.raises(HomeAssistantError, match="too long"):
        await install(hass, update_entity(hass, agent))


async def test_an_exit_code_without_a_message_is_reported(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    agent.run_result = {"run_id": RUN_ID, "exit_code": 3, "stdout": "", "stderr": ""}

    with pytest.raises(HomeAssistantError, match="exit code 3"):
        await install(hass, update_entity(hass, agent))


async def test_a_run_without_exit_code_data_counts_as_started(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """An answer that carries no exit code is not a failure."""
    agent.run_result = {"run_id": RUN_ID}

    await install(hass, update_entity(hass, agent))


async def test_an_error_result_of_the_agent_is_an_error(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The agent rejects the run (for example while it is paused)."""
    agent.run_errors[COMMAND_AGENT_UPDATE] = ("paused", "The agent is paused.")

    with pytest.raises(HomeAssistantError, match="The agent is paused"):
        await install(hass, update_entity(hass, agent))


async def test_an_agent_that_goes_away_with_bye_update_counts_as_started(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The installer stops the agent before the answer gets through: not an error."""
    entity_id = update_entity(hass, agent)
    agent.bye_on_run = "update"

    await install(hass, entity_id)

    assert agent.runs[-1]["command"] == COMMAND_AGENT_UPDATE
    # The agent is away: unavailable until it connects again.
    await agent.wait_for(lambda: state_of(hass, entity_id) == "unavailable")


async def test_an_agent_that_goes_away_for_another_reason_is_an_error(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """A PC that shuts down in the middle of the update did not start it."""
    agent.bye_on_run = "shutdown"

    with pytest.raises(HomeAssistantError):
        await install(hass, update_entity(hass, agent))


async def test_the_new_version_ends_the_update_after_the_agent_comes_back(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The agent restarts with the new version: the entity is current again."""
    entity_id = update_entity(hass, agent)
    agent.bye_on_run = "update"
    await install(hass, entity_id)
    await agent.wait_for(lambda: state_of(hass, entity_id) == "unavailable")

    agent.bye_on_run = None
    agent.snapshot = {**NEWER, "agent.version": "1.2.0"}
    entry.runtime_data.client.trigger_connect()

    await agent.wait_for(lambda: state_of(hass, entity_id) == "off")
    assert attribute_of(hass, entity_id, "installed_version") == "1.2.0"
