"""Button platform: commands that run without a value (PLAN.md S06, S10)."""

import logging

from homeassistant.components.button import ButtonEntity
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import HomeAssistantError, ServiceValidationError
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback

from .const import (
    CONF_BROADCAST,
    CONF_MACS,
    DEFAULT_BROADCAST,
    DOMAIN,
    WOL_FAST_RETRY_SECONDS,
    WOL_KEY,
)
from .entity import (
    QoffeeAutomatCommandEntity,
    QoffeeAutomatEntity,
    async_setup_command_platform,
)
from .protocol import CommandDescriptor, ControlDescriptor, MetricValue
from .runtime import QoffeeAutomatConfigEntry, QoffeeAutomatRuntimeData
from .wol import async_send_magic_packets

_LOGGER = logging.getLogger(__name__)

# Commands are started one after the other per platform.
PARALLEL_UPDATES = 1


def _build(
    runtime: QoffeeAutomatRuntimeData,
    descriptor: CommandDescriptor,
    control: ControlDescriptor,
) -> QoffeeAutomatButton | None:
    """Create the button unless the command needs a value that a press cannot give."""
    if any(var.required and var.default is None for var in descriptor.vars):
        _LOGGER.debug(
            "Command %s has required variables without a default: no button",
            descriptor.id,
        )
        return None
    return QoffeeAutomatButton(runtime, descriptor, control)


async def async_setup_entry(
    hass: HomeAssistant,
    entry: QoffeeAutomatConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    """Set up the buttons of the commands in the manifest and the Wake-on-LAN button.

    The Wake-on-LAN button exists once the MAC addresses of the PC are known; they come
    from the manifest and are kept in the config entry (T703).
    """
    await async_setup_command_platform(entry, async_add_entities, "button", _build)
    if entry.data.get(CONF_MACS):
        async_add_entities([QoffeeAutomatWakeButton(entry)])


class QoffeeAutomatWakeButton(QoffeeAutomatEntity, ButtonEntity):
    """Wakes the PC with Wake-on-LAN magic packets (PLAN.md S10, S16, T703).

    The button stays available while the agent is disconnected, because that is when it
    is needed. A press sends a magic packet for every stored MAC address to the
    broadcast address of the options and then makes the client retry the connection
    every second for five minutes, so Home Assistant notices the PC as soon as it is up.
    """

    _always_available = True
    _attr_translation_key = "wake_on_lan"

    def __init__(self, entry: QoffeeAutomatConfigEntry) -> None:
        """Create the button of the PC of ``entry``."""
        super().__init__(entry.runtime_data, WOL_KEY)
        self._entry = entry

    async def async_press(self) -> None:
        """Send the magic packets and switch the client to fast retry."""
        macs = list(self._entry.data.get(CONF_MACS, ()))
        if not macs:
            raise ServiceValidationError(
                translation_domain=DOMAIN, translation_key="wol_no_mac"
            )
        broadcast = self._entry.options.get(CONF_BROADCAST, DEFAULT_BROADCAST)
        try:
            sent = await async_send_magic_packets(macs, broadcast)
        except (OSError, ValueError) as err:
            raise HomeAssistantError(
                translation_domain=DOMAIN,
                translation_key="wol_failed",
                translation_placeholders={"error": str(err)},
            ) from err
        _LOGGER.debug("Sent %s magic packets to %s", sent, broadcast)
        self._runtime.client.start_fast_retry(WOL_FAST_RETRY_SECONDS)


class QoffeeAutomatButton(QoffeeAutomatCommandEntity, ButtonEntity):
    """Runs the command with its default values when pressed."""

    def _apply_value(self, value: MetricValue) -> None:
        """A button has no state."""

    async def async_press(self) -> None:
        """Run the command."""
        await self._async_run()
