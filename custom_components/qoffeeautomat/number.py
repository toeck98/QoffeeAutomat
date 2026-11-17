"""Number platform: commands with an int or float variable (PLAN.md S06, S10)."""

from homeassistant.components.number import NumberEntity, NumberMode
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

# Commands are started one after the other per platform.
PARALLEL_UPDATES = 1

# Bounds of a variable that has none; wide, so the agent decides what is valid.
UNBOUNDED_MIN = -1_000_000_000.0
UNBOUNDED_MAX = 1_000_000_000.0


def _build(
    runtime: QoffeeAutomatRuntimeData,
    descriptor: CommandDescriptor,
    control: ControlDescriptor,
) -> QoffeeAutomatNumber | None:
    variable = find_variable(descriptor, control, frozenset({"int", "float"}))
    if variable is None:
        return None
    return QoffeeAutomatNumber(runtime, descriptor, control, variable)


async def async_setup_entry(
    hass: HomeAssistant,
    entry: QoffeeAutomatConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    """Set up the numbers of the commands in the manifest."""
    await async_setup_command_platform(entry, async_add_entities, "number", _build)


class QoffeeAutomatNumber(QoffeeAutomatCommandEntity, NumberEntity):
    """Setting a value runs the command with that value."""

    def __init__(
        self,
        runtime: QoffeeAutomatRuntimeData,
        descriptor: CommandDescriptor,
        control: ControlDescriptor,
        variable: VariableDescriptor,
    ) -> None:
        """Create the number that sets ``variable``.

        Minimum, maximum, step and unit are those of the variable.
        """
        super().__init__(runtime, descriptor, control)
        self._variable = variable.name
        self._is_int = variable.type == "int"
        bounded = variable.min is not None and variable.max is not None
        self._attr_native_min_value = (
            variable.min if variable.min is not None else UNBOUNDED_MIN
        )
        self._attr_native_max_value = (
            variable.max if variable.max is not None else UNBOUNDED_MAX
        )
        if variable.step is not None:
            self._attr_native_step = variable.step
        elif self._is_int:
            self._attr_native_step = 1.0
        self._attr_native_unit_of_measurement = variable.unit
        self._attr_mode = NumberMode.AUTO if bounded else NumberMode.BOX
        self._attr_assumed_state = control.state_metric is None

    def _apply_value(self, value: MetricValue) -> None:
        if isinstance(value, int | float) and not isinstance(value, bool):
            self._attr_native_value = float(value)
        else:
            self._attr_native_value = None

    async def async_set_native_value(self, value: float) -> None:
        """Run the command with the value (a whole number for an ``int`` variable)."""
        await self._async_run_and_show(
            self._variable, round(value) if self._is_int else value
        )
