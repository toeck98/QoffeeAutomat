"""Tests of the PC as a media player (T716, D-038).

The media player is composed of ordinary metrics (``media.*`` and ``audio.*``) and
commands (``agent_media_control``, ``agent_media_volume``, ``agent_media_mute``); it
exists only while the manifest lists the feature ``media``. What plays on a real PC and
what a pause does there is checked by hand.
"""

from datetime import datetime, timedelta
from typing import Any

import pytest
from homeassistant.components.media_player import MediaPlayerEntityFeature
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import HomeAssistantError
from homeassistant.helpers import entity_registry as er
from homeassistant.util import dt as dt_util
from pytest_homeassistant_custom_component.common import MockConfigEntry

from custom_components.qoffeeautomat.const import (
    COMMAND_MEDIA_CONTROL,
    COMMAND_MEDIA_MUTE,
    COMMAND_MEDIA_VOLUME,
    DOMAIN,
    FEATURE_MEDIA,
    MEDIA_PLAYER_KEY,
)
from custom_components.qoffeeautomat.media_player import MEDIA_PLAYER_METRICS
from tests_integration.fake_agent import FakeAgent
from tests_integration.helpers import (
    NEW_REV,
    SECOND_REV,
    EntryFactory,
    attribute_of,
    changed_manifest,
    entity_id_of,
    find_entity_id,
    state_of,
)

# ``ts`` (unix milliseconds) of the snapshot that the fake agent sends after a
# ``subscribe``: 2025-11-14T12:00:00Z.
SNAPSHOT_TS = 1763126400000
SNAPSHOT_TIME = datetime.fromtimestamp(SNAPSHOT_TS / 1000, tz=dt_util.UTC)


def _metric(
    metric_id: str, name: str, kind: str, value_type: str, **extra: Any
) -> dict[str, Any]:
    """Return a metric of the media session: off by default, of the user."""
    return {
        "id": metric_id,
        "name": name,
        "kind": kind,
        "value_type": value_type,
        "enabled_default": False,
        "interval_seconds": 2,
        "context": "user",
        **extra,
    }


MEDIA_METRICS: list[dict[str, Any]] = [
    _metric(
        "media.state",
        "Media state",
        "sensor",
        "enum",
        options=["playing", "paused", "idle"],
    ),
    _metric("media.title", "Media title", "sensor", "string"),
    _metric("media.artist", "Media artist", "sensor", "string"),
    _metric("media.album", "Media album", "sensor", "string"),
    _metric("media.app", "Media app", "sensor", "string"),
    _metric("media.duration", "Media duration", "sensor", "number", unit="s"),
    _metric("media.position", "Media position", "sensor", "number", unit="s"),
    _metric(
        "media.position_updated_at",
        "Media position time",
        "sensor",
        "timestamp",
        device_class="timestamp",
    ),
    _metric("audio.muted", "Muted", "binary_sensor", "bool"),
]
# ``audio.volume`` is part of the fixture manifest of the fake agent already.

CONTROL_COMMAND: dict[str, Any] = {
    "id": COMMAND_MEDIA_CONTROL,
    "name": "Control media playback",
    "vars": [
        {
            "name": "Action",
            "type": "enum",
            "options": ["play", "pause", "play_pause", "next", "previous", "stop"],
            "required": True,
        }
    ],
}
VOLUME_COMMAND: dict[str, Any] = {
    "id": COMMAND_MEDIA_VOLUME,
    "name": "Set media volume",
    "vars": [{"name": "Level", "type": "int", "required": True, "min": 0, "max": 100}],
}
MUTE_COMMAND: dict[str, Any] = {
    "id": COMMAND_MEDIA_MUTE,
    "name": "Mute media",
    "vars": [{"name": "Muted", "type": "bool", "required": True}],
}

PLAYING: dict[str, Any] = {
    "media.state": "playing",
    "media.title": "Strobe",
    "media.artist": "deadmau5",
    "media.album": "For Lack of a Better Name",
    "media.app": "Spotify",
    "media.duration": 637,
    "media.position": 42,
    "media.position_updated_at": "2025-11-14T11:59:50Z",
    "audio.volume": 35.0,
    "audio.muted": False,
}


def media_manifest(
    agent: FakeAgent,
    *,
    commands: tuple[dict[str, Any], ...] | None = None,
    remove_metrics: tuple[str, ...] = (),
    **kwargs: Any,
) -> dict[str, Any]:
    """Return the manifest of an agent that provides the media player."""
    if commands is None:
        commands = (CONTROL_COMMAND, VOLUME_COMMAND, MUTE_COMMAND)
    dropped = set(remove_metrics)
    added = tuple(m for m in MEDIA_METRICS if m["id"] not in dropped)
    manifest = changed_manifest(
        agent,
        add_metrics=added,
        remove_metrics=remove_metrics,
        add_commands=commands,
        **kwargs,
    )
    manifest["features"] = [FEATURE_MEDIA]
    return manifest


def media_player(hass: HomeAssistant, agent: FakeAgent) -> str:
    """Return the entity id of the media player."""
    return entity_id_of(hass, "media_player", agent.agent_id, MEDIA_PLAYER_KEY)


def subscribed(agent: FakeAgent) -> set[str]:
    """Return the metrics of the last ``subscribe`` request."""
    metrics = agent.subscribed_metrics()[-1]
    assert isinstance(metrics, list)
    return set(metrics)


async def wait_for_subscription(agent: FakeAgent) -> None:
    """Wait until the media player has told the agent which metrics it follows."""
    await agent.wait_for(
        lambda: any(
            isinstance(metrics, list) and "media.state" in metrics
            for metrics in agent.subscribed_metrics()
        )
    )


async def push(agent: FakeAgent, values: dict[str, Any], ts: int = SNAPSHOT_TS) -> None:
    """Send a ``state`` frame with ``values`` and the time ``ts`` of the PC."""
    await agent.send({"t": "state", "ts": ts, "v": values})


def position_time(hass: HomeAssistant, entity_id: str) -> datetime:
    """Return the attribute ``media_position_updated_at`` as a datetime."""
    value = attribute_of(hass, entity_id, "media_position_updated_at")
    if isinstance(value, str):
        value = dt_util.parse_datetime(value)
    assert isinstance(value, datetime)
    return value


@pytest.fixture(name="agent")
def agent_fixture(fake_agent: FakeAgent) -> FakeAgent:
    """The fake agent with the feature media, its metrics and commands, playing."""
    fake_agent.set_manifest(media_manifest(fake_agent, rev=fake_agent.manifest["rev"]))
    fake_agent.snapshot = dict(PLAYING)
    return fake_agent


@pytest.fixture(name="entry")
async def entry_fixture(
    hass: HomeAssistant, agent: FakeAgent, entry_factory: EntryFactory
) -> MockConfigEntry:
    """Set up the entry and wait for the first values of the media player."""
    entry = await entry_factory()
    await wait_for_subscription(agent)
    entity_id = media_player(hass, agent)
    await agent.wait_for(lambda: state_of(hass, entity_id) == "playing")
    return entry


# -- the entity exists only with the feature ------------------------------------------


async def test_the_media_player_exists_when_the_manifest_lists_the_feature(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The unique id is the reserved key; the name comes from the translation."""
    entity_id = media_player(hass, agent)

    registered = er.async_get(hass).async_get(entity_id)
    assert registered is not None
    assert registered.unique_id == f"{agent.agent_id}_{MEDIA_PLAYER_KEY}"
    assert registered.translation_key == "media_player"
    assert registered.platform == DOMAIN
    assert attribute_of(hass, entity_id, "friendly_name").endswith("Media player")


async def test_without_the_feature_there_is_no_media_player(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """The metrics and commands are there, the feature is not: no entity."""
    manifest = media_manifest(fake_agent)
    manifest["features"] = ["notify", "wol"]
    fake_agent.set_manifest(manifest)

    await entry_factory()

    assert (
        find_entity_id(hass, "media_player", fake_agent.agent_id, MEDIA_PLAYER_KEY)
        is None
    )
    assert not hass.states.async_entity_ids("media_player")


async def test_a_manifest_without_any_features_creates_no_media_player(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """An agent that announces nothing has no media player."""
    manifest = changed_manifest(fake_agent)
    manifest["features"] = []
    fake_agent.set_manifest(manifest)

    await entry_factory()

    assert not hass.states.async_entity_ids("media_player")


async def test_the_entity_follows_the_feature_without_a_reload(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The feature goes away and comes back; entity and registry entry follow."""
    assert find_entity_id(hass, "media_player", agent.agent_id, MEDIA_PLAYER_KEY)
    connections = len(agent.connections)

    without = media_manifest(agent, rev=NEW_REV)
    without["features"] = []
    agent.set_manifest(without)
    await agent.send_manifest_changed()
    await agent.wait_for(
        lambda: (
            find_entity_id(hass, "media_player", agent.agent_id, MEDIA_PLAYER_KEY)
            is None
        )
    )
    await hass.async_block_till_done()
    assert not hass.states.async_entity_ids("media_player")

    agent.set_manifest(media_manifest(agent, rev=SECOND_REV))
    await agent.send_manifest_changed()
    await agent.wait_for(
        lambda: (
            find_entity_id(hass, "media_player", agent.agent_id, MEDIA_PLAYER_KEY)
            is not None
        )
    )
    await hass.async_block_till_done()

    assert hass.states.async_entity_ids("media_player")
    assert len(agent.connections) == connections


async def test_an_entry_that_an_earlier_run_left_behind_is_removed_without_the_feature(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """The agent was updated and no longer has the feature: no unavailable leftover."""
    manifest = changed_manifest(fake_agent)
    manifest["features"] = []
    fake_agent.set_manifest(manifest)
    entry = await entry_factory(setup=False)
    er.async_get(hass).async_get_or_create(
        "media_player",
        DOMAIN,
        f"{fake_agent.agent_id}_{MEDIA_PLAYER_KEY}",
        config_entry=entry,
    )

    await hass.config_entries.async_setup(entry.entry_id)
    await hass.async_block_till_done()

    assert (
        find_entity_id(hass, "media_player", fake_agent.agent_id, MEDIA_PLAYER_KEY)
        is None
    )


# -- the subscription ------------------------------------------------------------------


async def test_the_player_subscribes_to_the_metrics_of_the_media_session(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The disabled metric entities add nothing, the player adds what it follows."""
    assert set(MEDIA_PLAYER_METRICS) <= subscribed(agent)


async def test_the_player_asks_only_for_the_metrics_the_agent_announces(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """An older agent without the album does not get a subscription to it."""
    fake_agent.set_manifest(media_manifest(fake_agent, remove_metrics=("media.album",)))
    fake_agent.snapshot = dict(PLAYING)

    await entry_factory()
    await wait_for_subscription(fake_agent)

    assert "media.album" not in subscribed(fake_agent)
    assert "media.title" in subscribed(fake_agent)
    entity_id = media_player(hass, fake_agent)
    await fake_agent.wait_for(lambda: state_of(hass, entity_id) == "playing")
    assert attribute_of(hass, entity_id, "media_album_name") is None
    assert attribute_of(hass, entity_id, "media_title") == "Strobe"


async def test_a_disabled_player_costs_nothing(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """Disabled in the entity registry: nothing is subscribed (D-007)."""
    fake_agent.set_manifest(media_manifest(fake_agent, rev=fake_agent.manifest["rev"]))
    fake_agent.snapshot = dict(PLAYING)
    entry = await entry_factory(setup=False)
    er.async_get(hass).async_get_or_create(
        "media_player",
        DOMAIN,
        f"{fake_agent.agent_id}_{MEDIA_PLAYER_KEY}",
        config_entry=entry,
        disabled_by=er.RegistryEntryDisabler.USER,
    )

    await hass.config_entries.async_setup(entry.entry_id)
    await hass.async_block_till_done()
    await fake_agent.wait_ready()
    # The subscription is sent after a short delay; wait for the first one.
    await fake_agent.wait_for(lambda: bool(fake_agent.subscribed_metrics()))

    assert not hass.states.async_entity_ids("media_player")
    assert not {"media.state", "media.title", "media.position"} & subscribed(fake_agent)


# -- state and metadata ----------------------------------------------------------------


async def test_the_player_shows_state_and_metadata(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """Playing, with title, artist, album, program, length and position."""
    entity_id = media_player(hass, agent)

    assert state_of(hass, entity_id) == "playing"
    assert attribute_of(hass, entity_id, "media_title") == "Strobe"
    assert attribute_of(hass, entity_id, "media_artist") == "deadmau5"
    album = attribute_of(hass, entity_id, "media_album_name")
    assert album == "For Lack of a Better Name"
    assert attribute_of(hass, entity_id, "app_name") == "Spotify"
    assert attribute_of(hass, entity_id, "media_duration") == 637
    assert attribute_of(hass, entity_id, "media_position") == 42
    assert attribute_of(hass, entity_id, "media_content_type") == "music"


async def test_the_state_follows_the_metric(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """Paused keeps the track, a new track replaces the metadata."""
    entity_id = media_player(hass, agent)

    await push(agent, {"media.state": "paused"})
    await agent.wait_for(lambda: state_of(hass, entity_id) == "paused")
    assert attribute_of(hass, entity_id, "media_title") == "Strobe"

    await push(
        agent,
        {
            "media.state": "playing",
            "media.title": "Ghosts 'n' Stuff",
            "media.position": 0,
        },
    )
    await agent.wait_for(lambda: state_of(hass, entity_id) == "playing")
    assert attribute_of(hass, entity_id, "media_title") == "Ghosts 'n' Stuff"
    assert attribute_of(hass, entity_id, "media_position") == 0
    assert attribute_of(hass, entity_id, "media_artist") == "deadmau5"


async def test_an_idle_session_shows_no_track(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """Nothing plays: the player is idle and shows nothing of the last track."""
    entity_id = media_player(hass, agent)

    await push(agent, {"media.state": "idle"})
    await agent.wait_for(lambda: state_of(hass, entity_id) == "idle")

    for name in ("media_title", "media_artist", "media_album_name", "app_name"):
        assert attribute_of(hass, entity_id, name) is None, name
    assert attribute_of(hass, entity_id, "media_position") is None
    assert attribute_of(hass, entity_id, "media_duration") is None


async def test_empty_texts_and_bad_numbers_are_not_shown(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """A track without an album, a stream without a length, nonsense from outside."""
    entity_id = media_player(hass, agent)

    await push(
        agent,
        {
            "media.album": "",
            "media.artist": None,
            "media.duration": -1,
            "media.position": True,
        },
    )
    await agent.wait_for(
        lambda: attribute_of(hass, entity_id, "media_album_name") is None
    )

    assert attribute_of(hass, entity_id, "media_artist") is None
    assert attribute_of(hass, entity_id, "media_duration") is None
    assert attribute_of(hass, entity_id, "media_position") is None
    assert state_of(hass, entity_id) == "playing"


async def test_an_unknown_state_makes_the_player_unavailable(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """A value from a newer agent is not guessed."""
    entity_id = media_player(hass, agent)

    await push(agent, {"media.state": "buffering"})

    await agent.wait_for(lambda: state_of(hass, entity_id) == "unavailable")


# -- availability ----------------------------------------------------------------------


async def test_the_player_is_unavailable_without_a_user_session(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """Without a logged-in user the state metric has no value (S06)."""
    entity_id = media_player(hass, agent)

    await push(agent, {"media.state": None})
    await agent.wait_for(lambda: state_of(hass, entity_id) == "unavailable")

    await push(agent, {"media.state": "paused"})
    await agent.wait_for(lambda: state_of(hass, entity_id) == "paused")


async def test_the_player_is_unavailable_while_the_agent_is_gone(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The PC shuts down: the player is unavailable and no control is sent."""
    entity_id = media_player(hass, agent)

    await agent.send_bye("shutdown")
    await agent.wait_for(lambda: state_of(hass, entity_id) == "unavailable")


async def test_the_player_without_a_first_value_is_unavailable(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """The agent answers the subscription without a value for the state."""
    fake_agent.set_manifest(media_manifest(fake_agent, rev=fake_agent.manifest["rev"]))
    fake_agent.snapshot = {}

    await entry_factory()
    await wait_for_subscription(fake_agent)
    await hass.async_block_till_done()

    assert state_of(hass, media_player(hass, fake_agent)) == "unavailable"


# -- supported features ----------------------------------------------------------------


async def test_the_features_follow_the_commands_of_the_manifest(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """Control, volume and mute commands: everything but turning on and off."""
    entity_id = media_player(hass, agent)
    features = MediaPlayerEntityFeature(
        attribute_of(hass, entity_id, "supported_features")
    )

    expected = (
        MediaPlayerEntityFeature.PLAY
        | MediaPlayerEntityFeature.PAUSE
        | MediaPlayerEntityFeature.STOP
        | MediaPlayerEntityFeature.NEXT_TRACK
        | MediaPlayerEntityFeature.PREVIOUS_TRACK
        | MediaPlayerEntityFeature.VOLUME_SET
        | MediaPlayerEntityFeature.VOLUME_STEP
        | MediaPlayerEntityFeature.VOLUME_MUTE
    )
    assert features == expected


async def test_without_the_audio_commands_there_is_no_volume(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """Only the control command: the volume is not offered."""
    fake_agent.set_manifest(media_manifest(fake_agent, commands=(CONTROL_COMMAND,)))
    fake_agent.snapshot = dict(PLAYING)
    await entry_factory()
    entity_id = media_player(hass, fake_agent)
    await fake_agent.wait_for(lambda: state_of(hass, entity_id) == "playing")

    features = MediaPlayerEntityFeature(
        attribute_of(hass, entity_id, "supported_features")
    )

    assert features & MediaPlayerEntityFeature.PLAY
    assert features & MediaPlayerEntityFeature.NEXT_TRACK
    assert not features & MediaPlayerEntityFeature.VOLUME_SET
    assert not features & MediaPlayerEntityFeature.VOLUME_MUTE


async def test_without_the_control_command_nothing_is_controlled(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """Only the audio commands: a display of the state with a volume knob."""
    fake_agent.set_manifest(
        media_manifest(fake_agent, commands=(VOLUME_COMMAND, MUTE_COMMAND))
    )
    fake_agent.snapshot = dict(PLAYING)
    await entry_factory()
    entity_id = media_player(hass, fake_agent)
    await fake_agent.wait_for(lambda: state_of(hass, entity_id) == "playing")

    features = MediaPlayerEntityFeature(
        attribute_of(hass, entity_id, "supported_features")
    )

    assert not features & MediaPlayerEntityFeature.PLAY
    assert not features & MediaPlayerEntityFeature.PAUSE
    assert features & MediaPlayerEntityFeature.VOLUME_SET


# -- controls --------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("service", "action"),
    [
        ("media_play", "play"),
        ("media_pause", "pause"),
        ("media_play_pause", "play_pause"),
        ("media_stop", "stop"),
        ("media_next_track", "next"),
        ("media_previous_track", "previous"),
    ],
)
async def test_every_control_runs_the_control_command(
    hass: HomeAssistant,
    agent: FakeAgent,
    entry: MockConfigEntry,
    service: str,
    action: str,
) -> None:
    """The action is the only variable; the call does not wait for the end."""
    await hass.services.async_call(
        "media_player",
        service,
        {"entity_id": media_player(hass, agent)},
        blocking=True,
    )

    frame = agent.runs[-1]
    assert frame["command"] == COMMAND_MEDIA_CONTROL
    assert frame["vars"] == {"Action": action}
    assert frame["wait"] is False


async def test_set_volume_runs_the_volume_command_with_a_percentage(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """0.35 of Home Assistant is 35 percent of the audio action."""
    await hass.services.async_call(
        "media_player",
        "volume_set",
        {"entity_id": media_player(hass, agent), "volume_level": 0.35},
        blocking=True,
    )

    frame = agent.runs[-1]
    assert frame["command"] == COMMAND_MEDIA_VOLUME
    assert frame["vars"] == {"Level": 35}
    assert frame["wait"] is False


async def test_volume_up_and_down_are_steps_of_the_volume_command(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The volume is 35 percent; one step of Home Assistant is 10 percent."""
    entity_id = media_player(hass, agent)

    await hass.services.async_call(
        "media_player", "volume_up", {"entity_id": entity_id}, blocking=True
    )
    assert agent.runs[-1]["command"] == COMMAND_MEDIA_VOLUME
    assert agent.runs[-1]["vars"] == {"Level": 45}

    await hass.services.async_call(
        "media_player", "volume_down", {"entity_id": entity_id}, blocking=True
    )
    assert agent.runs[-1]["vars"] == {"Level": 25}


@pytest.mark.parametrize("muted", [True, False])
async def test_mute_runs_the_mute_command(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry, muted: bool
) -> None:
    """The flag travels as a boolean."""
    await hass.services.async_call(
        "media_player",
        "volume_mute",
        {"entity_id": media_player(hass, agent), "is_volume_muted": muted},
        blocking=True,
    )

    frame = agent.runs[-1]
    assert frame["command"] == COMMAND_MEDIA_MUTE
    assert frame["vars"] == {"Muted": muted}
    assert frame["wait"] is False


async def test_a_refused_control_becomes_a_home_assistant_error(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The agent says no: the automation sees the reason."""
    agent.run_errors[COMMAND_MEDIA_CONTROL] = ("no_session", "Nobody is logged in.")

    with pytest.raises(HomeAssistantError) as error:
        await hass.services.async_call(
            "media_player",
            "media_pause",
            {"entity_id": media_player(hass, agent)},
            blocking=True,
        )

    assert error.value.translation_key == "command_failed"
    placeholders = error.value.translation_placeholders
    assert placeholders is not None
    assert placeholders["command"] == COMMAND_MEDIA_CONTROL
    assert placeholders["message"] == "Nobody is logged in."


# -- volume ----------------------------------------------------------------------------


async def test_the_player_shows_the_master_volume_and_mute(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The audio metrics are the volume of the player."""
    entity_id = media_player(hass, agent)
    assert attribute_of(hass, entity_id, "volume_level") == pytest.approx(0.35)
    assert attribute_of(hass, entity_id, "is_volume_muted") is False

    await push(agent, {"audio.volume": 80, "audio.muted": True})
    await agent.wait_for(
        lambda: attribute_of(hass, entity_id, "is_volume_muted") is True
    )

    assert attribute_of(hass, entity_id, "volume_level") == pytest.approx(0.8)


# -- the position ----------------------------------------------------------------------


@pytest.mark.parametrize("age", [0, 10, 90])
async def test_the_position_time_is_related_to_the_clock_of_the_pc(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory, age: int
) -> None:
    """A position that was valid ``age`` seconds before the frame is that old here."""
    valid_at = SNAPSHOT_TIME - timedelta(seconds=age)
    fake_agent.set_manifest(media_manifest(fake_agent, rev=fake_agent.manifest["rev"]))
    fake_agent.snapshot = {
        **PLAYING,
        "media.position_updated_at": valid_at.strftime("%Y-%m-%dT%H:%M:%SZ"),
    }

    await entry_factory()
    entity_id = media_player(hass, fake_agent)
    await fake_agent.wait_for(lambda: state_of(hass, entity_id) == "playing")

    # The clock of the PC is of no interest: only how old the position is.
    expected = dt_util.utcnow() - timedelta(seconds=age)
    assert abs(position_time(hass, entity_id) - expected) < timedelta(seconds=5)


async def test_a_position_without_a_time_counts_as_valid_now(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """An agent that sends no time of the position: it is taken as fresh."""
    fake_agent.set_manifest(
        media_manifest(fake_agent, remove_metrics=["media.position_updated_at"])
    )
    fake_agent.snapshot = {
        key: value
        for key, value in PLAYING.items()
        if key != "media.position_updated_at"
    }

    await entry_factory()
    entity_id = media_player(hass, fake_agent)
    await fake_agent.wait_for(lambda: state_of(hass, entity_id) == "playing")

    assert abs(position_time(hass, entity_id) - dt_util.utcnow()) < timedelta(seconds=5)


async def test_a_new_position_renews_the_position_time(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """A jump in the track: the new position is valid at the time that comes with it."""
    entity_id = media_player(hass, agent)
    later = SNAPSHOT_TS + 60_000

    await push(
        agent,
        {"media.position": 300, "media.position_updated_at": "2025-11-14T13:20:55Z"},
        ts=later,
    )
    await agent.wait_for(lambda: attribute_of(hass, entity_id, "media_position") == 300)

    # 5 seconds before that frame.
    expected = dt_util.utcnow() - timedelta(seconds=5)
    assert abs(position_time(hass, entity_id) - expected) < timedelta(seconds=5)


async def test_a_change_of_state_alone_keeps_the_position_time(
    hass: HomeAssistant, agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """Metadata or volume change: Home Assistant keeps the old position time."""
    entity_id = media_player(hass, agent)
    before = position_time(hass, entity_id)

    await push(agent, {"media.artist": "Someone else"}, ts=SNAPSHOT_TS + 120_000)
    await agent.wait_for(
        lambda: attribute_of(hass, entity_id, "media_artist") == "Someone else"
    )

    assert position_time(hass, entity_id) == before
