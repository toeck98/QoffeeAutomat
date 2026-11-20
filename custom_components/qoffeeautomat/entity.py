"""Base entities of the QoffeeAutomat integration (PLAN.md S10)."""

import logging
from collections.abc import Callable, Mapping
from enum import StrEnum

from homeassistant.const import EntityCategory
from homeassistant.core import HomeAssistant, callback
from homeassistant.helpers import entity_registry as er
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
from .runtime import (
    AgentSetup,
    FleetRuntimeData,
    OnUnload,
    QoffeeAutomatConfigEntry,
    QoffeeAutomatRuntimeData,
)

_LOGGER = logging.getLogger(__name__)

_ENTITY_PREFIX = f"component.{DOMAIN}.entity."


async def async_setup_agents(
    entry: QoffeeAutomatConfigEntry, setup: AgentSetup
) -> None:
    """Run ``setup`` for every PC that the config entry serves.

    This is where the route disappears: the entry of a PC that is reached directly
    serves exactly that PC; the entry of a hub serves the PCs of the hub, those that are
    there now and those that appear later (``setup`` runs for each of them, and what it
    registers with ``on_unload`` is undone when the PC leaves or changes its route).
    Either way ``setup`` gets the runtime data of one PC and nothing that depends on the
    route.
    """
    data = entry.runtime_data
    if isinstance(data, FleetRuntimeData):
        await data.fleet.async_follow_agents(setup)
    else:
        await setup(data, entry.async_on_unload)


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
        self._runtime.entities.add(self)
        self.async_on_remove(
            self._runtime.connection.add_listener(self._handle_connection_change)
        )

    async def async_will_remove_from_hass(self) -> None:
        """Remember that the entity is gone."""
        self._added = False
        self._runtime.entities.discard(self)
        await super().async_will_remove_from_hass()

    async def async_discard(self) -> None:
        """Remove the entity from Home Assistant but keep its registry entry.

        Entities that are disabled were never added; nothing is to be done for them.
        """
        if self._added:
            await self.async_remove()

    @callback
    def async_refresh(self) -> None:
        """Write the state again, for example after the manifest changed.

        An entity that is not added to Home Assistant (yet, or any more) has nothing
        to write.
        """
        if self._added:
            self.async_write_ha_state()

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


type FeatureEntityBuilder = Callable[
    [QoffeeAutomatRuntimeData], QoffeeAutomatEntity | None
]


async def async_setup_feature_platform(
    hass: HomeAssistant,
    entry: QoffeeAutomatConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
    *,
    feature: str,
    domain: str,
    key: str,
    build: FeatureEntityBuilder,
) -> None:
    """Keep one entity equal to an optional feature of the agent (T702, T716).

    The entity exists only while the manifest lists ``feature``. When the agent starts
    to announce it, the entity is added; when the agent stops, it is removed together
    with its registry entry (the stale cleanup leaves keys that start with ``_``
    alone). While the feature stays, every change of the manifest makes the entity
    write its state again, because what it supports follows the commands and metrics
    of the manifest. No reload of the config entry is needed.
    """

    async def setup_agent(
        runtime: QoffeeAutomatRuntimeData, on_unload: OnUnload
    ) -> None:
        entity: QoffeeAutomatEntity | None = None

        async def apply(diff: ManifestDiff) -> None:
            nonlocal entity
            manifest = runtime.manifest_manager.manifest
            wanted = feature in manifest.features
            if wanted and entity is None:
                entity = build(runtime)
                if entity is not None:
                    async_add_entities([entity])
            elif not wanted:
                if entity is not None:
                    await entity.async_discard()
                    entity = None
                # Also an entry that an earlier run left behind: without the feature
                # there is no entity, not even an unavailable one.
                registry = er.async_get(hass)
                if entity_id := registry.async_get_entity_id(
                    domain, DOMAIN, unique_id(runtime.agent_id, key)
                ):
                    registry.async_remove(entity_id)
            elif entity is not None:
                entity.async_refresh()

        on_unload(await runtime.manifest_manager.async_add_listener(apply))

    await async_setup_agents(entry, setup_agent)


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

    async def setup_agent(
        runtime: QoffeeAutomatRuntimeData, on_unload: OnUnload
    ) -> None:
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
                if (
                    control is None
                    or control.type != control_type
                    or added.id in entities
                ):
                    continue
                if (entity := build(runtime, added, control)) is not None:
                    entities[added.id] = entity
                    created.append(entity)
            if created:
                async_add_entities(created)

        on_unload(await runtime.manifest_manager.async_add_listener(apply))

    await async_setup_agents(entry, setup_agent)
