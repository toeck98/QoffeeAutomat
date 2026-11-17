"""Text platform: commands with a string variable (PLAN.md S06, S10)."""

import logging
import re

from homeassistant.components.text import TextEntity, TextMode
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback

from .entity import (
    QoffeeAutomatCommandEntity,
    async_setup_command_platform,
    find_variable,
)
from .protocol import (
    CommandDescriptor,
    ControlDescriptor,
    MetricValue,
    VariableDescriptor,
)
from .runtime import QoffeeAutomatConfigEntry, QoffeeAutomatRuntimeData

_LOGGER = logging.getLogger(__name__)

# Commands are started one after the other per platform.
PARALLEL_UPDATES = 1

# Home Assistant refuses states longer than this, so a text entity cannot hold more.
MAX_TEXT_LENGTH = 255


def _build(
    runtime: QoffeeAutomatRuntimeData,
    descriptor: CommandDescriptor,
    control: ControlDescriptor,
) -> QoffeeAutomatText | None:
    variable = find_variable(descriptor, control, frozenset({"string"}))
    if variable is None:
        return None
    return QoffeeAutomatText(runtime, descriptor, control, variable)


async def async_setup_entry(
    hass: HomeAssistant,
    entry: QoffeeAutomatConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    """Set up the texts of the commands in the manifest."""
    await async_setup_command_platform(entry, async_add_entities, "text", _build)


def _usable_pattern(command_id: str, pattern: str | None) -> str | None:
    """Return the pattern if Python can compile it.

    The agent matches with a .NET regular expression; a pattern that Python cannot
    read is dropped here and left to the agent, which validates every value anyway.
    """
    if pattern is None:
        return None
    try:
        re.compile(pattern)
    except re.error:
        _LOGGER.debug("Ignoring pattern %r of command %s", pattern, command_id)
        return None
    return pattern


class QoffeeAutomatText(QoffeeAutomatCommandEntity, TextEntity):
    """Setting a value runs the command with that text."""

    def __init__(
        self,
        runtime: QoffeeAutomatRuntimeData,
        descriptor: CommandDescriptor,
        control: ControlDescriptor,
        variable: VariableDescriptor,
    ) -> None:
        """Create the text that sets ``variable``."""
        super().__init__(runtime, descriptor, control)
        self._variable = variable.name
        self._attr_native_value = None
        self._attr_native_max = min(variable.max_length or 1024, MAX_TEXT_LENGTH)
        self._attr_pattern = _usable_pattern(descriptor.id, variable.pattern)
        self._attr_mode = TextMode.PASSWORD if variable.secret else TextMode.TEXT
        self._attr_assumed_state = control.state_metric is None

    def _apply_value(self, value: MetricValue) -> None:
        if value is None:
            self._attr_native_value = None
        else:
            text = str(value).lower() if isinstance(value, bool) else str(value)
            self._attr_native_value = text[:MAX_TEXT_LENGTH]

    async def async_set_value(self, value: str) -> None:
        """Run the command with the text."""
        await self._async_run_and_show(self._variable, value)
