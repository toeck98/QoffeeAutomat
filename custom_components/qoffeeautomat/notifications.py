"""Notifications from Home Assistant on the desktop of the PC (PLAN.md S10, S16, T702).

The agent provides the command ``agent_notify`` together with the feature ``notify``
(D-044). It shows a toast notification on the desktop of the logged-in user: a message,
an optional title, an optional picture (the agent fetches the address, with limits on
the size and the type) and up to three buttons. A press of a button comes back as the
agent event ``notification_action`` with the id of the button (``action``) and the
``tag`` of the notification, which fires the bus event ``qoffeeautomat_event``::

    triggers:
      - trigger: event
        event_type: qoffeeautomat_event
        event_data:
          type: notification_action
          data:
            action: open_door

This module builds the variables of the command and runs it. The notify entity
(``notify.py``) and the action ``qoffeeautomat.send_notification`` (``services.py``)
both use it; neither pastes anything into a script, the values travel as variables
(D-012).
"""

import re
from collections.abc import Mapping, Sequence

from homeassistant.exceptions import HomeAssistantError, ServiceValidationError

from .commands import async_run_command
from .const import (
    COMMAND_NOTIFY,
    DOMAIN,
    MAX_NOTIFICATION_ACTIONS,
    NOTIFICATION_ID_PATTERN,
    NOTIFY_REQUEST_TIMEOUT,
)
from .protocol import Scalar
from .runtime import QoffeeAutomatRuntimeData

# The names of the variables of the command; they are the parameters of the built-in
# action ``notify.toast`` of the agent.
VAR_TITLE = "Title"
VAR_MESSAGE = "Message"
VAR_IMAGE_URL = "ImageUrl"
VAR_TAG = "Tag"

_ID = re.compile(NOTIFICATION_ID_PATTERN)


def _valid_id(value: object) -> bool:
    """Return whether ``value`` can be the id of a button or a tag."""
    return isinstance(value, str) and _ID.fullmatch(value) is not None


def _invalid_id(what: str, value: object) -> ServiceValidationError:
    return ServiceValidationError(
        translation_domain=DOMAIN,
        translation_key="invalid_notification_id",
        translation_placeholders={"what": what, "value": str(value)[:80]},
    )


def build_notification_variables(
    *,
    message: str,
    title: str | None = None,
    image_url: str | None = None,
    tag: str | None = None,
    actions: Sequence[Mapping[str, str]] = (),
) -> dict[str, Scalar]:
    """Return the variables of the command ``agent_notify``.

    Every button is a mapping with ``action`` (the id that comes back in the event)
    and ``title`` (the label). Raises :class:`ServiceValidationError` for what the
    agent would refuse anyway, so the automation learns what is wrong with its own
    data: more than three buttons, an id that is not a plain id, a button without a
    label, the same id twice. The length of the texts and the picture are checked by
    the agent.
    """
    variables: dict[str, Scalar] = {VAR_MESSAGE: message}
    if title:
        variables[VAR_TITLE] = title
    if image_url:
        variables[VAR_IMAGE_URL] = image_url
    if tag:
        if not _valid_id(tag):
            raise _invalid_id("tag", tag)
        variables[VAR_TAG] = tag

    if len(actions) > MAX_NOTIFICATION_ACTIONS:
        raise ServiceValidationError(
            translation_domain=DOMAIN,
            translation_key="too_many_notification_actions",
            translation_placeholders={"max": str(MAX_NOTIFICATION_ACTIONS)},
        )
    seen: set[str] = set()
    for number, action in enumerate(actions, start=1):
        action_id = action.get("action")
        label = action.get("title")
        if not _valid_id(action_id):
            raise _invalid_id("button", action_id)
        if not isinstance(label, str) or not label.strip():
            raise ServiceValidationError(
                translation_domain=DOMAIN,
                translation_key="notification_action_without_title",
                translation_placeholders={"action": str(action_id)},
            )
        if action_id in seen:
            raise ServiceValidationError(
                translation_domain=DOMAIN,
                translation_key="duplicate_notification_action",
                translation_placeholders={"action": str(action_id)},
            )
        seen.add(str(action_id))
        variables[f"Action{number}Id"] = str(action_id)
        variables[f"Action{number}Title"] = label
    return variables


async def async_send_notification(
    runtime: QoffeeAutomatRuntimeData,
    *,
    message: str,
    title: str | None = None,
    image_url: str | None = None,
    tag: str | None = None,
    actions: Sequence[Mapping[str, str]] = (),
    user_id: str | None = None,
) -> None:
    """Show a notification on the PC and wait until the agent says what became of it.

    Raises a translated :class:`HomeAssistantError` when the agent cannot show it (no
    user is logged in, Windows does not show notifications from the agent, or the
    values are refused). A picture that cannot be fetched does not count: the agent
    shows the notification without it.
    """
    variables = build_notification_variables(
        message=message, title=title, image_url=image_url, tag=tag, actions=actions
    )
    manager = runtime.manifest_manager
    if not manager.loaded or all(
        command.id != COMMAND_NOTIFY for command in manager.manifest.commands
    ):
        raise HomeAssistantError(
            translation_domain=DOMAIN, translation_key="notify_unsupported"
        )
    result = await async_run_command(
        runtime,
        COMMAND_NOTIFY,
        variables,
        wait=True,
        user_id=user_id,
        request_timeout=NOTIFY_REQUEST_TIMEOUT,
    )
    if result.timed_out or result.exit_code not in (0, None):
        reason = "timed out" if result.timed_out else (result.stderr or "").strip()
        raise HomeAssistantError(
            translation_domain=DOMAIN,
            translation_key="notification_failed",
            translation_placeholders={"message": reason or "unknown reason"},
        )
