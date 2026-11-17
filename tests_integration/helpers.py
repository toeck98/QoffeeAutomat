"""Helpers shared by the tests that run the integration against the fake agent."""

import copy
from collections.abc import Awaitable, Callable, Iterable
from typing import Any

from homeassistant.core import HomeAssistant
from homeassistant.helpers import entity_registry as er
from pytest_homeassistant_custom_component.common import MockConfigEntry

from custom_components.qoffeeautomat.client import QoffeeLinkClient
from custom_components.qoffeeautomat.const import DOMAIN
from tests_integration.fake_agent import FakeAgent

# The ``entry_factory`` fixture: adds (and by default sets up) an entry of the agent.
type EntryFactory = Callable[..., Awaitable[MockConfigEntry]]

NEW_REV = "aaaaaaaaaaaa"
# A revision after ``NEW_REV`` (a revision is 12 hex digits).
SECOND_REV = "bbbbbbbbbbbb"

MEMORY_METRIC: dict[str, Any] = {
    "id": "memory.used",
    "name": "Memory used",
    "kind": "sensor",
    "value_type": "number",
    "unit": "GiB",
    "device_class": "data_size",
    "state_class": "measurement",
    "precision": 2,
    "enabled_default": True,
}
NEW_COMMAND: dict[str, Any] = {
    "id": "tidy_desktop",
    "name": "Tidy desktop",
    "control": {"type": "button"},
}


def find_entity_id(
    hass: HomeAssistant, domain: str, agent_id: str, key: str
) -> str | None:
    """Return the entity id of a metric or command entity, if it is registered."""
    return er.async_get(hass).async_get_entity_id(domain, DOMAIN, f"{agent_id}_{key}")


def entity_id_of(hass: HomeAssistant, domain: str, agent_id: str, key: str) -> str:
    """Return the entity id of a registered metric or command entity."""
    entity_id = find_entity_id(hass, domain, agent_id, key)
    assert entity_id is not None, f"no {domain} entity for {key}"
    return entity_id


def state_of(hass: HomeAssistant, entity_id: str) -> str:
    """Return the state string of an entity."""
    state = hass.states.get(entity_id)
    assert state is not None, f"{entity_id} has no state"
    return state.state


def attribute_of(hass: HomeAssistant, entity_id: str, name: str) -> Any:
    """Return a state attribute (``None`` if missing)."""
    state = hass.states.get(entity_id)
    return state.attributes.get(name) if state is not None else None


def changed_manifest(
    fake_agent: FakeAgent,
    *,
    rev: str = NEW_REV,
    add_metrics: Iterable[dict[str, Any]] = (),
    remove_metrics: Iterable[str] = (),
    add_commands: Iterable[dict[str, Any]] = (),
    remove_commands: Iterable[str] = (),
) -> dict[str, Any]:
    """Return a copy of the agent's manifest with other metrics and commands."""
    manifest = copy.deepcopy(fake_agent.manifest)
    manifest["rev"] = rev
    dropped_metrics = set(remove_metrics)
    dropped_commands = set(remove_commands)
    manifest["metrics"] = [
        metric for metric in manifest["metrics"] if metric["id"] not in dropped_metrics
    ] + list(add_metrics)
    manifest["commands"] = [
        command
        for command in manifest["commands"]
        if command["id"] not in dropped_commands
    ] + list(add_commands)
    return manifest


async def reconnect(fake_agent: FakeAgent, client: QoffeeLinkClient) -> None:
    """Drop the connection and let the client connect again right away."""
    fake_agent.drop_connections()
    await fake_agent.wait_for(lambda: not client.connected)
    client.trigger_connect()
    await fake_agent.wait_for(lambda: client.connected)
