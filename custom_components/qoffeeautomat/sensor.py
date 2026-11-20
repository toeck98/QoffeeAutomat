"""Sensor platform: metric sensors and the power state (PLAN.md S06, S10)."""

import logging
from dataclasses import dataclass
from datetime import UTC, datetime
from enum import StrEnum

from homeassistant.components.sensor import (
    SensorDeviceClass,
    SensorEntity,
    SensorStateClass,
)
from homeassistant.components.sensor.const import DEVICE_CLASS_UNITS
from homeassistant.const import EntityCategory, Platform
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback
from homeassistant.helpers.typing import StateType
from homeassistant.util import dt as dt_util

from .entity import (
    QoffeeAutomatEntity,
    QoffeeAutomatMetricEntity,
    async_setup_agents,
    parse_known,
)
from .manifest_manager import ManifestDiff
from .protocol import MetricDescriptor, MetricValue
from .runtime import (
    POWER_STATE_OPTIONS,
    OnUnload,
    QoffeeAutomatConfigEntry,
    QoffeeAutomatRuntimeData,
)

_LOGGER = logging.getLogger(__name__)

PARALLEL_UPDATES = 0

# Home Assistant refuses states longer than this.
MAX_STATE_LENGTH = 255


@dataclass(frozen=True, slots=True)
class SensorSpec:
    """The Home Assistant properties of a sensor, validated against what HA knows."""

    value_type: str
    device_class: SensorDeviceClass | None = None
    unit: str | None = None
    state_class: SensorStateClass | None = None
    options: tuple[str, ...] | None = None
    precision: int | None = None


def _allowed_units(device_class: SensorDeviceClass) -> set[str] | None:
    """Return the units HA accepts for a device class (``None``: no restriction)."""
    allowed = DEVICE_CLASS_UNITS.get(device_class)
    if not allowed:
        return None
    units: set[str] = set()
    for item in allowed:
        if isinstance(item, type) and issubclass(item, StrEnum):
            units.update(member.value for member in item)
        elif isinstance(item, str):
            units.add(item)
    return units


def build_spec(descriptor: MetricDescriptor) -> SensorSpec:
    """Validate the sensor properties of a descriptor.

    A device class, unit or state class that this Home Assistant does not know (the
    agent may be newer) is dropped with a debug log instead of failing the entity.
    Timestamp metrics become timestamp sensors and enum metrics enum sensors.
    """
    metric_id = descriptor.id
    value_type = descriptor.value_type
    if value_type == "timestamp":
        return SensorSpec(value_type, device_class=SensorDeviceClass.TIMESTAMP)
    if value_type == "enum":
        if descriptor.options:
            return SensorSpec(
                value_type,
                device_class=SensorDeviceClass.ENUM,
                options=descriptor.options,
            )
        _LOGGER.debug("Enum metric %s has no options; showing it as text", metric_id)
        return SensorSpec("string")

    device_class = parse_known(
        SensorDeviceClass, descriptor.device_class, "device class", metric_id
    )
    if device_class in (SensorDeviceClass.ENUM, SensorDeviceClass.TIMESTAMP):
        _LOGGER.debug(
            "Ignoring device class %s of metric %s: it does not match value type %s",
            device_class,
            metric_id,
            value_type,
        )
        device_class = None
    if value_type != "number":
        # Units, state classes and precision only make sense for numbers.
        return SensorSpec(value_type, device_class=None)

    unit = descriptor.unit
    if unit is not None and device_class is not None:
        allowed = _allowed_units(device_class)
        if allowed is not None and unit not in allowed:
            _LOGGER.debug(
                "Ignoring unit %r of metric %s: not valid for device class %s",
                unit,
                metric_id,
                device_class,
            )
            unit = None
    return SensorSpec(
        value_type,
        device_class=device_class,
        unit=unit,
        state_class=parse_known(
            SensorStateClass, descriptor.state_class, "state class", metric_id
        ),
        precision=descriptor.precision,
    )


def convert_value(
    spec: SensorSpec, metric_id: str, value: MetricValue
) -> StateType | datetime:
    """Convert a value of the wire to the native value of the sensor.

    Returns ``None`` (state unknown) for a value that does not fit the sensor.
    """
    if value is None:
        return None
    result: StateType | datetime = None
    match spec.value_type:
        case "timestamp":
            parsed = dt_util.parse_datetime(value) if isinstance(value, str) else None
            if parsed is not None:
                result = parsed if parsed.tzinfo else parsed.replace(tzinfo=UTC)
        case "enum":
            if isinstance(value, str) and spec.options and value in spec.options:
                result = value
        case "number":
            if isinstance(value, int | float) and not isinstance(value, bool):
                result = value
        case _:
            text = str(value).lower() if isinstance(value, bool) else str(value)
            result = text[:MAX_STATE_LENGTH]
    if result is None:
        _LOGGER.debug(
            "Value %r of metric %s does not fit a %s sensor",
            value,
            metric_id,
            spec.value_type,
        )
    return result


async def async_setup_entry(
    hass: HomeAssistant,
    entry: QoffeeAutomatConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    """Set up the power state sensor and the sensors of the manifest."""

    async def setup_agent(
        runtime: QoffeeAutomatRuntimeData, on_unload: OnUnload
    ) -> None:
        async_add_entities([QoffeeAutomatPowerStateSensor(runtime)])
        entities: dict[str, QoffeeAutomatSensor] = {}

        async def apply(diff: ManifestDiff) -> None:
            for removed in diff.removed_metrics:
                if removed.kind != "sensor":
                    continue
                if entity := entities.pop(removed.id, None):
                    await entity.async_discard()
            created: list[QoffeeAutomatSensor] = []
            for added in diff.added_metrics:
                if added.kind != "sensor" or added.id in entities:
                    continue
                entities[added.id] = sensor = QoffeeAutomatSensor(runtime, added)
                created.append(sensor)
            if created:
                async_add_entities(created)

        on_unload(await runtime.manifest_manager.async_add_listener(apply))

    await async_setup_agents(entry, setup_agent)


class QoffeeAutomatSensor(QoffeeAutomatMetricEntity, SensorEntity):
    """A sensor fed by one metric."""

    def __init__(
        self, runtime: QoffeeAutomatRuntimeData, descriptor: MetricDescriptor
    ) -> None:
        """Create the sensor from the metric descriptor."""
        super().__init__(runtime, descriptor, Platform.SENSOR)
        self._spec = build_spec(descriptor)
        self._attr_device_class = self._spec.device_class
        self._attr_native_unit_of_measurement = self._spec.unit
        self._attr_state_class = self._spec.state_class
        self._attr_suggested_display_precision = self._spec.precision
        if self._spec.options is not None:
            self._attr_options = list(self._spec.options)

    def _apply_value(self, value: MetricValue) -> None:
        self._attr_native_value = convert_value(self._spec, self.descriptor.id, value)


class QoffeeAutomatPowerStateSensor(QoffeeAutomatEntity, SensorEntity):
    """The power state of the PC: running or the reason of the last ``bye``."""

    _always_available = True
    _attr_translation_key = "power_state"
    _attr_device_class = SensorDeviceClass.ENUM
    _attr_options = POWER_STATE_OPTIONS
    _attr_entity_category = EntityCategory.DIAGNOSTIC

    def __init__(self, runtime: QoffeeAutomatRuntimeData) -> None:
        """Create the sensor."""
        super().__init__(runtime, "_power_state")

    @property
    def native_value(self) -> str:
        """Return the power state."""
        return self._runtime.connection.power_state.value
