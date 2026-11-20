"""Changing the route of a PC between direct link and hub (PLAN.md S10, S20, T914).

A PC is reached on exactly one of two routes (D-031): through a config entry of its own
(direct link, or reverse link of a roaming PC) or through the config entry of a hub. The
unique ids of the entities and the identifier of the device are the same on both, so a
change of the route can keep the entity ids and every customisation of the user (name,
area, disabled, history) as long as the registry entries are handed from the old config
entry to the new one *before* the old entry goes away.

How the registries behave when the owning config entry changes (verified against Home
Assistant 2026.10, ``helpers/device_registry.py`` and ``entity_registry.py``):

* A device belongs to exactly one config entry. ``async_get_or_create`` with the
  identifiers of a device that belongs to another entry creates a *second* device, so
  the device has to be moved explicitly (``async_update_device(new_config_entry_id=
  ...)``; older versions move it with ``add_config_entry_id`` followed by
  ``remove_config_entry_id``).
* Moving a device removes the entities of the old entry that are on it, including their
  registry entries. They are therefore handed to the new entry first
  (``async_update_entity(config_entry_id=...)``, which keeps the entity id, the name and
  all other settings), and the device moves after them.
* Removing the old config entry clears the registry entries that still belong to it, so
  it comes last.
"""

import inspect
import logging
from collections.abc import Collection
from typing import TYPE_CHECKING

from homeassistant.config_entries import ConfigEntry, ConfigEntryState
from homeassistant.core import HomeAssistant, callback
from homeassistant.helpers import device_registry as dr
from homeassistant.helpers import entity_registry as er

from .const import CONF_HUB, DOMAIN
from .runtime import FleetRuntimeData

if TYPE_CHECKING:
    from .fleet import FleetManager

_LOGGER = logging.getLogger(__name__)

# Reasons of a refused change of the route; they are keys under the ``abort`` texts of
# the repair flows.
REASON_HUB_UNAVAILABLE = "hub_unavailable"
REASON_NO_DIRECT_ENTRY = "no_direct_entry"

# Home Assistant versions that know ``new_config_entry_id`` move a device in one call.
_NATIVE_DEVICE_MOVE = (
    "new_config_entry_id"
    in inspect.signature(dr.DeviceRegistry.async_update_device).parameters
)


class RouteSwitchError(Exception):
    """A change of the route is not possible; ``reason`` names the reason."""

    def __init__(self, reason: str) -> None:
        """Remember the reason."""
        super().__init__(reason)
        self.reason = reason


def is_hub_entry(entry: ConfigEntry) -> bool:
    """Return whether the config entry connects to a hub."""
    return bool(entry.data.get(CONF_HUB))


def fleet_of(entry: ConfigEntry) -> FleetManager | None:
    """Return the manager of a loaded hub entry (``None`` for any other entry)."""
    data = getattr(entry, "runtime_data", None)
    return data.fleet if isinstance(data, FleetRuntimeData) else None


def loaded_fleets(hass: HomeAssistant) -> list[FleetManager]:
    """Return the managers of all hub entries that are loaded."""
    return [
        fleet
        for entry in hass.config_entries.async_entries(DOMAIN)
        if entry.state is ConfigEntryState.LOADED
        and (fleet := fleet_of(entry)) is not None
    ]


def direct_entry_for(
    hass: HomeAssistant, agent_id: str, *, ignoring: Collection[str] = ()
) -> ConfigEntry | None:
    """Return the config entry of its own that serves the PC, if there is one.

    That is an entry that is not a hub's, has the agent id as unique id, is neither
    ignored nor disabled and is not one of ``ignoring`` (entries that are being
    removed).
    """
    for entry in hass.config_entries.async_entries(
        DOMAIN, include_ignore=False, include_disabled=False
    ):
        if (
            entry.unique_id == agent_id
            and not is_hub_entry(entry)
            and entry.entry_id not in ignoring
        ):
            return entry
    return None


def hub_entry_knowing(hass: HomeAssistant, agent_id: str) -> ConfigEntry | None:
    """Return a loaded hub entry whose hub lists the PC (served or not)."""
    for entry in hass.config_entries.async_entries(DOMAIN):
        fleet = fleet_of(entry)
        if (
            entry.state is ConfigEntryState.LOADED
            and fleet is not None
            and fleet.knows(agent_id)
        ):
            return entry
    return None


def _move_device(
    devices: dr.DeviceRegistry, device_id: str, source_id: str, target_id: str
) -> None:
    if _NATIVE_DEVICE_MOVE:
        devices.async_update_device(device_id, new_config_entry_id=target_id)
    else:
        devices.async_update_device(device_id, add_config_entry_id=target_id)
        devices.async_update_device(device_id, remove_config_entry_id=source_id)


@callback
def async_move_agent(hass: HomeAssistant, agent_id: str, target: ConfigEntry) -> None:
    """Hand the device and the registry entries of a PC to the config entry ``target``.

    The entities must not be added to Home Assistant by the old entry any more (they
    stay in the registry). Entity ids, names and all other settings are kept, because
    the entries themselves are changed and no new ones are created. Does nothing when
    no other entry of the integration owns a device of the PC.
    """
    devices = dr.async_get(hass)
    entities = er.async_get(hass)
    identifier = (DOMAIN, agent_id)
    for source in hass.config_entries.async_entries(DOMAIN):
        if source.entry_id == target.entry_id:
            continue
        for device in dr.async_entries_for_config_entry(devices, source.entry_id):
            if identifier not in device.identifiers:
                continue
            for entity in er.async_entries_for_device(
                entities, device.id, include_disabled_entities=True
            ):
                if entity.config_entry_id == source.entry_id:
                    entities.async_update_entity(
                        entity.entity_id, config_entry_id=target.entry_id
                    )
            _move_device(devices, device.id, source.entry_id, target.entry_id)
            _LOGGER.debug("Moved the device of %s to another config entry", agent_id)


async def async_adopt_agent(hass: HomeAssistant, entry: ConfigEntry) -> None:
    """Take over the PC of ``entry`` from the hubs that serve it (hub to direct link).

    Called when an entry of its own is set up, before it registers its device: the hubs
    let go of the PC (its entities leave Home Assistant, the registry entries stay) and
    the device and entries are handed to ``entry``. The PC is on the direct route from
    now on; the hubs keep skipping it and offer to switch back (repair issue).
    """
    agent_id = entry.unique_id
    if agent_id is None:
        return
    for fleet in loaded_fleets(hass):
        await fleet.async_release_agent(agent_id)
    async_move_agent(hass, agent_id, entry)


async def async_switch_to_hub(
    hass: HomeAssistant, hub_entry_id: str, agent_id: str
) -> None:
    """Move a PC from its direct route to the hub route, keeping entity ids.

    Raises :class:`RouteSwitchError` when the hub entry is not loaded, does not list the
    PC, or when there is no entry of its own to move from. The entry of its own is
    removed at the end; the PC then belongs to the hub entry.
    """
    hub = hass.config_entries.async_get_entry(hub_entry_id)
    fleet = fleet_of(hub) if hub is not None else None
    if (
        hub is None
        or hub.state is not ConfigEntryState.LOADED
        or fleet is None
        or not fleet.knows(agent_id)
    ):
        raise RouteSwitchError(REASON_HUB_UNAVAILABLE)
    source = direct_entry_for(hass, agent_id)
    if source is None:
        raise RouteSwitchError(REASON_NO_DIRECT_ENTRY)
    # The old entry stops serving the PC; its registry entries stay for the new one.
    await hass.config_entries.async_unload(source.entry_id)
    async_move_agent(hass, agent_id, hub)
    await hass.config_entries.async_remove(source.entry_id)
    await fleet.async_adopt_agent(agent_id)
