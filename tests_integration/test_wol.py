"""Tests of the Wake-on-LAN button (T703).

The packet format and the sending are tested on their own, against a UDP socket on
the loopback interface. The button, the stored MAC addresses, the options flow and the
fast retry of the client are tested with the integration against the fake agent.
"""

import asyncio
from collections.abc import AsyncIterator
from dataclasses import dataclass, field
from functools import partial
from typing import Any
from unittest.mock import AsyncMock, patch

import aiohttp
import pytest
from homeassistant.components.diagnostics import REDACTED
from homeassistant.core import HomeAssistant
from homeassistant.data_entry_flow import FlowResultType
from homeassistant.exceptions import HomeAssistantError
from homeassistant.helpers import entity_registry as er

from custom_components.qoffeeautomat import wol
from custom_components.qoffeeautomat.client import (
    FAST_RETRY_INTERVAL,
    CannotConnectError,
    QoffeeLinkClient,
)
from custom_components.qoffeeautomat.const import (
    CONF_BROADCAST,
    CONF_MACS,
    DEFAULT_BROADCAST,
    WOL_FAST_RETRY_SECONDS,
)
from custom_components.qoffeeautomat.diagnostics import (
    async_get_config_entry_diagnostics,
)
from custom_components.qoffeeautomat.runtime import QoffeeAutomatRuntimeData
from tests_integration.fake_agent import FakeAgent
from tests_integration.helpers import (
    EntryFactory,
    changed_manifest,
    entity_id_of,
    find_entity_id,
    state_of,
)

MAC = "a4:b1:c1:00:11:22"
OTHER_MAC = "a4:b1:c1:00:11:23"
SEND = "custom_components.qoffeeautomat.button.async_send_magic_packets"


def runtime_of(entry: Any) -> QoffeeAutomatRuntimeData:
    """Return the runtime data of a loaded entry."""
    runtime = entry.runtime_data
    assert isinstance(runtime, QoffeeAutomatRuntimeData)
    return runtime


async def press(hass: HomeAssistant, entity_id: str) -> None:
    """Press a button the way a user does."""
    await hass.services.async_call(
        "button", "press", {"entity_id": entity_id}, blocking=True
    )


# -- the magic packet -----------------------------------------------------------------


def test_the_magic_packet_has_the_documented_format() -> None:
    """Six bytes 0xFF, then the MAC address sixteen times: 102 bytes."""
    packet = wol.build_magic_packet("A4:B1:C1:00:11:22")

    assert len(packet) == wol.PACKET_LENGTH == 102
    assert packet[:6] == b"\xff" * 6
    assert packet[6:] == bytes.fromhex("a4b1c1001122") * 16
    assert packet.count(bytes.fromhex("a4b1c1001122")) == 16


@pytest.mark.parametrize(
    "spelling",
    [
        "a4:b1:c1:00:11:22",
        "A4:B1:C1:00:11:22",
        "a4-b1-c1-00-11-22",
        "a4b1.c100.1122",
        "a4b1c1001122",
        "  A4:B1:C1:00:11:22 ",
    ],
)
def test_every_usual_spelling_of_a_mac_address_gives_the_same_packet(
    spelling: str,
) -> None:
    assert wol.normalize_mac(spelling) == MAC
    assert wol.build_magic_packet(spelling) == wol.build_magic_packet(MAC)


@pytest.mark.parametrize(
    "bad",
    ["", "a4:b1:c1:00:11", "a4:b1:c1:00:11:22:33", "zz:b1:c1:00:11:22", "not a mac"],
)
def test_a_text_that_is_not_a_mac_address_is_refused(bad: str) -> None:
    with pytest.raises(ValueError, match="not a MAC address"):
        wol.build_magic_packet(bad)


@pytest.mark.parametrize(
    ("text", "expected"),
    [
        ("192.168.1.255", "192.168.1.255"),
        (" 255.255.255.255 ", "255.255.255.255"),
        ("10.0.0.255", "10.0.0.255"),
    ],
)
def test_a_broadcast_address_is_an_ipv4_address(text: str, expected: str) -> None:
    assert wol.validate_broadcast(text) == expected


@pytest.mark.parametrize("bad", ["", "192.168.1", "pc.local", "::1", "300.1.1.1"])
def test_anything_else_is_not_a_broadcast_address(bad: str) -> None:
    with pytest.raises(ValueError, match="IPv4"):
        wol.validate_broadcast(bad)


# -- sending --------------------------------------------------------------------------


@dataclass
class Listener(asyncio.DatagramProtocol):
    """Collects the datagrams that arrive on a UDP socket."""

    packets: list[bytes] = field(default_factory=list)

    def datagram_received(self, data: bytes, addr: tuple[Any, int]) -> None:
        self.packets.append(data)

    async def wait_for(self, count: int, limit: float = 3.0) -> None:
        """Wait until ``count`` datagrams arrived."""
        async with asyncio.timeout(limit):
            while len(self.packets) < count:  # noqa: ASYNC110
                await asyncio.sleep(0.01)


@pytest.fixture(name="udp")
async def udp_fixture(socket_enabled: None) -> AsyncIterator[tuple[Listener, int]]:
    """A UDP socket on the loopback interface: the listener and its port."""
    loop = asyncio.get_running_loop()
    transport, listener = await loop.create_datagram_endpoint(
        Listener, local_addr=("127.0.0.1", 0)
    )
    yield listener, transport.get_extra_info("sockname")[1]
    transport.close()


async def test_a_magic_packet_is_sent_for_every_mac_address(
    udp: tuple[Listener, int],
) -> None:
    listener, port = udp

    sent = await wol.async_send_magic_packets([MAC, OTHER_MAC], "127.0.0.1", port)
    await listener.wait_for(2)

    assert sent == 2
    assert sorted(listener.packets) == sorted(
        [wol.build_magic_packet(MAC), wol.build_magic_packet(OTHER_MAC)]
    )


async def test_an_address_that_is_listed_twice_gets_one_packet(
    udp: tuple[Listener, int],
) -> None:
    listener, port = udp

    sent = await wol.async_send_magic_packets(
        [MAC, MAC.upper().replace(":", "-"), OTHER_MAC], "127.0.0.1", port
    )
    await listener.wait_for(2)
    await asyncio.sleep(0.1)

    assert sent == 2
    assert len(listener.packets) == 2


async def test_nothing_is_sent_when_an_address_is_invalid(
    udp: tuple[Listener, int],
) -> None:
    listener, port = udp

    with pytest.raises(ValueError, match="not a MAC address"):
        await wol.async_send_magic_packets([MAC, "nonsense"], "127.0.0.1", port)
    with pytest.raises(ValueError, match="IPv4"):
        await wol.async_send_magic_packets([MAC], "pc.local", port)
    await asyncio.sleep(0.1)

    assert listener.packets == []


async def test_without_addresses_nothing_is_sent() -> None:
    assert await wol.async_send_magic_packets([], "255.255.255.255") == 0


async def test_a_network_that_refuses_every_packet_raises_the_error(
    socket_enabled: None,
) -> None:
    loop = asyncio.get_running_loop()
    refuse = AsyncMock(side_effect=OSError("Network is unreachable"))

    with (
        patch.object(loop, "sock_sendto", refuse),
        pytest.raises(OSError, match="unreachable"),
    ):
        await wol.async_send_magic_packets([MAC, OTHER_MAC], "255.255.255.255")

    # The second address was tried although the first one failed.
    assert refuse.await_count == 2


async def test_one_failed_packet_does_not_stop_the_others(
    socket_enabled: None,
) -> None:
    loop = asyncio.get_running_loop()
    partly = AsyncMock(side_effect=[OSError("busy"), 102])

    with patch.object(loop, "sock_sendto", partly):
        sent = await wol.async_send_magic_packets([MAC, OTHER_MAC], "255.255.255.255")

    assert sent == 1


# -- fast retry of the client ---------------------------------------------------------


def make_client(
    session: aiohttp.ClientSession, agent: FakeAgent, **options: Any
) -> QoffeeLinkClient:
    """A client with a long backoff, so that only the fast retry can be quick."""
    return QoffeeLinkClient(
        session,
        agent.host,
        agent.port,
        agent.token,
        agent.certificate.fingerprint,
        backoff_min=60,
        backoff_max=60,
        connect_timeout=2,
        **options,
    )


def test_fast_retry_is_once_per_second_for_five_minutes() -> None:
    assert FAST_RETRY_INTERVAL == 1.0
    assert WOL_FAST_RETRY_SECONDS == 300.0


async def test_fast_retry_attempts_at_the_interval_and_normal_backoff_does_not(
    session: aiohttp.ClientSession, fake_agent: FakeAgent
) -> None:
    await fake_agent.stop()
    client = make_client(session, fake_agent, fast_retry_interval=0.1)
    opens = AsyncMock(side_effect=CannotConnectError("down"))
    with patch.object(client, "_open", opens):
        client.start()
        try:
            await asyncio.sleep(0.5)
            assert opens.await_count == 1  # one attempt, then the 60 s backoff

            opens.reset_mock()
            client.start_fast_retry(WOL_FAST_RETRY_SECONDS)
            assert client.fast_retry_active
            await asyncio.sleep(0.55)
            assert 4 <= opens.await_count <= 8
        finally:
            await client.stop()


async def test_fast_retry_ends_when_the_client_is_connected(
    session: aiohttp.ClientSession, fake_agent: FakeAgent
) -> None:
    await fake_agent.stop()
    client = make_client(session, fake_agent, fast_retry_interval=0.05)
    client.start()
    try:
        await fake_agent.wait_for(lambda: client.last_error is not None)
        client.start_fast_retry(WOL_FAST_RETRY_SECONDS)
        assert client.fast_retry_active

        await fake_agent.start(fake_agent.port)
        await fake_agent.wait_ready(limit=3)
        await fake_agent.wait_for(lambda: client.connected)

        assert not client.fast_retry_active
    finally:
        await client.stop()


async def test_fast_retry_ends_after_its_time(
    session: aiohttp.ClientSession, fake_agent: FakeAgent
) -> None:
    client = make_client(session, fake_agent)

    client.start_fast_retry(0.05)
    assert client.fast_retry_active
    await asyncio.sleep(0.1)

    assert not client.fast_retry_active


# -- the stored MAC addresses ---------------------------------------------------------


async def test_the_mac_addresses_of_the_manifest_are_stored_in_the_entry(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    fake_agent.manifest["agent"]["macs"] = ["A4:B1:C1:00:11:22", "A4:B1:C1:00:11:23"]

    entry = await entry_factory()

    # Stored in the spelling that the device registry uses: lower case.
    assert entry.data[CONF_MACS] == [MAC, OTHER_MAC]


async def test_changed_addresses_replace_the_stored_ones(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    entry = await entry_factory()
    assert entry.data[CONF_MACS] == [MAC]

    manifest = changed_manifest(fake_agent)
    manifest["agent"]["macs"] = ["A4:B1:C1:00:11:99"]
    fake_agent.set_manifest(manifest)
    await fake_agent.send_manifest_changed()
    await fake_agent.wait_for(lambda: entry.data[CONF_MACS] == ["a4:b1:c1:00:11:99"])

    assert find_entity_id(hass, "button", fake_agent.agent_id, "_wake_on_lan")


async def test_a_manifest_without_addresses_keeps_the_stored_ones(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    entry = await entry_factory()
    stored_rev = runtime_of(entry).manifest_manager.rev

    manifest = changed_manifest(fake_agent)
    manifest["agent"]["macs"] = []
    fake_agent.set_manifest(manifest)
    await fake_agent.send_manifest_changed()
    await fake_agent.wait_for(
        lambda: runtime_of(entry).manifest_manager.rev != stored_rev
    )

    assert entry.data[CONF_MACS] == [MAC]


async def test_an_agent_without_addresses_gets_no_button(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    fake_agent.manifest["agent"]["macs"] = []

    entry = await entry_factory()

    assert CONF_MACS not in entry.data
    assert find_entity_id(hass, "button", fake_agent.agent_id, "_wake_on_lan") is None


async def test_the_button_appears_when_the_agent_reports_addresses_later(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    fake_agent.manifest["agent"]["macs"] = []
    entry = await entry_factory()
    assert find_entity_id(hass, "button", fake_agent.agent_id, "_wake_on_lan") is None

    manifest = changed_manifest(fake_agent)
    manifest["agent"]["macs"] = ["A4:B1:C1:00:11:22"]
    fake_agent.set_manifest(manifest)
    await fake_agent.send_manifest_changed()

    await fake_agent.wait_for(
        lambda: (
            find_entity_id(hass, "button", fake_agent.agent_id, "_wake_on_lan")
            is not None
        ),
        limit=10,
    )
    assert entry.data[CONF_MACS] == [MAC]


# -- the button -----------------------------------------------------------------------


async def test_the_button_has_a_translated_name_and_a_stable_unique_id(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    await entry_factory()

    wake = entity_id_of(hass, "button", fake_agent.agent_id, "_wake_on_lan")
    registered = er.async_get(hass).async_get(wake)

    assert registered is not None
    assert registered.unique_id == f"{fake_agent.agent_id}__wake_on_lan"
    assert registered.translation_key == "wake_on_lan"
    assert registered.has_entity_name


async def test_a_press_sends_the_packets_for_every_stored_address(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    fake_agent.manifest["agent"]["macs"] = ["A4:B1:C1:00:11:22", "A4:B1:C1:00:11:23"]
    await entry_factory()
    wake = entity_id_of(hass, "button", fake_agent.agent_id, "_wake_on_lan")

    with patch(SEND, AsyncMock(return_value=2)) as send:
        await press(hass, wake)

    send.assert_awaited_once_with([MAC, OTHER_MAC], DEFAULT_BROADCAST)


async def test_the_broadcast_address_of_the_options_is_used(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    entry = await entry_factory()
    hass.config_entries.async_update_entry(
        entry, options={CONF_BROADCAST: "192.168.1.255"}
    )
    wake = entity_id_of(hass, "button", fake_agent.agent_id, "_wake_on_lan")

    with patch(SEND, AsyncMock(return_value=1)) as send:
        await press(hass, wake)

    send.assert_awaited_once_with([MAC], "192.168.1.255")


async def test_the_packets_really_arrive_at_the_broadcast_address(
    hass: HomeAssistant,
    fake_agent: FakeAgent,
    entry_factory: EntryFactory,
    udp: tuple[Listener, int],
) -> None:
    """End to end with a real UDP socket; loopback stands in for the broadcast."""
    listener, port = udp
    fake_agent.manifest["agent"]["macs"] = ["A4:B1:C1:00:11:22", "A4:B1:C1:00:11:23"]
    entry = await entry_factory()
    hass.config_entries.async_update_entry(entry, options={CONF_BROADCAST: "127.0.0.1"})
    wake = entity_id_of(hass, "button", fake_agent.agent_id, "_wake_on_lan")

    with patch(SEND, partial(wol.async_send_magic_packets, port=port)):
        await press(hass, wake)
    await listener.wait_for(2)

    assert sorted(listener.packets) == sorted(
        [wol.build_magic_packet(MAC), wol.build_magic_packet(OTHER_MAC)]
    )


async def test_a_press_switches_the_client_to_fast_retry_for_five_minutes(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    entry = await entry_factory()
    wake = entity_id_of(hass, "button", fake_agent.agent_id, "_wake_on_lan")

    with (
        patch(SEND, AsyncMock(return_value=1)),
        patch.object(runtime_of(entry).client, "start_fast_retry") as fast_retry,
    ):
        await press(hass, wake)

    fast_retry.assert_called_once_with(300.0)


async def test_a_press_that_failed_to_send_reports_it_and_starts_no_fast_retry(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    entry = await entry_factory()
    wake = entity_id_of(hass, "button", fake_agent.agent_id, "_wake_on_lan")

    with (
        patch(SEND, AsyncMock(side_effect=OSError("Network is unreachable"))),
        patch.object(runtime_of(entry).client, "start_fast_retry") as fast_retry,
        pytest.raises(HomeAssistantError) as error,
    ):
        await press(hass, wake)

    assert error.value.translation_domain == "qoffeeautomat"
    assert error.value.translation_key == "wol_failed"
    assert error.value.translation_placeholders == {"error": "Network is unreachable"}
    fast_retry.assert_not_called()


async def test_the_button_stays_available_while_the_pc_is_off(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    entry = await entry_factory()
    agent_id = fake_agent.agent_id
    wake = entity_id_of(hass, "button", agent_id, "_wake_on_lan")
    lock = entity_id_of(hass, "button", agent_id, "lock_session")
    connectivity = entity_id_of(hass, "binary_sensor", agent_id, "_connectivity")
    power = entity_id_of(hass, "sensor", agent_id, "_power_state")
    assert state_of(hass, wake) != "unavailable"

    await fake_agent.send_bye("shutdown")
    await fake_agent.wait_for(lambda: state_of(hass, power) == "shutdown")
    await fake_agent.wait_for(lambda: not runtime_of(entry).client.connected)
    await hass.async_block_till_done()

    assert state_of(hass, connectivity) == "off"
    assert state_of(hass, lock) == "unavailable"
    assert state_of(hass, wake) != "unavailable"

    # And it works then: the packets go out and the client retries quickly.
    with (
        patch(SEND, AsyncMock(return_value=1)) as send,
        patch.object(runtime_of(entry).client, "start_fast_retry") as fast_retry,
    ):
        await press(hass, wake)
    send.assert_awaited_once()
    fast_retry.assert_called_once_with(WOL_FAST_RETRY_SECONDS)


async def test_after_a_press_the_client_connects_as_soon_as_the_pc_is_up(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """The agent goes away, the button is pressed, the agent returns: connected."""
    entry = await entry_factory()
    client = runtime_of(entry).client
    wake = entity_id_of(hass, "button", fake_agent.agent_id, "_wake_on_lan")
    await fake_agent.stop()
    await fake_agent.wait_for(lambda: not client.connected)

    with patch(SEND, AsyncMock(return_value=1)):
        await press(hass, wake)
    assert client.fast_retry_active
    await fake_agent.start(fake_agent.port)

    await fake_agent.wait_ready(limit=6)
    await fake_agent.wait_for(lambda: client.connected, limit=6)
    assert not client.fast_retry_active


# -- the options flow -----------------------------------------------------------------


async def test_the_options_flow_stores_the_broadcast_address(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    entry = await entry_factory()

    form = await hass.config_entries.options.async_init(entry.entry_id)
    assert form["type"] is FlowResultType.FORM
    assert form["step_id"] == "init"

    done = await hass.config_entries.options.async_configure(
        form["flow_id"], {CONF_BROADCAST: " 192.168.1.255 "}
    )

    assert done["type"] is FlowResultType.CREATE_ENTRY
    assert entry.options[CONF_BROADCAST] == "192.168.1.255"


async def test_the_options_flow_defaults_to_the_limited_broadcast(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    entry = await entry_factory()

    form = await hass.config_entries.options.async_init(entry.entry_id)
    done = await hass.config_entries.options.async_configure(form["flow_id"], {})

    assert done["type"] is FlowResultType.CREATE_ENTRY
    assert entry.options[CONF_BROADCAST] == DEFAULT_BROADCAST


@pytest.mark.parametrize("bad", ["", "192.168.1", "my-router.local", "fe80::1"])
async def test_the_options_flow_refuses_an_address_that_is_not_ipv4(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory, bad: str
) -> None:
    entry = await entry_factory()

    form = await hass.config_entries.options.async_init(entry.entry_id)
    again = await hass.config_entries.options.async_configure(
        form["flow_id"], {CONF_BROADCAST: bad}
    )

    assert again["type"] is FlowResultType.FORM
    assert again["errors"] == {CONF_BROADCAST: "invalid_broadcast"}
    assert CONF_BROADCAST not in entry.options


# -- privacy --------------------------------------------------------------------------


async def test_diagnostics_hide_the_stored_addresses_and_the_broadcast_address(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    entry = await entry_factory()
    hass.config_entries.async_update_entry(
        entry, options={CONF_BROADCAST: "192.168.1.255"}
    )

    diagnostics = await async_get_config_entry_diagnostics(hass, entry)

    assert diagnostics["entry"]["data"][CONF_MACS] == REDACTED
    assert diagnostics["entry"]["options"][CONF_BROADCAST] == REDACTED
    assert "192.168.1.255" not in str(diagnostics)
    assert "00:11:22" not in str(diagnostics)
