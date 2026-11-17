"""Constants for the QoffeeAutomat integration."""

from typing import Final

DOMAIN: Final = "qoffeeautomat"
NAME: Final = "QoffeeAutomat"

# Keys of the config entry data (PLAN.md S10): the entry stores host, port, the link
# token and the pinned SHA-256 fingerprint of the agent certificate.
CONF_TOKEN: Final = "token"  # noqa: S105
CONF_FINGERPRINT: Final = "fingerprint"
CONF_CODE: Final = "code"

DEFAULT_PORT: Final = 8481

# Stored in the config entry data: the boot id of the agent at the last connection. A
# different value on connect means the PC was started in between (PLAN.md S10).
CONF_LAST_BOOT_ID: Final = "last_boot_id"

# Wake-on-LAN (PLAN.md S10, S16, T703). The config entry data keep the MAC addresses
# that the manifest announced, so the button can wake a PC that is off; the option holds
# the broadcast address the magic packets go to.
CONF_MACS: Final = "macs"
CONF_BROADCAST: Final = "broadcast"
DEFAULT_BROADCAST: Final = "255.255.255.255"
# UDP port of the magic packet (the "discard" port that nearly every tool uses).
WOL_PORT: Final = 9
# After a press the client retries the connection every second for this long.
WOL_FAST_RETRY_SECONDS: Final = 300.0
# Key of the Wake-on-LAN button in the unique id; keys that start with ``_`` belong to
# entities the integration creates itself and are never removed as stale.
WOL_KEY: Final = "_wake_on_lan"

# Reverse link for roaming devices (PLAN.md D-043, ADR 0003, T710). An entry with
# ``reverse: true`` has no host, port, link token or fingerprint: the agent dials the
# endpoint below with a reverse token, of which the entry keeps only the SHA-256 hash.
CONF_REVERSE: Final = "reverse"
CONF_REVERSE_TOKEN_HASH: Final = "reverse_token_hash"  # noqa: S105
CONF_REVERSE_URL: Final = "url"
REVERSE_LINK_PATH: Final = "/api/qoffeeautomat/reverse/v1"
REVERSE_TOKEN_PREFIX: Final = "qalr_"  # noqa: S105
REVERSE_ENROLLMENT_PREFIX: Final = "qalr1."
# How long the config flow waits for the agent to use the enrollment string.
REVERSE_ENROLL_TIMEOUT_SECONDS: Final = 900.0
# How long a connection that has not sent ``hello`` yet may take at the endpoint.
REVERSE_HELLO_TIMEOUT_SECONDS: Final = 10.0

MANUFACTURER: Final = "QoffeeAutomat"
MODEL: Final = "QoffeeAutomat Agent"

# Bus event fired for every agent event and for ``booted`` (PLAN.md S10). Its data are
# ``device_id`` (the Home Assistant device), ``type`` and ``data``.
EVENT_QOFFEEAUTOMAT: Final = "qoffeeautomat_event"
# Event type that Home Assistant raises itself when the boot id of the PC changed.
EVENT_TYPE_BOOTED: Final = "booted"

# How long the ``run_command`` action waits for the result unless it is told otherwise.
DEFAULT_RUN_TIMEOUT: Final = 60.0
# The agent caps captured output at 64 KiB per stream; the action response is capped
# at the same size and reports ``truncated`` when it had to cut.
MAX_OUTPUT_CHARS: Final = 65536

# Wake, wait and run in one call (PLAN.md S10, T712): ``wait_for`` of the action, the
# default and maximum waiting time, and the metric that says a user session can run
# commands.
WAIT_FOR_NONE: Final = "none"
WAIT_FOR_CONNECTED: Final = "connected"
WAIT_FOR_SESSION_READY: Final = "session_ready"
WAIT_FOR_OPTIONS: Final = (WAIT_FOR_NONE, WAIT_FOR_CONNECTED, WAIT_FOR_SESSION_READY)
DEFAULT_READY_TIMEOUT: Final = 180.0
MAX_READY_TIMEOUT: Final = 3600.0
METRIC_SESSION_READY: Final = "session.ready"

# Optional features that the manifest lists (PLAN.md S07, S10, D-038). An entity that
# needs the cooperation of the agent exists only while the manifest lists its feature.
FEATURE_NOTIFY: Final = "notify"
FEATURE_MEDIA: Final = "media"
FEATURE_UPDATE: Final = "update"
# Keys of the feature entities in the unique id; they start with ``_`` like the other
# entities the integration creates itself, so the stale cleanup never touches them.
NOTIFY_KEY: Final = "_notify"
MEDIA_PLAYER_KEY: Final = "_media_player"
UPDATE_KEY: Final = "_update"

# Notifications on the desktop of the PC (PLAN.md S10, S16, T702). The agent provides
# the command ``agent_notify`` together with the feature ``notify``; a press of a button
# comes back as the agent event ``notification_action`` (``action`` and ``tag``).
COMMAND_NOTIFY: Final = "agent_notify"
EVENT_TYPE_NOTIFICATION_ACTION: Final = "notification_action"
MAX_NOTIFICATION_ACTIONS: Final = 3
# The agent fetches the picture and shows the toast, which can take a few seconds.
NOTIFY_REQUEST_TIMEOUT: Final = 45.0
# Same pattern as the agent: the id of a button and the tag of a notification.
NOTIFICATION_ID_PATTERN: Final = r"^[A-Za-z0-9_.:-]{1,64}$"

# The media player (PLAN.md S10, S16, T716, D-038): composed from metrics and commands
# that the agent announces together with the feature ``media``.
COMMAND_MEDIA_CONTROL: Final = "agent_media_control"
COMMAND_MEDIA_VOLUME: Final = "agent_media_volume"
COMMAND_MEDIA_MUTE: Final = "agent_media_mute"
METRIC_MEDIA_STATE: Final = "media.state"
METRIC_MEDIA_TITLE: Final = "media.title"
METRIC_MEDIA_ARTIST: Final = "media.artist"
METRIC_MEDIA_ALBUM: Final = "media.album"
METRIC_MEDIA_APP: Final = "media.app"
METRIC_MEDIA_DURATION: Final = "media.duration"
METRIC_MEDIA_POSITION: Final = "media.position"
METRIC_MEDIA_POSITION_UPDATED_AT: Final = "media.position_updated_at"
METRIC_AUDIO_VOLUME: Final = "audio.volume"
METRIC_AUDIO_MUTED: Final = "audio.muted"

# The update entity (PLAN.md S10, S13, S16, T704, D-046): composed from the metrics and
# the command that the agent announces together with the feature ``update``. The agent
# checks GitHub once a day (or never, if the user switched that off) and reports what it
# found; the entity only shows it and runs the command.
COMMAND_AGENT_UPDATE: Final = "agent_update"
METRIC_AGENT_VERSION: Final = "agent.version"
METRIC_AGENT_LATEST_VERSION: Final = "agent.latest_version"
METRIC_AGENT_RELEASE_URL: Final = "agent.release_url"
METRIC_AGENT_RELEASE_NOTES: Final = "agent.release_notes"
# The command downloads the installer before it answers. Its timeout on the agent is
# 900 seconds; the request waits a little longer.
UPDATE_REQUEST_TIMEOUT: Final = 960.0
# The longest ``release_summary`` that Home Assistant keeps.
MAX_RELEASE_SUMMARY_CHARS: Final = 255

# Delay that collects changes of the enabled metric entities into one ``subscribe``.
SUBSCRIBE_DEBOUNCE_SECONDS: Final = 0.5
# How long the setup waits for the first connection after the probe succeeded.
FIRST_CONNECT_TIMEOUT_SECONDS: Final = 15.0
