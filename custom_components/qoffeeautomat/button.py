"""Button platform: commands that run without a value (PLAN.md S06, S10)."""

import logging

from homeassistant.components.button import ButtonEntity
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback

from .entity import QoffeeAutomatCommandEntity, async_setup_command_platform
from .protocol import CommandDescriptor, ControlDescriptor, MetricValue
from .runtime import QoffeeAutomatConfigEntry, QoffeeAutomatRuntimeData

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
    """Set up the buttons of the commands in the manifest."""
    await async_setup_command_platform(entry, async_add_entities, "button", _build)


class QoffeeAutomatButton(QoffeeAutomatCommandEntity, ButtonEntity):
    """Runs the command with its default values when pressed."""

    def _apply_value(self, value: MetricValue) -> None:
        """A button has no state."""

    async def async_press(self) -> None:
        """Run the command."""
        await self._async_run()
