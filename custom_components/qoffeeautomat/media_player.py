"""Media player platform: the PC as a media player (PLAN.md S10, S16, T716, D-038).

The entity exists only while the manifest lists the feature ``media``. It is composed of
ordinary metrics and commands, there are no special messages: the metrics
``media.state``, ``media.title``, ``media.artist``, ``media.album``, ``media.app``,
``media.duration``, ``media.position`` and ``media.position_updated_at`` describe the
current media session of the user (what the volume flyout of Windows shows), and
``audio.volume`` and ``audio.muted`` the master volume. The commands
``agent_media_control`` (play, pause, play_pause, next, previous, stop),
``agent_media_volume`` and ``agent_media_mute`` control it. The entity subscribes to
these metrics while it is enabled (D-007); a PC where it is disabled costs nothing.

* The entity is unavailable while the agent is not connected and while the state metric
  has no value, which is the case without a logged-in user: the media session belongs to
  the user.
* The position is only sent when the track, the playback state or the position by a
  jump changes, together with the time at which it was valid
  (``media.position_updated_at``). Home Assistant interpolates from there. The time is
  related to the ``ts`` of the frame that carries it, both on the clock of the PC, and
  then to the clock of Home Assistant, so a PC whose clock is off still shows the right
  position, and a position that arrives late (after a new subscription) is not mistaken
  for a fresh one.
* Cover art is not supported: the agent does not publish it. This is a known
  limitation.
"""

from collections.abc import Callable
from datetime import UTC, datetime, timedelta
from typing import Final

from homeassistant.components.media_player import MediaPlayerEntity
from homeassistant.components.media_player.const import (
    MediaPlayerEntityFeature,
    MediaPlayerState,
    MediaType,
)
from homeassistant.core import HomeAssistant, callback
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback
from homeassistant.util import dt as dt_util

from .commands import async_run_command
from .const import (
    COMMAND_MEDIA_CONTROL,
    COMMAND_MEDIA_MUTE,
    COMMAND_MEDIA_VOLUME,
    FEATURE_MEDIA,
    MEDIA_PLAYER_KEY,
    METRIC_AUDIO_MUTED,
    METRIC_AUDIO_VOLUME,
    METRIC_MEDIA_ALBUM,
    METRIC_MEDIA_APP,
    METRIC_MEDIA_ARTIST,
    METRIC_MEDIA_DURATION,
    METRIC_MEDIA_POSITION,
    METRIC_MEDIA_POSITION_UPDATED_AT,
    METRIC_MEDIA_STATE,
    METRIC_MEDIA_TITLE,
)
from .entity import QoffeeAutomatEntity, async_setup_feature_platform
from .protocol import MetricValue
from .runtime import QoffeeAutomatConfigEntry, QoffeeAutomatRuntimeData

# The commands that the integration sends are started one after the other.
PARALLEL_UPDATES = 1

# The metrics the media player follows, in the order they are registered.
MEDIA_PLAYER_METRICS: Final = (
    METRIC_MEDIA_STATE,
    METRIC_MEDIA_TITLE,
    METRIC_MEDIA_ARTIST,
    METRIC_MEDIA_ALBUM,
    METRIC_MEDIA_APP,
    METRIC_MEDIA_DURATION,
    METRIC_MEDIA_POSITION,
    METRIC_MEDIA_POSITION_UPDATED_AT,
    METRIC_AUDIO_VOLUME,
    METRIC_AUDIO_MUTED,
)

_STATES: Final = {
    "playing": MediaPlayerState.PLAYING,
    "paused": MediaPlayerState.PAUSED,
    "idle": MediaPlayerState.IDLE,
}


async def async_setup_entry(
    hass: HomeAssistant,
    entry: QoffeeAutomatConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    """Set up the media player while the agent announces the feature ``media``."""
    await async_setup_feature_platform(
        hass,
        entry,
        async_add_entities,
        feature=FEATURE_MEDIA,
        domain="media_player",
        key=MEDIA_PLAYER_KEY,
        build=QoffeeAutomatMediaPlayer,
    )


class _MetricSlot:
    """A metric consumer that tells the media player which metric a value is of."""

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
    return value if isinstance(value, str) and value else None


def _seconds(value: MetricValue) -> int | None:
    """Return a number of seconds as a whole number, otherwise ``None``."""
    if isinstance(value, bool) or not isinstance(value, int | float):
        return None
    return int(value) if value >= 0 else None


class QoffeeAutomatMediaPlayer(QoffeeAutomatEntity, MediaPlayerEntity):
    """The current media session of the PC and the volume."""

    _attr_translation_key = "media_player"
    _attr_media_content_type = MediaType.MUSIC

    def __init__(self, runtime: QoffeeAutomatRuntimeData) -> None:
        """Create the media player of the PC."""
        super().__init__(runtime, MEDIA_PLAYER_KEY)
        self._values: dict[str, MetricValue] = {}
        self._position_updated_at: datetime | None = None

    async def async_added_to_hass(self) -> None:
        """Subscribe to the metrics of the media session while the entity is enabled."""
        await super().async_added_to_hass()
        manager = self._runtime.manifest_manager
        # The agent ignores nothing it does not know: only ask for what it announces.
        announced = {metric.id for metric in manager.manifest.metrics}
        subscriptions = self._runtime.subscriptions
        for metric_id in MEDIA_PLAYER_METRICS:
            if metric_id not in announced:
                continue
            self.async_on_remove(
                subscriptions.register(
                    metric_id, _MetricSlot(self.handle_metric, metric_id)
                )
            )
            # Another entity may follow the metric already; its value is known.
            if (value := subscriptions.last_value(metric_id)) is not None:
                self._values[metric_id] = value
        self._refresh_position_time()

    # -- values -----------------------------------------------------------------

    @callback
    def handle_metric(self, metric_id: str, value: MetricValue) -> None:
        """Take a value of one of the metrics and write exactly one state."""
        previous = self._values.get(metric_id)
        self._values[metric_id] = value
        if value != previous and metric_id in (
            METRIC_MEDIA_POSITION,
            METRIC_MEDIA_POSITION_UPDATED_AT,
        ):
            self._refresh_position_time()
        self.async_write_ha_state()

    def _refresh_position_time(self) -> None:
        """Work out, on the clock of Home Assistant, when the position was valid.

        The agent sends the time of the position (its clock) and the frame carries the
        time of sending (the same clock): their difference is how old the position is,
        whatever the clocks of the two computers say. A position without a time counts
        as valid now.
        """
        now = dt_util.utcnow()
        # The subscription knows every value of the frame that is being delivered; the
        # player may not have been told the time yet when it is told the position.
        subscriptions = self._runtime.subscriptions
        valid_at = _parse_time(
            subscriptions.last_value(METRIC_MEDIA_POSITION_UPDATED_AT)
        )
        frame_ts = subscriptions.frame_ts
        if valid_at is None or frame_ts is None:
            self._position_updated_at = now
            return
        age = max(0.0, frame_ts / 1000 - valid_at.timestamp())
        self._position_updated_at = now - timedelta(seconds=age)

    @property
    def available(self) -> bool:
        """Return whether the agent is connected and a media state is known.

        Without a logged-in user the agent reports no value for the state, and
        nothing can be played or controlled.
        """
        return super().available and _STATES.get(self._state_text()) is not None

    def _state_text(self) -> str:
        value = self._values.get(METRIC_MEDIA_STATE)
        return value if isinstance(value, str) else ""

    @property
    def state(self) -> MediaPlayerState | None:
        """Return playing, paused or idle."""
        return _STATES.get(self._state_text())

    @property
    def _has_media(self) -> bool:
        return self.state in (MediaPlayerState.PLAYING, MediaPlayerState.PAUSED)

    @property
    def media_title(self) -> str | None:
        """Return the title of the track."""
        return _text(self._values.get(METRIC_MEDIA_TITLE)) if self._has_media else None

    @property
    def media_artist(self) -> str | None:
        """Return the artist of the track."""
        return _text(self._values.get(METRIC_MEDIA_ARTIST)) if self._has_media else None

    @property
    def media_album_name(self) -> str | None:
        """Return the album of the track."""
        return _text(self._values.get(METRIC_MEDIA_ALBUM)) if self._has_media else None

    @property
    def app_name(self) -> str | None:
        """Return the program that plays."""
        return _text(self._values.get(METRIC_MEDIA_APP)) if self._has_media else None

    @property
    def media_duration(self) -> int | None:
        """Return the length of the track in seconds."""
        if not self._has_media:
            return None
        return _seconds(self._values.get(METRIC_MEDIA_DURATION))

    @property
    def media_position(self) -> int | None:
        """Return the position in the track in seconds, as of the update time."""
        if not self._has_media:
            return None
        return _seconds(self._values.get(METRIC_MEDIA_POSITION))

    @property
    def media_position_updated_at(self) -> datetime | None:
        """Return when the position was valid; Home Assistant interpolates from it."""
        return self._position_updated_at if self.media_position is not None else None

    @property
    def volume_level(self) -> float | None:
        """Return the master volume between 0 and 1."""
        value = self._values.get(METRIC_AUDIO_VOLUME)
        if isinstance(value, bool) or not isinstance(value, int | float):
            return None
        return max(0.0, min(1.0, value / 100))

    @property
    def is_volume_muted(self) -> bool | None:
        """Return whether the sound is muted."""
        value = self._values.get(METRIC_AUDIO_MUTED)
        return value if isinstance(value, bool) else None

    @property
    def supported_features(self) -> MediaPlayerEntityFeature:
        """Return what the agent can do: it follows the commands of the manifest."""
        manager = self._runtime.manifest_manager
        commands = (
            {command.id for command in manager.manifest.commands}
            if manager.loaded
            else set()
        )
        features = MediaPlayerEntityFeature(0)
        if COMMAND_MEDIA_CONTROL in commands:
            features |= (
                MediaPlayerEntityFeature.PLAY
                | MediaPlayerEntityFeature.PAUSE
                | MediaPlayerEntityFeature.STOP
                | MediaPlayerEntityFeature.NEXT_TRACK
                | MediaPlayerEntityFeature.PREVIOUS_TRACK
            )
        if COMMAND_MEDIA_VOLUME in commands:
            features |= (
                MediaPlayerEntityFeature.VOLUME_SET
                | MediaPlayerEntityFeature.VOLUME_STEP
            )
        if COMMAND_MEDIA_MUTE in commands:
            features |= MediaPlayerEntityFeature.VOLUME_MUTE
        return features

    # -- controls ---------------------------------------------------------------
    #
    # A command is started and not waited for; the state follows from the metrics,
    # which the agent samples every two seconds while the entity is enabled.

    async def _async_control(self, action: str) -> None:
        await async_run_command(
            self._runtime, COMMAND_MEDIA_CONTROL, {"Action": action}, wait=False
        )

    async def async_media_play(self) -> None:
        """Start or resume playback."""
        await self._async_control("play")

    async def async_media_pause(self) -> None:
        """Pause playback."""
        await self._async_control("pause")

    async def async_media_play_pause(self) -> None:
        """Toggle between playing and paused."""
        await self._async_control("play_pause")

    async def async_media_stop(self) -> None:
        """Stop playback."""
        await self._async_control("stop")

    async def async_media_next_track(self) -> None:
        """Skip to the next track."""
        await self._async_control("next")

    async def async_media_previous_track(self) -> None:
        """Go back to the previous track."""
        await self._async_control("previous")

    async def async_set_volume_level(self, volume: float) -> None:
        """Set the master volume (0 to 1) through the audio action of the agent."""
        level = round(max(0.0, min(1.0, volume)) * 100)
        await async_run_command(
            self._runtime, COMMAND_MEDIA_VOLUME, {"Level": level}, wait=False
        )

    async def async_mute_volume(self, mute: bool) -> None:
        """Mute or unmute the sound through the audio action of the agent."""
        await async_run_command(
            self._runtime, COMMAND_MEDIA_MUTE, {"Muted": mute}, wait=False
        )


def _parse_time(value: MetricValue) -> datetime | None:
    """Return the timestamp of a metric value as an aware datetime, or ``None``."""
    if not isinstance(value, str):
        return None
    parsed = dt_util.parse_datetime(value)
    if parsed is None:
        return None
    return parsed if parsed.tzinfo else parsed.replace(tzinfo=UTC)
