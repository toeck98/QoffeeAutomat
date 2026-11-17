"""Base entities of the QoffeeAutomat integration (PLAN.md S10)."""

import logging
from collections.abc import Callable, Mapping
from enum import StrEnum

from homeassistant.const import EntityCategory
from homeassistant.core import HomeAssistant, callback
from homeassistant.helpers.device_registry import DeviceInfo
from homeassistant.helpers.entity import Entity
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback
from homeassistant.helpers.translation import async_get_translations

from .commands import async_run_command
from .const import DOMAIN
from .manifest_manager import ManifestDiff, unique_id
from .protocol import (
    CommandDescriptor,
    ControlDescriptor,
    MetricDescriptor,
    MetricValue,
    Scalar,
    VariableDescriptor,
)
from .runtime import QoffeeAutomatConfigEntry, QoffeeAutomatRuntimeData

_LOGGER = logging.getLogger(__name__)

_ENTITY_PREFIX = f"component.{DOMAIN}.entity."


async def async_load_translation_keys(
    hass: HomeAssistant,
) -> frozenset[tuple[str, str]]:
    """Return ``(platform, key)`` of every entity name the integration translates."""
    translations = await async_get_translations(hass, "en", "entity", [DOMAIN])
    keys: set[tuple[str, str]] = set()
    for path in translations:
        if path.startswith(_ENTITY_PREFIX) and path.endswith(".name"):
            platform, _, key = path[len(_ENTITY_PREFIX) : -len(".name")].partition(".")
            keys.add((platform, key))
    return frozenset(keys)


def parse_known[E: StrEnum](
    enum_type: type[E], value: str | None, what: str, metric_id: str
) -> E | None:
    """Return the enum member of ``value`` or ``None``.

    An unknown value (from a newer agent) is dropped with a debug log instead of
    failing.
    """
    if value is None:
        return None
    try:
        return enum_type(value)
    except ValueError:
        _LOGGER.debug("Ignoring unknown %s %r of metric %s", what, value, metric_id)
        return None


class QoffeeAutomatEntity(Entity):
    """Base of all entities: device, unique id and availability.

    An entity is available while the agent is connected. After the agent said ``bye``
    (or the connection is gone) every entity is unavailable except those with
    ``_always_available`` (connectivity and power state).
    """

    _attr_has_entity_name = True
    _attr_should_poll = False
    _always_available = False

    def __init__(self, runtime: QoffeeAutomatRuntimeData, key: str) -> None:
        """Create the entity of ``key`` (metric id, command id or reserved key)."""
        self._runtime = runtime
        self._added = False
        self._attr_unique_id = unique_id(runtime.agent_id, key)
        self._attr_device_info = DeviceInfo(identifiers={(DOMAIN, runtime.agent_id)})

    @property
    def available(self) -> bool:
        """Return whether the entity can be used right now."""
        return self._always_available or self._runtime.connection.online

    async def async_added_to_hass(self) -> None:
        """Follow the connection state."""
        await super().async_added_to_hass()
        self._added = True
        self.async_on_remove(
            self._runtime.connection.add_listener(self._handle_connection_change)
        )

    async def async_will_remove_from_hass(self) -> None:
        """Remember that the entity is gone."""
        self._added = False
        await super().async_will_remove_from_hass()

    async def async_discard(self) -> None:
        """Remove the entity from Home Assistant but keep its registry entry.

        Entities that are disabled were never added; nothing is to be done for them.
        """
        if self._added:
            await self.async_remove()

    @callback
    def _handle_connection_change(self) -> None:
        self.async_write_ha_state()


class QoffeeAutomatMetricEntity(QoffeeAutomatEntity):
    """An entity that shows one metric of the agent."""

    def __init__(
        self,
        runtime: QoffeeAutomatRuntimeData,
        descriptor: MetricDescriptor,
        platform: str,
    ) -> None:
        """Create the entity from the metric descriptor."""
        super().__init__(runtime, descriptor.id)
        self.descriptor = descriptor
        self._has_value = True
        self._attr_icon = descriptor.icon
        self._attr_entity_registry_enabled_default = descriptor.enabled_default
        category = parse_known(
            EntityCategory, descriptor.entity_category, "entity category", descriptor.id
        )
        if category is EntityCategory.CONFIG:
            _LOGGER.debug(
                "Ignoring entity category config of metric %s: it is read-only",
                descriptor.id,
            )
            category = None
        self._attr_entity_category = category
        if descriptor.translation_key and (
            (platform, descriptor.translation_key) in runtime.translation_keys
        ):
            self._attr_translation_key = descriptor.translation_key
        else:
            self._attr_name = descriptor.name

    @property
    def available(self) -> bool:
        """Return whether the agent is connected and reports a value."""
        return super().available and self._has_value

    async def async_added_to_hass(self) -> None:
        """Start receiving values and include the metric in the subscription."""
        await super().async_added_to_hass()
        self.async_on_remove(
            self._runtime.subscriptions.register(self.descriptor.id, self)
        )

    @callback
    def handle_value(self, value: MetricValue) -> None:
        """Take a value from a ``state`` frame and write exactly one state."""
        self._has_value = value is not None
        self._apply_value(value)
        self.async_write_ha_state()

    def _apply_value(self, value: MetricValue) -> None:
        """Store the value in the attribute of the platform."""
        raise NotImplementedError


class QoffeeAutomatCommandEntity(QoffeeAutomatEntity):
    """An entity that runs one command of the agent (PLAN.md S06, S10).

    With a ``state_metric`` the entity shows the value of that metric and is part of
    the subscription while it is enabled. Without one it is optimistic: after the
    agent accepted the command the entity shows the value that was sent.
    """

    def __init__(
        self,
        runtime: QoffeeAutomatRuntimeData,
        descriptor: CommandDescriptor,
        control: ControlDescriptor,
    ) -> None:
        """Create the entity from the command descriptor."""
        super().__init__(runtime, descriptor.id)
        self.descriptor = descriptor
        self._state_metric = control.state_metric
        self._has_value = True
        self._attr_name = descriptor.name
        self._attr_icon = descriptor.icon

    @property
    def available(self) -> bool:
        """Return whether the agent is connected and the state metric has a value."""
        return super().available and self._has_value

    async def async_added_to_hass(self) -> None:
        """Include the state metric in the subscription."""
        await super().async_added_to_hass()
        if self._state_metric is not None:
            self.async_on_remove(
                self._runtime.subscriptions.register(self._state_metric, self)
            )

    @callback
    def handle_value(self, value: MetricValue) -> None:
        """Take the value of the state metric and write exactly one state."""
        self._has_value = value is not None
        self._apply_value(value)
        self.async_write_ha_state()

    def _apply_value(self, value: MetricValue) -> None:
        """Store a value of the state metric (``None``: unavailable)."""
        raise NotImplementedError

    async def _async_run(self, variables: Mapping[str, Scalar] | None = None) -> None:
        """Start the command; raises a translated error if the agent refuses it.

        The call does not wait for the end of the command: an entity must answer
        quickly, and the state metric reports what the command achieved.
        """
        await async_run_command(
            self._runtime, self.descriptor.id, variables, wait=False
        )

    async def _async_run_and_show(self, variable: str, value: Scalar) -> None:
        """Run the command with one variable; an optimistic entity shows the value."""
        await self._async_run({variable: value})
        if self._state_metric is None:
            self._apply_value(value)
            self.async_write_ha_state()


def find_variable(
    descriptor: CommandDescriptor,
    control: ControlDescriptor,
    types: frozenset[str],
) -> VariableDescriptor | None:
    """Return the variable that the control sets if it exists and has a fitting type.

    A control that does not fit its command (a newer agent, a bad manifest) creates no
    entity instead of one that cannot work.
    """
    for variable in descriptor.vars:
        if variable.name == control.var:
            if variable.type in types:
                return variable
            break
    _LOGGER.debug(
        "Command %s: no %s variable %r for its %s control",
        descriptor.id,
        "/".join(sorted(types)),
        control.var,
        control.type,
    )
    return None


type CommandEntityBuilder = Callable[
    [QoffeeAutomatRuntimeData, CommandDescriptor, ControlDescriptor],
    QoffeeAutomatCommandEntity | None,
]


async def async_setup_command_platform(
    entry: QoffeeAutomatConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
    control_type: str,
    build: CommandEntityBuilder,
) -> None:
    """Keep the entities of one control type equal to the commands of the manifest.

    Commands that appear get entities, commands that vanish lose them, and a command
    whose descriptor changed gets a new entity that reuses the registry entry. No
    reload of the config entry is needed.
    """
    runtime = entry.runtime_data
    entities: dict[str, QoffeeAutomatCommandEntity] = {}

    async def apply(diff: ManifestDiff) -> None:
        for removed in diff.removed_commands:
            if removed.control is None or removed.control.type != control_type:
                continue
            if entity := entities.pop(removed.id, None):
                await entity.async_discard()
        created: list[QoffeeAutomatCommandEntity] = []
        for added in diff.added_commands:
            control = added.control
            if control is None or control.type != control_type or added.id in entities:
                continue
            if (entity := build(runtime, added, control)) is not None:
                entities[added.id] = entity
                created.append(entity)
        if created:
            async_add_entities(created)

    entry.async_on_unload(await runtime.manifest_manager.async_add_listener(apply))
