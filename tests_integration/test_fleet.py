"""Tests of the hub route (T914).

The fleet client, the pairing with a hub and the per-agent link are tested against the
fake hub (``fake_hub.py``). The hub entry, the entities of its agents, ``run_command``,
events, Wake-on-LAN, diagnostics and the repair issues are tested with the integration
against the fake hub; the change of the route in both directions is tested with the
fake agent and the fake hub listing the same PC.
"""

import asyncio
import copy
import dataclasses
import json
from collections.abc import Callable
from typing import Any
from unittest.mock import AsyncMock, patch

import aiohttp
import pytest
from homeassistant.components.diagnostics import REDACTED
from homeassistant.config_entries import ConfigEntryState
from homeassistant.core import Context, HomeAssistant
from homeassistant.data_entry_flow import FlowResultType
from homeassistant.exceptions import HomeAssistantError, ServiceValidationError
from homeassistant.helpers import device_registry as dr
from homeassistant.helpers import entity_registry as er
from homeassistant.helpers import issue_registry as ir
from pytest_homeassistant_custom_component.common import (
    MockConfigEntry,
    async_capture_events,
)

from custom_components.qoffeeautomat import routes
from custom_components.qoffeeautomat.client import (
    AgentLink,
    AuthenticationError,
    CannotConnectError,
    CertificateMismatchError,
    DirectLinkDeniedError,
    FleetClient,
    NotConnectedError,
    PairingCodeExpiredError,
    PairingCodeInvalidError,
    RequestError,
    UnexpectedResponseError,
    UnsupportedProtocolError,
    async_hub_pair,
    async_pair,
)
from custom_components.qoffeeautomat.const import (
    CONF_AGENT_MACS,
    CONF_BROADCAST,
    CONF_FINGERPRINT,
    CONF_HUB,
    CONF_TOKEN,
    DEFAULT_BROADCAST,
    DOMAIN,
    EVENT_QOFFEEAUTOMAT,
    WOL_FAST_RETRY_SECONDS,
)
from custom_components.qoffeeautomat.diagnostics import (
    async_get_config_entry_diagnostics,
    async_get_device_diagnostics,
)
from custom_components.qoffeeautomat.fleet import FleetManager
from custom_components.qoffeeautomat.protocol import (
    AgentEvent,
    AgentManifestChanged,
    AgentState,
    AgentStatus,
    FleetAgent,
    ProtocolError,
    parse_hub_pair_response,
)
from custom_components.qoffeeautomat.repairs import (
    DATA_AGENT_ID,
    DATA_HUB_ENTRY_ID,
    SwitchToHubRepairFlow,
    async_create_fix_flow,
    direct_link_denied_issue_id,
    direct_route_issue_id,
    protocol_mismatch_issue_id,
)
from custom_components.qoffeeautomat.routes import (
    RouteSwitchError,
    async_switch_to_hub,
    direct_entry_for,
    fleet_of,
    hub_entry_knowing,
    loaded_fleets,
)
from custom_components.qoffeeautomat.runtime import (
    FleetRuntimeData,
    QoffeeAutomatRuntimeData,
    agent_runtime_of,
)
from tests_integration.fake_agent import FakeAgent, load_fixture
from tests_integration.fake_hub import (
    GAMING_ID,
    GAMING_MAC,
    HUB_PAIRING_CODE,
    MEDIA_ID,
    FakeHub,
)
from tests_integration.helpers import (
    EntryFactory,
    entity_id_of,
    find_entity_id,
    state_of,
)

USER_ID = "8f1c2d3e4a5b6c7d8e9f0a1b2c3d4e5f"
WRONG_FINGERPRINT = ":".join(["AB"] * 32)
TOKEN = "A" * 43
SEND = "custom_components.qoffeeautomat.button.async_send_magic_packets"
GAMING_MAC_STORED = "a4:b1:c1:00:11:22"

# The entities that the manifest of the fixtures gives every PC (the same list as in
# ``test_init.py``): metrics, commands, the event entity and the feature notify.
EXPECTED_ENTITIES = (
    ("sensor", "cpu.usage"),
    ("sensor", "audio.volume"),
    ("binary_sensor", "session.locked"),
    ("binary_sensor", "_connectivity"),
    ("sensor", "_power_state"),
    ("select", "set_display_profile"),
    ("number", "set_volume"),
    ("button", "lock_session"),
    ("text", "say_text"),
    ("event", "_events"),
    ("notify", "_notify"),
)


# -- helpers ----------------------------------------------------------------------


def hub_device(hass: HomeAssistant, hub: FakeHub) -> dr.DeviceEntry:
    """Return the device of the hub."""
    device = next(
        iter(
            dr.async_get(hass).async_get_devices(
                identifiers={(DOMAIN, f"hub_{hub.hub_id}")}
            )
        ),
        None,
    )
    assert device is not None
    return device


def agent_device(hass: HomeAssistant, agent_id: str) -> dr.DeviceEntry | None:
    """Return the device of a PC, if it has one."""
    return next(
        iter(dr.async_get(hass).async_get_devices(identifiers={(DOMAIN, agent_id)})),
        None,
    )


def entries_of_agent(hass: HomeAssistant, agent_id: str) -> list[er.RegistryEntry]:
    """Return the registry entries of the entities of a PC."""
    return [
        entry
        for entry in er.async_get(hass).entities.values()
        if entry.platform == DOMAIN and entry.unique_id.startswith(f"{agent_id}_")
    ]


def manager_of(entry: MockConfigEntry) -> FleetManager:
    """Return the fleet manager of a loaded hub entry."""
    fleet = fleet_of(entry)
    assert fleet is not None
    return fleet


def runtime_of(entry: MockConfigEntry, agent_id: str) -> QoffeeAutomatRuntimeData:
    """Return the runtime data that a hub entry keeps for a PC."""
    runtime = manager_of(entry).runtime_of(agent_id)
    assert runtime is not None
    return runtime


async def eventually(
    hass: HomeAssistant, predicate: Callable[[], bool], limit: float = 5.0
) -> None:
    """Let Home Assistant work until ``predicate()`` is true."""
    async with asyncio.timeout(limit):
        while True:
            await hass.async_block_till_done()
            if predicate():
                return
            await asyncio.sleep(0.02)


async def press(hass: HomeAssistant, entity_id: str) -> None:
    """Press a button the way a user does."""
    await hass.services.async_call(
        "button", "press", {"entity_id": entity_id}, blocking=True
    )


async def run_action(
    hass: HomeAssistant, device_id: str, command: str, **data: Any
) -> Any:
    """Call ``qoffeeautomat.run_command`` and return its response."""
    return await hass.services.async_call(
        DOMAIN,
        "run_command",
        {"device_id": device_id, "command": command, **data},
        blocking=True,
        return_response=True,
        context=Context(user_id=USER_ID),
    )


def make_client(
    session: aiohttp.ClientSession, hub: FakeHub, **kwargs: Any
) -> FleetClient:
    """Return a fleet client of the fake hub."""
    return FleetClient(
        session,
        hub.host,
        hub.port,
        hub.token,
        hub.certificate.fingerprint,
        **kwargs,
    )


# -- the parser of the pairing response -------------------------------------------


def test_the_pairing_response_of_the_fixtures_is_parsed() -> None:
    everything = parse_hub_pair_response(load_fixture("hub/pair_response.json"))
    assert everything.token == load_fixture("hub/pair_response.json")["token"]
    assert everything.hub_id == "6f1d3a2e-9c47-4b58-8e21-7d0a5c3b9f14"
    assert everything.hub_name == "QoffeeAutomat Hub"
    assert everything.scope_type == "all"
    assert everything.scope_tags == ()
    assert everything.fingerprint is not None
    assert everything.fingerprint.startswith("AB:CD:EF")

    tagged = parse_hub_pair_response(load_fixture("hub/pair_response.tags.json"))
    assert tagged.scope_type == "tags"
    assert tagged.scope_tags == ("gaming", "lab")
    assert tagged.fingerprint is None


@pytest.mark.parametrize(
    "damage",
    [
        lambda body: body.pop("token"),
        lambda body: body.update({"token": "too-short"}),
        lambda body: body.pop("hub"),
        lambda body: body["hub"].pop("id"),
        lambda body: body.pop("scope"),
        lambda body: body["scope"].update(type="some"),
        lambda body: body.update(scope={"type": "tags"}),
        lambda body: body.update(fingerprint="not a fingerprint"),
    ],
)
def test_a_pairing_response_that_violates_the_schema_is_refused(
    damage: Callable[[dict[str, Any]], object],
) -> None:
    body = copy.deepcopy(load_fixture("hub/pair_response.json"))
    damage(body)
    with pytest.raises(ProtocolError):
        parse_hub_pair_response(body)


def test_a_pairing_response_must_be_an_object() -> None:
    with pytest.raises(ProtocolError):
        parse_hub_pair_response(["token"])


# -- pairing with a hub -----------------------------------------------------------


async def test_pairing_with_a_hub_returns_the_token_and_the_scope(
    fake_hub: FakeHub, session: aiohttp.ClientSession
) -> None:
    paired = await async_hub_pair(
        session,
        fake_hub.host,
        fake_hub.port,
        fake_hub.certificate.fingerprint,
        HUB_PAIRING_CODE,
        client_name="Home",
    )

    assert paired.token == fake_hub.token
    assert paired.hub_id == fake_hub.hub_id
    assert paired.hub_name == fake_hub.hub_name
    assert paired.scope_type == "all"
    assert paired.fingerprint == fake_hub.certificate.fingerprint
    assert fake_hub.pair_requests == [
        {"code": HUB_PAIRING_CODE, "client": {"kind": "homeassistant", "name": "Home"}}
    ]


@pytest.mark.parametrize(
    ("status", "error"),
    [
        (401, PairingCodeInvalidError),
        (429, PairingCodeExpiredError),
        (500, UnexpectedResponseError),
    ],
)
async def test_the_pairing_endpoint_of_the_hub_reports_failures(
    fake_hub: FakeHub,
    session: aiohttp.ClientSession,
    status: int,
    error: type[Exception],
) -> None:
    fake_hub.pair_status = status
    with pytest.raises(error):
        await async_hub_pair(
            session,
            fake_hub.host,
            fake_hub.port,
            fake_hub.certificate.fingerprint,
            HUB_PAIRING_CODE,
            client_name="Home",
        )


async def test_pairing_with_a_wrong_code_is_refused(
    fake_hub: FakeHub, session: aiohttp.ClientSession
) -> None:
    with pytest.raises(PairingCodeInvalidError):
        await async_hub_pair(
            session,
            fake_hub.host,
            fake_hub.port,
            fake_hub.certificate.fingerprint,
            "000000",
            client_name="Home",
        )


async def test_a_pairing_response_with_the_wrong_shape_is_unexpected(
    fake_hub: FakeHub, session: aiohttp.ClientSession
) -> None:
    fake_hub.pair_response = {"token": "x"}
    with pytest.raises(UnexpectedResponseError):
        await async_hub_pair(
            session,
            fake_hub.host,
            fake_hub.port,
            fake_hub.certificate.fingerprint,
            HUB_PAIRING_CODE,
            client_name="Home",
        )


async def test_pairing_is_only_sent_to_the_pinned_certificate(
    fake_hub: FakeHub, session: aiohttp.ClientSession
) -> None:
    with pytest.raises(CertificateMismatchError):
        await async_hub_pair(
            session,
            fake_hub.host,
            fake_hub.port,
            WRONG_FINGERPRINT,
            HUB_PAIRING_CODE,
            client_name="Home",
        )
    assert fake_hub.pair_requests == []


async def test_pairing_with_a_hub_that_is_down_cannot_connect(
    fake_hub: FakeHub, session: aiohttp.ClientSession
) -> None:
    await fake_hub.stop()
    with pytest.raises(CannotConnectError):
        await async_hub_pair(
            session,
            fake_hub.host,
            fake_hub.port,
            fake_hub.certificate.fingerprint,
            HUB_PAIRING_CODE,
            client_name="Home",
            request_timeout=2.0,
        )


async def test_pairing_with_an_agent_that_its_hub_forbids_is_denied(
    fake_agent: FakeAgent, session: aiohttp.ClientSession
) -> None:
    fake_agent.pair_status = 403
    with pytest.raises(DirectLinkDeniedError):
        await async_pair(
            session,
            fake_agent.host,
            fake_agent.port,
            fake_agent.certificate.fingerprint,
            fake_agent.pairing_code,
            client_name="Home",
        )


# -- the fleet client -------------------------------------------------------------


async def test_the_probe_returns_the_hello_of_the_hub(
    fake_hub: FakeHub, session: aiohttp.ClientSession
) -> None:
    hello = await make_client(session, fake_hub).probe()

    assert hello.hub.id == fake_hub.hub_id
    assert hello.hub.name == fake_hub.hub_name
    assert "fleet" in hello.features
    assert fake_hub.connections[0].client_hello is not None
    # The client announces the feature that it needs.
    assert fake_hub.connections[0].client_hello["features"] == ["fleet"]
    assert fake_hub.connections[0].client_hello["client"]["kind"] == "homeassistant"


async def test_the_probe_reports_why_it_failed(
    fake_hub: FakeHub, session: aiohttp.ClientSession
) -> None:
    wrong_token = FleetClient(
        session,
        fake_hub.host,
        fake_hub.port,
        TOKEN,
        fake_hub.certificate.fingerprint,
    )
    with pytest.raises(AuthenticationError):
        await wrong_token.probe()
    assert fake_hub.rejected_connections == 1

    changed = FleetClient(
        session, fake_hub.host, fake_hub.port, fake_hub.token, WRONG_FINGERPRINT
    )
    with pytest.raises(CertificateMismatchError):
        await changed.probe()

    fake_hub.protocol_range = (2, 3)
    with pytest.raises(UnsupportedProtocolError) as unsupported:
        await make_client(session, fake_hub).probe()
    assert unsupported.value.agent_range == (2, 3)

    fake_hub.protocol_range = (1, 1)
    fake_hub.features = []
    with pytest.raises(UnexpectedResponseError):
        await make_client(session, fake_hub).probe()

    await fake_hub.stop()
    with pytest.raises(CannotConnectError):
        await make_client(session, fake_hub, connect_timeout=2.0).probe()


async def test_the_client_lists_the_agents_and_routes_frames_by_agent(
    fake_hub: FakeHub, session: aiohttp.ClientSession
) -> None:
    client = make_client(session, fake_hub)
    connected: list[bool] = []
    client.add_connection_listener(connected.append)
    client.start()
    try:
        await fake_hub.wait_for(lambda: client.connected)
        agents = {agent.id: agent for agent in await client.get_agents()}
        assert set(agents) == {GAMING_ID, MEDIA_ID}
        assert agents[GAMING_ID].agent is not None
        assert agents[GAMING_ID].agent.macs == (GAMING_MAC,)
        assert client.hello is not None
        assert client.hello.hub.id == fake_hub.hub_id

        link = AgentLink(client, agents[GAMING_ID])
        states: list[Any] = []
        events: list[Any] = []
        revisions: list[str] = []
        link.add_state_listener(states.append)
        link.add_event_listener(events.append)
        link.add_manifest_listener(lambda frame: revisions.append(frame.rev))

        # Frames of the other agent come first: none of them may reach this link.
        await fake_hub.send_state(MEDIA_ID, {"cpu.usage": 9})
        await fake_hub.send_event(MEDIA_ID, "ignored")
        await fake_hub.send_state(GAMING_ID, {"cpu.usage": 5})
        await fake_hub.send_event(GAMING_ID, "power_action", {"action": "sleep"})
        await fake_hub.send_manifest_changed(GAMING_ID)
        await fake_hub.wait_for(lambda: states and events and revisions)

        assert [state.values for state in states] == [{"cpu.usage": 5}]
        assert [(event.type, dict(event.data)) for event in events] == [
            ("power_action", {"action": "sleep"})
        ]
        assert revisions == [fake_hub.agents[GAMING_ID].manifest["rev"]]  # type: ignore[index]
    finally:
        await client.stop()
        await asyncio.sleep(0.02)
    assert connected == [True, False]


async def test_the_client_tells_which_agents_come_and_go(
    fake_hub: FakeHub, session: aiohttp.ClientSession
) -> None:
    client = make_client(session, fake_hub)
    added: list[str] = []
    removed: list[str] = []
    client.add_agent_added_listener(lambda frame: added.append(frame.agent_id))
    client.add_agent_removed_listener(lambda frame: removed.append(frame.agent_id))
    client.start()
    try:
        await fake_hub.wait_for(lambda: client.connected)
        await fake_hub.send_agent_added(fake_hub.add_agent("office", "Office-PC"))
        await fake_hub.send_agent_removed(MEDIA_ID)
        await fake_hub.wait_for(lambda: added and removed)
        assert added == ["office"]
        assert removed == [MEDIA_ID]
    finally:
        await client.stop()
        await asyncio.sleep(0.02)


async def test_the_client_reconnects_and_stops_for_good(
    fake_hub: FakeHub, session: aiohttp.ClientSession
) -> None:
    client = make_client(session, fake_hub, backoff_min=0.05, backoff_max=0.1)
    client.start()
    client.start()  # a second start changes nothing
    try:
        await fake_hub.wait_for(lambda: client.connected)
        fake_hub.drop_connections()
        await fake_hub.wait_for(lambda: not client.connected)
        await fake_hub.wait_for(lambda: client.connected)
    finally:
        await client.stop()
        await asyncio.sleep(0.02)
    assert not client.connected
    with pytest.raises(NotConnectedError):
        await client.get_agents()


async def test_the_client_stops_retrying_when_the_token_is_revoked(
    fake_hub: FakeHub, session: aiohttp.ClientSession
) -> None:
    client = make_client(session, fake_hub, backoff_min=0.05, backoff_max=0.1)
    rejected: list[None] = []
    client.add_auth_failed_listener(rejected.append)
    client.start()
    try:
        await fake_hub.wait_for(lambda: client.connected)
        fake_hub.token = "B" * 43
        await fake_hub.close_connections(4401)
        await fake_hub.wait_for(lambda: client.auth_failed)
        assert rejected == [None]

        # A new token ends the wait for good and connects right away.
        fake_hub.token = TOKEN
        client.update_token(TOKEN)
        await fake_hub.wait_for(lambda: client.connected)
        assert not client.auth_failed
        client.update_address(fake_hub.host, fake_hub.port)
        assert client.host == fake_hub.host
        assert client.port == fake_hub.port
    finally:
        await client.stop()
        await asyncio.sleep(0.02)


async def test_a_link_sends_requests_for_its_agent(
    fake_hub: FakeHub, session: aiohttp.ClientSession
) -> None:
    client = make_client(session, fake_hub)
    client.start()
    try:
        await fake_hub.wait_for(lambda: client.connected)
        agents = {agent.id: agent for agent in await client.get_agents()}
        link = AgentLink(client, agents[MEDIA_ID])

        manifest = await link.get_manifest()
        assert manifest["agent"]["id"] == MEDIA_ID
        assert fake_hub.manifest_requests == [MEDIA_ID]

        await link.subscribe(["cpu.usage"])
        assert fake_hub.subscribed(MEDIA_ID) == [["cpu.usage"]]
        assert fake_hub.subscribed(GAMING_ID) == []

        result = await link.run_command(
            "set_display_profile", {"Profile": "TV-Mode"}, user_id=USER_ID
        )
        assert result.exit_code == 0
        run = fake_hub.runs[-1]
        assert run["a"] == MEDIA_ID
        assert run["vars"] == {"Profile": "TV-Mode"}
        assert run["context"] == {"user_id": USER_ID}

        fake_hub.run_errors["boom"] = ("busy", "Another command is running.")
        with pytest.raises(RequestError) as failed:
            await link.run_command("boom")
        assert failed.value.code == "busy"

        # The subscription is sent again when a new connection forgot it.
        fake_hub.drop_connections()
        await fake_hub.wait_for(lambda: not client.connected)
        client.trigger_connect()
        await fake_hub.wait_for(lambda: client.connected)
        await link.async_resubscribe()
        assert fake_hub.subscribed(MEDIA_ID) == [["cpu.usage"], ["cpu.usage"]]

        await link.stop()
        # Frames of the agent are not routed to a stopped link any more.
        states: list[Any] = []
        link.add_state_listener(states.append)
        await fake_hub.send_state(MEDIA_ID, {"cpu.usage": 1})
        await asyncio.sleep(0.1)
        assert states == []
    finally:
        await client.stop()
        await asyncio.sleep(0.02)


async def test_a_link_reports_what_the_hub_does_not_know(
    fake_hub: FakeHub, session: aiohttp.ClientSession
) -> None:
    client = make_client(session, fake_hub)
    ghost = FleetAgent(id="ghost", name="Ghost", online=False, power_state="unknown")
    link = AgentLink(client, ghost)
    with pytest.raises(NotConnectedError):
        await link.get_manifest()

    client.start()
    try:
        await fake_hub.wait_for(lambda: client.connected)
        with pytest.raises(RequestError) as unknown:
            await link.get_manifest()
        assert unknown.value.code == "unknown_agent"

        # An agent that never connected has no manifest in the hub yet.
        fake_hub.add_agent("new", "New-PC", known=False)
        new = AgentLink(client, fake_hub_entry(fake_hub, "new"))
        with pytest.raises(RequestError) as offline:
            await new.get_manifest()
        assert offline.value.code == "agent_offline"
    finally:
        await client.stop()
        await asyncio.sleep(0.02)


def fake_hub_entry(hub: FakeHub, agent_id: str) -> FleetAgent:
    """Return what ``get_agents`` says about an agent of the fake hub."""
    agent = hub.agents[agent_id]
    return FleetAgent(
        id=agent.id,
        name=agent.name,
        online=agent.online,
        power_state=agent.power_state,
    )


async def test_the_presence_of_an_agent_becomes_connection_and_power_state(
    session: aiohttp.ClientSession,
) -> None:
    fleet = FleetClient(session, "127.0.0.1", 1, TOKEN, WRONG_FINGERPRINT)
    agent = FleetAgent(id="pc", name="PC", online=False, power_state="shutdown")
    link = AgentLink(fleet, agent)
    connections: list[bool] = []
    byes: list[str] = []
    link.add_connection_listener(connections.append)
    link.add_bye_listener(lambda frame: byes.append(frame.reason))

    # An agent that is off when the entry starts: the reason of its last ``bye`` shows.
    link.apply(agent)
    assert byes == ["shutdown"]
    assert connections == []
    assert not link.connected
    assert link.last_bye is not None
    assert link.last_bye.reason == "shutdown"
    link.apply(agent)
    assert byes == ["shutdown"]  # the same reason is not told twice

    # It comes online after a shutdown: that is a start of the PC.
    link.apply(dataclasses.replace(agent, online=True, power_state="unknown"))
    assert connections == [True]
    assert link.connected
    assert link.last_bye is None
    assert link.hello is not None
    assert link.hello.agent.id == "pc"
    assert link.hello.boot_id == "hub-1"
    link.handle_frame(AgentStatus(agent_id="pc", online=True, power_state="unknown"))
    assert connections == [True]  # still the same connection

    # A restart is a start as well, a resume from sleep is none.
    link.handle_frame(AgentStatus(agent_id="pc", online=False, power_state="restart"))
    assert byes == ["shutdown", "restart"]
    assert connections == [True, False]
    link.handle_frame(AgentStatus(agent_id="pc", online=True, power_state="unknown"))
    assert link.hello.boot_id == "hub-2"
    link.handle_frame(AgentStatus(agent_id="pc", online=False, power_state="sleep"))
    link.handle_frame(AgentStatus(agent_id="pc", online=True, power_state="unknown"))
    assert link.hello.boot_id == "hub-2"

    # Losing the hub forgets everything: no start is derived from what was before.
    link.handle_frame(AgentStatus(agent_id="pc", online=False, power_state="shutdown"))
    link.handle_fleet_down()
    link.handle_fleet_down()
    link.apply(dataclasses.replace(agent, online=True, power_state="unknown"))
    assert link.hello.boot_id == "hub-2"
    assert connections[-2:] == [False, True]

    # Frames of the agent become the frames of a direct link.
    states: list[Any] = []
    events: list[Any] = []
    revisions: list[str] = []
    link.add_state_listener(states.append)
    link.add_event_listener(events.append)
    link.add_manifest_listener(lambda frame: revisions.append(frame.rev))
    link.handle_frame(AgentState(agent_id="pc", ts=7, values={"cpu.usage": 3}))
    link.handle_frame(AgentEvent(agent_id="pc", type="x", ts=8, data={"a": 1}))
    link.handle_frame(AgentManifestChanged(agent_id="pc", rev="0123456789ab"))
    assert [(state.ts, dict(state.values)) for state in states] == [
        (7, {"cpu.usage": 3})
    ]
    assert [(event.type, event.ts, dict(event.data)) for event in events] == [
        ("x", 8, {"a": 1})
    ]
    assert revisions == ["0123456789ab"]
    # The next hello announces the revision that the hub told last.
    link.handle_fleet_down()
    link.apply(dataclasses.replace(agent, online=True, power_state="unknown"))
    assert link.hello is not None
    assert link.hello.manifest_rev == "0123456789ab"


# -- the hub entry ----------------------------------------------------------------


async def test_a_hub_with_two_agents_creates_two_devices_under_one_entry(
    hass: HomeAssistant, fake_hub: FakeHub, hub_entry_factory: EntryFactory
) -> None:
    entry = await hub_entry_factory()

    assert entry.state is ConfigEntryState.LOADED
    assert hass.config_entries.async_entries(DOMAIN) == [entry]
    assert isinstance(entry.runtime_data, FleetRuntimeData)
    hub = hub_device(hass, fake_hub)
    assert hub.name == fake_hub.hub_name
    assert hub.sw_version == "1.0.0"
    registry = er.async_get(hass)
    for agent_id in (GAMING_ID, MEDIA_ID):
        device = agent_device(hass, agent_id)
        assert device is not None
        assert device.via_device_id == hub.id
        assert device.config_entry_id == entry.entry_id
        for domain, key in EXPECTED_ENTITIES:
            entity_id = find_entity_id(hass, domain, agent_id, key)
            assert entity_id is not None, (agent_id, domain, key)
            registered = registry.async_get(entity_id)
            assert registered is not None
            assert registered.config_entry_id == entry.entry_id
            assert registered.device_id == device.id
    # The PC with a known MAC address has the Wake-on-LAN button, the other has none.
    assert find_entity_id(hass, "button", GAMING_ID, "_wake_on_lan") is not None
    assert find_entity_id(hass, "button", MEDIA_ID, "_wake_on_lan") is None
    assert entry.data[CONF_AGENT_MACS] == {GAMING_ID: [GAMING_MAC_STORED]}
    # The device of a PC shows what the hub knows about it.
    gaming = agent_device(hass, GAMING_ID)
    assert gaming is not None
    assert gaming.name == "Gaming-PC"
    assert gaming.sw_version == "1.0.0"
    # Every unique id is registered once.
    unique_ids = [
        e.unique_id for e in registry.entities.values() if e.platform == DOMAIN
    ]
    assert len(unique_ids) == len(set(unique_ids))


async def test_values_reach_the_entities_of_their_own_agent(
    hass: HomeAssistant, fake_hub: FakeHub, hub_entry_factory: EntryFactory
) -> None:
    await hub_entry_factory()
    gaming_cpu = entity_id_of(hass, "sensor", GAMING_ID, "cpu.usage")
    media_cpu = entity_id_of(hass, "sensor", MEDIA_ID, "cpu.usage")
    # The snapshot after the subscription of every agent.
    await eventually(
        hass,
        lambda: (
            state_of(hass, gaming_cpu) == "12" and state_of(hass, media_cpu) == "12"
        ),
    )

    await fake_hub.send_state(MEDIA_ID, {"cpu.usage": 77})

    await eventually(hass, lambda: state_of(hass, media_cpu) == "77")
    assert state_of(hass, gaming_cpu) == "12"


async def test_every_agent_has_its_own_subscription(
    hass: HomeAssistant, fake_hub: FakeHub, hub_entry_factory: EntryFactory
) -> None:
    entry = await hub_entry_factory()
    await fake_hub.wait_for(
        lambda: fake_hub.subscribed(GAMING_ID) and fake_hub.subscribed(MEDIA_ID)
    )

    for agent_id in (GAMING_ID, MEDIA_ID):
        metrics = fake_hub.subscribed(agent_id)[-1]
        assert isinstance(metrics, list)
        assert "cpu.usage" in metrics
        assert set(metrics) == set(runtime_of(entry, agent_id).subscriptions.metric_ids)


async def test_run_command_runs_through_the_hub(
    hass: HomeAssistant, fake_hub: FakeHub, hub_entry_factory: EntryFactory
) -> None:
    await hub_entry_factory()
    device = agent_device(hass, GAMING_ID)
    assert device is not None

    response = await run_action(
        hass,
        device.id,
        "set_display_profile",
        variables={"Profile": "TV-Mode"},
    )

    assert response["exit_code"] == 0
    assert response["stdout"] == "Applied TV-Mode\n"
    run = fake_hub.runs[-1]
    assert run["a"] == GAMING_ID
    assert run["command"] == "set_display_profile"
    assert run["vars"] == {"Profile": "TV-Mode"}
    # The hub forwards the Home Assistant user to the agent (and its audit log).
    assert run["context"] == {"user_id": USER_ID}


async def test_a_command_entity_runs_through_the_hub(
    hass: HomeAssistant, fake_hub: FakeHub, hub_entry_factory: EntryFactory
) -> None:
    await hub_entry_factory()

    await press(hass, entity_id_of(hass, "button", MEDIA_ID, "lock_session"))

    run = fake_hub.runs[-1]
    assert run["a"] == MEDIA_ID
    assert run["command"] == "lock_session"
    assert run["wait"] is False


async def test_a_command_for_an_agent_that_is_off_says_so(
    hass: HomeAssistant, fake_hub: FakeHub, hub_entry_factory: EntryFactory
) -> None:
    await hub_entry_factory()
    device = agent_device(hass, MEDIA_ID)
    assert device is not None
    power = entity_id_of(hass, "sensor", MEDIA_ID, "_power_state")
    await fake_hub.send_status(MEDIA_ID, online=False, power_state="shutdown")
    await eventually(hass, lambda: state_of(hass, power) == "shutdown")

    with pytest.raises(HomeAssistantError) as error:
        await run_action(hass, device.id, "lock_session")

    assert not isinstance(error.value, ServiceValidationError)
    assert error.value.translation_key == "agent_offline"


async def test_the_device_of_the_hub_is_not_a_pc(
    hass: HomeAssistant, fake_hub: FakeHub, hub_entry_factory: EntryFactory
) -> None:
    await hub_entry_factory()

    with pytest.raises(ServiceValidationError) as error:
        await run_action(hass, hub_device(hass, fake_hub).id, "lock_session")

    assert error.value.translation_key == "not_a_served_pc"


async def test_events_of_an_agent_are_fired_with_the_device_of_that_agent(
    hass: HomeAssistant, fake_hub: FakeHub, hub_entry_factory: EntryFactory
) -> None:
    await hub_entry_factory()
    events = async_capture_events(hass, EVENT_QOFFEEAUTOMAT)

    await fake_hub.send_event(MEDIA_ID, "notification_action", {"action": "open"})

    await eventually(hass, lambda: len(events) == 1)
    media = agent_device(hass, MEDIA_ID)
    assert media is not None
    assert events[0].data == {
        "device_id": media.id,
        "type": "notification_action",
        "data": {"action": "open"},
    }


async def test_an_agent_that_goes_away_and_comes_back(
    hass: HomeAssistant, fake_hub: FakeHub, hub_entry_factory: EntryFactory
) -> None:
    await hub_entry_factory()
    events = async_capture_events(hass, EVENT_QOFFEEAUTOMAT)
    cpu = entity_id_of(hass, "sensor", GAMING_ID, "cpu.usage")
    media_cpu = entity_id_of(hass, "sensor", MEDIA_ID, "cpu.usage")
    power = entity_id_of(hass, "sensor", GAMING_ID, "_power_state")
    connectivity = entity_id_of(hass, "binary_sensor", GAMING_ID, "_connectivity")
    await eventually(hass, lambda: state_of(hass, cpu) == "12")
    assert state_of(hass, power) == "running"
    assert state_of(hass, connectivity) == "on"

    await fake_hub.send_status(GAMING_ID, online=False, power_state="shutdown")

    await eventually(hass, lambda: state_of(hass, power) == "shutdown")
    assert state_of(hass, cpu) == "unavailable"
    assert state_of(hass, connectivity) == "off"
    assert state_of(hass, media_cpu) == "12"  # the other PC is not touched

    await fake_hub.send_status(GAMING_ID, online=True)

    await eventually(hass, lambda: state_of(hass, power) == "running")
    assert state_of(hass, connectivity) == "on"
    await eventually(hass, lambda: state_of(hass, cpu) == "12")
    # After a shutdown the next appearance is a start of the PC, after sleep it is not.
    gaming = agent_device(hass, GAMING_ID)
    assert gaming is not None
    assert [(e.data["type"], e.data["device_id"]) for e in events] == [
        ("booted", gaming.id)
    ]
    await fake_hub.send_status(GAMING_ID, online=False, power_state="sleep")
    await eventually(hass, lambda: state_of(hass, power) == "sleep")
    await fake_hub.send_status(GAMING_ID, online=True)
    await eventually(hass, lambda: state_of(hass, power) == "running")
    assert len(events) == 1


async def test_losing_the_hub_makes_every_agent_unavailable_until_it_is_back(
    hass: HomeAssistant, fake_hub: FakeHub, hub_entry_factory: EntryFactory
) -> None:
    entry = await hub_entry_factory()
    cpus = [
        entity_id_of(hass, "sensor", agent_id, "cpu.usage")
        for agent_id in (GAMING_ID, MEDIA_ID)
    ]
    await eventually(hass, lambda: all(state_of(hass, cpu) == "12" for cpu in cpus))
    subscriptions = len(fake_hub.subscribed(GAMING_ID))

    fake_hub.drop_connections()

    await eventually(
        hass, lambda: all(state_of(hass, cpu) == "unavailable" for cpu in cpus)
    )
    manager_of(entry).client.trigger_connect()
    await eventually(hass, lambda: all(state_of(hass, cpu) == "12" for cpu in cpus))
    # The new connection knew nothing: the subscriptions were sent again.
    assert len(fake_hub.subscribed(GAMING_ID)) > subscriptions


async def test_agents_appear_and_disappear_while_the_entry_is_loaded(
    hass: HomeAssistant, fake_hub: FakeHub, hub_entry_factory: EntryFactory
) -> None:
    entry = await hub_entry_factory()
    hub = hub_device(hass, fake_hub)

    office = fake_hub.add_agent("office-pc", "Office-PC", macs=["A4:B1:C1:00:11:99"])
    await fake_hub.send_agent_added(office)

    await eventually(
        hass,
        lambda: find_entity_id(hass, "sensor", "office-pc", "cpu.usage") is not None,
    )
    device = agent_device(hass, "office-pc")
    assert device is not None
    assert device.via_device_id == hub.id
    # The hub told the MAC address with the agent: the button exists at once.
    assert find_entity_id(hass, "button", "office-pc", "_wake_on_lan") is not None
    assert manager_of(entry).served_agent_ids == [GAMING_ID, MEDIA_ID, "office-pc"]

    await fake_hub.send_agent_removed(MEDIA_ID)

    await eventually(hass, lambda: agent_device(hass, MEDIA_ID) is None)
    assert entries_of_agent(hass, MEDIA_ID) == []
    assert manager_of(entry).served_agent_ids == [GAMING_ID, "office-pc"]
    assert not manager_of(entry).knows(MEDIA_ID)
    # The others are not touched.
    assert find_entity_id(hass, "sensor", GAMING_ID, "cpu.usage") is not None
    assert agent_device(hass, GAMING_ID) is not None


async def test_the_list_is_read_again_after_a_reconnect(
    hass: HomeAssistant, fake_hub: FakeHub, hub_entry_factory: EntryFactory
) -> None:
    """Changes that happened while the link was down are found when it is back."""
    entry = await hub_entry_factory()
    client = manager_of(entry).client
    fake_hub.drop_connections()
    await fake_hub.wait_for(lambda: not client.connected)
    # While the link is down: one PC goes, another appears.
    del fake_hub.agents[MEDIA_ID]
    fake_hub.add_agent("lab-pc", "Lab-PC")

    client.trigger_connect()

    await eventually(hass, lambda: agent_device(hass, "lab-pc") is not None)
    await eventually(hass, lambda: agent_device(hass, MEDIA_ID) is None)
    assert manager_of(entry).served_agent_ids == [GAMING_ID, "lab-pc"]


async def test_a_pc_that_never_connected_gets_its_manifest_later(
    hass: HomeAssistant, fake_hub: FakeHub, hub_entry_factory: EntryFactory
) -> None:
    fake_hub.add_agent("fresh-pc", "Fresh-PC", online=False, known=False)
    entry = await hub_entry_factory()

    # No manifest yet: the PC has its device and the entities that need none.
    assert agent_device(hass, "fresh-pc") is not None
    assert (
        find_entity_id(hass, "binary_sensor", "fresh-pc", "_connectivity") is not None
    )
    assert find_entity_id(hass, "sensor", "fresh-pc", "cpu.usage") is None
    assert not runtime_of(entry, "fresh-pc").manifest_manager.loaded

    # It connects to the hub: the hub has a manifest now and tells.
    agent = fake_hub.agents["fresh-pc"]
    agent.manifest = load_fixture("manifest.json")
    agent.manifest["agent"]["id"] = "fresh-pc"
    agent.manifest["agent"]["macs"] = ["aa:bb:cc:dd:ee:02"]
    await fake_hub.send_status("fresh-pc", online=True)
    await fake_hub.send_manifest_changed("fresh-pc")

    await eventually(
        hass,
        lambda: find_entity_id(hass, "sensor", "fresh-pc", "cpu.usage") is not None,
    )


async def test_the_wake_on_lan_button_works_on_the_hub_route(
    hass: HomeAssistant, fake_hub: FakeHub, hub_entry_factory: EntryFactory
) -> None:
    entry = await hub_entry_factory()
    wake = entity_id_of(hass, "button", GAMING_ID, "_wake_on_lan")
    # The button of a PC is available while the PC is off: that is when it is needed.
    await fake_hub.send_status(GAMING_ID, online=False, power_state="shutdown")
    await eventually(
        hass,
        lambda: (
            state_of(hass, entity_id_of(hass, "sensor", GAMING_ID, "_power_state"))
            == "shutdown"
        ),
    )
    assert state_of(hass, wake) != "unavailable"

    with patch(SEND, new=AsyncMock(return_value=1)) as send:
        await press(hass, wake)

    send.assert_awaited_once_with([GAMING_MAC_STORED], DEFAULT_BROADCAST)
    client = runtime_of(entry, GAMING_ID).client
    assert client.fast_retry_active
    assert WOL_FAST_RETRY_SECONDS > 0

    # The broadcast address is the option of the hub entry.
    hass.config_entries.async_update_entry(
        entry, options={CONF_BROADCAST: "192.168.1.255"}
    )
    with patch(SEND, new=AsyncMock(return_value=1)) as send:
        await press(hass, wake)
    send.assert_awaited_once_with([GAMING_MAC_STORED], "192.168.1.255")


async def test_run_command_can_wake_a_pc_through_the_hub(
    hass: HomeAssistant, fake_hub: FakeHub, hub_entry_factory: EntryFactory
) -> None:
    entry = await hub_entry_factory()
    device = agent_device(hass, GAMING_ID)
    assert device is not None
    await fake_hub.send_status(GAMING_ID, online=False, power_state="shutdown")
    await eventually(hass, lambda: not runtime_of(entry, GAMING_ID).connection.online)

    async def wake_up(*args: Any) -> int:
        # The PC starts as soon as the packets are sent: it dials the hub.
        await fake_hub.send_status(GAMING_ID, online=True)
        return 1

    with patch(
        "custom_components.qoffeeautomat.services.async_send_magic_packets",
        new=wake_up,
    ):
        response = await run_action(
            hass,
            device.id,
            "lock_session",
            wake=True,
            wait_for="connected",
            ready_timeout=10,
        )

    assert response["exit_code"] == 0
    assert fake_hub.runs[-1]["a"] == GAMING_ID


# -- setup that fails -------------------------------------------------------------


async def test_a_rejected_token_starts_reauthentication(
    hass: HomeAssistant, fake_hub: FakeHub, hub_entry_factory: EntryFactory
) -> None:
    entry = await hub_entry_factory(**{CONF_TOKEN: TOKEN})

    assert entry.state is ConfigEntryState.SETUP_ERROR
    flows = hass.config_entries.flow.async_progress_by_handler(DOMAIN)
    assert [flow["context"]["source"] for flow in flows] == ["reauth"]


async def test_a_hub_that_is_down_is_retried(
    hass: HomeAssistant, fake_hub: FakeHub, hub_entry_factory: EntryFactory
) -> None:
    await fake_hub.stop()

    entry = await hub_entry_factory()

    assert entry.state is ConfigEntryState.SETUP_RETRY


async def test_a_hub_without_the_fleet_link_is_retried(
    hass: HomeAssistant, fake_hub: FakeHub, hub_entry_factory: EntryFactory
) -> None:
    fake_hub.features = []

    entry = await hub_entry_factory()

    assert entry.state is ConfigEntryState.SETUP_RETRY


async def test_another_hub_at_the_address_is_refused(
    hass: HomeAssistant, fake_hub: FakeHub, hub_entry_factory: EntryFactory
) -> None:
    entry = await hub_entry_factory(setup=False)
    fake_hub.hub_id = "a-different-hub"

    assert not await hass.config_entries.async_setup(entry.entry_id)

    assert entry.state is ConfigEntryState.SETUP_ERROR


async def test_a_protocol_mismatch_raises_the_repair_issue_of_the_agent(
    hass: HomeAssistant, fake_hub: FakeHub, hub_entry_factory: EntryFactory
) -> None:
    fake_hub.protocol_range = (2, 3)

    entry = await hub_entry_factory()

    assert entry.state is ConfigEntryState.SETUP_ERROR
    issue = ir.async_get(hass).async_get_issue(
        DOMAIN, protocol_mismatch_issue_id(entry.entry_id)
    )
    assert issue is not None
    fake_hub.protocol_range = (1, 1)
    assert await hass.config_entries.async_reload(entry.entry_id)
    await hass.async_block_till_done()
    assert entry.state is ConfigEntryState.LOADED
    assert (
        ir.async_get(hass).async_get_issue(
            DOMAIN, protocol_mismatch_issue_id(entry.entry_id)
        )
        is None
    )


async def test_the_hub_entry_unloads_and_loads_again(
    hass: HomeAssistant, fake_hub: FakeHub, hub_entry_factory: EntryFactory
) -> None:
    entry = await hub_entry_factory()

    assert await hass.config_entries.async_unload(entry.entry_id)
    await hass.async_block_till_done()
    assert entry.state is ConfigEntryState.NOT_LOADED
    # The registry keeps the entities; they are unavailable meanwhile.
    cpu = entity_id_of(hass, "sensor", GAMING_ID, "cpu.usage")
    assert state_of(hass, cpu) == "unavailable"

    assert await hass.config_entries.async_setup(entry.entry_id)
    await hass.async_block_till_done()
    await eventually(hass, lambda: state_of(hass, cpu) == "12")
    assert entity_id_of(hass, "sensor", GAMING_ID, "cpu.usage") == cpu


# -- one route per PC -------------------------------------------------------------


async def test_an_agent_with_a_direct_entry_exists_once_and_raises_a_repair_issue(
    hass: HomeAssistant,
    fake_agent: FakeAgent,
    fake_hub: FakeHub,
    entry_factory: EntryFactory,
    hub_entry_factory: EntryFactory,
) -> None:
    direct = await entry_factory()
    hub = await hub_entry_factory()

    fleet = manager_of(hub)
    assert fleet.knows(GAMING_ID)
    assert fleet.runtime_of(GAMING_ID) is None
    assert fleet.served_agent_ids == [MEDIA_ID]
    assert direct_entry_for(hass, GAMING_ID) is direct
    assert direct_entry_for(hass, MEDIA_ID) is None
    assert direct_entry_for(hass, GAMING_ID, ignoring={direct.entry_id}) is None
    assert hub_entry_knowing(hass, GAMING_ID) is hub
    assert hub_entry_knowing(hass, "nobody") is None
    assert loaded_fleets(hass) == [fleet]

    # No unique id is registered twice, and the PC has one device and one set
    # of entities.
    unique_ids = [
        e.unique_id
        for e in er.async_get(hass).entities.values()
        if e.platform == DOMAIN
    ]
    assert len(unique_ids) == len(set(unique_ids))
    devices = dr.async_get(hass).async_get_devices(identifiers={(DOMAIN, GAMING_ID)})
    assert len(devices) == 1
    assert devices[0].via_device_id is None
    for registered in entries_of_agent(hass, GAMING_ID):
        assert registered.config_entry_id == direct.entry_id

    issue = ir.async_get(hass).async_get_issue(
        DOMAIN, direct_route_issue_id(hub.entry_id, GAMING_ID)
    )
    assert issue is not None
    assert issue.is_fixable
    assert issue.severity is ir.IssueSeverity.WARNING
    assert issue.translation_key == "direct_route"
    assert issue.translation_placeholders == {
        "name": "Gaming-PC",
        "hub": fake_hub.hub_name,
    }
    assert issue.data == {DATA_HUB_ENTRY_ID: hub.entry_id, DATA_AGENT_ID: GAMING_ID}
    # The PC that has no entry of its own is served by the hub.
    assert find_entity_id(hass, "sensor", MEDIA_ID, "cpu.usage") is not None


async def test_the_hub_serves_a_pc_when_its_direct_entry_is_removed(
    hass: HomeAssistant,
    fake_agent: FakeAgent,
    fake_hub: FakeHub,
    entry_factory: EntryFactory,
    hub_entry_factory: EntryFactory,
) -> None:
    direct = await entry_factory()
    hub = await hub_entry_factory()
    issue_id = direct_route_issue_id(hub.entry_id, GAMING_ID)
    assert ir.async_get(hass).async_get_issue(DOMAIN, issue_id) is not None

    await hass.config_entries.async_remove(direct.entry_id)

    await eventually(hass, lambda: manager_of(hub).runtime_of(GAMING_ID) is not None)
    assert ir.async_get(hass).async_get_issue(DOMAIN, issue_id) is None
    cpu = entity_id_of(hass, "sensor", GAMING_ID, "cpu.usage")
    registered = er.async_get(hass).async_get(cpu)
    assert registered is not None
    assert registered.config_entry_id == hub.entry_id
    await eventually(hass, lambda: state_of(hass, cpu) == "12")


async def test_removing_the_hub_entry_removes_its_repair_issues(
    hass: HomeAssistant,
    fake_agent: FakeAgent,
    fake_hub: FakeHub,
    entry_factory: EntryFactory,
    hub_entry_factory: EntryFactory,
) -> None:
    await entry_factory()
    hub = await hub_entry_factory()
    issue_id = direct_route_issue_id(hub.entry_id, GAMING_ID)
    assert ir.async_get(hass).async_get_issue(DOMAIN, issue_id) is not None

    await hass.config_entries.async_remove(hub.entry_id)
    await hass.async_block_till_done()

    assert ir.async_get(hass).async_get_issue(DOMAIN, issue_id) is None


async def test_switching_the_route_both_ways_keeps_entity_ids_and_names(
    hass: HomeAssistant,
    fake_agent: FakeAgent,
    fake_hub: FakeHub,
    entry_factory: EntryFactory,
    hub_entry_factory: EntryFactory,
) -> None:
    direct = await entry_factory()
    hub = await hub_entry_factory()
    registry = er.async_get(hass)
    devices = dr.async_get(hass)
    cpu = entity_id_of(hass, "sensor", GAMING_ID, "cpu.usage")
    registry.async_update_entity(cpu, name="My CPU")
    device = agent_device(hass, GAMING_ID)
    assert device is not None
    before = {e.unique_id: e.entity_id for e in entries_of_agent(hass, GAMING_ID)}
    assert len(before) >= len(EXPECTED_ENTITIES)
    await eventually(hass, lambda: state_of(hass, cpu) == "12")

    # -- direct route to hub route ---------------------------------------------------
    await async_switch_to_hub(hass, hub.entry_id, GAMING_ID)
    await hass.async_block_till_done()

    assert hass.config_entries.async_get_entry(direct.entry_id) is None
    after = {e.unique_id: e.entity_id for e in entries_of_agent(hass, GAMING_ID)}
    assert after == before
    for registered in entries_of_agent(hass, GAMING_ID):
        assert registered.config_entry_id == hub.entry_id
        assert registered.device_id == device.id
    renamed = registry.async_get(cpu)
    assert renamed is not None
    assert renamed.name == "My CPU"
    moved = devices.async_get(device.id)
    assert moved is not None
    assert moved.config_entry_id == hub.entry_id
    assert moved.via_device_id == hub_device(hass, fake_hub).id
    assert manager_of(hub).runtime_of(GAMING_ID) is not None
    assert (
        ir.async_get(hass).async_get_issue(
            DOMAIN, direct_route_issue_id(hub.entry_id, GAMING_ID)
        )
        is None
    )
    # The entities work through the hub: values arrive and commands run.
    await fake_hub.send_state(GAMING_ID, {"cpu.usage": 55})
    await eventually(hass, lambda: state_of(hass, cpu) == "55")
    state = hass.states.get(cpu)
    assert state is not None
    assert state.attributes["friendly_name"] == "My CPU"
    await run_action(hass, device.id, "lock_session")
    assert fake_hub.runs[-1]["a"] == GAMING_ID

    # -- hub route to direct route ---------------------------------------------------
    back = await entry_factory()
    await hass.async_block_till_done()

    after_back = {e.unique_id: e.entity_id for e in entries_of_agent(hass, GAMING_ID)}
    assert after_back == before
    for registered in entries_of_agent(hass, GAMING_ID):
        assert registered.config_entry_id == back.entry_id
        assert registered.device_id == device.id
    renamed = registry.async_get(cpu)
    assert renamed is not None
    assert renamed.name == "My CPU"
    returned = devices.async_get(device.id)
    assert returned is not None
    assert returned.config_entry_id == back.entry_id
    assert returned.via_device_id is None
    # The hub skips the PC again and offers the way back.
    assert manager_of(hub).runtime_of(GAMING_ID) is None
    assert manager_of(hub).knows(GAMING_ID)
    assert (
        ir.async_get(hass).async_get_issue(
            DOMAIN, direct_route_issue_id(hub.entry_id, GAMING_ID)
        )
        is not None
    )
    await eventually(hass, lambda: state_of(hass, cpu) == "12")
    assert agent_runtime_of(back).agent_id == GAMING_ID
    # Still one device and no unique id twice.
    assert devices.async_get_devices(identifiers={(DOMAIN, GAMING_ID)}) == [returned]
    unique_ids = [
        e.unique_id for e in registry.entities.values() if e.platform == DOMAIN
    ]
    assert len(unique_ids) == len(set(unique_ids))


async def test_the_other_agents_of_a_hub_are_not_touched_by_a_switch(
    hass: HomeAssistant,
    fake_agent: FakeAgent,
    fake_hub: FakeHub,
    entry_factory: EntryFactory,
    hub_entry_factory: EntryFactory,
) -> None:
    await entry_factory()
    hub = await hub_entry_factory()
    media_before = {e.unique_id: e.entity_id for e in entries_of_agent(hass, MEDIA_ID)}

    await async_switch_to_hub(hass, hub.entry_id, GAMING_ID)
    await hass.async_block_till_done()

    media_cpu = entity_id_of(hass, "sensor", MEDIA_ID, "cpu.usage")
    await eventually(hass, lambda: state_of(hass, media_cpu) == "12")
    assert {e.unique_id: e.entity_id for e in entries_of_agent(hass, MEDIA_ID)} == (
        media_before
    )
    assert manager_of(hub).served_agent_ids == [GAMING_ID, MEDIA_ID]


async def test_a_switch_is_refused_when_it_cannot_work(
    hass: HomeAssistant,
    fake_agent: FakeAgent,
    fake_hub: FakeHub,
    entry_factory: EntryFactory,
    hub_entry_factory: EntryFactory,
) -> None:
    direct = await entry_factory()
    hub = await hub_entry_factory()

    with pytest.raises(RouteSwitchError) as unknown:
        await async_switch_to_hub(hass, hub.entry_id, "nobody")
    assert unknown.value.reason == "hub_unavailable"
    with pytest.raises(RouteSwitchError) as missing:
        await async_switch_to_hub(hass, "no-such-entry", GAMING_ID)
    assert missing.value.reason == "hub_unavailable"
    # A PC without an entry of its own has nothing to move away from.
    with pytest.raises(RouteSwitchError) as none:
        await async_switch_to_hub(hass, hub.entry_id, MEDIA_ID)
    assert none.value.reason == "no_direct_entry"
    await hass.config_entries.async_unload(hub.entry_id)
    with pytest.raises(RouteSwitchError) as unloaded:
        await async_switch_to_hub(hass, hub.entry_id, GAMING_ID)
    assert unloaded.value.reason == "hub_unavailable"
    # Nothing changed.
    assert hass.config_entries.async_get_entry(direct.entry_id) is direct
    for registered in entries_of_agent(hass, GAMING_ID):
        assert registered.config_entry_id == direct.entry_id


async def test_moving_a_pc_that_no_other_entry_owns_does_nothing(
    hass: HomeAssistant,
    fake_agent: FakeAgent,
    entry_factory: EntryFactory,
) -> None:
    direct = await entry_factory()
    before = {e.unique_id: e.entity_id for e in entries_of_agent(hass, GAMING_ID)}

    routes.async_move_agent(hass, GAMING_ID, direct)
    routes.async_move_agent(hass, "nobody", direct)

    assert {
        e.unique_id: e.entity_id for e in entries_of_agent(hass, GAMING_ID)
    } == before
    await routes.async_adopt_agent(hass, direct)
    adopted = MockConfigEntry(domain=DOMAIN, unique_id=None)
    adopted.add_to_hass(hass)
    await routes.async_adopt_agent(hass, adopted)  # no id, nothing to adopt


# -- the repair flows -------------------------------------------------------------


async def start_repair(hass: HomeAssistant, flow: Any, issue_id: str) -> None:
    """Prepare a repair flow the way Home Assistant does and show its form."""
    flow.hass = hass
    flow.handler = DOMAIN
    flow.context = {"issue_id": issue_id}
    shown = await flow.async_step_init()
    assert shown["type"] is FlowResultType.FORM
    assert shown["step_id"] == "confirm"


async def test_the_route_repair_flow_moves_the_pc_to_the_hub(
    hass: HomeAssistant,
    fake_agent: FakeAgent,
    fake_hub: FakeHub,
    entry_factory: EntryFactory,
    hub_entry_factory: EntryFactory,
) -> None:
    direct = await entry_factory()
    hub = await hub_entry_factory()
    cpu = entity_id_of(hass, "sensor", GAMING_ID, "cpu.usage")
    issue = ir.async_get(hass).async_get_issue(
        DOMAIN, direct_route_issue_id(hub.entry_id, GAMING_ID)
    )
    assert issue is not None
    flow = await async_create_fix_flow(hass, issue.issue_id, issue.data)
    assert isinstance(flow, SwitchToHubRepairFlow)
    await start_repair(hass, flow, issue.issue_id)

    done = await flow.async_step_confirm({})

    assert done["type"] is FlowResultType.CREATE_ENTRY
    await hass.async_block_till_done()
    assert hass.config_entries.async_get_entry(direct.entry_id) is None
    assert entity_id_of(hass, "sensor", GAMING_ID, "cpu.usage") == cpu
    registered = er.async_get(hass).async_get(cpu)
    assert registered is not None
    assert registered.config_entry_id == hub.entry_id


async def test_the_route_repair_flow_aborts_with_the_reason(
    hass: HomeAssistant,
    fake_agent: FakeAgent,
    fake_hub: FakeHub,
    entry_factory: EntryFactory,
    hub_entry_factory: EntryFactory,
) -> None:
    direct = await entry_factory()
    hub = await hub_entry_factory()
    await hass.config_entries.async_unload(hub.entry_id)
    flow = await async_create_fix_flow(
        hass,
        direct_route_issue_id(hub.entry_id, GAMING_ID),
        {DATA_HUB_ENTRY_ID: hub.entry_id, DATA_AGENT_ID: GAMING_ID},
    )
    await start_repair(hass, flow, "direct_route")

    done = await flow.async_step_confirm({})

    assert done["type"] is FlowResultType.ABORT
    assert done["reason"] == "hub_unavailable"
    assert hass.config_entries.async_get_entry(direct.entry_id) is direct


async def test_the_denied_link_repair_flow_looks_for_the_hub(
    hass: HomeAssistant,
    fake_agent: FakeAgent,
    fake_hub: FakeHub,
    entry_factory: EntryFactory,
    hub_entry_factory: EntryFactory,
) -> None:
    direct = await entry_factory()
    flow = await async_create_fix_flow(
        hass,
        direct_link_denied_issue_id(direct.entry_id),
        {DATA_AGENT_ID: GAMING_ID},
    )
    assert isinstance(flow, SwitchToHubRepairFlow)
    await start_repair(hass, flow, "direct_link_denied")

    # No hub entry yet: Home Assistant has to be connected to the hub first.
    done = await flow.async_step_confirm({})
    assert done["type"] is FlowResultType.ABORT
    assert done["reason"] == "hub_unavailable"

    hub = await hub_entry_factory()
    flow = await async_create_fix_flow(
        hass,
        direct_link_denied_issue_id(direct.entry_id),
        {DATA_AGENT_ID: GAMING_ID},
    )
    await start_repair(hass, flow, "direct_link_denied")
    done = await flow.async_step_confirm({})
    assert done["type"] is FlowResultType.CREATE_ENTRY
    await hass.async_block_till_done()
    assert hass.config_entries.async_get_entry(direct.entry_id) is None
    assert manager_of(hub).runtime_of(GAMING_ID) is not None


async def test_the_route_repair_flow_aborts_when_there_is_no_direct_entry(
    hass: HomeAssistant, fake_hub: FakeHub, hub_entry_factory: EntryFactory
) -> None:
    hub = await hub_entry_factory()
    flow = SwitchToHubRepairFlow(MEDIA_ID, hub.entry_id)
    await start_repair(hass, flow, "direct_route")

    done = await flow.async_step_confirm({})

    assert done["type"] is FlowResultType.ABORT
    assert done["reason"] == "no_direct_entry"


async def test_repair_flows_need_the_data_of_their_issue(hass: HomeAssistant) -> None:
    with pytest.raises(ValueError, match="No repair flow"):
        await async_create_fix_flow(hass, "direct_route_x", {DATA_AGENT_ID: "pc"})
    with pytest.raises(ValueError, match="No repair flow"):
        await async_create_fix_flow(hass, "direct_link_denied_x", {"other": "pc"})
    flow = await async_create_fix_flow(
        hass, "direct_link_denied_x", {DATA_AGENT_ID: "pc"}
    )
    assert isinstance(flow, SwitchToHubRepairFlow)


# -- a direct link that the hub of the PC forbids ---------------------------------


async def test_a_denied_direct_link_raises_an_issue_that_offers_the_hub(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    fake_agent.link_status = 403

    entry = await entry_factory()

    assert entry.state is ConfigEntryState.SETUP_RETRY
    issue = ir.async_get(hass).async_get_issue(
        DOMAIN, direct_link_denied_issue_id(entry.entry_id)
    )
    assert issue is not None
    assert issue.is_fixable
    assert issue.translation_key == "direct_link_denied"
    assert issue.data == {DATA_AGENT_ID: fake_agent.agent_id}
    assert fake_agent.host not in json.dumps(issue.translation_placeholders)

    fake_agent.link_status = None
    assert await hass.config_entries.async_reload(entry.entry_id)
    await hass.async_block_till_done()

    assert entry.state is ConfigEntryState.LOADED
    assert (
        ir.async_get(hass).async_get_issue(
            DOMAIN, direct_link_denied_issue_id(entry.entry_id)
        )
        is None
    )


async def test_a_direct_link_that_the_hub_forbids_later_raises_the_issue(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    entry = await entry_factory()
    client = agent_runtime_of(entry).client
    issue_id = direct_link_denied_issue_id(entry.entry_id)
    assert ir.async_get(hass).async_get_issue(DOMAIN, issue_id) is None

    # The policy of the hub changes: the agent closes the clients and refuses new ones.
    fake_agent.link_status = 403
    fake_agent.drop_connections()
    await fake_agent.wait_for(lambda: not client.connected)
    client.trigger_connect()
    await fake_agent.wait_for(lambda: client.direct_link_denied)
    await hass.async_block_till_done()
    assert ir.async_get(hass).async_get_issue(DOMAIN, issue_id) is not None

    # The policy is lifted: the next connection removes the issue.
    fake_agent.link_status = None
    client.trigger_connect()
    await fake_agent.wait_for(lambda: client.connected)
    await hass.async_block_till_done()
    assert not client.direct_link_denied
    assert ir.async_get(hass).async_get_issue(DOMAIN, issue_id) is None


async def test_removing_the_entry_removes_the_denied_link_issue(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    fake_agent.link_status = 403
    entry = await entry_factory()
    issue_id = direct_link_denied_issue_id(entry.entry_id)
    assert ir.async_get(hass).async_get_issue(DOMAIN, issue_id) is not None

    await hass.config_entries.async_remove(entry.entry_id)
    await hass.async_block_till_done()

    assert ir.async_get(hass).async_get_issue(DOMAIN, issue_id) is None


# -- diagnostics ------------------------------------------------------------------


async def test_diagnostics_of_a_hub_contain_no_secret(
    hass: HomeAssistant, fake_hub: FakeHub, hub_entry_factory: EntryFactory
) -> None:
    entry = await hub_entry_factory()
    await fake_hub.send_status(MEDIA_ID, online=False, power_state="sleep")
    power = entity_id_of(hass, "sensor", MEDIA_ID, "_power_state")
    await eventually(hass, lambda: state_of(hass, power) == "sleep")

    diagnostics = await async_get_config_entry_diagnostics(hass, entry)

    data = diagnostics["entry"]["data"]
    assert data[CONF_TOKEN] == REDACTED
    assert data[CONF_FINGERPRINT] == REDACTED
    assert data["host"] == REDACTED
    assert data[CONF_AGENT_MACS] == REDACTED
    assert data[CONF_HUB] is True
    assert diagnostics["hub"]["connected"] is True
    assert diagnostics["hub"]["auth_failed"] is False
    assert diagnostics["hub"]["protocol"] == {"min": 1, "max": 1}
    assert diagnostics["agents"] == {
        GAMING_ID: {"served": True, "online": True, "power_state": "running"},
        MEDIA_ID: {"served": True, "online": False, "power_state": "sleep"},
    }
    text = json.dumps(diagnostics, default=str)
    for secret in (
        fake_hub.token,
        fake_hub.host,
        fake_hub.certificate.fingerprint,
        GAMING_MAC,
        GAMING_MAC_STORED,
        "Gaming-PC",
        fake_hub.hub_name,
    ):
        assert secret not in text, f"diagnostics leak {secret!r}"


async def test_device_diagnostics_of_a_hub_describe_the_pc_or_the_hub(
    hass: HomeAssistant, fake_hub: FakeHub, hub_entry_factory: EntryFactory
) -> None:
    entry = await hub_entry_factory()
    pc = agent_device(hass, GAMING_ID)
    assert pc is not None

    of_pc = await async_get_device_diagnostics(hass, entry, pc)
    of_hub = await async_get_device_diagnostics(hass, entry, hub_device(hass, fake_hub))

    assert of_pc["agent"]["id"] == GAMING_ID
    assert of_pc["agent"]["hostname"] == REDACTED
    assert of_pc["connection"]["online"] is True
    assert {entity["domain"] for entity in of_pc["entities"]} >= {"sensor", "button"}
    assert "hub" in of_hub
    assert "agent" not in of_hub
    assert of_hub["entities"] == []
    assert fake_hub.token not in json.dumps(of_pc, default=str)


async def test_the_entry_of_a_hub_has_no_single_pc(
    hass: HomeAssistant, fake_hub: FakeHub, hub_entry_factory: EntryFactory
) -> None:
    entry = await hub_entry_factory()

    with pytest.raises(TypeError):
        agent_runtime_of(entry)
