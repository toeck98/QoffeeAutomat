"""Switch platform: commands with a boolean variable (PLAN.md S06, S10)."""

from typing import Any

from homeassistant.components.switch import SwitchEntity
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback

from .binary_sensor import convert_state
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

# Commands are started one after the other per platform.
PARALLEL_UPDATES = 1


def _build(
    runtime: QoffeeAutomatRuntimeData,
    descriptor: CommandDescriptor,
    control: ControlDescriptor,
) -> QoffeeAutomatSwitch | None:
    variable = find_variable(descriptor, control, frozenset({"bool"}))
    if variable is None:
        return None
    return QoffeeAutomatSwitch(runtime, descriptor, control, variable)


async def async_setup_entry(
    hass: HomeAssistant,
    entry: QoffeeAutomatConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    """Set up the switches of the commands in the manifest."""
    await async_setup_command_platform(entry, async_add_entities, "switch", _build)


class QoffeeAutomatSwitch(QoffeeAutomatCommandEntity, SwitchEntity):
    """On runs the command with ``true``, off with ``false``."""

    def __init__(
        self,
        runtime: QoffeeAutomatRuntimeData,
        descriptor: CommandDescriptor,
        control: ControlDescriptor,
        variable: VariableDescriptor,
    ) -> None:
        """Create the switch that sets ``variable``."""
        super().__init__(runtime, descriptor, control)
        self._variable = variable.name
        self._attr_assumed_state = control.state_metric is None

    def _apply_value(self, value: MetricValue) -> None:
        self._attr_is_on = convert_state(self.descriptor.id, value)

    async def async_turn_on(self, **kwargs: Any) -> None:
        """Run the command with ``true``."""
        await self._async_run_and_show(self._variable, True)

    async def async_turn_off(self, **kwargs: Any) -> None:
        """Run the command with ``false``."""
        await self._async_run_and_show(self._variable, False)
