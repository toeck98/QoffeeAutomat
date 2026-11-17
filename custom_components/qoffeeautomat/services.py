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
"""

import logging

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
from .const import DEFAULT_RUN_TIMEOUT, DOMAIN, MAX_OUTPUT_CHARS
from .protocol import RunResultData
from .runtime import QoffeeAutomatRuntimeData

_LOGGER = logging.getLogger(__name__)

SERVICE_RUN_COMMAND = "run_command"
ATTR_COMMAND = "command"
ATTR_VARIABLES = "variables"
ATTR_WAIT = "wait"
ATTR_TIMEOUT = "timeout"

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
    """Return the runtime data of the agent behind the target device."""
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
    if entry is None:
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
    runtime: QoffeeAutomatRuntimeData = entry.runtime_data
    return runtime


async def _async_run_command(call: ServiceCall) -> ServiceResponse:
    """Handle ``qoffeeautomat.run_command``."""
    command: str = call.data[ATTR_COMMAND]
    wait: bool = call.data[ATTR_WAIT]
    runtime = _runtime_of_device(call.hass, call.data[ATTR_DEVICE_ID], command)
    # The values may be secret: only the names are logged.
    _LOGGER.debug(
        "Running %s with variables %s", command, sorted(call.data[ATTR_VARIABLES])
    )
    result = await async_run_command(
        runtime,
        command,
        call.data[ATTR_VARIABLES],
        wait=wait,
        user_id=call.context.user_id,
        request_timeout=call.data[ATTR_TIMEOUT],
    )
    return build_response(result, wait=wait)


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
