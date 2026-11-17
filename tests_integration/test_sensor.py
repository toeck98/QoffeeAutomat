"""Tests of the sensor and binary sensor platforms (T504)."""

import asyncio
import logging
from datetime import datetime, timedelta
from typing import Any

import pytest
from homeassistant.components.sensor import SensorDeviceClass, SensorStateClass
from homeassistant.core import HomeAssistant
from homeassistant.helpers import entity_registry as er
from homeassistant.helpers.entity import Entity
from homeassistant.helpers.entity_registry import RegistryEntryDisabler
from pytest_homeassistant_custom_component.common import MockConfigEntry

from custom_components.qoffeeautomat.binary_sensor import convert_state
from custom_components.qoffeeautomat.protocol import MetricDescriptor
from custom_components.qoffeeautomat.sensor import (
    MAX_STATE_LENGTH,
    SensorSpec,
    build_spec,
    convert_value,
)
from tests_integration.fake_agent import FakeAgent
from tests_integration.helpers import (
    MEMORY_METRIC,
    SECOND_REV,
    EntryFactory,
    attribute_of,
    changed_manifest,
    entity_id_of,
    find_entity_id,
    reconnect,
    state_of,
)

EXTRA_METRICS: list[dict[str, Any]] = [
    {
        "id": "system.last_boot",
        "name": "Last boot",
        "kind": "sensor",
        "value_type": "timestamp",
    },
    {
        "id": "audio.output",
        "name": "Output device",
        "kind": "sensor",
        "value_type": "enum",
        "options": ["speakers", "headphones"],
    },
    {
        "id": "window.title",
        "name": "Window title",
        "kind": "sensor",
        "value_type": "string",
        "enabled_default": False,
    },
    {"id": "user.name", "name": "User", "kind": "sensor", "value_type": "string"},
    {
        "id": "privacy.webcam",
        "name": "Webcam in use",
        "kind": "binary_sensor",
        "value_type": "bool",
        "device_class": "occupancy",
    },
    {
        "id": "future.thing",
        "name": "Future thing",
        "kind": "sensor",
        "value_type": "number",
        "device_class": "hologram",
        "unit": "bogus",
        "state_class": "bogus",
        "entity_category": "bogus",
    },
    {
        "id": "pc.power",
        "name": "Not shown",
        "translation_key": "power_state",
        "kind": "sensor",
        "value_type": "string",
    },
]

SNAPSHOT: dict[str, Any] = {
    "cpu.usage": 12,
    "session.locked": False,
    "audio.volume": 35.5,
    "audio.output": "speakers",
    "system.last_boot": "2026-11-17T08:30:00+00:00",
    "user.name": "markus",
    "privacy.webcam": False,
    "future.thing": 7,
    "pc.power": "on",
    "window.title": "Some window",
}

# Metrics of the enabled entities (window.title is disabled by default), sorted as the
# integration sends them.
SUBSCRIBED = [
    "audio.output",
    "audio.volume",
    "cpu.usage",
    "future.thing",
    "pc.power",
    "privacy.webcam",
    "session.locked",
    "system.last_boot",
    "user.name",
]


def descriptor(**overrides: Any) -> MetricDescriptor:
    """Return a number sensor descriptor with the given fields replaced."""
    fields: dict[str, Any] = {
        "id": "test.metric",
        "name": "Test metric",
        "kind": "sensor",
        "value_type": "number",
    }
    return MetricDescriptor(**{**fields, **overrides})


@pytest.fixture(name="agent")
def agent_fixture(fake_agent: FakeAgent) -> FakeAgent:
    """The fake agent with metrics of every value type and a state snapshot."""
    # The number of ``set_volume`` follows ``audio.volume`` and would keep it in the
    # subscription; these tests are about the sensors alone (T505 tests the controls).
    fake_agent.set_manifest(
        changed_manifest(
            fake_agent, add_metrics=EXTRA_METRICS, remove_commands=["set_volume"]
        )
    )
    fake_agent.snapshot = SNAPSHOT
    return fake_agent


@pytest.fixture(name="entry")
async def entry_fixture(
    hass: HomeAssistant, agent: FakeAgent, entry_factory: EntryFactory
) -> MockConfigEntry:
    """Set up the entry and wait for the subscription and the first values."""
    entry = await entry_factory()
    await agent.wait_for(lambda: agent.subscribed_metrics()[-1:] == [SUBSCRIBED])
    await agent.wait_for(
        lambda: state_of(hass, eid(hass, agent, "user.name")) == "markus"
    )
    return entry


def eid(hass: HomeAssistant, agent: FakeAgent, key: str, domain: str = "sensor") -> str:
    """Return the entity id of a metric."""
    return entity_id_of(hass, domain, agent.agent_id, key)


# -- entities from descriptors ----------------------------------------------------


async def test_sensor_entities_carry_the_descriptor_properties(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """Unit, state class, icon, category and name come from the descriptor."""
    registry = er.async_get(hass)
    cpu = eid(hass, agent, "cpu.usage")
    volume = eid(hass, agent, "audio.volume")

    assert state_of(hass, cpu) == "12"
    assert attribute_of(hass, cpu, "unit_of_measurement") == "%"
    assert attribute_of(hass, cpu, "state_class") == "measurement"
    registered = registry.async_get(cpu)
    assert registered is not None
    assert registered.entity_category == "diagnostic"
    assert registered.unique_id == f"{agent.agent_id}_cpu.usage"
    assert state_of(hass, volume) == "35.5"
    assert attribute_of(hass, volume, "icon") == "mdi:volume-high"
    # No translation exists for ``cpu_usage``: the manifest name is shown.
    assert attribute_of(hass, cpu, "friendly_name") == "Gaming-PC CPU usage"


async def test_translation_key_is_used_when_the_integration_translates_it(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """A known translation key wins over the manifest name."""
    power = eid(hass, agent, "pc.power")

    assert attribute_of(hass, power, "friendly_name") == "Gaming-PC Power state"


async def test_binary_sensor_entities(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """Binary sensors show booleans and take their device class."""
    locked = eid(hass, agent, "session.locked", "binary_sensor")
    webcam = eid(hass, agent, "privacy.webcam", "binary_sensor")

    assert state_of(hass, locked) == "off"
    assert state_of(hass, webcam) == "off"
    assert attribute_of(hass, webcam, "device_class") == "occupancy"

    await agent.send_state({"privacy.webcam": True, "session.locked": 1})
    await agent.wait_for(lambda: state_of(hass, webcam) == "on")
    assert state_of(hass, locked) == "on"

    await agent.send_state({"privacy.webcam": "yes"})
    await agent.wait_for(lambda: state_of(hass, webcam) == "unknown")

    await agent.send_state({"privacy.webcam": None})
    await agent.wait_for(lambda: state_of(hass, webcam) == "unavailable")


async def test_metric_that_is_disabled_by_default_is_disabled_in_the_registry(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """``enabled_default: false`` creates a disabled entity that gets no values."""
    window = eid(hass, agent, "window.title")

    registered = er.async_get(hass).async_get(window)
    assert registered is not None
    assert registered.disabled_by is RegistryEntryDisabler.INTEGRATION
    assert hass.states.get(window) is None
    assert "window.title" not in agent.subscribed_metrics()[-1]


async def test_timestamp_metric_is_a_timestamp_sensor(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """Timestamp metrics become sensors of device class timestamp with a datetime."""
    boot = eid(hass, agent, "system.last_boot")

    assert attribute_of(hass, boot, "device_class") == SensorDeviceClass.TIMESTAMP
    assert state_of(hass, boot) == "2026-11-17T08:30:00+00:00"
    assert attribute_of(hass, boot, "unit_of_measurement") is None

    # A time without a zone is read as UTC, text that is no time is unknown.
    await agent.send_state({"system.last_boot": "2026-11-18T09:00:00"})
    await agent.wait_for(lambda: state_of(hass, boot) == "2026-11-18T09:00:00+00:00")
    await agent.send_state({"system.last_boot": "last tuesday"})
    await agent.wait_for(lambda: state_of(hass, boot) == "unknown")


async def test_enum_metric_is_an_enum_sensor(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """Enum metrics become sensors of device class enum with their options."""
    output = eid(hass, agent, "audio.output")

    assert attribute_of(hass, output, "device_class") == SensorDeviceClass.ENUM
    assert attribute_of(hass, output, "options") == ["speakers", "headphones"]
    assert state_of(hass, output) == "speakers"

    await agent.send_state({"audio.output": "headphones"})
    await agent.wait_for(lambda: state_of(hass, output) == "headphones")
    # A value outside the options (an agent that is newer) must not break the entity.
    await agent.send_state({"audio.output": "hdmi"})
    await agent.wait_for(lambda: state_of(hass, output) == "unknown")


async def test_null_makes_the_entity_unavailable_and_a_value_restores_it(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """``null`` means unavailable (S07); wrong types are unknown, not errors."""
    cpu = eid(hass, agent, "cpu.usage")

    await agent.send_state({"cpu.usage": None})
    await agent.wait_for(lambda: state_of(hass, cpu) == "unavailable")
    await agent.send_state({"cpu.usage": 40})
    await agent.wait_for(lambda: state_of(hass, cpu) == "40")
    await agent.send_state({"cpu.usage": "forty"})
    await agent.wait_for(lambda: state_of(hass, cpu) == "unknown")


async def test_long_text_is_cut_to_the_state_limit(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """Home Assistant refuses longer states, so text metrics are shortened."""
    user = eid(hass, agent, "user.name")

    await agent.send_state({"user.name": "x" * 1000})
    await agent.wait_for(lambda: len(state_of(hass, user)) == MAX_STATE_LENGTH)


async def test_unknown_device_class_unit_and_state_class_are_dropped(
    hass: HomeAssistant,
    agent: FakeAgent,
    entry_factory: EntryFactory,
    caplog: pytest.LogCaptureFixture,
) -> None:
    """Values of a newer agent that this Home Assistant lacks do not fail."""
    caplog.set_level(logging.DEBUG, logger="custom_components.qoffeeautomat")

    await entry_factory()
    thing = eid(hass, agent, "future.thing")
    await agent.wait_for(lambda: state_of(hass, thing) == "7")

    assert attribute_of(hass, thing, "device_class") is None
    assert attribute_of(hass, thing, "state_class") is None
    # Without a device class there is nothing to check the unit against.
    assert attribute_of(hass, thing, "unit_of_measurement") == "bogus"
    registered = er.async_get(hass).async_get(thing)
    assert registered is not None
    assert registered.entity_category is None
    for what in ("device class", "state class", "entity category"):
        assert f"Ignoring unknown {what}" in caplog.text


# -- state delivery ---------------------------------------------------------------


def spy_on_state_writes(monkeypatch: pytest.MonkeyPatch) -> list[str]:
    """Record the entity id of every ``async_write_ha_state`` call from now on."""
    writes: list[str] = []
    original = Entity.async_write_ha_state

    def spy(self: Entity) -> None:
        writes.append(self.entity_id)
        original(self)

    monkeypatch.setattr(Entity, "async_write_ha_state", spy)
    return writes


async def test_state_frame_with_one_metric_writes_exactly_one_entity_state(
    hass: HomeAssistant,
    agent: FakeAgent,
    entry: MockConfigEntry,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Only the entity of the changed metric is written."""
    cpu = eid(hass, agent, "cpu.usage")
    writes = spy_on_state_writes(monkeypatch)

    await agent.send_state({"cpu.usage": 55})
    await agent.wait_for(lambda: state_of(hass, cpu) == "55")

    assert writes == [cpu]


async def test_state_frame_only_touches_entities_of_its_metrics(
    hass: HomeAssistant,
    agent: FakeAgent,
    entry: MockConfigEntry,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Metrics without an entity (unknown, disabled) are ignored."""
    cpu = eid(hass, agent, "cpu.usage")
    volume = eid(hass, agent, "audio.volume")
    writes = spy_on_state_writes(monkeypatch)

    frame = {
        "cpu.usage": 60,
        "audio.volume": 10,
        "window.title": "ignored",
        "no.such.metric": 1,
    }
    await agent.send_state(frame)
    await agent.wait_for(lambda: state_of(hass, volume) == "10")

    assert sorted(writes) == sorted([cpu, volume])


# -- subscription -----------------------------------------------------------------


async def test_subscribe_lists_the_metrics_of_enabled_entities(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The subscription holds exactly the metrics of the enabled entities."""
    assert agent.subscribed_metrics()[-1] == SUBSCRIBED


async def test_disabling_an_entity_removes_its_metric_from_the_next_subscribe(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """Disabling an entity in Home Assistant shrinks the subscription."""
    cpu = eid(hass, agent, "cpu.usage")
    sent = len(agent.subscribed_metrics())

    er.async_get(hass).async_update_entity(cpu, disabled_by=RegistryEntryDisabler.USER)
    await agent.wait_for(lambda: len(agent.subscribed_metrics()) == sent + 1)

    assert agent.subscribed_metrics()[-1] == [m for m in SUBSCRIBED if m != "cpu.usage"]
    assert hass.states.get(cpu) is None


async def test_several_changes_are_sent_as_one_subscribe(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """Changes in quick succession are debounced into a single request."""
    registry = er.async_get(hass)
    sent = len(agent.subscribed_metrics())

    for key in ("cpu.usage", "audio.volume", "user.name"):
        registry.async_update_entity(
            eid(hass, agent, key), disabled_by=RegistryEntryDisabler.USER
        )
    await agent.wait_for(lambda: len(agent.subscribed_metrics()) > sent)
    await asyncio.sleep(0.3)

    assert len(agent.subscribed_metrics()) == sent + 1
    assert agent.subscribed_metrics()[-1] == [
        m for m in SUBSCRIBED if m not in ("cpu.usage", "audio.volume", "user.name")
    ]


async def test_enabling_an_entity_adds_its_metric_to_the_subscribe(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """An entity that is enabled (the entry reloads) joins the subscription."""
    window = eid(hass, agent, "window.title")

    er.async_get(hass).async_update_entity(window, disabled_by=None)
    await hass.config_entries.async_reload(entry.entry_id)
    await hass.async_block_till_done()
    await agent.wait_for(
        lambda: agent.subscribed_metrics()[-1] == [*SUBSCRIBED, "window.title"]
    )
    await agent.wait_for(lambda: state_of(hass, window) == "Some window")


async def test_added_and_removed_metrics_update_the_subscribe(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """A metric that appears or vanishes on the agent changes the subscription."""
    # The fixture's manifest already has the default new revision; a change needs
    # another one (a valid one: 12 hex digits), otherwise the integration rightly
    # ignores the announcement.
    agent.set_manifest(
        changed_manifest(
            agent,
            rev=SECOND_REV,
            add_metrics=[MEMORY_METRIC],
            remove_metrics=["audio.volume"],
        )
    )
    await agent.send_manifest_changed()

    # The sensor and the binary sensor platform apply the change one after the other
    # and the manager refreshes in a background task. Wait for the visible result of
    # the change first, let the entities settle, and only then look at the subscribe,
    # so that no order of the platforms can show a half-applied state.
    def applied() -> bool:
        gone = find_entity_id(hass, "sensor", agent.agent_id, "audio.volume")
        new = find_entity_id(hass, "sensor", agent.agent_id, "memory.used")
        return gone is None and new is not None

    await agent.wait_for(applied)
    await hass.async_block_till_done()
    expected = sorted([*(m for m in SUBSCRIBED if m != "audio.volume"), "memory.used"])
    # The debounced subscribe runs in a background task that ``async_block_till_done``
    # does not wait for; poll for the final set (the last request, never an earlier
    # intermediate one).
    await agent.wait_for(lambda: agent.subscribed_metrics()[-1:] == [expected])
    assert entry.runtime_data.subscriptions.metric_ids == frozenset(expected)


async def test_subscription_is_sent_again_after_a_reconnect(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The set survives a lost connection and values flow again."""
    client = entry.runtime_data.client
    cpu = eid(hass, agent, "cpu.usage")

    await reconnect(agent, client)
    await agent.wait_for(lambda: agent.ready_connections[0].subscriptions != [])

    assert agent.ready_connections[0].subscriptions[-1]["metrics"] == SUBSCRIBED
    await agent.send_state({"cpu.usage": 77})
    await agent.wait_for(lambda: state_of(hass, cpu) == "77")


# -- pure helpers -----------------------------------------------------------------


def test_build_spec_keeps_valid_numeric_properties() -> None:
    """A temperature with a valid unit is passed through."""
    spec = build_spec(
        descriptor(
            device_class="temperature",
            unit="°C",
            state_class="measurement",
            precision=1,
        )
    )

    assert spec == SensorSpec(
        "number",
        device_class=SensorDeviceClass.TEMPERATURE,
        unit="°C",
        state_class=SensorStateClass.MEASUREMENT,
        precision=1,
    )


def test_build_spec_drops_what_this_home_assistant_does_not_know() -> None:
    """Unknown classes and a unit that does not fit the device class are dropped."""
    spec = build_spec(
        descriptor(device_class="hologram", unit="bogus", state_class="bogus")
    )
    assert spec == SensorSpec("number", unit="bogus")

    spec = build_spec(descriptor(device_class="temperature", unit="bogus"))
    assert spec.device_class is SensorDeviceClass.TEMPERATURE
    assert spec.unit is None


def test_build_spec_does_not_accept_enum_or_timestamp_class_for_numbers() -> None:
    """These classes need their own value types."""
    assert build_spec(descriptor(device_class="enum")).device_class is None
    assert build_spec(descriptor(device_class="timestamp")).device_class is None


def test_build_spec_ignores_numeric_properties_of_text_metrics() -> None:
    """A text metric has no unit, state class or precision."""
    spec = build_spec(
        descriptor(
            value_type="string", unit="%", state_class="measurement", precision=2
        )
    )

    assert spec == SensorSpec("string")


def test_build_spec_of_timestamp_and_enum_metrics() -> None:
    """The value type decides the device class."""
    timestamp = build_spec(descriptor(value_type="timestamp", unit="s", precision=1))
    assert timestamp == SensorSpec(
        "timestamp", device_class=SensorDeviceClass.TIMESTAMP
    )

    enum = build_spec(descriptor(value_type="enum", options=("a", "b")))
    assert enum == SensorSpec(
        "enum", device_class=SensorDeviceClass.ENUM, options=("a", "b")
    )

    # An enum without options cannot be an enum sensor: it is shown as text.
    assert build_spec(descriptor(value_type="enum")) == SensorSpec("string")


@pytest.mark.parametrize(
    ("value_type", "value", "expected"),
    [
        ("number", 3, 3),
        ("number", 2.5, 2.5),
        ("number", True, None),
        ("number", "3", None),
        ("number", None, None),
        ("string", "abc", "abc"),
        ("string", 4, "4"),
        ("string", True, "true"),
        ("string", "x" * 300, "x" * MAX_STATE_LENGTH),
        ("bool", False, "false"),
    ],
)
def test_convert_value(value_type: str, value: Any, expected: Any) -> None:
    """Values are converted to what the sensor type can show."""
    spec = build_spec(descriptor(value_type=value_type))

    assert convert_value(spec, "test.metric", value) == expected


def test_convert_value_of_enums_and_timestamps() -> None:
    """Enum values must be options; timestamps get a time zone."""
    enum = build_spec(descriptor(value_type="enum", options=("a", "b")))
    assert convert_value(enum, "test.metric", "a") == "a"
    assert convert_value(enum, "test.metric", "c") is None
    assert convert_value(enum, "test.metric", 1) is None

    timestamp = build_spec(descriptor(value_type="timestamp"))
    aware = convert_value(timestamp, "test.metric", "2026-01-01T00:00:00+01:00")
    naive = convert_value(timestamp, "test.metric", "2026-01-01T00:00:00")
    assert isinstance(aware, datetime)
    assert aware.utcoffset() == timedelta(hours=1)
    assert isinstance(naive, datetime)
    assert naive.utcoffset() == timedelta(0)
    assert convert_value(timestamp, "test.metric", "never") is None
    assert convert_value(timestamp, "test.metric", 5) is None


@pytest.mark.parametrize(
    ("value", "expected"),
    [(True, True), (False, False), (1, True), (0, False), (2, None), ("on", None)],
)
def test_convert_state(value: Any, expected: bool | None) -> None:
    """Only booleans and 0 or 1 are states of a binary sensor."""
    assert convert_state("test.metric", value) is expected
