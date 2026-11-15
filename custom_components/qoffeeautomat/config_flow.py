"""Config flow for QoffeeAutomat (stub, implemented in milestone M5)."""

from typing import Any

from homeassistant.config_entries import ConfigFlow, ConfigFlowResult

from .const import DOMAIN


class QoffeeAutomatConfigFlow(ConfigFlow, domain=DOMAIN):
    """Handle the config flow for QoffeeAutomat."""

    VERSION = 1

    async def async_step_user(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        """Handle a flow started by the user (not implemented yet)."""
        return self.async_abort(reason="not_implemented")
