"""Event platform and bus events (PLAN.md S07, S10).

Every ``event`` frame of the agent, and the ``booted`` signal that the connection state
raises when the boot id of the PC changed, fires the bus event ``qoffeeautomat_event``
and triggers the event entity of the device. The bus event does not depend on the
entity: it is also fired while the user has disabled the entity.

Automations can trigger on the bus event or on the event entity::

    triggers:
      - trigger: event
        event_type: qoffeeautomat_event
        event_data:
          type: booted
"""

import logging
from collections.abc import Mapping
from typing import Any

from homeassistant.components.event import EventEntity
from homeassistant.core import HomeAssistant, callback
from homeassistant.helpers import device_registry as dr
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback

from .const import DOMAIN, EVENT_QOFFEEAUTOMAT, EVENT_TYPE_BOOTED
from .entity import QoffeeAutomatEntity
from .manifest_manager import ManifestDiff
from .protocol import Event
from .runtime import QoffeeAutomatConfigEntry, QoffeeAutomatRuntimeData

_LOGGER = logging.getLogger(__name__)

PARALLEL_UPDATES = 0


def event_types_of(runtime: QoffeeAutomatRuntimeData) -> list[str]:
    """Return ``booted`` and the event types that the manifest announces."""
    types = [EVENT_TYPE_BOOTED]
    for descriptor in runtime.manifest_manager.manifest.events:
        if descriptor.type not in types:
            types.append(descriptor.type)
    return types


async def async_setup_entry(
    hass: HomeAssistant,
    entry: QoffeeAutomatConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    """Set up the event entity and fire the bus event for agent events."""
    runtime = entry.runtime_data

    @callback
    def fire(event_type: str, data: Mapping[str, Any]) -> None:
        device = dr.async_get(hass).async_get_device_by_identifier(
            (DOMAIN, runtime.agent_id), entry.entry_id
        )
        hass.bus.async_fire(
            EVENT_QOFFEEAUTOMAT,
            {
                "device_id": device.id if device is not None else None,
                "type": event_type,
                "data": dict(data),
            },
        )

    @callback
    def on_event(frame: Event) -> None:
        fire(frame.type, frame.data)

    @callback
    def on_booted() -> None:
        fire(EVENT_TYPE_BOOTED, {})

    async_add_entities([QoffeeAutomatEventEntity(runtime)])
    entry.async_on_unload(runtime.client.add_event_listener(on_event))
    entry.async_on_unload(runtime.connection.add_booted_listener(on_booted))


class QoffeeAutomatEventEntity(QoffeeAutomatEntity, EventEntity):
    """Shows the last event of the agent; ``booted`` is raised by the integration."""

    _attr_translation_key = "events"

    def __init__(self, runtime: QoffeeAutomatRuntimeData) -> None:
        """Create the entity with the event types of the manifest."""
        super().__init__(runtime, "_events")
        self._attr_event_types = event_types_of(runtime)

    async def async_added_to_hass(self) -> None:
        """Follow the events of the agent, the boot signal and the event types."""
        await super().async_added_to_hass()
        self.async_on_remove(
            self._runtime.client.add_event_listener(self._handle_agent_event)
        )
        self.async_on_remove(
            self._runtime.connection.add_booted_listener(self._handle_booted)
        )
        self.async_on_remove(
            await self._runtime.manifest_manager.async_add_listener(
                self._async_handle_manifest
            )
        )

    @callback
    def _handle_agent_event(self, frame: Event) -> None:
        self._trigger(frame.type, frame.data)

    @callback
    def _handle_booted(self) -> None:
        self._trigger(EVENT_TYPE_BOOTED, {})

    def _trigger(self, event_type: str, data: Mapping[str, Any]) -> None:
        if event_type not in self.event_types:
            # The bus event is fired for every type; the entity knows the manifest's.
            _LOGGER.debug("Event type %s is not in the manifest", event_type)
            return
        self._trigger_event(event_type, dict(data))
        self.async_write_ha_state()

    async def _async_handle_manifest(self, diff: ManifestDiff) -> None:
        if not diff.events_changed:
            return
        types = event_types_of(self._runtime)
        if types != self._attr_event_types:
            self._attr_event_types = types
            self.async_write_ha_state()
