"""The hub route: one config entry per hub, one device per PC (PLAN.md S10, S20, T914).

A config entry with ``hub: true`` holds one :class:`FleetClient` (a single WebSocket to
the hub, ``/hub/v1/fleet``) and a :class:`FleetManager`. The manager follows the list of
PCs of the hub: for every PC that has no config entry of its own it creates the same
runtime data that a direct entry has (:class:`QoffeeAutomatRuntimeData`) around an
:class:`AgentLink`, so the entity platforms cannot tell the route. The device of a PC is
linked to the device of the hub with ``via_device``. PCs appear and disappear while the
entry is loaded (``agent_added``, ``agent_removed``, and the list that is read again
after every reconnect), and the subscription of every PC is kept separately.

A PC that also has an entry of its own is skipped, so no unique id is registered twice;
a repair issue offers to switch it to the hub route (``routes.py``).
"""

import asyncio
import logging
from collections.abc import Callable, Coroutine, Mapping, Sequence
from dataclasses import dataclass, field
from functools import partial
from typing import Any

from homeassistant.const import CONF_HOST, CONF_PORT, Platform
from homeassistant.core import HomeAssistant, callback
from homeassistant.exceptions import (
    ConfigEntryAuthFailed,
    ConfigEntryError,
    ConfigEntryNotReady,
)
from homeassistant.helpers import device_registry as dr
from homeassistant.helpers.aiohttp_client import async_get_clientsession
from homeassistant.loader import async_get_integration

from .client import (
    AgentLink,
    AuthenticationError,
    FleetClient,
    QoffeeLinkError,
    UnsupportedProtocolError,
)
from .const import (
    CONF_AGENT_MACS,
    CONF_FINGERPRINT,
    CONF_TOKEN,
    DOMAIN,
    FIRST_CONNECT_TIMEOUT_SECONDS,
    HUB_IDENTIFIER_PREFIX,
    HUB_MODEL,
    MANUFACTURER,
)
from .entity import async_load_translation_keys
from .manifest_manager import ManifestManager, register_device
from .protocol import (
    AgentAdded,
    AgentInfo,
    AgentRemoved,
    FleetAgent,
    HubInfo,
    ProtocolError,
)
from .repairs import (
    async_create_direct_route_issue,
    async_create_protocol_mismatch_issue,
    async_delete_direct_route_issue,
    async_delete_protocol_mismatch_issue,
)
from .routes import direct_entry_for
from .runtime import (
    AgentSetup,
    ConnectionState,
    FleetRuntimeData,
    QoffeeAutomatConfigEntry,
    QoffeeAutomatRuntimeData,
    SubscriptionManager,
    entry_broadcast,
)
from .wol import normalize_mac

_LOGGER = logging.getLogger(__name__)


def _ignore_boot_id(boot_id: str) -> None:
    """Do not keep boot ids: the hub knows none, the link derives them (S20)."""


class _AgentMacs:
    """The MAC addresses of the PCs of a hub, kept per agent id in the hub entry."""

    def __init__(
        self, hass: HomeAssistant, entry: QoffeeAutomatConfigEntry, agent_id: str
    ) -> None:
        self._hass = hass
        self._entry = entry
        self._agent_id = agent_id

    def get(self) -> list[str]:
        stored: Mapping[str, Sequence[str]] = self._entry.data.get(CONF_AGENT_MACS, {})
        return list(stored.get(self._agent_id, ()))

    def put(self, macs: list[str]) -> None:
        stored = {**self._entry.data.get(CONF_AGENT_MACS, {}), self._agent_id: macs}
        self._hass.config_entries.async_update_entry(
            self._entry, data={**self._entry.data, CONF_AGENT_MACS: stored}
        )


@dataclass(slots=True)
class _ServedAgent:
    """A PC that the hub entry serves now."""

    link: AgentLink
    runtime: QoffeeAutomatRuntimeData
    # What the platforms registered for the PC; undone when the PC goes.
    cleanups: list[Callable[[], None]] = field(default_factory=list)


class FleetManager:
    """The PCs of one hub as seen by Home Assistant."""

    def __init__(
        self,
        hass: HomeAssistant,
        entry: QoffeeAutomatConfigEntry,
        client: FleetClient,
        hub: HubInfo,
        *,
        client_version: str,
        translation_keys: frozenset[tuple[str, str]],
    ) -> None:
        """Follow ``client``; call :meth:`async_start` once it is connected."""
        self._hass = hass
        self._entry = entry
        self._client = client
        self._hub = hub
        self._client_version = client_version
        self._translation_keys = translation_keys
        # Every PC in the scope of the token, served or skipped.
        self._known: dict[str, FleetAgent] = {}
        self._served: dict[str, _ServedAgent] = {}
        self._setups: list[AgentSetup] = []
        # Entries that are being removed and do not count as a route any more.
        self._ignoring: set[str] = set()
        # Changes of the list, the PCs and the platforms take turns.
        self._lock = asyncio.Lock()
        self._settled = asyncio.Event()
        self._started = False
        self._removers: tuple[Callable[[], None], ...] = (
            client.add_connection_listener(self._on_connection),
            client.add_auth_failed_listener(self._on_auth_failed),
        )

    # -- what the platforms and services ask ------------------------------------

    @property
    def client(self) -> FleetClient:
        """The fleet link of the hub."""
        return self._client

    @property
    def hub(self) -> HubInfo:
        """The hub as it introduced itself."""
        return self._hub

    @property
    def hub_identifier(self) -> tuple[str, str]:
        """The identifier of the device of the hub."""
        return (DOMAIN, f"{HUB_IDENTIFIER_PREFIX}{self._hub.id}")

    @property
    def known_agents(self) -> Mapping[str, FleetAgent]:
        """Every PC that the hub lists for the token, served by this entry or not."""
        return self._known

    @property
    def served_agent_ids(self) -> list[str]:
        """The ids of the PCs that this entry serves."""
        return sorted(self._served)

    def knows(self, agent_id: str) -> bool:
        """Return whether the hub lists the PC."""
        return agent_id in self._known

    def runtime_of(self, agent_id: str) -> QoffeeAutomatRuntimeData | None:
        """Return the runtime data of a PC that this entry serves."""
        served = self._served.get(agent_id)
        return served.runtime if served is not None else None

    # -- lifecycle --------------------------------------------------------------

    async def async_wait_connected(self) -> None:
        """Wait until the fleet link is up or the token was rejected.

        Raises :class:`TimeoutError` if neither happens in time.
        """
        async with asyncio.timeout(FIRST_CONNECT_TIMEOUT_SECONDS):
            await self._settled.wait()

    async def async_start(self) -> None:
        """Register the hub device, load the PCs and keep following the hub.

        Raises :class:`QoffeeLinkError` or :class:`ProtocolError` if the list of PCs
        cannot be read.
        """
        self._register_hub_device()
        await self._async_sync()
        self._started = True
        self._removers += (
            self._client.add_agent_added_listener(self._on_agent_added),
            self._client.add_agent_removed_listener(self._on_agent_removed),
        )

    async def async_shutdown(self) -> None:
        """Stop following the hub and let go of all PCs (the registry stays)."""
        for remove in self._removers:
            remove()
        self._removers = ()
        self._started = False
        self._setups.clear()
        for served in tuple(self._served.values()):
            await self._async_teardown(served)
        self._served.clear()

    async def async_follow_agents(self, setup: AgentSetup) -> None:
        """Run ``setup`` for every PC now and for every PC that is served later."""
        async with self._lock:
            self._setups.append(setup)
            for served in tuple(self._served.values()):
                await setup(served.runtime, served.cleanups.append)

    async def async_release_agent(self, agent_id: str) -> None:
        """Stop serving a PC that got an entry of its own (hub to direct link).

        The entities leave Home Assistant, the registry entries stay for the new entry.
        The PC counts as skipped from now on, and the repair issue offers the way back.
        """
        async with self._lock:
            served = self._served.pop(agent_id, None)
            if served is not None:
                await self._async_teardown(served)
            if (agent := self._known.get(agent_id)) is not None:
                self._create_route_issue(agent)

    async def async_adopt_agent(self, agent_id: str) -> None:
        """Serve a PC that lost its entry of its own (direct link to hub)."""
        async with self._lock:
            agent = self._known.get(agent_id)
            if agent is not None and agent_id not in self._served:
                await self._async_update(agent)

    @callback
    def async_schedule_resync(self, ignoring_entry_id: str) -> None:
        """Read the list again after another entry of the integration was removed."""
        self._schedule(self._async_resync(ignoring_entry_id))

    # -- the hub tells ----------------------------------------------------------

    @callback
    def _on_connection(self, connected: bool) -> None:
        if not connected:
            return
        self._settled.set()
        if self._started:
            # A new connection forgot the subscriptions and may have missed changes.
            self._schedule(self._async_sync_quietly())

    @callback
    def _on_auth_failed(self, _: None) -> None:
        self._settled.set()

    @callback
    def _on_agent_added(self, frame: AgentAdded) -> None:
        self._schedule(self._async_handle_added(frame.to_agent()))

    @callback
    def _on_agent_removed(self, frame: AgentRemoved) -> None:
        self._schedule(self._async_handle_removed(frame.agent_id))

    def _schedule(self, coroutine: Coroutine[Any, Any, None]) -> None:
        self._entry.async_create_background_task(
            self._hass, coroutine, "qoffeeautomat hub"
        )

    async def _async_handle_added(self, agent: FleetAgent) -> None:
        async with self._lock:
            self._known[agent.id] = agent
            await self._async_update(agent)

    async def _async_handle_removed(self, agent_id: str) -> None:
        async with self._lock:
            await self._async_remove(agent_id)

    async def _async_resync(self, ignoring_entry_id: str) -> None:
        self._ignoring.add(ignoring_entry_id)
        try:
            await self._async_sync_quietly()
        finally:
            self._ignoring.discard(ignoring_entry_id)

    async def _async_sync_quietly(self) -> None:
        try:
            await self._async_sync()
        except (QoffeeLinkError, ProtocolError) as err:
            # The next connection reads the list again.
            _LOGGER.warning("Could not read the PCs of the hub: %s", err)

    # -- the list of PCs --------------------------------------------------------

    async def _async_sync(self) -> None:
        """Read the list of PCs and make the served PCs equal to it."""
        async with self._lock:
            agents = await self._client.get_agents()
            listed = {agent.id for agent in agents}
            for agent_id in (set(self._known) | set(self._served)) - listed:
                await self._async_remove(agent_id)
            self._known = {agent.id: agent for agent in agents}
            for agent in agents:
                await self._async_update(agent)
            self._remove_stale_devices()

    async def _async_update(self, agent: FleetAgent) -> None:
        """Serve, update or skip one PC of the list."""
        if direct_entry_for(self._hass, agent.id, ignoring=self._ignoring) is not None:
            # One route per PC (D-031): the entry of its own has the unique ids.
            if (served := self._served.pop(agent.id, None)) is not None:
                await self._async_teardown(served)
            self._create_route_issue(agent)
            return
        async_delete_direct_route_issue(self._hass, self._entry.entry_id, agent.id)
        served = self._served.get(agent.id)
        if served is None:
            await self._async_add(agent)
            return
        served.link.apply(agent)
        await served.link.async_resubscribe()

    def _create_route_issue(self, agent: FleetAgent) -> None:
        async_create_direct_route_issue(
            self._hass, self._entry.entry_id, self._entry.title, agent.id, agent.name
        )

    async def _async_add(self, agent: FleetAgent) -> None:
        """Create the runtime data of a PC and let the platforms add its entities."""
        link = AgentLink(
            self._client,
            agent,
            client_name=self._hass.config.location_name,
            client_version=self._client_version,
        )
        macs = _AgentMacs(self._hass, self._entry, agent.id)
        if agent.agent is not None and not macs.get():
            # The hub knows the adapters of a PC that connected to it before; with
            # them the Wake-on-LAN button exists even while the PC is off.
            known = list(dict.fromkeys(normalize_mac(mac) for mac in agent.agent.macs))
            if known:
                macs.put(known)
        manager = ManifestManager(
            self._hass,
            self._entry,
            link,
            macs=macs,
            via_identifier=self.hub_identifier,
        )
        connection = ConnectionState(
            link,
            agent.name,
            last_boot_id=None,
            store_boot_id=_ignore_boot_id,
            on_manifest_rev=manager.handle_hello_rev,
        )
        runtime = QoffeeAutomatRuntimeData(
            client=link,
            manifest_manager=manager,
            connection=connection,
            subscriptions=SubscriptionManager(self._hass, link, agent.name),
            agent_id=agent.id,
            translation_keys=self._translation_keys,
            name=agent.name,
            macs=macs.get,
            broadcast=partial(entry_broadcast, self._entry),
        )
        served = _ServedAgent(link, runtime)
        self._served[agent.id] = served

        self._register_agent_device(agent)
        manager.start_following()
        link.apply(agent)
        try:
            # The hub serves the manifest from its cache, also while the PC is off. A PC
            # that never connected has none yet: its first connection loads it.
            await manager.async_refresh()
        except (QoffeeLinkError, ProtocolError) as err:
            _LOGGER.debug("The hub has no manifest of %s yet: %s", agent.id, err)
        for setup in tuple(self._setups):
            await setup(runtime, served.cleanups.append)

    async def _async_remove(self, agent_id: str) -> None:
        """Forget a PC that left the hub or the scope of the token."""
        self._known.pop(agent_id, None)
        async_delete_direct_route_issue(self._hass, self._entry.entry_id, agent_id)
        if (served := self._served.pop(agent_id, None)) is not None:
            await self._async_teardown(served)
        registry = dr.async_get(self._hass)
        for device in dr.async_entries_for_config_entry(registry, self._entry.entry_id):
            if (DOMAIN, agent_id) in device.identifiers:
                registry.async_remove_device(device.id)

    async def _async_teardown(self, served: _ServedAgent) -> None:
        """Take a PC out of Home Assistant; its registry entries stay."""
        for cleanup in reversed(served.cleanups):
            cleanup()
        served.cleanups.clear()
        await served.runtime.async_discard_entities()
        served.runtime.subscriptions.async_shutdown()
        served.runtime.manifest_manager.close()
        served.runtime.connection.close()
        await served.link.stop()

    # -- devices ----------------------------------------------------------------

    @callback
    def _register_hub_device(self) -> None:
        dr.async_get(self._hass).async_get_or_create(
            config_entry_id=self._entry.entry_id,
            identifiers={self.hub_identifier},
            name=self._hub.name,
            manufacturer=MANUFACTURER,
            model=HUB_MODEL,
            sw_version=self._hub.version,
            entry_type=dr.DeviceEntryType.SERVICE,
        )

    @callback
    def _register_agent_device(self, agent: FleetAgent) -> None:
        """Create the device of a PC at once, before it has a manifest."""
        info = agent.agent or AgentInfo(
            id=agent.id,
            name=agent.name,
            version="",
            os="",
            arch="",
            hostname="",
            macs=(),
        )
        register_device(
            self._hass,
            self._entry.entry_id,
            info,
            via_identifier=self.hub_identifier,
        )

    @callback
    def _remove_stale_devices(self) -> None:
        """Remove the devices of PCs that the hub does not list any more."""
        registry = dr.async_get(self._hass)
        hub_key = self.hub_identifier[1]
        for device in dr.async_entries_for_config_entry(registry, self._entry.entry_id):
            keys = {key for domain, key in device.identifiers if domain == DOMAIN}
            if keys and hub_key not in keys and keys.isdisjoint(self._known):
                registry.async_remove_device(device.id)


async def async_setup_hub_entry(
    hass: HomeAssistant,
    entry: QoffeeAutomatConfigEntry,
    platforms: list[Platform],
) -> bool:
    """Set up a config entry that connects to a hub."""
    integration = await async_get_integration(hass, DOMAIN)
    host: str = entry.data[CONF_HOST]
    port: int = entry.data[CONF_PORT]
    placeholders = {"host": host, "port": str(port)}
    version = str(integration.version or "0")
    client = FleetClient(
        async_get_clientsession(hass),
        host,
        port,
        entry.data[CONF_TOKEN],
        entry.data[CONF_FINGERPRINT],
        client_name=hass.config.location_name,
        client_version=version,
    )

    # Test the first connection before the setup goes on, so a hub that is down leads
    # to "retrying" instead of an entry without devices.
    try:
        hello = await client.probe()
    except AuthenticationError as err:
        raise ConfigEntryAuthFailed(
            translation_domain=DOMAIN, translation_key="hub_invalid_auth"
        ) from err
    except UnsupportedProtocolError as err:
        async_create_protocol_mismatch_issue(
            hass, entry.entry_id, entry.title, err.agent_range
        )
        raise ConfigEntryError(
            translation_domain=DOMAIN, translation_key="hub_unsupported_protocol"
        ) from err
    except QoffeeLinkError as err:
        raise ConfigEntryNotReady(
            translation_domain=DOMAIN,
            translation_key="hub_cannot_connect",
            translation_placeholders=placeholders,
        ) from err
    async_delete_protocol_mismatch_issue(hass, entry.entry_id)
    if entry.unique_id is not None and hello.hub.id != entry.unique_id:
        raise ConfigEntryError(translation_domain=DOMAIN, translation_key="wrong_hub")

    manager = FleetManager(
        hass,
        entry,
        client,
        hello.hub,
        client_version=version,
        translation_keys=await async_load_translation_keys(hass),
    )
    # Callbacks run in reverse order: the manager lets go of the PCs, then the client.
    entry.async_on_unload(client.stop)
    entry.async_on_unload(manager.async_shutdown)

    client.start()
    try:
        await manager.async_wait_connected()
    except TimeoutError as err:
        raise ConfigEntryNotReady(
            translation_domain=DOMAIN,
            translation_key="hub_cannot_connect",
            translation_placeholders=placeholders,
        ) from err
    if client.auth_failed:
        raise ConfigEntryAuthFailed(
            translation_domain=DOMAIN, translation_key="hub_invalid_auth"
        )
    try:
        await manager.async_start()
    except (QoffeeLinkError, ProtocolError) as err:
        raise ConfigEntryNotReady(
            translation_domain=DOMAIN, translation_key="hub_agents_unavailable"
        ) from err

    entry.runtime_data = FleetRuntimeData(fleet=manager)
    await hass.config_entries.async_forward_entry_setups(entry, platforms)

    # From now on a rejected token (revoked in the hub) starts reauthentication.
    entry.async_on_unload(
        client.add_auth_failed_listener(lambda _: entry.async_start_reauth(hass))
    )
    return True
