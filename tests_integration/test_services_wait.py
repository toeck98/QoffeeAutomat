"""Tests of wake, wait_for and ready_timeout of the run_command action (T712).

The fake agent plays a PC that is switched off, comes online and later reports that
its session is ready. The times are scaled down from the ones in the task (online after
ten seconds, ready after twenty) to keep the tests quick; what is checked is the order:
the command runs after the ready signal and not before.
"""

import asyncio
from typing import Any
from unittest.mock import AsyncMock, patch

import pytest
import voluptuous as vol
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import HomeAssistantError, ServiceValidationError
from homeassistant.helpers import device_registry as dr
from pytest_homeassistant_custom_component.common import MockConfigEntry

from custom_components.qoffeeautomat.client import LinkClient
from custom_components.qoffeeautomat.const import (
    CONF_BROADCAST,
    CONF_MACS,
    DEFAULT_BROADCAST,
    DEFAULT_READY_TIMEOUT,
    DOMAIN,
)
from custom_components.qoffeeautomat.protocol import MetricValue
from custom_components.qoffeeautomat.services import RUN_COMMAND_SCHEMA
from tests_integration.fake_agent import FakeAgent
from tests_integration.helpers import EntryFactory, changed_manifest

SEND = "custom_components.qoffeeautomat.services.async_send_magic_packets"
MAC = "a4:b1:c1:00:11:22"
SESSION_READY = "session.ready"
SESSION_READY_METRIC: dict[str, Any] = {
    "id": SESSION_READY,
    "name": "Session ready",
    "translation_key": "session_ready",
    "kind": "binary_sensor",
    "value_type": "bool",
    "enabled_default": False,
}
# Scaled-down times of the acceptance test, in seconds.
ONLINE_AFTER = 0.5
READY_AFTER = 1.5


@pytest.fixture(name="entry")
async def entry_fixture(
    fake_agent: FakeAgent, entry_factory: EntryFactory
) -> MockConfigEntry:
    """Set up the entry of an agent that announces the metric ``session.ready``.

    The metric is disabled by default, so no entity subscribes to it.
    """
    fake_agent.set_manifest(
        changed_manifest(fake_agent, add_metrics=[SESSION_READY_METRIC])
    )
    entry = await entry_factory()
    # From now on a subscription that includes the metric gets this snapshot.
    fake_agent.snapshot = {SESSION_READY: False}
    return entry


class _Sink:
    """A metric consumer of an enabled entity that ignores the values."""

    def handle_value(self, value: MetricValue) -> None:
        """Take a value and do nothing."""


def client_of(entry: MockConfigEntry) -> LinkClient:
    """Return the client of a loaded entry."""
    client = entry.runtime_data.client
    assert isinstance(client, LinkClient)
    return client


def device_id_of(hass: HomeAssistant, agent: FakeAgent) -> str:
    """Return the id of the device of the agent in the device registry."""
    registry = dr.async_get(hass)
    for config_entry in hass.config_entries.async_entries(DOMAIN):
        device = registry.async_get_device_by_identifier(
            (DOMAIN, agent.agent_id), config_entry.entry_id
        )
        if device is not None:
            return device.id
    raise AssertionError("device of the agent not found")


async def run_action(
    hass: HomeAssistant, agent: FakeAgent, command: str = "lock_session", **fields: Any
) -> dict[str, Any]:
    """Call ``qoffeeautomat.run_command`` and return its response data."""
    response = await hass.services.async_call(
        DOMAIN,
        "run_command",
        {"device_id": device_id_of(hass, agent), "command": command, **fields},
        blocking=True,
        return_response=True,
    )
    assert response is not None
    return dict(response)


async def switch_off(fake_agent: FakeAgent, client: LinkClient) -> None:
    """The PC goes away: the agent stops and the client notices."""
    await fake_agent.stop()
    await fake_agent.wait_for(lambda: not client.connected)


async def switch_on(fake_agent: FakeAgent, client: LinkClient) -> None:
    """The PC is back: the agent listens again and the client connected."""
    await fake_agent.start(fake_agent.port)
    await fake_agent.wait_for(lambda: client.connected, limit=8)
    await fake_agent.wait_ready()


def watches_session(fake_agent: FakeAgent) -> bool:
    """True if the latest subscription of the client includes ``session.ready``."""
    requests = fake_agent.subscribed_metrics()
    return bool(requests) and SESSION_READY in requests[-1]


# -- the scenario of the task ---------------------------------------------------------


async def test_wake_and_wait_for_the_session_run_the_command_after_the_ready_signal(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """Online after a while, ready later: the command runs only after the signal."""
    client = client_of(entry)
    await switch_off(fake_agent, client)

    with patch(SEND, AsyncMock(return_value=1)) as send:
        call = hass.async_create_task(
            run_action(
                hass,
                fake_agent,
                wake=True,
                wait_for="session_ready",
                ready_timeout=20,
            )
        )
        await asyncio.sleep(ONLINE_AFTER)
        assert not call.done()
        await switch_on(fake_agent, client)
        # Connected, but the session is not ready: nothing runs yet, and the metric is
        # subscribed for the call although its entity is disabled.
        await fake_agent.wait_for(lambda: watches_session(fake_agent))
        await asyncio.sleep(READY_AFTER - ONLINE_AFTER)
        assert not call.done()
        assert fake_agent.runs == []

        await fake_agent.send_state({SESSION_READY: True})
        async with asyncio.timeout(5):
            response = await call

    send.assert_awaited_once_with([MAC], DEFAULT_BROADCAST)
    assert [frame["command"] for frame in fake_agent.runs] == ["lock_session"]
    assert response["exit_code"] == 0
    # The subscription for the call ends with the call.
    await fake_agent.wait_for(lambda: not watches_session(fake_agent))


async def test_wait_for_connected_does_not_wait_for_the_session(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """``connected`` runs the command as soon as the PC is connected."""
    client = client_of(entry)
    await switch_off(fake_agent, client)

    with patch(SEND, AsyncMock(return_value=1)) as send:
        call = hass.async_create_task(
            run_action(hass, fake_agent, wake=True, wait_for="connected")
        )
        await asyncio.sleep(ONLINE_AFTER)
        assert fake_agent.runs == []
        await switch_on(fake_agent, client)
        async with asyncio.timeout(5):
            await call

    send.assert_awaited_once()
    assert len(fake_agent.runs) == 1
    assert not watches_session(fake_agent)


async def test_a_session_that_is_ready_already_does_not_wait(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The PC is on and ready: no packets, and the call returns at once."""
    # The subscription of the call brings a snapshot with the current value.
    fake_agent.snapshot = {SESSION_READY: True}

    with patch(SEND, AsyncMock(return_value=1)) as send:
        async with asyncio.timeout(5):
            await run_action(
                hass, fake_agent, wake=True, wait_for="session_ready", ready_timeout=10
            )

    send.assert_not_awaited()
    assert len(fake_agent.runs) == 1


async def test_the_value_of_an_existing_subscription_is_remembered(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """With ``session.ready`` subscribed already no new snapshot is needed."""
    subscriptions = entry.runtime_data.subscriptions
    remove = subscriptions.register(SESSION_READY, _Sink())
    try:
        await fake_agent.wait_for(lambda: watches_session(fake_agent))
        await fake_agent.send_state({SESSION_READY: True})
        await fake_agent.wait_for(
            lambda: subscriptions.last_value(SESSION_READY) is True
        )
        before = len(fake_agent.subscribed_metrics())

        async with asyncio.timeout(5):
            await run_action(
                hass, fake_agent, wait_for="session_ready", ready_timeout=10
            )

        assert len(fake_agent.runs) == 1
        assert len(fake_agent.subscribed_metrics()) == before
    finally:
        remove()


# -- a call without the new fields ----------------------------------------------------


async def test_a_call_without_the_new_fields_behaves_as_before(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """No packets, no waiting, no queue; the response is the usual one."""
    with patch(SEND, AsyncMock(return_value=1)) as send:
        response = await run_action(hass, fake_agent)

    send.assert_not_awaited()
    assert set(response) == {
        "run_id",
        "exit_code",
        "stdout",
        "stderr",
        "duration_ms",
        "timed_out",
        "truncated",
    }
    assert not entry.runtime_data.call_lock.locked()
    assert not watches_session(fake_agent)


async def test_an_offline_agent_without_wake_fails_at_once_as_before(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """Without ``wake`` and ``wait_for`` an agent that is away is an error."""
    await switch_off(fake_agent, client_of(entry))

    with (
        patch(SEND, AsyncMock(return_value=1)) as send,
        pytest.raises(HomeAssistantError) as error,
    ):
        await run_action(hass, fake_agent)

    assert error.value.translation_key == "agent_offline"
    send.assert_not_awaited()


def test_the_defaults_of_the_new_fields() -> None:
    """The schema fills in ``none``, no wake and 180 seconds."""
    data = RUN_COMMAND_SCHEMA({"device_id": "abc", "command": "lock_session"})

    assert data["wake"] is False
    assert data["wait_for"] == "none"
    assert data["ready_timeout"] == DEFAULT_READY_TIMEOUT == 180.0


@pytest.mark.parametrize(
    "fields",
    [
        {"wait_for": "later"},
        {"ready_timeout": 0},
        {"ready_timeout": 100000},
        {"wake": "maybe"},
    ],
)
async def test_the_new_fields_are_validated(
    hass: HomeAssistant,
    fake_agent: FakeAgent,
    entry: MockConfigEntry,
    fields: dict[str, Any],
) -> None:
    """Wrong values are refused before anything is sent or woken."""
    with patch(SEND, AsyncMock(return_value=1)) as send, pytest.raises(vol.Invalid):
        await run_action(hass, fake_agent, **fields)

    send.assert_not_awaited()
    assert fake_agent.runs == []


# -- wake -----------------------------------------------------------------------------


async def test_wake_sends_nothing_while_the_pc_is_connected(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The packets go out only when the agent is not connected."""
    with patch(SEND, AsyncMock(return_value=1)) as send:
        await run_action(hass, fake_agent, wake=True, wait_for="connected")

    send.assert_not_awaited()
    assert len(fake_agent.runs) == 1


async def test_wake_uses_the_broadcast_address_of_the_options(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The action sends where the Wake-on-LAN button sends."""
    hass.config_entries.async_update_entry(
        entry, options={CONF_BROADCAST: "192.168.1.255"}
    )
    client = client_of(entry)
    await switch_off(fake_agent, client)

    with patch(SEND, AsyncMock(return_value=1)) as send:
        call = hass.async_create_task(
            run_action(hass, fake_agent, wake=True, wait_for="connected")
        )
        await fake_agent.wait_for(lambda: send.await_count == 1)
        await switch_on(fake_agent, client)
        async with asyncio.timeout(5):
            await call

    send.assert_awaited_once_with([MAC], "192.168.1.255")


async def test_wake_makes_the_client_retry_quickly(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The client switches to fast retry for the waiting time."""
    client = client_of(entry)
    await switch_off(fake_agent, client)

    with (
        patch(SEND, AsyncMock(return_value=1)),
        patch.object(client, "start_fast_retry") as fast_retry,
        pytest.raises(HomeAssistantError),
    ):
        await run_action(
            hass, fake_agent, wake=True, wait_for="connected", ready_timeout=1
        )

    fast_retry.assert_called_once_with(1.0)


async def test_no_second_wake_up_while_the_pc_is_being_woken(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """After a button press (or an earlier call) the packets are not sent again."""
    client = client_of(entry)
    await switch_off(fake_agent, client)
    client.start_fast_retry(60)

    with (
        patch(SEND, AsyncMock(return_value=1)) as send,
        pytest.raises(HomeAssistantError) as error,
    ):
        await run_action(
            hass, fake_agent, wake=True, wait_for="connected", ready_timeout=1
        )

    send.assert_not_awaited()
    assert error.value.translation_key == "ready_timeout_connected"


async def test_wake_without_a_known_mac_address_is_a_validation_error(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """Nothing to send to: a message that says so, and nothing runs."""
    fake_agent.manifest["agent"]["macs"] = []
    entry = await entry_factory()
    assert not entry.data.get(CONF_MACS)
    await switch_off(fake_agent, client_of(entry))

    with pytest.raises(ServiceValidationError) as error:
        await run_action(hass, fake_agent, wake=True, wait_for="connected")

    assert error.value.translation_key == "wol_no_mac"
    assert fake_agent.runs == []


async def test_a_failing_network_is_reported_and_nothing_runs(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The error of the socket becomes ``wol_failed``."""
    await switch_off(fake_agent, client_of(entry))

    with (
        patch(SEND, AsyncMock(side_effect=OSError("Network is unreachable"))),
        pytest.raises(HomeAssistantError) as error,
    ):
        await run_action(hass, fake_agent, wake=True, wait_for="connected")

    assert error.value.translation_key == "wol_failed"
    assert error.value.translation_placeholders == {"error": "Network is unreachable"}
    assert fake_agent.runs == []


# -- timeouts -------------------------------------------------------------------------


async def test_a_pc_that_does_not_connect_names_the_missing_connection(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """The timeout names the condition and nothing runs."""
    await switch_off(fake_agent, client_of(entry))

    with (
        patch(SEND, AsyncMock(return_value=1)),
        pytest.raises(HomeAssistantError) as error,
    ):
        await run_action(
            hass, fake_agent, wake=True, wait_for="session_ready", ready_timeout=1
        )

    assert not isinstance(error.value, ServiceValidationError)
    assert error.value.translation_domain == DOMAIN
    assert error.value.translation_key == "ready_timeout_connected"
    assert error.value.translation_placeholders == {
        "name": entry.title,
        "command": "lock_session",
        "timeout": "1",
    }
    assert fake_agent.runs == []
    assert not entry.runtime_data.call_lock.locked()


async def test_a_session_that_is_not_ready_names_the_missing_session(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """Connected, but the session never comes: the timeout says so; nothing runs."""
    with pytest.raises(HomeAssistantError) as error:
        await run_action(hass, fake_agent, wait_for="session_ready", ready_timeout=1)

    assert error.value.translation_key == "ready_timeout_session_ready"
    assert error.value.translation_placeholders is not None
    assert error.value.translation_placeholders["timeout"] == "1"
    assert fake_agent.runs == []
    assert not entry.runtime_data.call_lock.locked()
    # The subscription for the call is gone again.
    await fake_agent.wait_for(lambda: not watches_session(fake_agent))


async def test_waiting_for_the_session_of_an_agent_without_the_metric_is_refused(
    hass: HomeAssistant, fake_agent: FakeAgent, entry_factory: EntryFactory
) -> None:
    """An agent that never reports ``session.ready`` fails at once, nothing is woken."""
    entry = await entry_factory()
    await switch_off(fake_agent, client_of(entry))

    with (
        patch(SEND, AsyncMock(return_value=1)) as send,
        pytest.raises(ServiceValidationError) as error,
    ):
        await run_action(hass, fake_agent, wake=True, wait_for="session_ready")

    assert error.value.translation_key == "session_ready_unsupported"
    send.assert_not_awaited()
    assert fake_agent.runs == []


# -- several calls for the same PC ----------------------------------------------------


async def test_calls_for_the_same_pc_wake_it_once_and_run_in_call_order(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """Two calls wait for the same PC: one wake-up, then the commands in order."""
    client = client_of(entry)
    await switch_off(fake_agent, client)

    with patch(SEND, AsyncMock(return_value=1)) as send:
        first = hass.async_create_task(
            run_action(hass, fake_agent, "first", wake=True, wait_for="connected")
        )
        second = hass.async_create_task(
            run_action(hass, fake_agent, "second", wake=True, wait_for="connected")
        )
        third = hass.async_create_task(
            run_action(hass, fake_agent, "third", wake=True, wait_for="connected")
        )
        await asyncio.sleep(ONLINE_AFTER)
        assert fake_agent.runs == []
        await switch_on(fake_agent, client)
        async with asyncio.timeout(10):
            await asyncio.gather(first, second, third)

    send.assert_awaited_once()
    assert [frame["command"] for frame in fake_agent.runs] == [
        "first",
        "second",
        "third",
    ]


async def test_a_call_that_waits_for_its_turn_too_long_says_so(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """A call that never got its turn did not wake anything and runs nothing."""
    client = client_of(entry)
    await switch_off(fake_agent, client)

    with patch(SEND, AsyncMock(return_value=1)) as send:
        first = hass.async_create_task(
            run_action(
                hass,
                fake_agent,
                "first",
                wake=True,
                wait_for="connected",
                ready_timeout=30,
            )
        )
        await fake_agent.wait_for(lambda: send.await_count == 1)

        with pytest.raises(HomeAssistantError) as error:
            await run_action(
                hass,
                fake_agent,
                "second",
                wake=True,
                wait_for="connected",
                ready_timeout=1,
            )

        assert error.value.translation_key == "ready_timeout_queued"
        assert error.value.translation_placeholders is not None
        assert error.value.translation_placeholders["command"] == "second"
        send.assert_awaited_once()

        await switch_on(fake_agent, client)
        async with asyncio.timeout(10):
            await first

    assert [frame["command"] for frame in fake_agent.runs] == ["first"]


async def test_the_turn_is_given_back_when_the_command_fails(
    hass: HomeAssistant, fake_agent: FakeAgent, entry: MockConfigEntry
) -> None:
    """A failing command does not block the calls behind it."""
    fake_agent.run_errors["broken"] = ("exec_failed", "It broke")

    with pytest.raises(HomeAssistantError):
        await run_action(hass, fake_agent, "broken", wait_for="connected")
    assert not entry.runtime_data.call_lock.locked()

    await run_action(hass, fake_agent, "fine", wait_for="connected")
    assert [frame["command"] for frame in fake_agent.runs] == ["broken", "fine"]
