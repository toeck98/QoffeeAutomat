"""Config flow for QoffeeAutomat (PLAN.md S09, S10).

Steps: discovery (zeroconf) or manual host and port, then the certificate fingerprint
for the user to compare, then the one-time pairing code, then the entry is created with
host, port, link token and pinned fingerprint. Also reauthentication (the token was
revoked), reconfiguration of host and port, and address updates from rediscovery.
"""

import logging
from collections.abc import Awaitable, Mapping
from typing import Any

import probatio as vol
from homeassistant.config_entries import (
    ConfigEntry,
    ConfigEntryState,
    ConfigFlow,
    ConfigFlowResult,
)
from homeassistant.const import CONF_HOST, CONF_PORT
from homeassistant.core import HomeAssistant
from homeassistant.helpers.aiohttp_client import async_get_clientsession
from homeassistant.helpers.service_info.zeroconf import ZeroconfServiceInfo

from .client import (
    AuthenticationError,
    CannotConnectError,
    PairingCodeExpiredError,
    PairingCodeInvalidError,
    QoffeeLinkClient,
    QoffeeLinkError,
    UnsupportedProtocolError,
    async_fetch_fingerprint,
    async_pair,
)
from .const import CONF_CODE, CONF_FINGERPRINT, CONF_TOKEN, DEFAULT_PORT, DOMAIN
from .protocol import PROTOCOL_VERSION, Hello

_LOGGER = logging.getLogger(__name__)

USER_SCHEMA = vol.Schema(
    {
        vol.Required(CONF_HOST): str,
        vol.Required(CONF_PORT, default=DEFAULT_PORT): vol.All(
            vol.Coerce(int), vol.Range(min=1, max=65535)
        ),
    }
)
PAIR_SCHEMA = vol.Schema({vol.Required(CONF_CODE): str})


class _StepError(Exception):
    """A failed call of a flow step; ``key`` is the translation key of the error."""

    def __init__(self, key: str) -> None:
        super().__init__(key)
        self.key = key


def _error_key(error: QoffeeLinkError) -> str:
    """Map a client error to the key under ``config.error`` in strings.json."""
    match error:
        case PairingCodeInvalidError():
            return "invalid_code"
        case PairingCodeExpiredError():
            return "code_expired"
        case UnsupportedProtocolError():
            return "unsupported_protocol"
        case AuthenticationError():
            return "invalid_auth"
        case CannotConnectError():
            return "cannot_connect"
        case _:
            return "unknown"


async def _call[T](call: Awaitable[T]) -> T:
    """Await a client call and turn every failure into a :class:`_StepError`."""
    try:
        return await call
    except QoffeeLinkError as err:
        raise _StepError(_error_key(err)) from err
    except Exception as err:
        _LOGGER.exception("Unexpected error in the QoffeeAutomat config flow")
        raise _StepError("unknown") from err


async def _probe(
    hass: HomeAssistant, host: str, port: int, token: str, fingerprint: str
) -> Hello:
    """Connect once with a token and pinned fingerprint and return the ``hello``."""
    client = QoffeeLinkClient(
        async_get_clientsession(hass),
        host,
        port,
        token,
        fingerprint,
        client_name=hass.config.location_name,
    )
    return await client.probe()


def _supports_protocol(txt_value: str | None) -> bool:
    """Tell whether the TXT record ``pv`` (``min-max``) has a version we speak."""
    if txt_value is None:
        return True
    try:
        low, _high = (int(part) for part in txt_value.split("-", 1))
    except ValueError:
        return True  # an unreadable record is not a reason to refuse the agent
    return low <= PROTOCOL_VERSION


def _entry_client(entry: ConfigEntry) -> QoffeeLinkClient | None:
    """Return the client of a loaded entry (``runtime_data.client``, set up in T503)."""
    client = getattr(getattr(entry, "runtime_data", None), "client", None)
    return client if isinstance(client, QoffeeLinkClient) else None


def _placeholders(name: str, host: str, port: int, fingerprint: str) -> dict[str, str]:
    return {
        "name": name,
        "host": host,
        "port": str(port),
        "fingerprint": fingerprint,
    }


class QoffeeAutomatConfigFlow(ConfigFlow, domain=DOMAIN):
    """Handle the config flow for QoffeeAutomat."""

    VERSION = 1

    def __init__(self) -> None:
        """Initialize the flow."""
        self._host = ""
        self._port = DEFAULT_PORT
        self._name = ""
        self._fingerprint = ""

    # -- manual and discovered setup ------------------------------------------

    async def async_step_user(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        """Ask for host and port of the agent."""
        errors: dict[str, str] = {}
        if user_input is not None:
            host = user_input[CONF_HOST].strip()
            port = user_input[CONF_PORT]
            self._async_abort_entries_match({CONF_HOST: host, CONF_PORT: port})
            try:
                fingerprint = await _call(async_fetch_fingerprint(host, port))
            except _StepError as err:
                errors["base"] = err.key
            else:
                self._host, self._port, self._name = host, port, host
                self._fingerprint = fingerprint
                return await self.async_step_confirm()
        return self.async_show_form(
            step_id="user",
            data_schema=self.add_suggested_values_to_schema(USER_SCHEMA, user_input),
            errors=errors,
        )

    async def async_step_zeroconf(
        self, discovery_info: ZeroconfServiceInfo
    ) -> ConfigFlowResult:
        """Handle an agent that announced itself with ``_qoffeeautomat._tcp``."""
        properties = discovery_info.properties
        agent_id = properties.get("id")
        if not agent_id:
            return self.async_abort(reason="invalid_discovery")
        host = discovery_info.host
        port = discovery_info.port or DEFAULT_PORT

        await self.async_set_unique_id(agent_id)
        entry = self.hass.config_entries.async_entry_for_domain_unique_id(
            DOMAIN, agent_id
        )
        if entry is not None:
            self._discovered_again(entry, host, port)
            return self.async_abort(reason="already_configured")

        if not _supports_protocol(properties.get("pv")):
            return self.async_abort(reason="unsupported_protocol")
        try:
            fingerprint = await _call(async_fetch_fingerprint(host, port))
        except _StepError as err:
            return self.async_abort(reason=err.key)
        self._host, self._port = host, port
        self._name = properties.get("name") or host
        self._fingerprint = fingerprint
        return await self.async_step_confirm()

    def _discovered_again(self, entry: ConfigEntry, host: str, port: int) -> None:
        """Handle an announcement of an agent that already has an entry.

        Store a new address and make the entry's client connect right now instead
        of waiting for its backoff (PLAN.md S10).
        """
        changed = (entry.data[CONF_HOST], entry.data[CONF_PORT]) != (host, port)
        if changed:
            self.hass.config_entries.async_update_entry(
                entry, data={**entry.data, CONF_HOST: host, CONF_PORT: port}
            )
        client = _entry_client(entry)
        if client is not None:
            if changed:
                client.update_address(host, port)
            client.trigger_connect()
        elif changed and entry.state is ConfigEntryState.SETUP_RETRY:
            self.hass.config_entries.async_schedule_reload(entry.entry_id)

    async def async_step_confirm(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        """Show the agent name and certificate fingerprint for the user to compare."""
        if user_input is not None:
            return await self.async_step_pair()
        self.context["title_placeholders"] = {"name": self._name}
        return self.async_show_form(
            step_id="confirm",
            description_placeholders=_placeholders(
                self._name, self._host, self._port, self._fingerprint
            ),
        )

    async def async_step_pair(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        """Ask for the pairing code, pair, and create the entry."""
        errors: dict[str, str] = {}
        if user_input is not None:
            try:
                paired = await _call(
                    async_pair(
                        async_get_clientsession(self.hass),
                        self._host,
                        self._port,
                        self._fingerprint,
                        user_input[CONF_CODE].strip(),
                        client_name=self.hass.config.location_name,
                    )
                )
                await _call(
                    _probe(
                        self.hass,
                        self._host,
                        self._port,
                        paired.token,
                        self._fingerprint,
                    )
                )
            except _StepError as err:
                errors["base"] = err.key
            else:
                await self.async_set_unique_id(paired.agent_id)
                self._abort_if_unique_id_configured(
                    updates={CONF_HOST: self._host, CONF_PORT: self._port}
                )
                return self.async_create_entry(
                    title=paired.agent_name,
                    data={
                        CONF_HOST: self._host,
                        CONF_PORT: self._port,
                        CONF_TOKEN: paired.token,
                        CONF_FINGERPRINT: self._fingerprint,
                    },
                )
        return self.async_show_form(
            step_id="pair",
            data_schema=PAIR_SCHEMA,
            errors=errors,
            description_placeholders=_placeholders(
                self._name, self._host, self._port, self._fingerprint
            ),
        )

    # -- reauthentication -------------------------------------------------------

    async def async_step_reauth(
        self, entry_data: Mapping[str, Any]
    ) -> ConfigFlowResult:
        """Start reauthentication after the agent rejected the link token."""
        return await self.async_step_reauth_confirm()

    async def async_step_reauth_confirm(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        """Pair again with a new code; the pinned fingerprint stays as it is."""
        entry = self._get_reauth_entry()
        errors: dict[str, str] = {}
        if user_input is not None:
            try:
                paired = await _call(
                    async_pair(
                        async_get_clientsession(self.hass),
                        entry.data[CONF_HOST],
                        entry.data[CONF_PORT],
                        entry.data[CONF_FINGERPRINT],
                        user_input[CONF_CODE].strip(),
                        client_name=self.hass.config.location_name,
                    )
                )
            except _StepError as err:
                errors["base"] = err.key
            else:
                await self.async_set_unique_id(paired.agent_id)
                self._abort_if_unique_id_mismatch(reason="wrong_agent")
                return self.async_update_reload_and_abort(
                    entry, data_updates={CONF_TOKEN: paired.token}
                )
        return self.async_show_form(
            step_id="reauth_confirm",
            data_schema=PAIR_SCHEMA,
            errors=errors,
            description_placeholders=_placeholders(
                entry.title,
                entry.data[CONF_HOST],
                entry.data[CONF_PORT],
                entry.data[CONF_FINGERPRINT],
            ),
        )

    # -- reconfiguration --------------------------------------------------------

    async def async_step_reconfigure(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        """Change host and port; the agent must still be the same one."""
        entry = self._get_reconfigure_entry()
        errors: dict[str, str] = {}
        if user_input is not None:
            host = user_input[CONF_HOST].strip()
            port = user_input[CONF_PORT]
            try:
                hello = await _call(
                    _probe(
                        self.hass,
                        host,
                        port,
                        entry.data[CONF_TOKEN],
                        entry.data[CONF_FINGERPRINT],
                    )
                )
            except _StepError as err:
                errors["base"] = err.key
            else:
                await self.async_set_unique_id(hello.agent.id)
                self._abort_if_unique_id_mismatch(reason="wrong_agent")
                return self.async_update_reload_and_abort(
                    entry, data_updates={CONF_HOST: host, CONF_PORT: port}
                )
        return self.async_show_form(
            step_id="reconfigure",
            data_schema=self.add_suggested_values_to_schema(
                USER_SCHEMA, user_input or entry.data
            ),
            errors=errors,
        )
