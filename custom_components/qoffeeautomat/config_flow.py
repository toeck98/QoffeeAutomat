"""Config flow for QoffeeAutomat (PLAN.md S09, S10).

Steps: discovery (zeroconf) or manual host and port, then the certificate fingerprint
for the user to compare, then the one-time pairing code, then the entry is created with
host, port, link token and pinned fingerprint. Also reauthentication (the token was
revoked), reconfiguration of host and port, and address updates from rediscovery.

A roaming PC that cannot be dialled is added the other way round (D-043, ADR 0003):
Home Assistant creates a reverse token, shows the enrollment string for the agent and
waits for the agent to dial in; the entry then has no host or link token, only the hash
of the reverse token. Reconfiguring such an entry creates a new token.

The third way is a hub (PLAN.md S20, T914): host and port of the hub, the certificate
fingerprint for the user to compare, then the pairing code that an administrator
created in the hub for Home Assistant. The entry (``hub: true``, unique id is the id of
the hub) stores host, port, fleet token and pinned fingerprint like an agent entry and
serves all PCs of the hub. Reauthentication and reconfiguration work for it as well.
Pairing a PC directly that a hub entry serves asks for confirmation first: the PC then
moves to the direct route, and its entities keep their ids (``routes.py``).
"""

import asyncio
import logging
from collections.abc import Awaitable, Mapping
from typing import Any

import probatio as vol
from homeassistant.config_entries import (
    ConfigEntry,
    ConfigEntryState,
    ConfigFlow,
    ConfigFlowResult,
    OptionsFlow,
)
from homeassistant.const import CONF_HOST, CONF_PORT
from homeassistant.core import HomeAssistant, callback
from homeassistant.helpers.aiohttp_client import async_get_clientsession
from homeassistant.helpers.network import NoURLAvailableError, get_url
from homeassistant.helpers.service_info.zeroconf import ZeroconfServiceInfo

from .client import (
    AuthenticationError,
    CannotConnectError,
    DirectLinkDeniedError,
    FleetClient,
    PairingCodeExpiredError,
    PairingCodeInvalidError,
    QoffeeLinkClient,
    QoffeeLinkError,
    UnsupportedProtocolError,
    async_fetch_fingerprint,
    async_hub_pair,
    async_pair,
    normalize_fingerprint,
)
from .const import (
    CONF_BROADCAST,
    CONF_CODE,
    CONF_FINGERPRINT,
    CONF_HUB,
    CONF_REVERSE,
    CONF_REVERSE_TOKEN_HASH,
    CONF_REVERSE_URL,
    CONF_TOKEN,
    DEFAULT_BROADCAST,
    DEFAULT_HUB_PORT,
    DEFAULT_PORT,
    DOMAIN,
    REVERSE_ENROLL_TIMEOUT_SECONDS,
)
from .protocol import PROTOCOL_VERSION, FleetHello, Hello
from .reverse import (
    async_ensure_view,
    async_get_reverse_links,
    build_enrollment,
    create_token,
    hash_token,
    websocket_url,
)
from .routes import hub_entry_knowing
from .wol import validate_broadcast

_LOGGER = logging.getLogger(__name__)

USER_SCHEMA = vol.Schema(
    {
        vol.Required(CONF_HOST): str,
        vol.Required(CONF_PORT, default=DEFAULT_PORT): vol.All(
            vol.Coerce(int), vol.Range(min=1, max=65535)
        ),
    }
)
HUB_SCHEMA = vol.Schema(
    {
        vol.Required(CONF_HOST): str,
        vol.Required(CONF_PORT, default=DEFAULT_HUB_PORT): vol.All(
            vol.Coerce(int), vol.Range(min=1, max=65535)
        ),
    }
)
PAIR_SCHEMA = vol.Schema({vol.Required(CONF_CODE): str})
REVERSE_SCHEMA = vol.Schema(
    {vol.Required(CONF_REVERSE_URL): str, vol.Optional(CONF_FINGERPRINT): str}
)
OPTIONS_SCHEMA = vol.Schema(
    {vol.Required(CONF_BROADCAST, default=DEFAULT_BROADCAST): str}
)


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
        case DirectLinkDeniedError():
            # The hub of the PC forbids direct links (S20): it has to be added as a hub.
            return "direct_link_denied"
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


async def _probe_hub(
    hass: HomeAssistant, host: str, port: int, token: str, fingerprint: str
) -> FleetHello:
    """Open the fleet link once with a token and the pinned fingerprint."""
    client = FleetClient(
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
        # The reverse link (an agent that dials Home Assistant).
        self._reverse_url = ""
        self._reverse_pin: str | None = None
        self._reverse_token = ""
        self._reverse_hash = ""
        self._reverse_task: asyncio.Task[Hello] | None = None
        self._reverse_hello: Hello | None = None
        # A PC that was paired directly while a hub serves it: the entry waits for the
        # confirmation that the PC moves to the direct route.
        self._pending_title = ""
        self._pending_data: dict[str, Any] = {}
        self._hub_title = ""

    @staticmethod
    @callback
    def async_get_options_flow(config_entry: ConfigEntry) -> QoffeeAutomatOptionsFlow:
        """Return the options flow (the Wake-on-LAN broadcast address)."""
        return QoffeeAutomatOptionsFlow()

    @callback
    def async_remove(self) -> None:
        """Stop waiting for an agent when the flow ends."""
        if self._reverse_task is not None and not self._reverse_task.done():
            self._reverse_task.cancel()
        if self._reverse_hash:
            async_get_reverse_links(self.hass).remove_pending(self._reverse_hash)

    # -- manual and discovered setup ------------------------------------------

    async def async_step_user(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        """Ask how the PC is reached: dialled, dialling in, or through a hub."""
        return self.async_show_menu(
            step_id="user", menu_options=["manual", "reverse", "hub"]
        )

    async def async_step_manual(
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
            step_id="manual",
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
        of waiting for its backoff (PLAN.md S10). An entry whose agent dials Home
        Assistant has no address and nothing to connect.
        """
        if entry.data.get(CONF_REVERSE):
            return
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
                existing = self.hass.config_entries.async_entry_for_domain_unique_id(
                    DOMAIN, paired.agent_id
                )
                if existing is not None and existing.data.get(CONF_REVERSE):
                    # The PC is added already, and dials Home Assistant itself.
                    return self.async_abort(reason="already_configured")
                self._abort_if_unique_id_configured(
                    updates={CONF_HOST: self._host, CONF_PORT: self._port}
                )
                data = {
                    CONF_HOST: self._host,
                    CONF_PORT: self._port,
                    CONF_TOKEN: paired.token,
                    CONF_FINGERPRINT: self._fingerprint,
                }
                hub = hub_entry_knowing(self.hass, paired.agent_id)
                if hub is None:
                    return self.async_create_entry(title=paired.agent_name, data=data)
                # A hub serves the PC: one route per PC (D-031), so ask first.
                self._pending_title = paired.agent_name
                self._pending_data = data
                self._hub_title = hub.title
                return await self.async_step_switch_route()
        return self.async_show_form(
            step_id="pair",
            data_schema=PAIR_SCHEMA,
            errors=errors,
            description_placeholders=_placeholders(
                self._name, self._host, self._port, self._fingerprint
            ),
        )

    async def async_step_switch_route(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        """Confirm that a PC that a hub serves moves to the direct link."""
        if user_input is not None:
            return self.async_create_entry(
                title=self._pending_title, data=self._pending_data
            )
        return self.async_show_form(
            step_id="switch_route",
            description_placeholders={
                "name": self._pending_title,
                "hub": self._hub_title,
            },
        )

    # -- a hub (the fleet link) -------------------------------------------------

    async def async_step_hub(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        """Ask for host and port of the hub."""
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
                return await self.async_step_hub_confirm()
        return self.async_show_form(
            step_id="hub",
            data_schema=self.add_suggested_values_to_schema(HUB_SCHEMA, user_input),
            errors=errors,
        )

    async def async_step_hub_confirm(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        """Show the certificate fingerprint of the hub for the user to compare."""
        if user_input is not None:
            return await self.async_step_hub_pair()
        self.context["title_placeholders"] = {"name": self._name}
        return self.async_show_form(
            step_id="hub_confirm",
            description_placeholders=_placeholders(
                self._name, self._host, self._port, self._fingerprint
            ),
        )

    async def async_step_hub_pair(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        """Ask for the pairing code of the hub, pair, and create the entry."""
        errors: dict[str, str] = {}
        if user_input is not None:
            try:
                paired = await _call(
                    async_hub_pair(
                        async_get_clientsession(self.hass),
                        self._host,
                        self._port,
                        self._fingerprint,
                        user_input[CONF_CODE].strip(),
                        client_name=self.hass.config.location_name,
                    )
                )
                # The token has to open the fleet link; the hello names the hub.
                hello = await _call(
                    _probe_hub(
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
                await self.async_set_unique_id(hello.hub.id)
                self._abort_if_unique_id_configured(
                    updates={CONF_HOST: self._host, CONF_PORT: self._port}
                )
                return self.async_create_entry(
                    title=hello.hub.name,
                    data={
                        CONF_HUB: True,
                        CONF_HOST: self._host,
                        CONF_PORT: self._port,
                        CONF_TOKEN: paired.token,
                        CONF_FINGERPRINT: self._fingerprint,
                    },
                )
        return self.async_show_form(
            step_id="hub_pair",
            data_schema=PAIR_SCHEMA,
            errors=errors,
            description_placeholders=_placeholders(
                self._name, self._host, self._port, self._fingerprint
            ),
        )

    # -- a PC that dials Home Assistant (reverse link) --------------------------

    def _reverse_placeholders(self) -> dict[str, str]:
        return {
            "url": self._reverse_url,
            "enrollment": build_enrollment(
                self._reverse_url, self._reverse_token, self._reverse_pin
            ),
        }

    def _read_reverse_form(
        self, user_input: dict[str, Any], errors: dict[str, str]
    ) -> bool:
        """Take the address of Home Assistant and the pin from the form."""
        try:
            self._reverse_url = websocket_url(user_input[CONF_REVERSE_URL])
        except ValueError:
            errors[CONF_REVERSE_URL] = "invalid_reverse_url"
            return False
        self._reverse_pin = None
        if pin := (user_input.get(CONF_FINGERPRINT) or "").strip():
            try:
                self._reverse_pin = normalize_fingerprint(pin)
            except ValueError:
                errors[CONF_FINGERPRINT] = "invalid_fingerprint"
                return False
        return True

    def _suggested_reverse_input(self) -> dict[str, str]:
        """Suggest the external address of Home Assistant (Nabu Casa included)."""
        try:
            return {
                CONF_REVERSE_URL: get_url(self.hass, prefer_external=True),
            }
        except NoURLAvailableError:
            return {}

    async def async_step_reverse(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        """Ask where the PC can reach Home Assistant."""
        errors: dict[str, str] = {}
        if user_input is not None and self._read_reverse_form(user_input, errors):
            return await self.async_step_reverse_wait()
        return self.async_show_form(
            step_id="reverse",
            data_schema=self.add_suggested_values_to_schema(
                REVERSE_SCHEMA, user_input or self._suggested_reverse_input()
            ),
            errors=errors,
        )

    async def _async_wait_for_agent(self, hello: asyncio.Future[Hello]) -> Hello:
        async with asyncio.timeout(REVERSE_ENROLL_TIMEOUT_SECONDS):
            return await hello

    async def async_step_reverse_wait(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        """Show the enrollment string and wait until the PC uses it."""
        if self._reverse_task is None:
            if not async_ensure_view(self.hass):
                return self.async_abort(reason="reverse_no_http")
            self._reverse_token = create_token()
            self._reverse_hash = hash_token(self._reverse_token)
            future = async_get_reverse_links(self.hass).add_pending(self._reverse_hash)
            self._reverse_task = self.hass.async_create_task(
                self._async_wait_for_agent(future)
            )
        if not self._reverse_task.done():
            return self.async_show_progress(
                step_id="reverse_wait",
                progress_action="waiting_for_agent",
                description_placeholders=self._reverse_placeholders(),
                progress_task=self._reverse_task,
            )
        try:
            self._reverse_hello = self._reverse_task.result()
        except TimeoutError, asyncio.CancelledError:
            return self.async_show_progress_done(next_step_id="reverse_timeout")
        return self.async_show_progress_done(next_step_id="reverse_finish")

    async def async_step_reverse_timeout(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        """Give up: no PC used the enrollment string in time."""
        return self.async_abort(reason="reverse_timeout")

    async def async_step_reverse_finish(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        """Create the entry of the PC that dialled in."""
        hello = self._reverse_hello
        if hello is None:
            return self.async_abort(reason="unknown")
        await self.async_set_unique_id(hello.agent.id)
        self._abort_if_unique_id_configured()
        return self.async_create_entry(
            title=hello.agent.name,
            data={
                CONF_REVERSE: True,
                CONF_REVERSE_TOKEN_HASH: self._reverse_hash,
            },
        )

    async def async_step_reverse_rotate(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        """Create a new token for an entry of a PC that dials Home Assistant."""
        errors: dict[str, str] = {}
        if user_input is not None and self._read_reverse_form(user_input, errors):
            self._reverse_token = create_token()
            self._reverse_hash = hash_token(self._reverse_token)
            return await self.async_step_reverse_show()
        return self.async_show_form(
            step_id="reverse_rotate",
            data_schema=self.add_suggested_values_to_schema(
                REVERSE_SCHEMA, user_input or self._suggested_reverse_input()
            ),
            errors=errors,
        )

    async def async_step_reverse_show(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        """Show the new enrollment string; the old token stops working on submit."""
        if user_input is not None:
            return self.async_update_reload_and_abort(
                self._get_reconfigure_entry(),
                data_updates={CONF_REVERSE_TOKEN_HASH: self._reverse_hash},
            )
        return self.async_show_form(
            step_id="reverse_show",
            description_placeholders=self._reverse_placeholders(),
        )

    # -- reauthentication -------------------------------------------------------

    async def async_step_reauth(
        self, entry_data: Mapping[str, Any]
    ) -> ConfigFlowResult:
        """Start reauthentication after the agent or the hub rejected the token."""
        if entry_data.get(CONF_HUB):
            return await self.async_step_reauth_hub()
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

    async def async_step_reauth_hub(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        """Pair with the hub again with a new code; the fingerprint stays."""
        entry = self._get_reauth_entry()
        errors: dict[str, str] = {}
        if user_input is not None:
            try:
                paired = await _call(
                    async_hub_pair(
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
                await self.async_set_unique_id(paired.hub_id)
                self._abort_if_unique_id_mismatch(reason="wrong_hub")
                return self.async_update_reload_and_abort(
                    entry, data_updates={CONF_TOKEN: paired.token}
                )
        return self.async_show_form(
            step_id="reauth_hub",
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
        if entry.data.get(CONF_REVERSE):
            # There is no address to change; the way to a new start is a new token.
            return await self.async_step_reverse_rotate()
        if entry.data.get(CONF_HUB):
            return await self.async_step_reconfigure_hub()
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

    async def async_step_reconfigure_hub(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        """Change host and port of a hub; it must still be the same hub."""
        entry = self._get_reconfigure_entry()
        errors: dict[str, str] = {}
        if user_input is not None:
            host = user_input[CONF_HOST].strip()
            port = user_input[CONF_PORT]
            try:
                hello = await _call(
                    _probe_hub(
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
                await self.async_set_unique_id(hello.hub.id)
                self._abort_if_unique_id_mismatch(reason="wrong_hub")
                return self.async_update_reload_and_abort(
                    entry, data_updates={CONF_HOST: host, CONF_PORT: port}
                )
        return self.async_show_form(
            step_id="reconfigure_hub",
            data_schema=self.add_suggested_values_to_schema(
                HUB_SCHEMA, user_input or entry.data
            ),
            errors=errors,
        )


class QoffeeAutomatOptionsFlow(OptionsFlow):
    """The options of an entry: the broadcast address of Wake-on-LAN (T703).

    The button reads the option when it is pressed, so a change needs no reload.
    """

    async def async_step_init(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        """Ask for the broadcast address the magic packets are sent to."""
        errors: dict[str, str] = {}
        if user_input is not None:
            try:
                broadcast = validate_broadcast(user_input[CONF_BROADCAST])
            except ValueError:
                errors[CONF_BROADCAST] = "invalid_broadcast"
            else:
                return self.async_create_entry(data={CONF_BROADCAST: broadcast})
        return self.async_show_form(
            step_id="init",
            data_schema=self.add_suggested_values_to_schema(
                OPTIONS_SCHEMA, user_input or self.config_entry.options
            ),
            errors=errors,
        )
