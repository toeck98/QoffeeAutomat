"""Manifest cache, device registration and stale entity cleanup (PLAN.md S07, S10).

The manager fetches the manifest of the agent, caches it by ``rev`` and tells the
platforms which metrics and commands were added or removed, so entities appear and
vanish without reloading the config entry. Entities of vanished metrics and commands
are removed from the entity registry; entities that are still in the manifest keep
their registry entry and with it every user customisation (name, area, disabled).
"""

import asyncio
import logging
from collections.abc import Awaitable, Callable
from dataclasses import dataclass

from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant, callback
from homeassistant.helpers import device_registry as dr
from homeassistant.helpers import entity_registry as er

from .client import QoffeeLinkClient, QoffeeLinkError
from .const import DOMAIN, MANUFACTURER, MODEL
from .protocol import (
    AgentInfo,
    CommandDescriptor,
    Manifest,
    ManifestChanged,
    MetricDescriptor,
    ProtocolError,
    parse_manifest,
)

_LOGGER = logging.getLogger(__name__)

# Entity registry domains of metric entities and of command entities. Entities of other
# domains (the event entity, T506) are never touched by the stale cleanup.
METRIC_DOMAINS = frozenset({"sensor", "binary_sensor"})
COMMAND_DOMAINS = frozenset({"button", "switch", "select", "number", "text"})

# Unique ids of the entities that the integration creates itself (connectivity, power
# state) have a key that starts with this character. Metric and command ids start with
# a letter, so they can never collide with them.
RESERVED_KEY_PREFIX = "_"


def unique_id(agent_id: str, key: str) -> str:
    """Return the unique id of the entity of a metric, a command or a reserved key."""
    return f"{agent_id}_{key}"


@dataclass(frozen=True, slots=True)
class ManifestDiff:
    """What changed between two manifests.

    A metric or command whose descriptor changed is reported as removed (old
    descriptor) and added (new descriptor); the registry entry survives, so the entity
    keeps its customisations.
    """

    added_metrics: tuple[MetricDescriptor, ...] = ()
    removed_metrics: tuple[MetricDescriptor, ...] = ()
    added_commands: tuple[CommandDescriptor, ...] = ()
    removed_commands: tuple[CommandDescriptor, ...] = ()
    # True when the list of event types of the agent is different (the event entity
    # then updates its event types).
    events_changed: bool = False

    @property
    def empty(self) -> bool:
        """True when nothing changed."""
        return not (
            self.added_metrics
            or self.removed_metrics
            or self.added_commands
            or self.removed_commands
            or self.events_changed
        )


type DiffListener = Callable[[ManifestDiff], Awaitable[None]]


def _diff[T: (MetricDescriptor, CommandDescriptor)](
    old: tuple[T, ...], new: tuple[T, ...]
) -> tuple[tuple[T, ...], tuple[T, ...]]:
    """Return ``(added, removed)`` between two descriptor tuples."""
    old_by_id = {item.id: item for item in old}
    new_by_id = {item.id: item for item in new}
    added = tuple(item for item in new if old_by_id.get(item.id) != item)
    removed = tuple(item for item in old if new_by_id.get(item.id) != item)
    return added, removed


def compute_diff(old: Manifest | None, new: Manifest) -> ManifestDiff:
    """Compare two manifests (``old`` ``None`` means everything is new)."""
    added_metrics, removed_metrics = _diff(old.metrics if old else (), new.metrics)
    added_commands, removed_commands = _diff(old.commands if old else (), new.commands)
    return ManifestDiff(
        added_metrics,
        removed_metrics,
        added_commands,
        removed_commands,
        events_changed=(old.events if old else ()) != new.events,
    )


def command_domain(command: CommandDescriptor) -> str | None:
    """Return the entity platform of a command (``None``: no Home Assistant entity)."""
    control = command.control
    if control is not None and control.type in COMMAND_DOMAINS:
        return control.type
    return None


def _configuration_url(ui_url: str | None) -> str | None:
    """Return the agent UI address if the device registry accepts it."""
    if ui_url and ui_url.startswith(("http://", "https://")):
        return ui_url
    return None


@callback
def register_device(
    hass: HomeAssistant, config_entry_id: str, agent: AgentInfo
) -> dr.DeviceEntry:
    """Create or update the device of the PC (PLAN.md S10)."""
    return dr.async_get(hass).async_get_or_create(
        config_entry_id=config_entry_id,
        identifiers={(DOMAIN, agent.id)},
        connections={
            (dr.CONNECTION_NETWORK_MAC, dr.format_mac(mac)) for mac in agent.macs
        },
        name=agent.name,
        manufacturer=MANUFACTURER,
        model=MODEL,
        sw_version=agent.version,
        configuration_url=_configuration_url(agent.ui_url),
    )


class ManifestManager:
    """Fetch, cache and diff the manifest of one agent."""

    def __init__(
        self, hass: HomeAssistant, entry: ConfigEntry, client: QoffeeLinkClient
    ) -> None:
        """Create the manager; call :meth:`async_load` once the client is connected."""
        self._hass = hass
        self._entry = entry
        self._client = client
        self._manifest: Manifest | None = None
        self._listeners: list[DiffListener] = []
        self._lock = asyncio.Lock()
        self._remove_client_listener: Callable[[], None] | None = None

    @property
    def manifest(self) -> Manifest:
        """The cached manifest; only valid after :meth:`async_load`."""
        if self._manifest is None:
            msg = "the manifest was not loaded yet"
            raise RuntimeError(msg)
        return self._manifest

    @property
    def rev(self) -> str | None:
        """Revision of the cached manifest."""
        return self._manifest.rev if self._manifest else None

    async def async_load(self) -> Manifest:
        """Fetch the manifest for the first time and start following changes.

        Raises :class:`QoffeeLinkError` or :class:`ProtocolError`.
        """
        await self.async_refresh()
        self._remove_client_listener = self._client.add_manifest_listener(
            self._handle_manifest_changed
        )
        return self.manifest

    def close(self) -> None:
        """Stop following the client."""
        if self._remove_client_listener is not None:
            self._remove_client_listener()
            self._remove_client_listener = None

    async def async_add_listener(self, listener: DiffListener) -> Callable[[], None]:
        """Register a platform; it first receives the current manifest as additions.

        Later calls carry the changes of every manifest update.
        """
        self._listeners.append(listener)

        def remove() -> None:
            if listener in self._listeners:
                self._listeners.remove(listener)

        if self._manifest is not None:
            await listener(compute_diff(None, self._manifest))
        return remove

    async def async_refresh(self, expected_rev: str | None = None) -> bool:
        """Fetch the manifest unless the cached one already has ``expected_rev``.

        Returns True if a different manifest was applied.
        """
        async with self._lock:
            if (
                expected_rev is not None
                and self._manifest is not None
                and self._manifest.rev == expected_rev
            ):
                return False
            fetched = parse_manifest(await self._client.get_manifest())
            previous = self._manifest
            if previous is not None and previous.rev == fetched.rev:
                return False
            self._manifest = fetched
            register_device(self._hass, self._entry.entry_id, fetched.agent)
            if previous is not None:
                await self._async_notify(compute_diff(previous, fetched))
            self.async_remove_stale()
            return True

    async def _async_notify(self, diff: ManifestDiff) -> None:
        if diff.empty:
            return
        for listener in tuple(self._listeners):
            try:
                await listener(diff)
            except Exception:
                _LOGGER.exception("Error in a manifest listener")

    @callback
    def async_remove_stale(self) -> None:
        """Remove registry entries of metrics and commands the agent no longer has."""
        manifest = self.manifest
        prefix = unique_id(manifest.agent.id, "")
        metric_kinds = {metric.id: metric.kind for metric in manifest.metrics}
        # A command whose control type changed leaves an entity of the old platform.
        command_domains = {
            command.id: command_domain(command) for command in manifest.commands
        }
        registry = er.async_get(self._hass)
        for registered in list(
            er.async_entries_for_config_entry(registry, self._entry.entry_id)
        ):
            if not registered.unique_id.startswith(prefix):
                continue
            key = registered.unique_id[len(prefix) :]
            if key.startswith(RESERVED_KEY_PREFIX):
                continue
            if registered.domain in METRIC_DOMAINS:
                # The metric kind is the registry domain ("sensor", "binary_sensor").
                known = metric_kinds.get(key) == registered.domain
            elif registered.domain in COMMAND_DOMAINS:
                known = command_domains.get(key) == registered.domain
            else:
                continue
            if not known:
                _LOGGER.debug("Removing stale entity %s", registered.entity_id)
                registry.async_remove(registered.entity_id)

    # -- following the agent ----------------------------------------------------

    @callback
    def handle_hello_rev(self, rev: str) -> None:
        """Refresh after a (re)connect if the agent announces another revision."""
        if self._manifest is not None and rev != self._manifest.rev:
            self._schedule_refresh(rev)

    @callback
    def _handle_manifest_changed(self, frame: ManifestChanged) -> None:
        if frame.rev != self.rev:
            self._schedule_refresh(frame.rev)

    def _schedule_refresh(self, rev: str) -> None:
        self._entry.async_create_background_task(
            self._hass, self._refresh_quietly(rev), "qoffeeautomat manifest refresh"
        )

    async def _refresh_quietly(self, rev: str) -> None:
        try:
            await self.async_refresh(rev)
        except (QoffeeLinkError, ProtocolError) as err:
            # A lost connection triggers another refresh through ``hello`` on connect.
            _LOGGER.warning("Could not update the manifest (revision %s): %s", rev, err)
