"""The action ``qoffeeautomat.run_command`` (PLAN.md S07, S10).

The action runs a command of the agent on the PC of the target device. Variables are
sent as values, never spliced into a script (D-012); the agent validates them. An
automation can build them from templates::

    actions:
      - action: qoffeeautomat.run_command
        target:
          device_id: "{{ device_id('sensor.gaming_pc_power_state') }}"
        data:
          command: set_display_profile
          variables:
            Profile: "{{ states('input_select.display_profile') }}"
        response_variable: result
      - action: notify.mobile_app_phone
        data:
          message: "Exit code {{ result.exit_code }}: {{ result.stdout }}"

The response of an action that waits holds ``run_id``, ``exit_code``, ``stdout``,
``stderr``, ``duration_ms``, ``timed_out`` and ``truncated``. A command the agent does
not know, or variables it rejects, raise ``ServiceValidationError``; an agent that is
off raises ``HomeAssistantError``. See also ``services.yaml``.

One call can also start the PC and wait for it (T712). ``wake`` sends the Wake-on-LAN
packets when the agent is not connected and makes the client retry every second;
``wait_for`` is ``none`` (the default), ``connected`` or ``session_ready`` (the metric
``session.ready`` is on); ``ready_timeout`` limits the whole preparation. When it
expires the call fails with an error that names the condition that was not reached and
the command does not run. Calls that wake or wait for the same PC take turns in call
order, so a second call never sends a second wake-up; a call without these fields runs
at once, as before. The action goes through the entry of the PC, so it works the same
for a PC that is reached directly or through a reverse link.

    actions:
      - action: qoffeeautomat.run_command
        target:
          device_id: "{{ device_id('sensor.gaming_pc_power_state') }}"
        data:
          command: start_mode
          variables:
            Mode: tv
          wake: true
          wait_for: session_ready
          ready_timeout: 240

The action ``qoffeeautomat.send_notification`` (T702) shows a notification on the
desktop of the PC, with an optional picture and up to three buttons. It needs the
feature ``notify`` of the agent. A press of a button fires the bus event
``qoffeeautomat_event`` with ``type: notification_action`` and the ``action`` of the
button; see ``notifications.py``.

    actions:
      - action: qoffeeautomat.send_notification
        target:
          device_id: "{{ device_id('sensor.gaming_pc_power_state') }}"
        data:
          title: Doorbell
          message: Somebody is at the door.
          image_url: http://homeassistant.local:8123/local/door.png
          tag: door
          actions:
            - action: open_door
              title: Open the door
            - action: ignore
              title: Ignore
"""

import asyncio
import logging
from collections.abc import Callable
from enum import StrEnum

import voluptuous as vol
from homeassistant.config_entries import ConfigEntryState
from homeassistant.const import ATTR_DEVICE_ID
from homeassistant.core import (
    HomeAssistant,
    ServiceCall,
    ServiceResponse,
    SupportsResponse,
    callback,
)
from homeassistant.exceptions import HomeAssistantError, ServiceValidationError
from homeassistant.helpers import config_validation as cv
from homeassistant.helpers import device_registry as dr

from .commands import async_run_command
from .const import (
    COMMAND_NOTIFY,
    DEFAULT_READY_TIMEOUT,
    DEFAULT_RUN_TIMEOUT,
    DOMAIN,
    MAX_OUTPUT_CHARS,
    MAX_READY_TIMEOUT,
    METRIC_SESSION_READY,
    WAIT_FOR_NONE,
    WAIT_FOR_OPTIONS,
    WAIT_FOR_SESSION_READY,
)
from .notifications import async_send_notification
from .protocol import MetricValue, RunResultData
from .routes import fleet_of
from .runtime import QoffeeAutomatRuntimeData, agent_runtime_of
from .wol import async_send_magic_packets

_LOGGER = logging.getLogger(__name__)

SERVICE_RUN_COMMAND = "run_command"
SERVICE_SEND_NOTIFICATION = "send_notification"
ATTR_MESSAGE = "message"
ATTR_TITLE = "title"
ATTR_IMAGE_URL = "image_url"
ATTR_TAG = "tag"
ATTR_ACTIONS = "actions"
ATTR_ACTION = "action"
ATTR_COMMAND = "command"
ATTR_VARIABLES = "variables"
ATTR_WAIT = "wait"
ATTR_TIMEOUT = "timeout"
ATTR_WAKE = "wake"
ATTR_WAIT_FOR = "wait_for"
ATTR_READY_TIMEOUT = "ready_timeout"

RUN_COMMAND_SCHEMA = vol.Schema(
    {
        vol.Required(ATTR_DEVICE_ID): vol.All(cv.ensure_list, [cv.string]),
        vol.Required(ATTR_COMMAND): cv.string,
        vol.Optional(ATTR_VARIABLES, default=dict): {
            cv.string: vol.Any(bool, int, float, str)
        },
        vol.Optional(ATTR_WAIT, default=True): cv.boolean,
        vol.Optional(ATTR_TIMEOUT, default=DEFAULT_RUN_TIMEOUT): vol.All(
            vol.Coerce(float), vol.Range(min=1, max=86400)
        ),
        vol.Optional(ATTR_WAKE, default=False): cv.boolean,
        vol.Optional(ATTR_WAIT_FOR, default=WAIT_FOR_NONE): vol.In(WAIT_FOR_OPTIONS),
        vol.Optional(ATTR_READY_TIMEOUT, default=DEFAULT_READY_TIMEOUT): vol.All(
            vol.Coerce(float), vol.Range(min=1, max=MAX_READY_TIMEOUT)
        ),
    }
)


SEND_NOTIFICATION_SCHEMA = vol.Schema(
    {
        vol.Required(ATTR_DEVICE_ID): vol.All(cv.ensure_list, [cv.string]),
        vol.Required(ATTR_MESSAGE): cv.string,
        vol.Optional(ATTR_TITLE): cv.string,
        vol.Optional(ATTR_IMAGE_URL): cv.url,
        vol.Optional(ATTR_TAG): cv.string,
        vol.Optional(ATTR_ACTIONS, default=list): vol.All(
            cv.ensure_list,
            [
                vol.Schema(
                    {
                        vol.Required(ATTR_ACTION): cv.string,
                        vol.Required(ATTR_TITLE): cv.string,
                    }
                )
            ],
        ),
    }
)


def _cap(text: str | None) -> tuple[str | None, bool]:
    """Return the text cut to the response limit and whether it was cut."""
    if text is not None and len(text) > MAX_OUTPUT_CHARS:
        return text[:MAX_OUTPUT_CHARS], True
    return text, False


def build_response(result: RunResultData, *, wait: bool) -> ServiceResponse:
    """Return the response data of the action for a run result."""
    if not wait:
        return {"run_id": result.run_id}
    stdout, stdout_cut = _cap(result.stdout)
    stderr, stderr_cut = _cap(result.stderr)
    return {
        "run_id": result.run_id,
        "exit_code": result.exit_code,
        "stdout": stdout,
        "stderr": stderr,
        "duration_ms": result.duration_ms,
        "timed_out": result.timed_out,
        "truncated": stdout_cut or stderr_cut,
    }


def _runtime_of_device(
    hass: HomeAssistant, device_ids: list[str], command: str
) -> QoffeeAutomatRuntimeData:
    """Return the runtime data of the PC behind the target device.

    The PC is reached through an entry of its own or through the entry of a hub; the
    caller cannot tell which (S20).
    """
    if len(device_ids) != 1:
        raise ServiceValidationError(
            translation_domain=DOMAIN, translation_key="single_device_required"
        )
    device = dr.async_get(hass).async_get(device_ids[0])
    entry = None
    if device is not None:
        for entry_id in device.config_entries:
            candidate = hass.config_entries.async_get_entry(entry_id)
            if candidate is not None and candidate.domain == DOMAIN:
                entry = candidate
                break
    if device is None or entry is None:
        raise ServiceValidationError(
            translation_domain=DOMAIN,
            translation_key="device_not_found",
            translation_placeholders={"device_id": device_ids[0]},
        )
    if entry.state is not ConfigEntryState.LOADED:
        # The PC was off when Home Assistant started: the entry waits for it.
        raise HomeAssistantError(
            translation_domain=DOMAIN,
            translation_key="agent_offline",
            translation_placeholders={"command": command, "message": entry.title},
        )
    fleet = fleet_of(entry)
    if fleet is None:
        return agent_runtime_of(entry)
    agent_ids = [key for domain, key in device.identifiers if domain == DOMAIN]
    runtime = next(
        (found for key in agent_ids if (found := fleet.runtime_of(key)) is not None),
        None,
    )
    if runtime is None:
        # The device of the hub itself, or of a PC that the hub entry does not serve.
        raise ServiceValidationError(
            translation_domain=DOMAIN,
            translation_key="not_a_served_pc",
            translation_placeholders={"device_id": device_ids[0]},
        )
    return runtime


class Stage(StrEnum):
    """The condition a call is waiting for; names the translation of a timeout."""

    QUEUED = "queued"
    CONNECTED = "connected"
    SESSION_READY = "session_ready"


class _Signal:
    """A metric consumer that only tells that a value arrived."""

    def __init__(self, changed: asyncio.Event) -> None:
        self._changed = changed

    def handle_value(self, value: MetricValue) -> None:
        """Wake the waiter up so that it looks at the value again."""
        self._changed.set()


class PcWaiter:
    """Wake a PC and wait until it can run a command (T712).

    Calls that wake or wait for the same PC take turns in call order: the lock of the
    runtime data is fair, and a call that still waits for its turn when its time is
    over fails without having woken anything. The waiting time covers the whole
    preparation (turn, wake, connection, session). Call :meth:`release` once the
    command has run or failed.
    """

    def __init__(
        self,
        runtime: QoffeeAutomatRuntimeData,
        command: str,
        *,
        wake: bool,
        wait_for: str,
        ready_timeout: float,
    ) -> None:
        """Prepare the wait of a call for the PC of ``runtime``."""
        self._runtime = runtime
        self._command = command
        self._wake = wake
        self._wait_for = wait_for
        self._ready_timeout = ready_timeout
        self._acquired = False
        self.stage = Stage.QUEUED

    def validate(self) -> None:
        """Refuse a wait that cannot succeed, before anything is woken.

        The agent has to announce the metric ``session.ready``; an older one or a
        platform without sessions does not, and waiting for it would only time out.
        """
        manager = self._runtime.manifest_manager
        if (
            self._wait_for == WAIT_FOR_SESSION_READY
            and manager.loaded
            and all(
                metric.id != METRIC_SESSION_READY for metric in manager.manifest.metrics
            )
        ):
            raise ServiceValidationError(
                translation_domain=DOMAIN,
                translation_key="session_ready_unsupported",
                translation_placeholders=self._placeholders(),
            )

    def _placeholders(self) -> dict[str, str]:
        return {
            "name": self._runtime.name,
            "command": self._command,
            "timeout": f"{self._ready_timeout:g}",
        }

    async def async_prepare(self) -> None:
        """Wait for the turn, wake the PC if needed and wait for the condition.

        Raises :class:`HomeAssistantError` naming the condition that was not reached
        when the waiting time is over.
        """
        connection = self._runtime.connection
        try:
            async with asyncio.timeout(self._ready_timeout):
                await self._runtime.call_lock.acquire()
                self._acquired = True
                self.stage = Stage.CONNECTED
                if self._wake and not connection.online:
                    await self._async_wake()
                if self._wait_for != WAIT_FOR_NONE:
                    await self._async_until(lambda: connection.online, asyncio.Event())
                if self._wait_for == WAIT_FOR_SESSION_READY:
                    self.stage = Stage.SESSION_READY
                    await self._async_wait_session_ready()
        except TimeoutError:
            raise HomeAssistantError(
                translation_domain=DOMAIN,
                translation_key=f"ready_timeout_{self.stage}",
                translation_placeholders=self._placeholders(),
            ) from None

    def release(self) -> None:
        """Give the turn to the next call."""
        if self._acquired:
            self._acquired = False
            self._runtime.call_lock.release()

    async def _async_wake(self) -> None:
        """Send the magic packets and let the client retry every second."""
        client = self._runtime.client
        if not client.fast_retry_active:
            # Otherwise a button press or an earlier call woke the PC a moment ago.
            macs = self._runtime.macs()
            if not macs:
                raise ServiceValidationError(
                    translation_domain=DOMAIN, translation_key="wol_no_mac"
                )
            broadcast = self._runtime.broadcast()
            try:
                sent = await async_send_magic_packets(macs, broadcast)
            except (OSError, ValueError) as err:
                raise HomeAssistantError(
                    translation_domain=DOMAIN,
                    translation_key="wol_failed",
                    translation_placeholders={"error": str(err)},
                ) from err
            _LOGGER.debug("Sent %s magic packets to %s", sent, broadcast)
        client.start_fast_retry(self._ready_timeout)

    async def _async_wait_session_ready(self) -> None:
        """Wait until the metric ``session.ready`` is on.

        The metric is subscribed for the duration of the wait, also when its entity
        is disabled; the value of a subscription that exists already is remembered.
        """
        connection = self._runtime.connection
        subscriptions = self._runtime.subscriptions
        changed = asyncio.Event()
        remove = subscriptions.register(METRIC_SESSION_READY, _Signal(changed))
        try:
            await self._async_until(
                lambda: (
                    connection.online
                    and subscriptions.last_value(METRIC_SESSION_READY) is True
                ),
                changed,
            )
        finally:
            remove()

    async def _async_until(
        self, predicate: Callable[[], bool], changed: asyncio.Event
    ) -> None:
        """Wait until ``predicate()`` is true; ``changed`` tells that it may be."""
        remove = self._runtime.connection.add_listener(changed.set)
        try:
            while not predicate():
                changed.clear()
                await changed.wait()
        finally:
            remove()


async def _async_run_command(call: ServiceCall) -> ServiceResponse:
    """Handle ``qoffeeautomat.run_command``."""
    command: str = call.data[ATTR_COMMAND]
    wait: bool = call.data[ATTR_WAIT]
    runtime = _runtime_of_device(call.hass, call.data[ATTR_DEVICE_ID], command)
    # The values may be secret: only the names are logged.
    _LOGGER.debug(
        "Running %s with variables %s", command, sorted(call.data[ATTR_VARIABLES])
    )
    waiter: PcWaiter | None = None
    if call.data[ATTR_WAKE] or call.data[ATTR_WAIT_FOR] != WAIT_FOR_NONE:
        waiter = PcWaiter(
            runtime,
            command,
            wake=call.data[ATTR_WAKE],
            wait_for=call.data[ATTR_WAIT_FOR],
            ready_timeout=call.data[ATTR_READY_TIMEOUT],
        )
        waiter.validate()
    try:
        if waiter is not None:
            await waiter.async_prepare()
        result = await async_run_command(
            runtime,
            command,
            call.data[ATTR_VARIABLES],
            wait=wait,
            user_id=call.context.user_id,
            request_timeout=call.data[ATTR_TIMEOUT],
        )
    finally:
        if waiter is not None:
            waiter.release()
    return build_response(result, wait=wait)


async def _async_send_notification(call: ServiceCall) -> None:
    """Handle ``qoffeeautomat.send_notification``."""
    runtime = _runtime_of_device(call.hass, call.data[ATTR_DEVICE_ID], COMMAND_NOTIFY)
    # The texts may be private: only what was asked for is logged, not the values.
    _LOGGER.debug(
        "Sending a notification with %s buttons", len(call.data[ATTR_ACTIONS])
    )
    await async_send_notification(
        runtime,
        message=call.data[ATTR_MESSAGE],
        title=call.data.get(ATTR_TITLE),
        image_url=call.data.get(ATTR_IMAGE_URL),
        tag=call.data.get(ATTR_TAG),
        actions=call.data[ATTR_ACTIONS],
        user_id=call.context.user_id,
    )


@callback
def async_setup_services(hass: HomeAssistant) -> None:
    """Register the actions of the integration."""
    hass.services.async_register(
        DOMAIN,
        SERVICE_RUN_COMMAND,
        _async_run_command,
        schema=RUN_COMMAND_SCHEMA,  # type: ignore[arg-type]
        supports_response=SupportsResponse.OPTIONAL,
    )
    hass.services.async_register(
        DOMAIN,
        SERVICE_SEND_NOTIFICATION,
        _async_send_notification,
        schema=SEND_NOTIFICATION_SCHEMA,  # type: ignore[arg-type]
    )
