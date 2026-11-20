"""Runtime state of a config entry: connection state, subscription, runtime data.

``ConnectionState`` turns the events of the QoffeeLink client into what entities need
(availability, power state, the ``booted`` signal). ``SubscriptionManager`` keeps the
agent's metric subscription equal to the metric entities that are enabled and routes
``state`` frames to exactly the entities that they concern.
"""

import asyncio
import logging
from collections.abc import Awaitable, Callable, Coroutine
from dataclasses import dataclass, field
from enum import StrEnum
from typing import TYPE_CHECKING, Any, Protocol

from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant, callback
from homeassistant.helpers.debounce import Debouncer

from .client import LinkClient, QoffeeLinkError
from .const import (
    CONF_BROADCAST,
    CONF_MACS,
    DEFAULT_BROADCAST,
    FIRST_CONNECT_TIMEOUT_SECONDS,
    SUBSCRIBE_DEBOUNCE_SECONDS,
)
from .manifest_manager import ManifestManager
from .protocol import Bye, Hello, MetricValue, State

if TYPE_CHECKING:
    from .fleet import FleetManager

_LOGGER = logging.getLogger(__name__)


class PowerState(StrEnum):
    """Values of the power state sensor (PLAN.md S10)."""

    RUNNING = "running"
    SLEEP = "sleep"
    HIBERNATE = "hibernate"
    SHUTDOWN = "shutdown"
    RESTART = "restart"
    UNKNOWN = "unknown"


POWER_STATE_OPTIONS = [state.value for state in PowerState]

# ``bye`` reasons without an entry (``service_stop``, ``update``) leave the power
# state ``unknown``: the PC itself is still on.
_BYE_POWER_STATE = {
    "sleep": PowerState.SLEEP,
    "hibernate": PowerState.HIBERNATE,
    "shutdown": PowerState.SHUTDOWN,
    "restart": PowerState.RESTART,
}

type Notify = Callable[[], None]


def _call_all(listeners: list[Notify], what: str) -> None:
    for listener in tuple(listeners):
        try:
            listener()
        except Exception:
            _LOGGER.exception("Error in a %s listener", what)


class ConnectionState:
    """Availability, power state and boot detection of one agent."""

    def __init__(
        self,
        client: LinkClient,
        name: str,
        *,
        last_boot_id: str | None,
        store_boot_id: Callable[[str], None],
        on_manifest_rev: Callable[[str], None],
    ) -> None:
        """Follow ``client``; ``store_boot_id`` persists a new boot id."""
        self._client = client
        self._name = name
        self._last_boot_id = last_boot_id
        self._store_boot_id = store_boot_id
        self._on_manifest_rev = on_manifest_rev
        self._online = False
        self._power_state = PowerState.UNKNOWN
        self._ever_available = False
        self._logged_unavailable = False
        self._settled = asyncio.Event()
        self._listeners: list[Notify] = []
        self._booted_listeners: list[Notify] = []
        self._pending_booted = False
        self._removers: tuple[Callable[[], None], ...] = (
            client.add_connection_listener(self._on_connection),
            client.add_bye_listener(self._on_bye),
            client.add_auth_failed_listener(self._on_auth_failed),
        )

    # -- state ------------------------------------------------------------------

    @property
    def online(self) -> bool:
        """True while connected and the agent did not announce that it goes away."""
        return self._online

    @property
    def power_state(self) -> PowerState:
        """What is known about the power state of the PC."""
        return self._power_state

    @property
    def last_boot_id(self) -> str | None:
        """The last boot id seen on a connection."""
        return self._last_boot_id

    def close(self) -> None:
        """Stop following the client."""
        for remove in self._removers:
            remove()
        self._removers = ()

    async def async_wait_first_connection(self) -> None:
        """Wait until the client connected or the token was rejected.

        Raises :class:`TimeoutError` if neither happens in time.
        """
        async with asyncio.timeout(FIRST_CONNECT_TIMEOUT_SECONDS):
            await self._settled.wait()

    # -- listeners --------------------------------------------------------------

    def add_listener(self, listener: Notify) -> Callable[[], None]:
        """Call ``listener`` when availability or power state changed."""
        self._listeners.append(listener)

        def remove() -> None:
            if listener in self._listeners:
                self._listeners.remove(listener)

        return remove

    def add_booted_listener(self, listener: Notify) -> Callable[[], None]:
        """Call ``listener`` when the PC was started since the last connection.

        A boot that was detected before any listener existed (the first connection
        happens while the platforms are still being set up) is delivered to every
        listener that registers until the next ``hello`` arrives; each listener gets it
        once.
        """
        self._booted_listeners.append(listener)

        def remove() -> None:
            if listener in self._booted_listeners:
                self._booted_listeners.remove(listener)

        if self._pending_booted:
            _call_all([listener], "booted")
        return remove

    # -- client events ----------------------------------------------------------

    @callback
    def _on_connection(self, connected: bool) -> None:
        if connected:
            self._online = True
            self._power_state = PowerState.RUNNING
            hello = self._client.hello
            if hello is not None:
                self._handle_hello(hello)
            self._settled.set()
        else:
            self._online = False
            if self._power_state is PowerState.RUNNING:
                self._power_state = PowerState.UNKNOWN
        self._changed()

    @callback
    def _on_bye(self, frame: Bye) -> None:
        self._online = False
        self._power_state = _BYE_POWER_STATE.get(frame.reason, PowerState.UNKNOWN)
        self._changed()

    @callback
    def _on_auth_failed(self, _: None) -> None:
        self._settled.set()

    def _handle_hello(self, hello: Hello) -> None:
        self._on_manifest_rev(hello.manifest_rev)
        self._pending_booted = False
        previous, self._last_boot_id = self._last_boot_id, hello.boot_id
        if hello.boot_id == previous:
            return
        self._store_boot_id(hello.boot_id)
        if previous is None:
            return  # the first connection ever has nothing to compare with
        if self._booted_listeners:
            _call_all(self._booted_listeners, "booted")
        else:
            self._pending_booted = True

    def _changed(self) -> None:
        """Log availability transitions once and tell the entities."""
        if self._online:
            self._ever_available = True
            if self._logged_unavailable:
                self._logged_unavailable = False
                _LOGGER.info("QoffeeAutomat agent %s is available again", self._name)
        elif self._ever_available and not self._logged_unavailable:
            self._logged_unavailable = True
            _LOGGER.info("QoffeeAutomat agent %s is unavailable", self._name)
        _call_all(self._listeners, "connection")


class MetricConsumer(Protocol):
    """An entity that shows the value of one metric."""

    def handle_value(self, value: MetricValue) -> None:
        """Take a new value (``None`` means the metric is unavailable)."""


class SubscriptionManager:
    """Keep the subscription equal to the enabled metric entities."""

    def __init__(self, hass: HomeAssistant, client: LinkClient, name: str) -> None:
        """Follow the ``state`` frames of ``client``."""
        self._client = client
        self._name = name
        self._consumers: dict[str, list[MetricConsumer]] = {}
        # Last value of every metric seen on the current connection, so that a caller
        # that subscribes later (the wait of ``run_command``) learns the current value
        # without a new snapshot.
        self._values: dict[str, MetricValue] = {}
        # ``ts`` of the ``state`` frame that is being delivered (unix milliseconds, the
        # clock of the PC). An entity whose value comes with a time of the PC (the
        # position of the media player) reads it from here to relate that time to this
        # frame instead of to the clock of Home Assistant.
        self._frame_ts: int | None = None
        self._sent: frozenset[str] | None = None
        self._debouncer: Debouncer[Coroutine[Any, Any, None]] = Debouncer(
            hass,
            _LOGGER,
            cooldown=SUBSCRIBE_DEBOUNCE_SECONDS,
            immediate=False,
            function=self._async_send,
            background=True,
        )
        self._remove_listener = client.add_state_listener(self._on_state)
        self._remove_connection_listener = client.add_connection_listener(
            self._on_connection
        )

    def last_value(self, metric_id: str) -> MetricValue:
        """Return the last value of a metric on this connection (``None``: unknown)."""
        return self._values.get(metric_id)

    @property
    def frame_ts(self) -> int | None:
        """Return the ``ts`` (unix ms, clock of the PC) of the last ``state`` frame."""
        return self._frame_ts

    @callback
    def _on_connection(self, connected: bool) -> None:
        if not connected:
            self._values.clear()
            self._frame_ts = None

    @property
    def metric_ids(self) -> frozenset[str]:
        """Metric ids of the entities that are currently added to Home Assistant."""
        return frozenset(self._consumers)

    def register(self, metric_id: str, consumer: MetricConsumer) -> Callable[[], None]:
        """Add an enabled entity; the returned callable removes it again."""
        self._consumers.setdefault(metric_id, []).append(consumer)
        self._debouncer.async_schedule_call()

        def remove() -> None:
            consumers = self._consumers.get(metric_id)
            if consumers is None or consumer not in consumers:
                return
            consumers.remove(consumer)
            if not consumers:
                del self._consumers[metric_id]
            self._debouncer.async_schedule_call()

        return remove

    @callback
    def _on_state(self, frame: State) -> None:
        self._frame_ts = frame.ts
        self._values.update(frame.values)
        # O(1) per metric in the frame: only the entities of these metrics are touched.
        for metric_id, value in frame.values.items():
            consumers = self._consumers.get(metric_id)
            if consumers:
                for consumer in tuple(consumers):
                    consumer.handle_value(value)

    async def _async_send(self) -> None:
        metrics = self.metric_ids
        if metrics == self._sent or (not metrics and self._sent is None):
            return
        try:
            await self._client.subscribe(sorted(metrics))
        except QoffeeLinkError as err:
            # The client keeps the set and sends it again after a reconnect.
            _LOGGER.debug("Could not send the subscription of %s: %s", self._name, err)
            return
        self._sent = metrics

    @callback
    def async_shutdown(self) -> None:
        """Stop following and cancel a pending subscribe."""
        self._remove_listener()
        self._remove_connection_listener()
        self._debouncer.async_shutdown()


class Discardable(Protocol):
    """An entity that can leave Home Assistant and keep its registry entry."""

    async def async_discard(self) -> None:
        """Remove the entity from Home Assistant."""


def _no_macs() -> list[str]:
    return []


def _default_broadcast() -> str:
    return DEFAULT_BROADCAST


@dataclass(slots=True)
class QoffeeAutomatRuntimeData:
    """Everything the entities of one PC need, whichever route reaches the PC.

    A PC with a config entry of its own (direct or reverse link) has one of these as
    ``entry.runtime_data``; on the hub route (S20) the hub entry keeps one per PC in its
    :class:`FleetRuntimeData`. The entity code only reads this object and so cannot tell
    the routes apart: ``client`` is a :class:`QoffeeLinkClient` on one route and an
    :class:`AgentLink` on the other, with the same interface.
    """

    client: LinkClient
    manifest_manager: ManifestManager
    connection: ConnectionState
    subscriptions: SubscriptionManager
    agent_id: str
    # ``(platform, translation key)`` pairs that the integration translates; other
    # keys of the manifest fall back to the manifest name.
    translation_keys: frozenset[tuple[str, str]]
    # The name Home Assistant shows for the PC in messages.
    name: str = ""
    # The MAC addresses to wake the PC with and the broadcast address of the magic
    # packets (T703); read at the moment of use, so a change needs no reload.
    macs: Callable[[], list[str]] = _no_macs
    broadcast: Callable[[], str] = _default_broadcast
    # The entities of the PC that are added to Home Assistant now; a change of the route
    # takes them out again (the registry entries stay).
    entities: set[Discardable] = field(default_factory=set)
    # Calls of ``run_command`` that wake or wait for the PC take turns (T712).
    call_lock: asyncio.Lock = field(default_factory=asyncio.Lock)

    async def async_discard_entities(self) -> None:
        """Take all entities of the PC out of Home Assistant, keeping their entries."""
        for entity in tuple(self.entities):
            await entity.async_discard()


# Registers a function that is called when the PC is not served by the entry any more
# (the entry is unloaded, or on the hub route the PC left or changed its route).
type OnUnload = Callable[[Callable[[], None]], None]
# What a platform does for one PC: add its entities and follow the manifest.
type AgentSetup = Callable[[QoffeeAutomatRuntimeData, OnUnload], Awaitable[None]]


@dataclass(slots=True)
class FleetRuntimeData:
    """What a loaded config entry of a hub shares with its platforms (S20)."""

    fleet: FleetManager


type QoffeeAutomatConfigEntry = ConfigEntry[QoffeeAutomatRuntimeData | FleetRuntimeData]


def entry_macs(entry: ConfigEntry) -> list[str]:
    """Return the MAC addresses that the entry of a directly reached PC keeps."""
    return list(entry.data.get(CONF_MACS, ()))


def entry_broadcast(entry: ConfigEntry) -> str:
    """Return the broadcast address of the Wake-on-LAN option of an entry."""
    return str(entry.options.get(CONF_BROADCAST, DEFAULT_BROADCAST))


def agent_runtime_of(entry: QoffeeAutomatConfigEntry) -> QoffeeAutomatRuntimeData:
    """Return the runtime data of a PC that has an entry of its own.

    The runtime data of a PC on the hub route are found through the hub manager
    (``FleetRuntimeData.fleet.runtime_of``).
    """
    data = entry.runtime_data
    if isinstance(data, FleetRuntimeData):
        msg = "the entry of a hub has no single PC"
        raise TypeError(msg)
    return data
