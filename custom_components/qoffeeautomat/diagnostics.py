"""Diagnostics of the QoffeeAutomat integration (PLAN.md S10).

The output never contains the link token, the certificate fingerprint, addresses (host
names, IP and MAC addresses, URLs) or metric values: the value of a metric such as the
IPv4 address of the PC is an address itself, so entity states are left out and only the
structure (ids, kinds, counts) is reported.
"""

from typing import TYPE_CHECKING, Any

from homeassistant.components.diagnostics import async_redact_data
from homeassistant.core import HomeAssistant
from homeassistant.helpers import device_registry as dr
from homeassistant.helpers import entity_registry as er

from .const import (
    CONF_AGENT_MACS,
    CONF_FINGERPRINT,
    CONF_REVERSE_TOKEN_HASH,
    CONF_TOKEN,
    DOMAIN,
)
from .routes import fleet_of
from .runtime import (
    QoffeeAutomatConfigEntry,
    QoffeeAutomatRuntimeData,
    agent_runtime_of,
)

if TYPE_CHECKING:
    from .fleet import FleetManager

REDACTED_KEYS = frozenset(
    {
        CONF_TOKEN,
        CONF_AGENT_MACS,
        CONF_REVERSE_TOKEN_HASH,
        CONF_FINGERPRINT,
        "host",
        "hostname",
        "ip",
        "ip_address",
        "ipv4",
        "ipv6",
        "mac",
        "macs",
        "mac_address",
        "broadcast",
        "connections",
        "configuration_url",
        "ui_url",
        "password",
        "secret",
        "code",
        "pairing_code",
        "authorization",
    }
)


def _entry_summary(entry: QoffeeAutomatConfigEntry) -> dict[str, Any]:
    """Return the redacted config entry data."""
    return {
        "title": "**REDACTED**",
        "version": entry.version,
        "state": entry.state.value,
        "data": async_redact_data(dict(entry.data), REDACTED_KEYS),
        "options": async_redact_data(dict(entry.options), REDACTED_KEYS),
    }


def _hub_summary(fleet: FleetManager) -> dict[str, Any]:
    """Return the state of the fleet link and the PCs that the hub lists.

    Names of PCs are left out like the title of the entry; ids and states are not
    addresses or secrets.
    """
    client = fleet.client
    last_error = client.last_error
    return {
        "hub": {
            "version": fleet.hub.version,
            "connected": client.connected,
            "auth_failed": client.auth_failed,
            "last_error": type(last_error).__name__ if last_error else None,
            "protocol": (
                {"min": client.hello.protocol.min, "max": client.hello.protocol.max}
                if client.hello
                else None
            ),
        },
        "agents": {
            agent_id: {
                "served": (runtime := fleet.runtime_of(agent_id)) is not None,
                "online": runtime.connection.online
                if runtime is not None
                else agent.online,
                "power_state": runtime.connection.power_state.value
                if runtime is not None
                else agent.power_state,
            }
            for agent_id, agent in fleet.known_agents.items()
        },
    }


def _agent_summary(runtime: QoffeeAutomatRuntimeData) -> dict[str, Any]:
    """Return what the agent reported about itself and the state of the link."""
    client = runtime.client
    last_error = client.last_error
    connection = {
        "online": runtime.connection.online,
        "power_state": runtime.connection.power_state.value,
        "auth_failed": client.auth_failed,
        "last_error": type(last_error).__name__ if last_error else None,
        "protocol": (
            {"min": client.hello.protocol.min, "max": client.hello.protocol.max}
            if client.hello
            else None
        ),
    }
    if not runtime.manifest_manager.loaded:
        # An agent that dials Home Assistant and has not connected yet.
        return {
            "agent": None,
            "connection": connection,
            "manifest": None,
            "subscribed_metrics": sorted(runtime.subscriptions.metric_ids),
        }
    manifest = runtime.manifest_manager.manifest
    agent = manifest.agent
    return {
        "agent": async_redact_data(
            {
                "id": agent.id,
                "version": agent.version,
                "os": agent.os,
                "os_version": agent.os_version,
                "arch": agent.arch,
                "hostname": agent.hostname,
                "macs": list(agent.macs),
                "ui_url": agent.ui_url,
            },
            REDACTED_KEYS,
        ),
        "connection": connection,
        "manifest": {
            "rev": manifest.rev,
            "features": list(manifest.features),
            "metrics": [
                {
                    "id": metric.id,
                    "kind": metric.kind,
                    "value_type": metric.value_type,
                    "enabled_default": metric.enabled_default,
                }
                for metric in manifest.metrics
            ],
            "commands": [
                {
                    "id": command.id,
                    "control": command.control.type if command.control else None,
                    "variables": len(command.vars),
                }
                for command in manifest.commands
            ],
            "events": [event.type for event in manifest.events],
        },
        "subscribed_metrics": sorted(runtime.subscriptions.metric_ids),
    }


async def async_get_config_entry_diagnostics(
    hass: HomeAssistant, entry: QoffeeAutomatConfigEntry
) -> dict[str, Any]:
    """Return the diagnostics of a config entry (of one PC, or of a hub)."""
    fleet = fleet_of(entry)
    if fleet is not None:
        return {"entry": _entry_summary(entry), **_hub_summary(fleet)}
    return {"entry": _entry_summary(entry), **_agent_summary(agent_runtime_of(entry))}


async def async_get_device_diagnostics(
    hass: HomeAssistant, entry: QoffeeAutomatConfigEntry, device: dr.DeviceEntry
) -> dict[str, Any]:
    """Return the diagnostics of the device of an agent (or of the hub)."""
    registry = er.async_get(hass)
    entities = [
        {
            "entity_id": entity.entity_id,
            "domain": entity.domain,
            "original_name": entity.original_name,
            "translation_key": entity.translation_key,
            "entity_category": entity.entity_category,
            "disabled_by": entity.disabled_by,
        }
        for entity in er.async_entries_for_device(
            registry, device.id, include_disabled_entities=True
        )
    ]
    device_info = async_redact_data(
        {
            "manufacturer": device.manufacturer,
            "model": device.model,
            "sw_version": device.sw_version,
            "name": device.name,
            "connections": [list(item) for item in device.connections],
            "configuration_url": device.configuration_url,
        },
        REDACTED_KEYS,
    )
    fleet = fleet_of(entry)
    if fleet is None:
        summary = _agent_summary(agent_runtime_of(entry))
    else:
        # The device of one PC of the hub; the device of the hub itself has no PC.
        agent_runtime = next(
            (
                found
                for domain, key in device.identifiers
                if domain == DOMAIN and (found := fleet.runtime_of(key)) is not None
            ),
            None,
        )
        summary = (
            _agent_summary(agent_runtime) if agent_runtime else _hub_summary(fleet)
        )
    return {
        "entry": _entry_summary(entry),
        "device": device_info,
        "entities": entities,
        **summary,
    }
