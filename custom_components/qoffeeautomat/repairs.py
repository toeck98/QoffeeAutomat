"""Repair issues of the QoffeeAutomat integration (PLAN.md S10).

The only issue is the protocol mismatch: the agent announced a QoffeeLink protocol
range that does not overlap with the one of this integration. The user has to update
the agent or the integration; afterwards a repair flow reloads the config entry, and a
successful setup removes the issue.
"""

from typing import Final

from homeassistant.components.repairs import ConfirmRepairFlow, RepairsFlow
from homeassistant.components.repairs.models import RepairsFlowResult
from homeassistant.core import HomeAssistant, callback
from homeassistant.helpers import issue_registry as ir

from .const import DOMAIN
from .protocol import PROTOCOL_VERSION

ISSUE_PROTOCOL_MISMATCH: Final = "protocol_mismatch"
DATA_ENTRY_ID: Final = "entry_id"


def protocol_mismatch_issue_id(entry_id: str) -> str:
    """Return the issue id of the protocol mismatch of one config entry."""
    return f"{ISSUE_PROTOCOL_MISMATCH}_{entry_id}"


@callback
def async_create_protocol_mismatch_issue(
    hass: HomeAssistant,
    entry_id: str,
    name: str,
    agent_range: tuple[int, int] | None,
) -> None:
    """Tell the user that the agent and the integration share no protocol version."""
    shown_range = f"{agent_range[0]}-{agent_range[1]}" if agent_range else "?"
    ir.async_create_issue(
        hass,
        DOMAIN,
        protocol_mismatch_issue_id(entry_id),
        data={DATA_ENTRY_ID: entry_id},
        is_fixable=True,
        is_persistent=False,
        severity=ir.IssueSeverity.ERROR,
        translation_key=ISSUE_PROTOCOL_MISMATCH,
        translation_placeholders={
            "name": name,
            "agent_protocol": shown_range,
            "integration_protocol": f"1-{PROTOCOL_VERSION}",
        },
    )


@callback
def async_delete_protocol_mismatch_issue(hass: HomeAssistant, entry_id: str) -> None:
    """Remove the protocol mismatch issue; does nothing if there is none."""
    ir.async_delete_issue(hass, DOMAIN, protocol_mismatch_issue_id(entry_id))


class ProtocolMismatchRepairFlow(ConfirmRepairFlow):
    """Reload the config entry once the agent or the integration was updated."""

    def __init__(self, entry_id: str) -> None:
        """Create the flow for the config entry ``entry_id``."""
        super().__init__()
        self._entry_id = entry_id

    async def async_step_confirm(
        self, user_input: dict[str, str] | None = None
    ) -> RepairsFlowResult:
        """Schedule the reload when the user confirmed that the update is done.

        The reload runs after the flow ended; if the versions still do not match, the
        setup creates the issue again.
        """
        if user_input is not None:
            self.hass.config_entries.async_schedule_reload(self._entry_id)
        return await super().async_step_confirm(user_input)


async def async_create_fix_flow(
    hass: HomeAssistant,
    issue_id: str,
    data: dict[str, str | int | float | None] | None,
) -> RepairsFlow:
    """Create the repair flow of an issue."""
    entry_id = data.get(DATA_ENTRY_ID) if data else None
    if issue_id.startswith(ISSUE_PROTOCOL_MISMATCH) and isinstance(entry_id, str):
        return ProtocolMismatchRepairFlow(entry_id)
    msg = f"No repair flow for the issue {issue_id}"
    raise ValueError(msg)
