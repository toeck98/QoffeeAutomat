"""Repair issues of the QoffeeAutomat integration (PLAN.md S10, S20).

* The protocol mismatch: the agent announced a QoffeeLink protocol range that does not
  overlap with the one of this integration. The user has to update the agent or the
  integration; afterwards a repair flow reloads the config entry, and a successful setup
  removes the issue.
* The route (T914): a PC that has an entry of its own is also listed by a hub that Home
  Assistant is connected to (``direct_route``), or the hub forbids direct links to a PC
  that has an entry of its own (``direct_link_denied``). Both offer to switch the PC to
  the hub route; the switch keeps entity ids and customisations (``routes.py``). The
  user can also ignore the first one and stay on the direct link, which is the default.
"""

from typing import Final

from homeassistant.components.repairs import ConfirmRepairFlow, RepairsFlow
from homeassistant.components.repairs.models import RepairsFlowResult
from homeassistant.core import HomeAssistant, callback
from homeassistant.helpers import issue_registry as ir

from .const import DOMAIN
from .protocol import PROTOCOL_VERSION
from .routes import (
    REASON_HUB_UNAVAILABLE,
    RouteSwitchError,
    async_switch_to_hub,
    hub_entry_knowing,
)

ISSUE_PROTOCOL_MISMATCH: Final = "protocol_mismatch"
ISSUE_DIRECT_ROUTE: Final = "direct_route"
ISSUE_DIRECT_LINK_DENIED: Final = "direct_link_denied"
DATA_ENTRY_ID: Final = "entry_id"
DATA_HUB_ENTRY_ID: Final = "hub_entry_id"
DATA_AGENT_ID: Final = "agent_id"


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


def direct_route_issue_id(hub_entry_id: str, agent_id: str) -> str:
    """Return the issue id for a PC that a hub entry skips because it has its own."""
    return f"{ISSUE_DIRECT_ROUTE}_{hub_entry_id}_{agent_id}"


@callback
def async_create_direct_route_issue(
    hass: HomeAssistant,
    hub_entry_id: str,
    hub_name: str,
    agent_id: str,
    agent_name: str,
) -> None:
    """Offer to move a PC from its direct link to the hub that also lists it."""
    ir.async_create_issue(
        hass,
        DOMAIN,
        direct_route_issue_id(hub_entry_id, agent_id),
        data={DATA_HUB_ENTRY_ID: hub_entry_id, DATA_AGENT_ID: agent_id},
        is_fixable=True,
        is_persistent=False,
        severity=ir.IssueSeverity.WARNING,
        translation_key=ISSUE_DIRECT_ROUTE,
        translation_placeholders={"name": agent_name, "hub": hub_name},
    )


@callback
def async_delete_direct_route_issue(
    hass: HomeAssistant, hub_entry_id: str, agent_id: str
) -> None:
    """Remove the issue of one PC of a hub; does nothing if there is none."""
    ir.async_delete_issue(hass, DOMAIN, direct_route_issue_id(hub_entry_id, agent_id))


@callback
def async_delete_direct_route_issues(hass: HomeAssistant, hub_entry_id: str) -> None:
    """Remove the route issues of all PCs of a hub (the hub entry was removed)."""
    prefix = f"{ISSUE_DIRECT_ROUTE}_{hub_entry_id}_"
    for domain, issue_id in list(ir.async_get(hass).issues):
        if domain == DOMAIN and issue_id.startswith(prefix):
            ir.async_delete_issue(hass, domain, issue_id)


def direct_link_denied_issue_id(entry_id: str) -> str:
    """Return the issue id of a direct link that the hub of the PC forbids."""
    return f"{ISSUE_DIRECT_LINK_DENIED}_{entry_id}"


@callback
def async_create_direct_link_denied_issue(
    hass: HomeAssistant, entry_id: str, agent_id: str, name: str
) -> None:
    """Tell the user that the hub of the PC does not allow the direct link (D-029)."""
    ir.async_create_issue(
        hass,
        DOMAIN,
        direct_link_denied_issue_id(entry_id),
        data={DATA_AGENT_ID: agent_id},
        is_fixable=True,
        is_persistent=False,
        severity=ir.IssueSeverity.WARNING,
        translation_key=ISSUE_DIRECT_LINK_DENIED,
        translation_placeholders={"name": name},
    )


@callback
def async_delete_direct_link_denied_issue(hass: HomeAssistant, entry_id: str) -> None:
    """Remove the denied direct link issue; does nothing if there is none."""
    ir.async_delete_issue(hass, DOMAIN, direct_link_denied_issue_id(entry_id))


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


class SwitchToHubRepairFlow(ConfirmRepairFlow):
    """Move a PC from its direct link to the hub route, keeping its entities."""

    def __init__(self, agent_id: str, hub_entry_id: str | None) -> None:
        """Create the flow for a PC; without ``hub_entry_id`` the hub is looked up."""
        super().__init__()
        self._agent_id = agent_id
        self._hub_entry_id = hub_entry_id

    async def async_step_confirm(
        self, user_input: dict[str, str] | None = None
    ) -> RepairsFlowResult:
        """Switch the route when confirmed; abort with a reason if that fails."""
        if user_input is not None:
            hub_entry_id = self._hub_entry_id
            if hub_entry_id is None:
                hub = hub_entry_knowing(self.hass, self._agent_id)
                if hub is None:
                    return self.async_abort(reason=REASON_HUB_UNAVAILABLE)
                hub_entry_id = hub.entry_id
            try:
                await async_switch_to_hub(self.hass, hub_entry_id, self._agent_id)
            except RouteSwitchError as err:
                return self.async_abort(reason=err.reason)
        return await super().async_step_confirm(user_input)


async def async_create_fix_flow(
    hass: HomeAssistant,
    issue_id: str,
    data: dict[str, str | int | float | None] | None,
) -> RepairsFlow:
    """Create the repair flow of an issue."""
    values = data or {}
    entry_id = values.get(DATA_ENTRY_ID)
    agent_id = values.get(DATA_AGENT_ID)
    hub_entry_id = values.get(DATA_HUB_ENTRY_ID)
    if issue_id.startswith(ISSUE_PROTOCOL_MISMATCH) and isinstance(entry_id, str):
        return ProtocolMismatchRepairFlow(entry_id)
    if isinstance(agent_id, str):
        if issue_id.startswith(ISSUE_DIRECT_ROUTE) and isinstance(hub_entry_id, str):
            return SwitchToHubRepairFlow(agent_id, hub_entry_id)
        if issue_id.startswith(ISSUE_DIRECT_LINK_DENIED):
            return SwitchToHubRepairFlow(agent_id, None)
    msg = f"No repair flow for the issue {issue_id}"
    raise ValueError(msg)
