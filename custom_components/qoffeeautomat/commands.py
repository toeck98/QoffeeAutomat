"""Run commands on the agent and turn its errors into Home Assistant errors (S07, S10).

Both the command entities and the ``qoffeeautomat.run_command`` action go through
:func:`async_run_command`. Errors the user can fix (unknown or disabled command,
invalid variables) become :class:`ServiceValidationError`, everything else
:class:`HomeAssistantError`. Both carry a translation key and the message of the agent
in the ``message`` placeholder.
"""

from collections.abc import Mapping

from homeassistant.exceptions import HomeAssistantError, ServiceValidationError

from .client import (
    NotConnectedError,
    QoffeeLinkError,
    RequestError,
    RequestTimeoutError,
)
from .const import DOMAIN
from .protocol import (
    ERROR_AGENT_OFFLINE,
    ERROR_BAD_REQUEST,
    ERROR_COMMAND_DISABLED,
    ERROR_INVALID_VARS,
    ERROR_UNKNOWN_COMMAND,
    RunResultData,
    Scalar,
)
from .runtime import QoffeeAutomatRuntimeData

# Error codes of the agent that say the request itself is wrong.
VALIDATION_ERROR_CODES = frozenset(
    {
        ERROR_BAD_REQUEST,
        ERROR_COMMAND_DISABLED,
        ERROR_INVALID_VARS,
        ERROR_UNKNOWN_COMMAND,
    }
)


def translate_error(command: str, error: QoffeeLinkError) -> HomeAssistantError:
    """Return the Home Assistant error for an error of the client."""
    placeholders = {"command": command, "message": str(error)}
    if isinstance(error, RequestError):
        placeholders["message"] = error.message
        if error.code in VALIDATION_ERROR_CODES:
            return ServiceValidationError(
                translation_domain=DOMAIN,
                translation_key="command_rejected",
                translation_placeholders=placeholders,
            )
        # The hub answers for an agent that is off (route through the hub, S20).
        key = "agent_offline" if error.code == ERROR_AGENT_OFFLINE else "command_failed"
    elif isinstance(error, NotConnectedError):
        key = "agent_offline"
    elif isinstance(error, RequestTimeoutError):
        key = "command_timeout"
    else:
        key = "command_failed"
    return HomeAssistantError(
        translation_domain=DOMAIN,
        translation_key=key,
        translation_placeholders=placeholders,
    )


async def async_run_command(
    runtime: QoffeeAutomatRuntimeData,
    command: str,
    variables: Mapping[str, Scalar] | None = None,
    *,
    wait: bool,
    user_id: str | None = None,
    request_timeout: float | None = None,
) -> RunResultData:
    """Run ``command`` on the agent; raises a translated Home Assistant error."""
    try:
        return await runtime.client.run_command(
            command,
            variables,
            wait=wait,
            user_id=user_id,
            request_timeout=request_timeout,
        )
    except QoffeeLinkError as err:
        raise translate_error(command, err) from err
