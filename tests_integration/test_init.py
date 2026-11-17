"""Tests of the entry setup, device and dynamic entity management (T503)."""

import asyncio
import dataclasses
import logging
from unittest.mock import MagicMock, patch

import pytest
from homeassistant.config_entries import ConfigEntryState
from homeassistant.core import HomeAssistant
from homeassistant.helpers import device_registry as dr
from homeassistant.helpers import entity_registry as er

from custom_components.qoffeeautomat import config_flow
from custom_components.qoffeeautomat.client import QoffeeLinkClient
from custom_components.qoffeeautomat.const import CONF_LAST_BOOT_ID, DOMAIN
from custom_components.qoffeeautomat.manifest_manager import (
    ManifestDiff,
    compute_diff,
    register_device,
)
from custom_components.qoffeeautomat.protocol import ProtocolError, parse_manifest
from custom_components.qoffeeautomat.runtime import QoffeeAutomatRuntimeData
from tests_integration.fake_agent import FakeAgent, load_fixture
from tests_integration.helpers import (
    MEMORY_METRIC,
    NEW_COMMAND,
    NEW_REV,
    EntryFactory,
    attribute_of,
    changed_manifest,
    entity_id_of,
    find_entity_id,
    reconnect,
    state_of,
)

RUNTIME_LOGGER = "custom_components.qoffeeautomat.runtime"


def runtime_of(entry: object) -> QoffeeAutomatRuntimeData:
    """Return the runtime data of a loaded entry."""
    runtime = getattr(entry, "runtime_data", None)
    assert isinstance(runtime, QoffeeAutomatRuntimeData)
    return runtime


# -- setup ------------------------------------------------------------------------


async def test_setup_loads_entry_and_connects(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """The entry loads, runtime data holds the connected client and the manifest."""
    entry = await entry_factory()

    assert entry.state is ConfigEntryState.LOADED
    runtime = runtime_of(entry)
    assert runtime.client.connected
    assert runtime.agent_id == fake_agent.agent_id
    assert runtime.manifest_manager.manifest.rev == fake_agent.manifest["rev"]
    # The setup probes once, then keeps one connection.
    await fake_agent.wait_for(lambda: len(fake_agent.ready_connections) == 1)
    assert len(fake_agent.connections) == 2
    # The client of the loaded entry is the one that zeroconf announcements wake up.
    assert config_flow._entry_client(entry) is runtime.client


async def test_device_is_registered_with_link_to_the_agent_ui(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """The device page shows the agent data and links to the agent UI."""
    entry = await entry_factory()

    device = dr.async_get(hass).async_get_device_by_identifier(
        (DOMAIN, fake_agent.agent_id), entry.entry_id
    )
    assert device is not None
    assert device.config_entry_id == entry.entry_id
    assert device.name == "Gaming-PC"
    assert device.manufacturer == "QoffeeAutomat"
    assert device.model == "QoffeeAutomat Agent"
    assert device.sw_version == "1.0.0"
    assert device.configuration_url == "https://gaming-pc.local:8481/"
    assert (dr.CONNECTION_NETWORK_MAC, "a4:b1:c1:00:11:22") in device.connections


async def test_device_without_usable_ui_url_has_no_link(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """An address that is not http or https is not offered as configuration URL."""
    entry = await entry_factory()
    agent = runtime_of(entry).manifest_manager.manifest.agent

    device = register_device(
        hass, entry.entry_id, dataclasses.replace(agent, ui_url="ftp://pc/")
    )

    assert device.configuration_url is None


async def test_setup_creates_entities_of_the_manifest(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """Metrics, commands, events, connectivity and power state get entities."""
    entry = await entry_factory()
    agent_id = fake_agent.agent_id

    for domain, key in (
        ("sensor", "cpu.usage"),
        ("sensor", "audio.volume"),
        ("binary_sensor", "session.locked"),
        ("binary_sensor", "_connectivity"),
        ("sensor", "_power_state"),
        ("select", "set_display_profile"),
        ("number", "set_volume"),
        ("button", "lock_session"),
        ("text", "say_text"),
        ("event", "_events"),
    ):
        assert find_entity_id(hass, domain, agent_id, key) is not None, key
    device = dr.async_get(hass).async_get_device_by_identifier(
        (DOMAIN, agent_id), entry.entry_id
    )
    assert device is not None
    registry = er.async_get(hass)
    assert {
        entity.unique_id for entity in er.async_entries_for_device(registry, device.id)
    } == {
        f"{agent_id}_{key}"
        for key in (
            "cpu.usage",
            "audio.volume",
            "session.locked",
            "_connectivity",
            "_power_state",
            "set_display_profile",
            "set_volume",
            "lock_session",
            "say_text",
            "_events",
        )
    }


async def test_setup_retries_when_the_agent_is_not_reachable(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """A transient connection error leads to ConfigEntryNotReady."""
    await fake_agent.stop()

    entry = await entry_factory()

    assert entry.state is ConfigEntryState.SETUP_RETRY


async def test_setup_starts_reauth_when_the_token_is_rejected(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """A rejected token leads to ConfigEntryAuthFailed and reauthentication."""
    entry = await entry_factory(token="x" * 43)

    assert entry.state is ConfigEntryState.SETUP_ERROR
    flows = hass.config_entries.flow.async_progress_by_handler(DOMAIN)
    assert [flow["context"]["source"] for flow in flows] == ["reauth"]


async def test_setup_fails_without_a_common_protocol_version(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """Retrying cannot help against an agent that speaks only other versions."""
    fake_agent.protocol_range = (2, 3)

    entry = await entry_factory()

    assert entry.state is ConfigEntryState.SETUP_ERROR


async def test_setup_retries_when_the_manifest_is_invalid(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """A manifest that cannot be parsed is retried later."""
    with patch(
        "custom_components.qoffeeautomat.manifest_manager.parse_manifest",
        side_effect=ProtocolError("broken"),
    ):
        entry = await entry_factory()

    assert entry.state is ConfigEntryState.SETUP_RETRY


async def test_setup_retries_when_no_connection_follows_the_probe(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """The setup does not wait forever for the first connection."""
    timeout = "custom_components.qoffeeautomat.runtime.FIRST_CONNECT_TIMEOUT_SECONDS"
    with patch(timeout, 0), patch.object(QoffeeLinkClient, "start"):
        entry = await entry_factory()

    assert entry.state is ConfigEntryState.SETUP_RETRY


async def test_unload_and_reload(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """Unloading closes the connection and entities; a reload brings them back."""
    entry = await entry_factory()
    client = runtime_of(entry).client

    assert await hass.config_entries.async_unload(entry.entry_id)
    await hass.async_block_till_done()

    assert entry.state is ConfigEntryState.NOT_LOADED
    assert not client.connected
    await fake_agent.wait_for(lambda: not fake_agent.ready_connections)

    await hass.config_entries.async_setup(entry.entry_id)
    await hass.async_block_till_done()
    assert entry.state is ConfigEntryState.LOADED
    assert runtime_of(entry).client.connected


# -- dynamic entities -------------------------------------------------------------


async def test_added_and_removed_metric_changes_entities_without_reload(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """A new metric gets an entity and a vanished one loses it; no reload happens."""
    entry = await entry_factory()
    runtime = runtime_of(entry)
    agent_id = fake_agent.agent_id
    removed_entity_id = entity_id_of(hass, "sensor", agent_id, "audio.volume")

    fake_agent.set_manifest(
        changed_manifest(
            fake_agent, add_metrics=[MEMORY_METRIC], remove_metrics=["audio.volume"]
        )
    )
    await fake_agent.send_manifest_changed()
    await fake_agent.wait_for(
        lambda: find_entity_id(hass, "sensor", agent_id, "memory.used") is not None
    )
    await hass.async_block_till_done()

    assert find_entity_id(hass, "sensor", agent_id, "audio.volume") is None
    assert hass.states.get(removed_entity_id) is None
    new_entity_id = entity_id_of(hass, "sensor", agent_id, "memory.used")
    assert hass.states.get(new_entity_id) is not None
    assert entry.state is ConfigEntryState.LOADED
    assert runtime_of(entry) is runtime
    # The probe of the setup plus the one connection of the client: no reconnect.
    assert len(fake_agent.connections) == 2
    assert runtime.manifest_manager.rev == NEW_REV


async def test_added_and_removed_command_is_reported_to_platforms(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """Platforms learn about added and removed commands through the manager."""
    entry = await entry_factory()
    diffs: list[ManifestDiff] = []

    async def listener(diff: ManifestDiff) -> None:
        diffs.append(diff)

    remove = await runtime_of(entry).manifest_manager.async_add_listener(listener)
    # A new listener first gets the whole current manifest as additions.
    assert {command.id for command in diffs[0].added_commands} == {
        "set_display_profile",
        "set_volume",
        "lock_session",
        "say_text",
    }
    diffs.clear()

    fake_agent.set_manifest(
        changed_manifest(
            fake_agent, add_commands=[NEW_COMMAND], remove_commands=["say_text"]
        )
    )
    await fake_agent.send_manifest_changed()
    await fake_agent.wait_for(lambda: bool(diffs))

    assert [command.id for command in diffs[0].added_commands] == ["tidy_desktop"]
    assert [command.id for command in diffs[0].removed_commands] == ["say_text"]
    assert not diffs[0].added_metrics
    assert not diffs[0].removed_metrics
    remove()


async def test_stale_entities_are_removed_and_customisations_kept(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """Entities of vanished metrics and commands go; others keep their registry data."""
    registry = er.async_get(hass)
    agent_id = fake_agent.agent_id
    entry = await entry_factory(setup=False)
    registry.async_get_or_create(
        "button", DOMAIN, f"{agent_id}_gone_command", config_entry=entry
    )
    registry.async_get_or_create(
        "sensor", DOMAIN, f"{agent_id}_gone.metric", config_entry=entry
    )
    other_domain = registry.async_get_or_create(
        "event", DOMAIN, f"{agent_id}_events", config_entry=entry
    )
    customised = registry.async_get_or_create(
        "sensor", DOMAIN, f"{agent_id}_cpu.usage", config_entry=entry
    )
    registry.async_update_entity(customised.entity_id, name="My CPU")

    await hass.config_entries.async_setup(entry.entry_id)
    await hass.async_block_till_done()

    assert find_entity_id(hass, "button", agent_id, "gone_command") is None
    assert find_entity_id(hass, "sensor", agent_id, "gone.metric") is None
    # Entities of platforms that other tasks implement are never touched.
    assert registry.async_get(other_domain.entity_id) is not None

    fake_agent.set_manifest(changed_manifest(fake_agent, add_metrics=[MEMORY_METRIC]))
    await fake_agent.send_manifest_changed()
    await fake_agent.wait_for(
        lambda: find_entity_id(hass, "sensor", agent_id, "memory.used") is not None
    )
    await hass.async_block_till_done()

    kept = registry.async_get(customised.entity_id)
    assert kept is not None
    assert kept.name == "My CPU"


async def test_changed_descriptor_keeps_the_entity_customisations(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """An entity whose metric changed is replaced but keeps its registry entry."""
    entry = await entry_factory()
    registry = er.async_get(hass)
    agent_id = fake_agent.agent_id
    entity_id = entity_id_of(hass, "sensor", agent_id, "audio.volume")
    registry.async_update_entity(entity_id, name="Speaker volume")
    await hass.async_block_till_done()

    manifest = changed_manifest(fake_agent)
    for metric in manifest["metrics"]:
        if metric["id"] == "audio.volume":
            metric["unit"] = "dB"
    fake_agent.set_manifest(manifest)
    await fake_agent.send_manifest_changed()
    await fake_agent.wait_for(
        lambda: attribute_of(hass, entity_id, "unit_of_measurement") == "dB"
    )

    assert entity_id_of(hass, "sensor", agent_id, "audio.volume") == entity_id
    entry = registry.async_get(entity_id)
    assert entry is not None
    assert entry.name == "Speaker volume"


async def test_manifest_is_only_fetched_for_a_new_revision(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """The manifest is cached by ``rev``."""
    await entry_factory()
    assert fake_agent.manifest_requests == 1

    await fake_agent.send_manifest_changed()  # same revision as the cached one
    await hass.async_block_till_done()
    assert fake_agent.manifest_requests == 1

    fake_agent.set_manifest(changed_manifest(fake_agent))
    await fake_agent.send_manifest_changed()
    await fake_agent.wait_for(lambda: fake_agent.manifest_requests == 2)
    await hass.async_block_till_done()
    assert fake_agent.manifest_requests == 2


async def test_reconnect_with_a_new_manifest_revision_refreshes_it(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """A manifest change while the PC was away is noticed in ``hello``."""
    entry = await entry_factory()
    runtime = runtime_of(entry)
    agent_id = fake_agent.agent_id

    fake_agent.set_manifest(changed_manifest(fake_agent, add_metrics=[MEMORY_METRIC]))
    await reconnect(fake_agent, runtime.client)
    await fake_agent.wait_for(
        lambda: find_entity_id(hass, "sensor", agent_id, "memory.used") is not None
    )

    assert runtime.manifest_manager.rev == NEW_REV


async def test_failed_manifest_refresh_is_survived(
    hass: HomeAssistant,
    fake_agent: FakeAgent,
    entry_factory: EntryFactory,
    caplog: pytest.LogCaptureFixture,
) -> None:
    """A manifest that cannot be parsed later is logged; the entry stays loaded."""
    entry = await entry_factory()
    fake_agent.manifest = {"rev": NEW_REV}  # not a valid manifest

    await fake_agent.send_manifest_changed()
    await fake_agent.wait_for(lambda: "Could not update the manifest" in caplog.text)

    assert entry.state is ConfigEntryState.LOADED
    assert runtime_of(entry).manifest_manager.rev != NEW_REV


def test_compute_diff_of_unchanged_manifests_is_empty() -> None:
    """Equal manifests produce an empty diff; a first manifest is all additions."""
    manifest = parse_manifest(load_fixture("manifest.json"))

    assert compute_diff(manifest, manifest).empty
    first = compute_diff(None, manifest)
    assert len(first.added_metrics) == 3
    assert len(first.added_commands) == 4
    assert not first.removed_metrics
    assert not first.removed_commands


# -- availability, sleep, power state ---------------------------------------------


async def test_entities_follow_the_connection(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """Without a connection entities are unavailable, connectivity and power stay."""
    entry = await entry_factory()
    agent_id = fake_agent.agent_id
    cpu = entity_id_of(hass, "sensor", agent_id, "cpu.usage")
    connectivity = entity_id_of(hass, "binary_sensor", agent_id, "_connectivity")
    power = entity_id_of(hass, "sensor", agent_id, "_power_state")
    await fake_agent.wait_for(lambda: state_of(hass, cpu) == "12")
    assert state_of(hass, connectivity) == "on"
    assert state_of(hass, power) == "running"

    fake_agent.drop_connections()
    await fake_agent.wait_for(lambda: state_of(hass, cpu) == "unavailable")

    assert state_of(hass, connectivity) == "off"
    assert state_of(hass, power) == "unknown"

    runtime_of(entry).client.trigger_connect()
    await fake_agent.wait_for(lambda: state_of(hass, connectivity) == "on")
    await fake_agent.wait_for(lambda: state_of(hass, cpu) == "12")
    assert state_of(hass, power) == "running"


async def test_bye_sleep_makes_everything_unavailable_but_connectivity_and_power(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """After ``bye`` with reason sleep the power state shows sleep."""
    entry = await entry_factory()
    agent_id = fake_agent.agent_id
    client = runtime_of(entry).client
    cpu = entity_id_of(hass, "sensor", agent_id, "cpu.usage")
    locked = entity_id_of(hass, "binary_sensor", agent_id, "session.locked")
    connectivity = entity_id_of(hass, "binary_sensor", agent_id, "_connectivity")
    power = entity_id_of(hass, "sensor", agent_id, "_power_state")
    await fake_agent.wait_for(lambda: state_of(hass, cpu) == "12")

    await fake_agent.send_bye("sleep")
    await fake_agent.wait_for(lambda: state_of(hass, power) == "sleep")
    await fake_agent.wait_for(lambda: not client.connected)
    await hass.async_block_till_done()

    assert state_of(hass, cpu) == "unavailable"
    assert state_of(hass, locked) == "unavailable"
    assert state_of(hass, connectivity) == "off"
    assert state_of(hass, power) == "sleep"
    # Every entity of the device other than those two is unavailable.
    registry = er.async_get(hass)
    device = dr.async_get(hass).async_get_device_by_identifier(
        (DOMAIN, agent_id), entry.entry_id
    )
    assert device is not None
    for registered in er.async_entries_for_device(registry, device.id):
        if registered.entity_id not in (connectivity, power):
            assert state_of(hass, registered.entity_id) == "unavailable"

    # The PC wakes up: the client reconnects and everything returns.
    client.trigger_connect()
    await fake_agent.wait_for(lambda: state_of(hass, power) == "running")
    await fake_agent.wait_for(lambda: state_of(hass, cpu) == "12")
    assert state_of(hass, connectivity) == "on"


@pytest.mark.parametrize(
    ("reason", "expected"),
    [
        ("hibernate", "hibernate"),
        ("shutdown", "shutdown"),
        ("restart", "restart"),
        ("service_stop", "unknown"),
        ("update", "unknown"),
    ],
)
async def test_power_state_follows_the_bye_reason(
    hass: HomeAssistant,
    fake_agent: FakeAgent,
    entry_factory: EntryFactory,
    reason: str,
    expected: str,
) -> None:
    """The last ``bye`` reason is the power state; reasons without one are unknown."""
    await entry_factory()
    power = entity_id_of(hass, "sensor", fake_agent.agent_id, "_power_state")

    await fake_agent.send_bye(reason)

    await fake_agent.wait_for(lambda: state_of(hass, power) == expected)


async def test_unavailability_is_logged_once(
    hass: HomeAssistant,
    fake_agent: FakeAgent,
    entry_factory: EntryFactory,
    caplog: pytest.LogCaptureFixture,
) -> None:
    """Going away and coming back is logged once each, however long it takes."""
    caplog.set_level(logging.INFO, logger=RUNTIME_LOGGER)
    entry = await entry_factory()
    client = runtime_of(entry).client
    port = fake_agent.port

    await fake_agent.stop()
    await fake_agent.wait_for(lambda: not client.connected)
    for _ in range(3):  # failing attempts must not log again
        client.trigger_connect()
        await asyncio.sleep(0.1)
    assert client.last_error is not None
    await fake_agent.start(port)
    client.trigger_connect()
    await fake_agent.wait_for(lambda: client.connected)

    messages = [r.getMessage() for r in caplog.records if r.name == RUNTIME_LOGGER]
    assert [m for m in messages if m.endswith("is unavailable")] == [
        "QoffeeAutomat agent Gaming-PC is unavailable"
    ]
    assert [m for m in messages if m.endswith("is available again")] == [
        "QoffeeAutomat agent Gaming-PC is available again"
    ]


async def test_revoked_token_starts_reauthentication(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """The agent revoking the token while connected starts reauthentication."""
    await entry_factory()

    await fake_agent.close_connections(4401)

    def reauth_started() -> bool:
        return any(
            flow["context"]["source"] == "reauth"
            for flow in hass.config_entries.flow.async_progress_by_handler(DOMAIN)
        )

    await fake_agent.wait_for(reauth_started)


# -- boot id ----------------------------------------------------------------------


async def test_new_boot_id_signals_booted_exactly_once(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """A changed boot id signals ``booted`` once and is stored in the entry."""
    entry = await entry_factory(last_boot_id="the-boot-before")
    runtime = runtime_of(entry)
    booted = MagicMock()

    # The boot was detected on the first connection, while the platforms (and the
    # event entity of T506) were still being set up: the first listener gets it.
    runtime.connection.add_booted_listener(booted)
    assert booted.call_count == 1
    assert entry.data[CONF_LAST_BOOT_ID] == fake_agent.boot_id

    # Reconnecting with the same boot id does not signal again ...
    await reconnect(fake_agent, runtime.client)
    await reconnect(fake_agent, runtime.client)
    assert booted.call_count == 1

    # ... a new boot id signals once, however often the client reconnects after.
    fake_agent.boot_id = "the-next-boot"
    await reconnect(fake_agent, runtime.client)
    await reconnect(fake_agent, runtime.client)
    assert booted.call_count == 2
    assert entry.data[CONF_LAST_BOOT_ID] == "the-next-boot"


async def test_same_boot_id_does_not_signal_booted(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """A Home Assistant restart or an agent restart is no PC start."""
    entry = await entry_factory()  # the stored boot id equals the agent's
    runtime = runtime_of(entry)
    booted = MagicMock()
    runtime.connection.add_booted_listener(booted)

    await reconnect(fake_agent, runtime.client)

    booted.assert_not_called()
    assert entry.data[CONF_LAST_BOOT_ID] == fake_agent.boot_id


async def test_first_connection_stores_the_boot_id_without_signal(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """Nothing to compare with yet: remember the boot id, do not signal."""
    entry = await entry_factory(last_boot_id=None)
    booted = MagicMock()

    runtime_of(entry).connection.add_booted_listener(booted)

    booted.assert_not_called()
    assert entry.data[CONF_LAST_BOOT_ID] == fake_agent.boot_id


async def test_booted_reaches_every_registered_listener(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """Listeners registered before the boot are called, a failing one is isolated."""
    entry = await entry_factory()
    runtime = runtime_of(entry)
    broken = MagicMock(side_effect=RuntimeError("listener bug"))
    healthy = MagicMock()
    remove_broken = runtime.connection.add_booted_listener(broken)
    runtime.connection.add_booted_listener(healthy)

    fake_agent.boot_id = "another-boot"
    await reconnect(fake_agent, runtime.client)

    broken.assert_called_once_with()
    healthy.assert_called_once_with()
    remove_broken()
