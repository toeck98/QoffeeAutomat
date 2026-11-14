"""The QoffeeAutomat integration (skeleton, see docs/plan/PLAN.md S10)."""

from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant


async def async_setup_entry(hass: HomeAssistant, entry: ConfigEntry) -> bool:
    """Set up QoffeeAutomat from a config entry (implemented in milestone M5)."""
    return True


async def async_unload_entry(hass: HomeAssistant, entry: ConfigEntry) -> bool:
    """Unload a config entry (implemented in milestone M5)."""
    return True
