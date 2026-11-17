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

# Delay that collects changes of the enabled metric entities into one ``subscribe``.
SUBSCRIBE_DEBOUNCE_SECONDS: Final = 0.5
# How long the setup waits for the first connection after the probe succeeded.
FIRST_CONNECT_TIMEOUT_SECONDS: Final = 15.0
