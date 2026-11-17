"""Binary sensor platform: metric binary sensors and connectivity (PLAN.md S06, S10)."""

import logging

from homeassistant.components.binary_sensor import BinarySensorEntity
from homeassistant.components.binary_sensor.const import BinarySensorDeviceClass
from homeassistant.const import EntityCategory, Platform
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback

from .entity import QoffeeAutomatEntity, QoffeeAutomatMetricEntity, parse_known
from .manifest_manager import ManifestDiff
from .protocol import MetricDescriptor, MetricValue
from .runtime import QoffeeAutomatConfigEntry, QoffeeAutomatRuntimeData

_LOGGER = logging.getLogger(__name__)

PARALLEL_UPDATES = 0


def convert_state(metric_id: str, value: MetricValue) -> bool | None:
    """Convert a value of the wire to the state; ``None`` means unknown."""
    if isinstance(value, bool):
        return value
    if isinstance(value, int | float) and value in (0, 1):
        return bool(value)
    if value is not None:
        _LOGGER.debug("Value %r of metric %s is not a boolean", value, metric_id)
    return None


async def async_setup_entry(
    hass: HomeAssistant,
    entry: QoffeeAutomatConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    """Set up the connectivity sensor and the binary sensors of the manifest."""
    runtime = entry.runtime_data
    async_add_entities([QoffeeAutomatConnectivitySensor(runtime)])
    entities: dict[str, QoffeeAutomatBinarySensor] = {}

    async def apply(diff: ManifestDiff) -> None:
        for removed in diff.removed_metrics:
            if removed.kind != "binary_sensor":
                continue
            if entity := entities.pop(removed.id, None):
                await entity.async_discard()
        created: list[QoffeeAutomatBinarySensor] = []
        for added in diff.added_metrics:
            if added.kind != "binary_sensor" or added.id in entities:
                continue
            entities[added.id] = sensor = QoffeeAutomatBinarySensor(runtime, added)
            created.append(sensor)
        if created:
            async_add_entities(created)

    entry.async_on_unload(await runtime.manifest_manager.async_add_listener(apply))


class QoffeeAutomatBinarySensor(QoffeeAutomatMetricEntity, BinarySensorEntity):
    """A binary sensor fed by one metric."""

    def __init__(
        self, runtime: QoffeeAutomatRuntimeData, descriptor: MetricDescriptor
    ) -> None:
        """Create the binary sensor from the metric descriptor."""
        super().__init__(runtime, descriptor, Platform.BINARY_SENSOR)
        self._attr_device_class = parse_known(
            BinarySensorDeviceClass,
            descriptor.device_class,
            "device class",
            descriptor.id,
        )

    def _apply_value(self, value: MetricValue) -> None:
        self._attr_is_on = convert_state(self.descriptor.id, value)


class QoffeeAutomatConnectivitySensor(QoffeeAutomatEntity, BinarySensorEntity):
    """On while the agent is connected; stays available while the PC is offline."""

    _always_available = True
    _attr_device_class = BinarySensorDeviceClass.CONNECTIVITY
    _attr_entity_category = EntityCategory.DIAGNOSTIC

    def __init__(self, runtime: QoffeeAutomatRuntimeData) -> None:
        """Create the sensor."""
        super().__init__(runtime, "_connectivity")

    @property
    def is_on(self) -> bool:
        """Return whether the agent is connected."""
        return self._runtime.connection.online
