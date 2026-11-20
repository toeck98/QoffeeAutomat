"""Tests of the config flow (T502); the flow module is covered completely."""

import dataclasses
import json
from collections.abc import Iterator
from ipaddress import ip_address
from pathlib import Path
from types import SimpleNamespace
from typing import Any
from unittest.mock import AsyncMock, MagicMock, patch

import pytest
from homeassistant import config_entries
from homeassistant.config_entries import ConfigEntryState
from homeassistant.const import CONF_HOST, CONF_PORT
from homeassistant.core import HomeAssistant
from homeassistant.data_entry_flow import FlowResult, FlowResultType
from homeassistant.helpers.service_info.zeroconf import ZeroconfServiceInfo
from pytest_homeassistant_custom_component.common import MockConfigEntry

from custom_components.qoffeeautomat import config_flow
from custom_components.qoffeeautomat.client import (
    AuthenticationError,
    CannotConnectError,
    CertificateMismatchError,
    DirectLinkDeniedError,
    PairingCodeExpiredError,
    PairingCodeInvalidError,
    QoffeeLinkClient,
    UnexpectedResponseError,
    UnsupportedProtocolError,
)
from custom_components.qoffeeautomat.const import (
    CONF_CODE,
    CONF_FINGERPRINT,
    CONF_HUB,
    CONF_TOKEN,
    DEFAULT_HUB_PORT,
    DEFAULT_PORT,
    DOMAIN,
)
from custom_components.qoffeeautomat.protocol import (
    FleetHello,
    Hello,
    PairResponse,
    parse_fleet_message,
    parse_hub_pair_response,
    parse_message,
)
from tests_integration.fake_agent import PAIRING_CODE, FakeAgent, load_fixture
from tests_integration.fake_hub import HUB_PAIRING_CODE, FakeHub

FETCH = "custom_components.qoffeeautomat.config_flow.async_fetch_fingerprint"
PAIR = "custom_components.qoffeeautomat.config_flow.async_pair"
PROBE = "custom_components.qoffeeautomat.config_flow._probe"
HUB_PAIR = "custom_components.qoffeeautomat.config_flow.async_hub_pair"
PROBE_HUB = "custom_components.qoffeeautomat.config_flow._probe_hub"
HUB_KNOWING = "custom_components.qoffeeautomat.config_flow.hub_entry_knowing"

FINGERPRINT = ":".join(["AB"] * 32)
HELLO = parse_message(load_fixture("hello.json"))
assert isinstance(HELLO, Hello)
AGENT_ID = HELLO.agent.id
TOKEN = load_fixture("pair_response.json")["token"]
PAIRED = PairResponse(token=TOKEN, agent_id=AGENT_ID, agent_name="Gaming-PC")
OTHER_HELLO = dataclasses.replace(
    HELLO, agent=dataclasses.replace(HELLO.agent, id="another-agent")
)
HUB_HELLO = parse_fleet_message(load_fixture("hub/fleet_hello.json"))
assert isinstance(HUB_HELLO, FleetHello)
HUB_ID = HUB_HELLO.hub.id
HUB_PAIRED = parse_hub_pair_response(load_fixture("hub/pair_response.json"))
OTHER_HUB_PAIRED = dataclasses.replace(HUB_PAIRED, hub_id="another-hub")
OTHER_HUB_HELLO = dataclasses.replace(
    HUB_HELLO, hub=dataclasses.replace(HUB_HELLO.hub, id="another-hub")
)

INTEGRATION = (
    Path(__file__).resolve().parents[1] / "custom_components" / "qoffeeautomat"
)


@pytest.fixture(autouse=True)
def _custom_integrations(enable_custom_integrations: None) -> None:
    """Let Home Assistant find the integration in ``custom_components``."""


@pytest.fixture(name="setup_entry")
def setup_entry_fixture() -> Iterator[AsyncMock]:
    """Replace the entry setup, which is not part of this task."""
    with patch(
        "custom_components.qoffeeautomat.async_setup_entry", return_value=True
    ) as mock:
        yield mock


def make_entry(**data: Any) -> MockConfigEntry:
    """Return a config entry of the agent with the given data overrides."""
    return MockConfigEntry(
        domain=DOMAIN,
        unique_id=AGENT_ID,
        title="Gaming-PC",
        data={
            CONF_HOST: "192.168.1.20",
            CONF_PORT: DEFAULT_PORT,
            CONF_TOKEN: TOKEN,
            CONF_FINGERPRINT: FINGERPRINT,
            **data,
        },
    )


def make_hub_entry(**data: Any) -> MockConfigEntry:
    """Return a config entry of a hub with the given data overrides."""
    return MockConfigEntry(
        domain=DOMAIN,
        unique_id=HUB_ID,
        title="QoffeeAutomat Hub",
        data={
            CONF_HUB: True,
            CONF_HOST: "192.168.1.30",
            CONF_PORT: DEFAULT_HUB_PORT,
            CONF_TOKEN: HUB_PAIRED.token,
            CONF_FINGERPRINT: FINGERPRINT,
            **data,
        },
    )


def make_discovery(
    *,
    host: str = "192.168.1.20",
    port: int | None = DEFAULT_PORT,
    properties: dict[str, str] | None = None,
) -> ZeroconfServiceInfo:
    """Return a zeroconf announcement of an agent."""
    address = ip_address(host)
    return ZeroconfServiceInfo(
        ip_address=address,
        ip_addresses=[address],
        port=port,
        hostname="gaming-pc.local.",
        type="_qoffeeautomat._tcp.local.",
        name="Gaming-PC._qoffeeautomat._tcp.local.",
        properties=(
            {"id": AGENT_ID, "name": "Gaming-PC", "v": "1.0.0", "pv": "1-1"}
            if properties is None
            else properties
        ),
    )


async def start_menu(hass: HomeAssistant) -> FlowResult:
    """Start the flow as the user; it asks how the PC is reached."""
    return await hass.config_entries.flow.async_init(
        DOMAIN, context={"source": config_entries.SOURCE_USER}
    )


async def start_user(hass: HomeAssistant) -> FlowResult:
    """Start the flow as the user and choose that Home Assistant connects to the PC."""
    result = await start_menu(hass)
    assert result["type"] is FlowResultType.MENU
    assert result["menu_options"] == ["manual", "reverse", "hub"]
    return await hass.config_entries.flow.async_configure(
        result["flow_id"], {"next_step_id": "manual"}
    )


async def start_hub(hass: HomeAssistant) -> FlowResult:
    """Start the flow as the user and choose that Home Assistant connects to a hub."""
    result = await start_menu(hass)
    assert result["type"] is FlowResultType.MENU
    return await hass.config_entries.flow.async_configure(
        result["flow_id"], {"next_step_id": "hub"}
    )


async def reach_hub_pair_step(
    hass: HomeAssistant, host: str = "10.0.0.30"
) -> FlowResult:
    """Walk the hub flow up to the pairing-code form."""
    result = await start_hub(hass)
    with patch(FETCH, new=AsyncMock(return_value=FINGERPRINT)):
        result = await hass.config_entries.flow.async_configure(
            result["flow_id"], {CONF_HOST: host, CONF_PORT: DEFAULT_HUB_PORT}
        )
    result = await hass.config_entries.flow.async_configure(result["flow_id"], {})
    assert result["step_id"] == "hub_pair"
    return result


async def start_zeroconf(
    hass: HomeAssistant, discovery: ZeroconfServiceInfo
) -> FlowResult:
    """Start the flow from a zeroconf announcement."""
    return await hass.config_entries.flow.async_init(
        DOMAIN, context={"source": config_entries.SOURCE_ZEROCONF}, data=discovery
    )


async def reach_pair_step(hass: HomeAssistant, host: str = "10.0.0.5") -> FlowResult:
    """Walk the user flow up to the pairing-code form."""
    result = await start_user(hass)
    with patch(FETCH, new=AsyncMock(return_value=FINGERPRINT)):
        result = await hass.config_entries.flow.async_configure(
            result["flow_id"], {CONF_HOST: host, CONF_PORT: DEFAULT_PORT}
        )
    result = await hass.config_entries.flow.async_configure(result["flow_id"], {})
    assert result["step_id"] == "pair"
    return result


async def pair(hass: HomeAssistant, flow_id: str, code: str = "123456") -> FlowResult:
    """Submit a pairing code with a patched client."""
    return await hass.config_entries.flow.async_configure(flow_id, {CONF_CODE: code})


# -- user flow --------------------------------------------------------------------


async def test_user_flow_creates_the_entry(
    hass: HomeAssistant, setup_entry: AsyncMock
) -> None:
    result = await start_user(hass)
    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "manual"

    with patch(FETCH, new=AsyncMock(return_value=FINGERPRINT)) as fetch:
        result = await hass.config_entries.flow.async_configure(
            result["flow_id"], {CONF_HOST: " 10.0.0.5 ", CONF_PORT: 9000}
        )
    fetch.assert_awaited_once_with("10.0.0.5", 9000)
    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "confirm"
    assert result["description_placeholders"] == {
        "name": "10.0.0.5",
        "host": "10.0.0.5",
        "port": "9000",
        "fingerprint": FINGERPRINT,
    }

    result = await hass.config_entries.flow.async_configure(result["flow_id"], {})
    assert result["step_id"] == "pair"

    with (
        patch(PAIR, new=AsyncMock(return_value=PAIRED)) as pair_call,
        patch(PROBE, new=AsyncMock(return_value=HELLO)) as probe,
    ):
        result = await pair(hass, result["flow_id"], " 123456 ")

    assert pair_call.await_args is not None
    assert pair_call.await_args.args[1:5] == ("10.0.0.5", 9000, FINGERPRINT, "123456")
    probe.assert_awaited_once()
    assert result["type"] is FlowResultType.CREATE_ENTRY
    assert result["title"] == "Gaming-PC"
    assert result["data"] == {
        CONF_HOST: "10.0.0.5",
        CONF_PORT: 9000,
        CONF_TOKEN: TOKEN,
        CONF_FINGERPRINT: FINGERPRINT,
    }
    assert result["result"].unique_id == AGENT_ID
    await hass.async_block_till_done()
    setup_entry.assert_awaited_once()


@pytest.mark.parametrize(
    ("error", "key"),
    [
        (CannotConnectError("down"), "cannot_connect"),
        (CertificateMismatchError("changed"), "cannot_connect"),
        (RuntimeError("boom"), "unknown"),
    ],
)
async def test_user_flow_reports_connection_errors(
    hass: HomeAssistant, setup_entry: AsyncMock, error: Exception, key: str
) -> None:
    result = await start_user(hass)
    with patch(FETCH, new=AsyncMock(side_effect=error)):
        result = await hass.config_entries.flow.async_configure(
            result["flow_id"], {CONF_HOST: "10.0.0.5", CONF_PORT: DEFAULT_PORT}
        )
    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "manual"
    assert result["errors"] == {"base": key}

    with patch(FETCH, new=AsyncMock(return_value=FINGERPRINT)):
        result = await hass.config_entries.flow.async_configure(
            result["flow_id"], {CONF_HOST: "10.0.0.5", CONF_PORT: DEFAULT_PORT}
        )
    assert result["step_id"] == "confirm"


async def test_user_flow_refuses_a_configured_address(
    hass: HomeAssistant, setup_entry: AsyncMock
) -> None:
    make_entry(**{CONF_HOST: "10.0.0.5"}).add_to_hass(hass)
    result = await start_user(hass)
    result = await hass.config_entries.flow.async_configure(
        result["flow_id"], {CONF_HOST: "10.0.0.5", CONF_PORT: DEFAULT_PORT}
    )
    assert result["type"] is FlowResultType.ABORT
    assert result["reason"] == "already_configured"


@pytest.mark.parametrize(
    ("pair_error", "probe_error", "key"),
    [
        (PairingCodeInvalidError("no"), None, "invalid_code"),
        (PairingCodeExpiredError("old"), None, "code_expired"),
        (CannotConnectError("down"), None, "cannot_connect"),
        (UnexpectedResponseError("odd"), None, "unknown"),
        (None, UnsupportedProtocolError("v"), "unsupported_protocol"),
        (None, AuthenticationError("token"), "invalid_auth"),
    ],
)
async def test_pair_step_reports_errors_and_recovers(
    hass: HomeAssistant,
    setup_entry: AsyncMock,
    pair_error: Exception | None,
    probe_error: Exception | None,
    key: str,
) -> None:
    result = await reach_pair_step(hass)
    pair_mock = AsyncMock(return_value=PAIRED, side_effect=pair_error)
    probe_mock = AsyncMock(return_value=HELLO, side_effect=probe_error)
    with patch(PAIR, new=pair_mock), patch(PROBE, new=probe_mock):
        result = await pair(hass, result["flow_id"])
        assert result["type"] is FlowResultType.FORM
        assert result["step_id"] == "pair"
        assert result["errors"] == {"base": key}

        pair_mock.side_effect = None
        probe_mock.side_effect = None
        result = await pair(hass, result["flow_id"])
    assert result["type"] is FlowResultType.CREATE_ENTRY


async def test_the_same_agent_cannot_be_configured_twice(
    hass: HomeAssistant, setup_entry: AsyncMock
) -> None:
    entry = make_entry()
    entry.add_to_hass(hass)
    result = await reach_pair_step(hass, host="10.0.0.99")
    with (
        patch(PAIR, new=AsyncMock(return_value=PAIRED)),
        patch(PROBE, new=AsyncMock(return_value=HELLO)),
    ):
        result = await pair(hass, result["flow_id"])
    assert result["type"] is FlowResultType.ABORT
    assert result["reason"] == "already_configured"
    assert len(hass.config_entries.async_entries(DOMAIN)) == 1
    assert entry.data[CONF_HOST] == "10.0.0.99"


# -- zeroconf ---------------------------------------------------------------------


async def test_zeroconf_flow_creates_the_entry(
    hass: HomeAssistant, setup_entry: AsyncMock
) -> None:
    with patch(FETCH, new=AsyncMock(return_value=FINGERPRINT)):
        result = await start_zeroconf(hass, make_discovery())
    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "confirm"
    assert result["description_placeholders"] == {
        "name": "Gaming-PC",
        "host": "192.168.1.20",
        "port": str(DEFAULT_PORT),
        "fingerprint": FINGERPRINT,
    }
    flow = hass.config_entries.flow.async_get(result["flow_id"])
    assert flow["context"]["unique_id"] == AGENT_ID
    assert flow["context"]["title_placeholders"] == {"name": "Gaming-PC"}

    result = await hass.config_entries.flow.async_configure(result["flow_id"], {})
    with (
        patch(PAIR, new=AsyncMock(return_value=PAIRED)),
        patch(PROBE, new=AsyncMock(return_value=HELLO)),
    ):
        result = await pair(hass, result["flow_id"])
    assert result["type"] is FlowResultType.CREATE_ENTRY
    assert result["data"][CONF_HOST] == "192.168.1.20"


async def test_zeroconf_uses_defaults_for_missing_port_and_name(
    hass: HomeAssistant, setup_entry: AsyncMock
) -> None:
    discovery = make_discovery(port=None, properties={"id": AGENT_ID})
    with patch(FETCH, new=AsyncMock(return_value=FINGERPRINT)) as fetch:
        result = await start_zeroconf(hass, discovery)
    fetch.assert_awaited_once_with("192.168.1.20", DEFAULT_PORT)
    assert result["description_placeholders"]["name"] == "192.168.1.20"


async def test_zeroconf_without_agent_id_is_aborted(hass: HomeAssistant) -> None:
    result = await start_zeroconf(hass, make_discovery(properties={"name": "x"}))
    assert result["type"] is FlowResultType.ABORT
    assert result["reason"] == "invalid_discovery"


@pytest.mark.parametrize(
    ("pv", "supported"),
    [("1-1", True), ("1-3", True), ("garbage", True), ("2-3", False)],
)
async def test_zeroconf_checks_the_protocol_range(
    hass: HomeAssistant, setup_entry: AsyncMock, pv: str, supported: bool
) -> None:
    discovery = make_discovery(properties={"id": AGENT_ID, "pv": pv})
    with patch(FETCH, new=AsyncMock(return_value=FINGERPRINT)):
        result = await start_zeroconf(hass, discovery)
    if supported:
        assert result["step_id"] == "confirm"
    else:
        assert result["type"] is FlowResultType.ABORT
        assert result["reason"] == "unsupported_protocol"


@pytest.mark.parametrize(
    ("error", "reason"),
    [(CannotConnectError("down"), "cannot_connect"), (RuntimeError(), "unknown")],
)
async def test_zeroconf_aborts_when_the_agent_is_not_reachable(
    hass: HomeAssistant, error: Exception, reason: str
) -> None:
    with patch(FETCH, new=AsyncMock(side_effect=error)):
        result = await start_zeroconf(hass, make_discovery())
    assert result["type"] is FlowResultType.ABORT
    assert result["reason"] == reason


async def test_rediscovery_with_a_new_address_updates_the_entry(
    hass: HomeAssistant,
) -> None:
    entry = make_entry()
    entry.add_to_hass(hass)
    client = MagicMock(spec=QoffeeLinkClient)
    entry.runtime_data = SimpleNamespace(client=client)

    with patch(FETCH, new=AsyncMock()) as fetch:
        result = await start_zeroconf(hass, make_discovery(host="192.168.1.77"))

    assert result["type"] is FlowResultType.ABORT
    assert result["reason"] == "already_configured"
    fetch.assert_not_awaited()
    assert entry.data[CONF_HOST] == "192.168.1.77"
    assert entry.data[CONF_TOKEN] == TOKEN
    client.update_address.assert_called_once_with("192.168.1.77", DEFAULT_PORT)
    client.trigger_connect.assert_called_once_with()


async def test_announcement_of_a_disconnected_agent_connects_immediately(
    hass: HomeAssistant,
) -> None:
    entry = make_entry()
    entry.add_to_hass(hass)
    client = MagicMock(spec=QoffeeLinkClient)
    client.connected = False
    entry.runtime_data = SimpleNamespace(client=client)

    result = await start_zeroconf(hass, make_discovery())

    assert result["reason"] == "already_configured"
    client.update_address.assert_not_called()
    client.trigger_connect.assert_called_once_with()


@pytest.mark.parametrize(
    ("host", "state", "reload_expected"),
    [
        ("192.168.1.77", ConfigEntryState.SETUP_RETRY, True),
        ("192.168.1.77", ConfigEntryState.NOT_LOADED, False),
        ("192.168.1.20", ConfigEntryState.SETUP_RETRY, False),
    ],
)
async def test_rediscovery_of_an_entry_without_client(
    hass: HomeAssistant,
    host: str,
    state: ConfigEntryState,
    reload_expected: bool,
) -> None:
    entry = make_entry()
    entry.add_to_hass(hass)
    entry.mock_state(hass, state)

    with patch.object(hass.config_entries, "async_schedule_reload") as reload:
        result = await start_zeroconf(hass, make_discovery(host=host))

    assert result["reason"] == "already_configured"
    assert entry.data[CONF_HOST] == host
    if reload_expected:
        reload.assert_called_once_with(entry.entry_id)
    else:
        reload.assert_not_called()


# -- reauthentication -------------------------------------------------------------


async def test_reauth_flow_replaces_the_token(
    hass: HomeAssistant, setup_entry: AsyncMock
) -> None:
    entry = make_entry(**{CONF_TOKEN: "old-token"})
    entry.add_to_hass(hass)
    await hass.config_entries.async_setup(entry.entry_id)

    result = await entry.start_reauth_flow(hass)
    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "reauth_confirm"
    assert result["description_placeholders"]["host"] == "192.168.1.20"

    with patch(PAIR, new=AsyncMock(side_effect=PairingCodeInvalidError("no"))):
        result = await pair(hass, result["flow_id"])
    assert result["errors"] == {"base": "invalid_code"}

    with patch(PAIR, new=AsyncMock(return_value=PAIRED)) as pair_call:
        result = await pair(hass, result["flow_id"])
    # The pinned fingerprint of the entry is used, not a new one.
    assert pair_call.await_args is not None
    assert pair_call.await_args.args[1:4] == ("192.168.1.20", DEFAULT_PORT, FINGERPRINT)
    assert result["type"] is FlowResultType.ABORT
    assert result["reason"] == "reauth_successful"
    assert entry.data[CONF_TOKEN] == TOKEN
    await hass.async_block_till_done()


async def test_reauth_flow_refuses_another_agent(
    hass: HomeAssistant, setup_entry: AsyncMock
) -> None:
    entry = make_entry(**{CONF_TOKEN: "old-token"})
    entry.add_to_hass(hass)
    result = await entry.start_reauth_flow(hass)
    other = dataclasses.replace(PAIRED, agent_id="another-agent")
    with patch(PAIR, new=AsyncMock(return_value=other)):
        result = await pair(hass, result["flow_id"])
    assert result["type"] is FlowResultType.ABORT
    assert result["reason"] == "wrong_agent"
    assert entry.data[CONF_TOKEN] == "old-token"


# -- reconfiguration --------------------------------------------------------------


async def test_reconfigure_flow_changes_host_and_port(
    hass: HomeAssistant, setup_entry: AsyncMock
) -> None:
    entry = make_entry()
    entry.add_to_hass(hass)
    await hass.config_entries.async_setup(entry.entry_id)

    result = await entry.start_reconfigure_flow(hass)
    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "reconfigure"

    with patch(PROBE, new=AsyncMock(side_effect=AuthenticationError("token"))):
        result = await hass.config_entries.flow.async_configure(
            result["flow_id"], {CONF_HOST: "10.1.1.1", CONF_PORT: 9001}
        )
    assert result["type"] is FlowResultType.FORM
    assert result["errors"] == {"base": "invalid_auth"}

    with patch(PROBE, new=AsyncMock(return_value=HELLO)) as probe:
        result = await hass.config_entries.flow.async_configure(
            result["flow_id"], {CONF_HOST: " 10.1.1.1 ", CONF_PORT: 9001}
        )
    # The stored token and the pinned fingerprint are used for the check.
    assert probe.await_args is not None
    assert probe.await_args.args[1:] == ("10.1.1.1", 9001, TOKEN, FINGERPRINT)
    assert result["type"] is FlowResultType.ABORT
    assert result["reason"] == "reconfigure_successful"
    assert entry.data[CONF_HOST] == "10.1.1.1"
    assert entry.data[CONF_PORT] == 9001
    assert entry.data[CONF_TOKEN] == TOKEN
    await hass.async_block_till_done()


async def test_reconfigure_flow_refuses_another_agent(
    hass: HomeAssistant, setup_entry: AsyncMock
) -> None:
    entry = make_entry()
    entry.add_to_hass(hass)
    result = await entry.start_reconfigure_flow(hass)
    with patch(PROBE, new=AsyncMock(return_value=OTHER_HELLO)):
        result = await hass.config_entries.flow.async_configure(
            result["flow_id"], {CONF_HOST: "10.1.1.1", CONF_PORT: 9001}
        )
    assert result["type"] is FlowResultType.ABORT
    assert result["reason"] == "wrong_agent"
    assert entry.data[CONF_HOST] == "192.168.1.20"


# -- a hub (T914) -----------------------------------------------------------------


async def test_hub_flow_creates_the_entry(
    hass: HomeAssistant, setup_entry: AsyncMock
) -> None:
    result = await start_hub(hass)
    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "hub"

    with patch(FETCH, new=AsyncMock(return_value=FINGERPRINT)) as fetch:
        result = await hass.config_entries.flow.async_configure(
            result["flow_id"], {CONF_HOST: " 10.0.0.30 ", CONF_PORT: 9490}
        )
    fetch.assert_awaited_once_with("10.0.0.30", 9490)
    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "hub_confirm"
    assert result["description_placeholders"] == {
        "name": "10.0.0.30",
        "host": "10.0.0.30",
        "port": "9490",
        "fingerprint": FINGERPRINT,
    }

    result = await hass.config_entries.flow.async_configure(result["flow_id"], {})
    assert result["step_id"] == "hub_pair"

    with (
        patch(HUB_PAIR, new=AsyncMock(return_value=HUB_PAIRED)) as pair_call,
        patch(PROBE_HUB, new=AsyncMock(return_value=HUB_HELLO)) as probe,
    ):
        result = await pair(hass, result["flow_id"], " 123456 ")

    assert pair_call.await_args is not None
    assert pair_call.await_args.args[1:5] == ("10.0.0.30", 9490, FINGERPRINT, "123456")
    probe.assert_awaited_once()
    assert probe.await_args is not None
    # The token that pairing produced has to open the fleet link.
    assert probe.await_args.args[1:] == (
        "10.0.0.30",
        9490,
        HUB_PAIRED.token,
        FINGERPRINT,
    )
    assert result["type"] is FlowResultType.CREATE_ENTRY
    assert result["title"] == HUB_HELLO.hub.name
    assert result["data"] == {
        CONF_HUB: True,
        CONF_HOST: "10.0.0.30",
        CONF_PORT: 9490,
        CONF_TOKEN: HUB_PAIRED.token,
        CONF_FINGERPRINT: FINGERPRINT,
    }
    assert result["result"].unique_id == HUB_ID
    await hass.async_block_till_done()
    setup_entry.assert_awaited_once()


@pytest.mark.parametrize(
    ("error", "key"),
    [
        (CannotConnectError("down"), "cannot_connect"),
        (CertificateMismatchError("changed"), "cannot_connect"),
        (RuntimeError("boom"), "unknown"),
    ],
)
async def test_hub_flow_reports_connection_errors(
    hass: HomeAssistant, setup_entry: AsyncMock, error: Exception, key: str
) -> None:
    result = await start_hub(hass)
    with patch(FETCH, new=AsyncMock(side_effect=error)):
        result = await hass.config_entries.flow.async_configure(
            result["flow_id"], {CONF_HOST: "10.0.0.30", CONF_PORT: DEFAULT_HUB_PORT}
        )
    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "hub"
    assert result["errors"] == {"base": key}

    with patch(FETCH, new=AsyncMock(return_value=FINGERPRINT)):
        result = await hass.config_entries.flow.async_configure(
            result["flow_id"], {CONF_HOST: "10.0.0.30", CONF_PORT: DEFAULT_HUB_PORT}
        )
    assert result["step_id"] == "hub_confirm"


async def test_hub_flow_refuses_a_configured_address(
    hass: HomeAssistant, setup_entry: AsyncMock
) -> None:
    make_hub_entry(**{CONF_HOST: "10.0.0.30"}).add_to_hass(hass)
    result = await start_hub(hass)
    result = await hass.config_entries.flow.async_configure(
        result["flow_id"], {CONF_HOST: "10.0.0.30", CONF_PORT: DEFAULT_HUB_PORT}
    )
    assert result["type"] is FlowResultType.ABORT
    assert result["reason"] == "already_configured"


@pytest.mark.parametrize(
    ("pair_error", "probe_error", "key"),
    [
        (PairingCodeInvalidError("no"), None, "invalid_code"),
        (PairingCodeExpiredError("old"), None, "code_expired"),
        (CannotConnectError("down"), None, "cannot_connect"),
        (UnexpectedResponseError("odd"), None, "unknown"),
        (None, UnsupportedProtocolError("v"), "unsupported_protocol"),
        (None, AuthenticationError("token"), "invalid_auth"),
    ],
)
async def test_hub_pair_step_reports_errors_and_recovers(
    hass: HomeAssistant,
    setup_entry: AsyncMock,
    pair_error: Exception | None,
    probe_error: Exception | None,
    key: str,
) -> None:
    result = await reach_hub_pair_step(hass)
    pair_mock = AsyncMock(return_value=HUB_PAIRED, side_effect=pair_error)
    probe_mock = AsyncMock(return_value=HUB_HELLO, side_effect=probe_error)
    with patch(HUB_PAIR, new=pair_mock), patch(PROBE_HUB, new=probe_mock):
        result = await pair(hass, result["flow_id"])
        assert result["type"] is FlowResultType.FORM
        assert result["step_id"] == "hub_pair"
        assert result["errors"] == {"base": key}

        pair_mock.side_effect = None
        probe_mock.side_effect = None
        result = await pair(hass, result["flow_id"])
    assert result["type"] is FlowResultType.CREATE_ENTRY


async def test_the_same_hub_cannot_be_configured_twice(
    hass: HomeAssistant, setup_entry: AsyncMock
) -> None:
    entry = make_hub_entry()
    entry.add_to_hass(hass)
    result = await reach_hub_pair_step(hass, host="10.0.0.99")
    with (
        patch(HUB_PAIR, new=AsyncMock(return_value=HUB_PAIRED)),
        patch(PROBE_HUB, new=AsyncMock(return_value=HUB_HELLO)),
    ):
        result = await pair(hass, result["flow_id"])
    assert result["type"] is FlowResultType.ABORT
    assert result["reason"] == "already_configured"
    assert len(hass.config_entries.async_entries(DOMAIN)) == 1
    assert entry.data[CONF_HOST] == "10.0.0.99"


async def test_hub_reauth_flow_replaces_the_token(
    hass: HomeAssistant, setup_entry: AsyncMock
) -> None:
    entry = make_hub_entry(**{CONF_TOKEN: "old-token"})
    entry.add_to_hass(hass)
    await hass.config_entries.async_setup(entry.entry_id)

    result = await entry.start_reauth_flow(hass)
    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "reauth_hub"
    assert result["description_placeholders"]["host"] == "192.168.1.30"

    with patch(HUB_PAIR, new=AsyncMock(side_effect=PairingCodeInvalidError("no"))):
        result = await pair(hass, result["flow_id"])
    assert result["errors"] == {"base": "invalid_code"}

    with patch(HUB_PAIR, new=AsyncMock(return_value=HUB_PAIRED)) as pair_call:
        result = await pair(hass, result["flow_id"])
    # The pinned fingerprint of the entry is used, not a new one.
    assert pair_call.await_args is not None
    assert pair_call.await_args.args[1:4] == (
        "192.168.1.30",
        DEFAULT_HUB_PORT,
        FINGERPRINT,
    )
    assert result["type"] is FlowResultType.ABORT
    assert result["reason"] == "reauth_successful"
    assert entry.data[CONF_TOKEN] == HUB_PAIRED.token
    await hass.async_block_till_done()


async def test_hub_reauth_flow_refuses_another_hub(
    hass: HomeAssistant, setup_entry: AsyncMock
) -> None:
    entry = make_hub_entry(**{CONF_TOKEN: "old-token"})
    entry.add_to_hass(hass)
    result = await entry.start_reauth_flow(hass)
    with patch(HUB_PAIR, new=AsyncMock(return_value=OTHER_HUB_PAIRED)):
        result = await pair(hass, result["flow_id"])
    assert result["type"] is FlowResultType.ABORT
    assert result["reason"] == "wrong_hub"
    assert entry.data[CONF_TOKEN] == "old-token"


async def test_hub_reconfigure_flow_changes_host_and_port(
    hass: HomeAssistant, setup_entry: AsyncMock
) -> None:
    entry = make_hub_entry()
    entry.add_to_hass(hass)
    await hass.config_entries.async_setup(entry.entry_id)

    result = await entry.start_reconfigure_flow(hass)
    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "reconfigure_hub"

    with patch(PROBE_HUB, new=AsyncMock(side_effect=AuthenticationError("token"))):
        result = await hass.config_entries.flow.async_configure(
            result["flow_id"], {CONF_HOST: "10.1.1.1", CONF_PORT: 9001}
        )
    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "reconfigure_hub"
    assert result["errors"] == {"base": "invalid_auth"}

    with patch(PROBE_HUB, new=AsyncMock(return_value=HUB_HELLO)) as probe:
        result = await hass.config_entries.flow.async_configure(
            result["flow_id"], {CONF_HOST: " 10.1.1.1 ", CONF_PORT: 9001}
        )
    # The stored token and the pinned fingerprint are used for the check.
    assert probe.await_args is not None
    assert probe.await_args.args[1:] == (
        "10.1.1.1",
        9001,
        HUB_PAIRED.token,
        FINGERPRINT,
    )
    assert result["type"] is FlowResultType.ABORT
    assert result["reason"] == "reconfigure_successful"
    assert entry.data[CONF_HOST] == "10.1.1.1"
    assert entry.data[CONF_PORT] == 9001
    await hass.async_block_till_done()


async def test_hub_reconfigure_flow_refuses_another_hub(
    hass: HomeAssistant, setup_entry: AsyncMock
) -> None:
    entry = make_hub_entry()
    entry.add_to_hass(hass)
    result = await entry.start_reconfigure_flow(hass)
    with patch(PROBE_HUB, new=AsyncMock(return_value=OTHER_HUB_HELLO)):
        result = await hass.config_entries.flow.async_configure(
            result["flow_id"], {CONF_HOST: "10.1.1.1", CONF_PORT: 9001}
        )
    assert result["type"] is FlowResultType.ABORT
    assert result["reason"] == "wrong_hub"
    assert entry.data[CONF_HOST] == "192.168.1.30"


# -- the route of a PC (T914) -----------------------------------------------------


async def test_pair_step_reports_a_direct_link_that_the_hub_denies(
    hass: HomeAssistant, setup_entry: AsyncMock
) -> None:
    result = await reach_pair_step(hass)
    with patch(PAIR, new=AsyncMock(side_effect=DirectLinkDeniedError("denied"))):
        result = await pair(hass, result["flow_id"])
    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "pair"
    assert result["errors"] == {"base": "direct_link_denied"}


async def test_pairing_a_pc_that_a_hub_serves_asks_for_confirmation(
    hass: HomeAssistant, setup_entry: AsyncMock
) -> None:
    hub = make_hub_entry()
    hub.add_to_hass(hass)
    result = await reach_pair_step(hass)
    with (
        patch(PAIR, new=AsyncMock(return_value=PAIRED)),
        patch(PROBE, new=AsyncMock(return_value=HELLO)),
        patch(HUB_KNOWING, return_value=hub),
    ):
        result = await pair(hass, result["flow_id"])

    # Nothing is created before the user agreed to move the PC to the direct route.
    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "switch_route"
    assert result["description_placeholders"] == {
        "name": "Gaming-PC",
        "hub": "QoffeeAutomat Hub",
    }
    assert hass.config_entries.async_entries(DOMAIN) == [hub]

    result = await hass.config_entries.flow.async_configure(result["flow_id"], {})
    assert result["type"] is FlowResultType.CREATE_ENTRY
    assert result["title"] == "Gaming-PC"
    assert result["data"] == {
        CONF_HOST: "10.0.0.5",
        CONF_PORT: DEFAULT_PORT,
        CONF_TOKEN: TOKEN,
        CONF_FINGERPRINT: FINGERPRINT,
    }
    assert result["result"].unique_id == AGENT_ID
    await hass.async_block_till_done()


# -- helpers and real network -----------------------------------------------------


async def test_probe_connects_once_with_the_pinned_fingerprint(
    hass: HomeAssistant,
) -> None:
    with patch(
        "custom_components.qoffeeautomat.config_flow.QoffeeLinkClient"
    ) as client_class:
        client_class.return_value.probe = AsyncMock(return_value=HELLO)
        hello = await config_flow._probe(  # the private helper is under test
            hass, "10.0.0.5", 8481, TOKEN, FINGERPRINT
        )
    assert hello is HELLO
    args = client_class.call_args.args
    assert args[1:] == ("10.0.0.5", 8481, TOKEN, FINGERPRINT)
    assert client_class.call_args.kwargs["client_name"] == hass.config.location_name


async def test_probe_hub_opens_the_fleet_link_once_with_the_pinned_fingerprint(
    hass: HomeAssistant,
) -> None:
    with patch(
        "custom_components.qoffeeautomat.config_flow.FleetClient"
    ) as client_class:
        client_class.return_value.probe = AsyncMock(return_value=HUB_HELLO)
        hello = await config_flow._probe_hub(  # the private helper is under test
            hass, "10.0.0.5", 8490, TOKEN, FINGERPRINT
        )
    assert hello is HUB_HELLO
    args = client_class.call_args.args
    assert args[1:] == ("10.0.0.5", 8490, TOKEN, FINGERPRINT)
    assert client_class.call_args.kwargs["client_name"] == hass.config.location_name


async def test_hub_flow_against_the_fake_hub(
    hass: HomeAssistant, fake_hub: FakeHub, setup_entry: AsyncMock
) -> None:
    result = await start_hub(hass)
    result = await hass.config_entries.flow.async_configure(
        result["flow_id"], {CONF_HOST: fake_hub.host, CONF_PORT: fake_hub.port}
    )
    assert result["step_id"] == "hub_confirm"
    assert (
        result["description_placeholders"]["fingerprint"]
        == fake_hub.certificate.fingerprint
    )
    result = await hass.config_entries.flow.async_configure(result["flow_id"], {})

    result = await pair(hass, result["flow_id"], "000000")
    assert result["errors"] == {"base": "invalid_code"}

    result = await pair(hass, result["flow_id"], HUB_PAIRING_CODE)
    assert result["type"] is FlowResultType.CREATE_ENTRY
    assert result["data"][CONF_HUB] is True
    assert result["data"][CONF_TOKEN] == fake_hub.token
    assert result["data"][CONF_FINGERPRINT] == fake_hub.certificate.fingerprint
    assert result["result"].unique_id == fake_hub.hub_id
    await hass.async_block_till_done()


async def test_user_flow_against_the_fake_agent(
    hass: HomeAssistant, fake_agent: FakeAgent, setup_entry: AsyncMock
) -> None:
    result = await start_user(hass)
    result = await hass.config_entries.flow.async_configure(
        result["flow_id"], {CONF_HOST: fake_agent.host, CONF_PORT: fake_agent.port}
    )
    assert result["step_id"] == "confirm"
    assert (
        result["description_placeholders"]["fingerprint"]
        == fake_agent.certificate.fingerprint
    )
    result = await hass.config_entries.flow.async_configure(result["flow_id"], {})

    result = await pair(hass, result["flow_id"], "000000")
    assert result["errors"] == {"base": "invalid_code"}

    result = await pair(hass, result["flow_id"], PAIRING_CODE)
    assert result["type"] is FlowResultType.CREATE_ENTRY
    assert result["data"][CONF_TOKEN] == fake_agent.token
    assert result["data"][CONF_FINGERPRINT] == fake_agent.certificate.fingerprint
    assert result["result"].unique_id == fake_agent.agent_id
    await hass.async_block_till_done()


# -- translations -----------------------------------------------------------------


def _keys(node: Any, prefix: str = "") -> set[str]:
    if isinstance(node, dict):
        return {
            found
            for key, value in node.items()
            for found in _keys(value, f"{prefix}/{key}")
        }
    return {prefix}


def test_german_translation_has_the_keys_of_the_english_one() -> None:
    english = json.loads((INTEGRATION / "translations" / "en.json").read_text("utf-8"))
    german = json.loads((INTEGRATION / "translations" / "de.json").read_text("utf-8"))
    assert _keys(german) == _keys(english)


def test_every_error_and_step_of_the_flow_has_a_text() -> None:
    strings = json.loads((INTEGRATION / "strings.json").read_text("utf-8"))["config"]
    assert {
        "user",
        "manual",
        "confirm",
        "pair",
        "reauth_confirm",
        "reconfigure",
        "reverse",
        "reverse_rotate",
        "reverse_show",
        "hub",
        "hub_confirm",
        "hub_pair",
        "reauth_hub",
        "reconfigure_hub",
        "switch_route",
    } <= set(strings["step"])
    assert "hub" in strings["step"]["user"]["menu_options"]
    assert "waiting_for_agent" in strings["progress"]
    assert {
        "cannot_connect",
        "invalid_code",
        "code_expired",
        "unsupported_protocol",
        "direct_link_denied",
        "unknown",
    } <= set(strings["error"])
    assert {
        "already_configured",
        "wrong_agent",
        "wrong_hub",
        "reauth_successful",
    } <= set(strings["abort"])
