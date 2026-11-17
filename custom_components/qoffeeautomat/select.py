"""Select platform: commands with an enum variable (PLAN.md S06, S10)."""

from homeassistant.components.select import SelectEntity
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


def _build(
    runtime: QoffeeAutomatRuntimeData,
    descriptor: CommandDescriptor,
    control: ControlDescriptor,
) -> QoffeeAutomatSelect | None:
    variable = find_variable(descriptor, control, frozenset({"enum"}))
    if variable is None or not variable.options:
        return None
    return QoffeeAutomatSelect(runtime, descriptor, control, variable)


async def async_setup_entry(
    hass: HomeAssistant,
    entry: QoffeeAutomatConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    """Set up the selects of the commands in the manifest."""
    await async_setup_command_platform(entry, async_add_entities, "select", _build)


class QoffeeAutomatSelect(QoffeeAutomatCommandEntity, SelectEntity):
    """Selecting an option runs the command with that option."""

    def __init__(
        self,
        runtime: QoffeeAutomatRuntimeData,
        descriptor: CommandDescriptor,
        control: ControlDescriptor,
        variable: VariableDescriptor,
    ) -> None:
        """Create the select that sets ``variable``; its options are the choices."""
        super().__init__(runtime, descriptor, control)
        self._variable = variable.name
        self._attr_options = list(variable.options or ())
        self._attr_assumed_state = control.state_metric is None

    def _apply_value(self, value: MetricValue) -> None:
        # A value that is not one of the options (a newer agent) shows as unknown.
        if isinstance(value, str) and value in self._attr_options:
            self._attr_current_option = value
        else:
            self._attr_current_option = None

    async def async_select_option(self, option: str) -> None:
        """Run the command with the chosen option."""
        await self._async_run_and_show(self._variable, option)
