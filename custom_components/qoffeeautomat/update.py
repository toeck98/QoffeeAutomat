"""Update platform: the agent on the PC as an update entity (S10, S13, S16, T704).

The entity exists only while the manifest lists the feature ``update``. It is composed
of ordinary metrics and one command, there are no special messages:

* ``agent.version`` is the installed version, ``agent.latest_version`` the newest
  release that the agent found on GitHub, ``agent.release_url`` and
  ``agent.release_notes`` describe that release. The agent asks GitHub once a day (the
  only request that it makes to the internet on its own, S09); with the check switched
  off in its settings ``agent.latest_version`` has no value and the entity state is
  unknown.
* The command ``agent_update`` (the built-in action ``agent.update``) installs the
  release. The agent downloads the installer, compares its SHA-256 checksum with the one
  of the release, verifies the Authenticode signature when the running build is signed,
  tells every client ``bye`` with the reason ``update`` and runs the installer silently.
  An installer that fails a check is refused and the agent answers with the reason,
  which this entity turns into an error.

The entity follows the four metrics while it is enabled (D-007); a disabled entity costs
nothing. While the install action runs Home Assistant shows the update as in progress.
The agent goes away with the installer, so the entity is unavailable until the new
version is running and connected; its first ``agent.version`` ends the update.
"""

import re
from collections.abc import Callable
from typing import Any, Final

from homeassistant.components.update import UpdateEntity, UpdateEntityFeature
from homeassistant.core import HomeAssistant, callback
from homeassistant.exceptions import HomeAssistantError
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback

from .client import NotConnectedError
from .commands import async_run_command
from .const import (
    COMMAND_AGENT_UPDATE,
    DOMAIN,
    FEATURE_UPDATE,
    MAX_RELEASE_SUMMARY_CHARS,
    METRIC_AGENT_LATEST_VERSION,
    METRIC_AGENT_RELEASE_NOTES,
    METRIC_AGENT_RELEASE_URL,
    METRIC_AGENT_VERSION,
    UPDATE_KEY,
    UPDATE_REQUEST_TIMEOUT,
)
from .entity import QoffeeAutomatEntity, async_setup_feature_platform
from .protocol import Bye, MetricValue
from .runtime import QoffeeAutomatConfigEntry, QoffeeAutomatRuntimeData

# The install action starts the command and waits for its answer; two of them at once
# make no sense.
PARALLEL_UPDATES = 1

# The metrics the update entity follows, in the order they are registered.
UPDATE_METRICS: Final = (
    METRIC_AGENT_VERSION,
    METRIC_AGENT_LATEST_VERSION,
    METRIC_AGENT_RELEASE_URL,
    METRIC_AGENT_RELEASE_NOTES,
)

# ``bye`` reason of an agent that goes away because an installer replaces it.
_BYE_UPDATE: Final = "update"
# Markdown that a one-line summary should not start with: heading marks, list bullets,
# block quotes and emphasis.
_LEADING_MARKUP = re.compile(r"^[\s#>*_`+-]+")


async def async_setup_entry(
    hass: HomeAssistant,
    entry: QoffeeAutomatConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    """Set up the update entity while the agent announces the feature ``update``."""
    await async_setup_feature_platform(
        hass,
        entry,
        async_add_entities,
        feature=FEATURE_UPDATE,
        domain="update",
        key=UPDATE_KEY,
        build=QoffeeAutomatUpdateEntity,
    )


class _MetricSlot:
    """A metric consumer that tells the entity which metric a value is of."""

    def __init__(
        self, handler: Callable[[str, MetricValue], None], metric_id: str
    ) -> None:
        self._handler = handler
        self._metric_id = metric_id

    def handle_value(self, value: MetricValue) -> None:
        """Hand the value and the id of the metric on."""
        self._handler(self._metric_id, value)


def _text(value: MetricValue) -> str | None:
    """Return a non-empty text value, otherwise ``None``."""
    if isinstance(value, str) and (stripped := value.strip()):
        return stripped
    return None


def summarize(notes: str | None) -> str | None:
    """Return a one-line excerpt of release notes for ``release_summary``.

    The first line with text, without the Markdown marks it starts with, cut to the
    255 characters that Home Assistant keeps (the last one is an ellipsis if the line
    had to be cut).
    """
    if notes is None:
        return None
    for line in notes.splitlines():
        text = _LEADING_MARKUP.sub("", line).replace("**", "").replace("`", "").strip()
        if not text:
            continue
        if len(text) > MAX_RELEASE_SUMMARY_CHARS:
            return text[: MAX_RELEASE_SUMMARY_CHARS - 1].rstrip() + "…"
        return text
    return None


class QoffeeAutomatUpdateEntity(QoffeeAutomatEntity, UpdateEntity):
    """The installed and the newest version of the agent, and the way to install it."""

    _attr_translation_key = "agent"
    # The name of the software, for the dialog of the update.
    _attr_title = "QoffeeAutomat"

    def __init__(self, runtime: QoffeeAutomatRuntimeData) -> None:
        """Create the update entity of the PC."""
        super().__init__(runtime, UPDATE_KEY)
        self._values: dict[str, MetricValue] = {}

    async def async_added_to_hass(self) -> None:
        """Subscribe to the version metrics while the entity is enabled."""
        await super().async_added_to_hass()
        manager = self._runtime.manifest_manager
        # Only ask for what the agent announces.
        announced = {metric.id for metric in manager.manifest.metrics}
        subscriptions = self._runtime.subscriptions
        for metric_id in UPDATE_METRICS:
            if metric_id not in announced:
                continue
            self.async_on_remove(
                subscriptions.register(
                    metric_id, _MetricSlot(self.handle_metric, metric_id)
                )
            )
            # Another entity (the sensor of the metric) may follow it already.
            if (value := subscriptions.last_value(metric_id)) is not None:
                self._values[metric_id] = value

    # -- values -----------------------------------------------------------------

    @callback
    def handle_metric(self, metric_id: str, value: MetricValue) -> None:
        """Take a value of one of the metrics and write exactly one state."""
        self._values[metric_id] = value
        self.async_write_ha_state()

    @property
    def installed_version(self) -> str | None:
        """Return the version of the running agent."""
        return _text(self._values.get(METRIC_AGENT_VERSION))

    @property
    def latest_version(self) -> str | None:
        """Return the newest release; ``None`` while the agent does not know one.

        The agent does not know one while its update check is switched off, before the
        first check and after a failed one. The state is unknown then, not "up to
        date": the entity must not claim what nobody checked.
        """
        return _text(self._values.get(METRIC_AGENT_LATEST_VERSION))

    @property
    def release_url(self) -> str | None:
        """Return the page of the newest release."""
        return _text(self._values.get(METRIC_AGENT_RELEASE_URL))

    @property
    def release_summary(self) -> str | None:
        """Return the first line of the release notes."""
        return summarize(_text(self._values.get(METRIC_AGENT_RELEASE_NOTES)))

    @property
    def supported_features(self) -> UpdateEntityFeature:
        """Return what the agent can do: install only with the command, notes if any."""
        manager = self._runtime.manifest_manager
        commands = (
            {command.id for command in manager.manifest.commands}
            if manager.loaded
            else set()
        )
        features = UpdateEntityFeature.RELEASE_NOTES
        if COMMAND_AGENT_UPDATE in commands:
            features |= UpdateEntityFeature.INSTALL
        return features

    async def async_release_notes(self) -> str | None:
        """Return the release notes (Markdown, shortened by the agent)."""
        return _text(self._values.get(METRIC_AGENT_RELEASE_NOTES))

    # -- install ----------------------------------------------------------------

    async def async_install(
        self, version: str | None, backup: bool, **kwargs: Any
    ) -> None:
        """Run ``agent_update`` and wait for the agent's answer.

        The agent installs the newest release; a specific version is not supported
        (``SPECIFIC_VERSION`` is not set), and the PC is not backed up. The answer
        comes before the installer replaces the agent, or the connection ends because
        the agent said ``bye`` with the reason ``update``; both mean that the update
        started. A refusal (wrong checksum, untrusted signature, no installer in the
        release) is an error with the reason of the agent. Nothing is spliced into
        anything: the command has no variables (D-012).
        """
        said_bye = False

        @callback
        def on_bye(frame: Bye) -> None:
            nonlocal said_bye
            if frame.reason == _BYE_UPDATE:
                said_bye = True

        remove = self._runtime.client.add_bye_listener(on_bye)
        try:
            result = await async_run_command(
                self._runtime,
                COMMAND_AGENT_UPDATE,
                None,
                wait=True,
                request_timeout=UPDATE_REQUEST_TIMEOUT,
            )
        except HomeAssistantError as err:
            # The installer stops the agent; the answer may not get through.
            if said_bye and isinstance(err.__cause__, NotConnectedError):
                return
            raise
        finally:
            remove()
        if result.timed_out or result.exit_code not in (None, 0):
            message = (result.stderr or result.stdout or "").strip()
            if not message:
                message = (
                    "it took too long"
                    if result.timed_out
                    else f"exit code {result.exit_code}"
                )
            raise HomeAssistantError(
                translation_domain=DOMAIN,
                translation_key="update_refused",
                translation_placeholders={"message": message},
            )
