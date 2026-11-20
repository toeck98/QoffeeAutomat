"""Shared fixtures of the integration tests."""

from collections.abc import AsyncIterator, Iterator
from pathlib import Path
from typing import Any
from unittest.mock import patch

import aiohttp
import pytest
from homeassistant.config_entries import ConfigEntryState
from homeassistant.const import CONF_HOST, CONF_PORT
from homeassistant.core import HomeAssistant
from pytest_homeassistant_custom_component.common import MockConfigEntry

from custom_components.qoffeeautomat.const import (
    CONF_FINGERPRINT,
    CONF_HUB,
    CONF_LAST_BOOT_ID,
    CONF_TOKEN,
    DOMAIN,
)
from tests_integration.fake_agent import Certificate, FakeAgent, create_certificate
from tests_integration.fake_hub import FakeHub
from tests_integration.helpers import EntryFactory


@pytest.fixture(name="certificate")
def certificate_fixture(tmp_path: Path) -> Certificate:
    """Return a fresh self-signed certificate for the fake agent."""
    return create_certificate(tmp_path)


@pytest.fixture(name="hub_certificate")
def hub_certificate_fixture(tmp_path: Path) -> Certificate:
    """Return a fresh self-signed certificate for the fake hub."""
    return create_certificate(tmp_path, "fake-hub")


@pytest.fixture(name="fake_hub")
async def fake_hub_fixture(
    socket_enabled: None, hub_certificate: Certificate
) -> AsyncIterator[FakeHub]:
    """Run a fake hub on a free loopback port; it lists two agents."""
    hub = FakeHub(hub_certificate)
    await hub.start()
    yield hub
    await hub.stop()


@pytest.fixture(name="fake_agent")
async def fake_agent_fixture(
    socket_enabled: None, certificate: Certificate
) -> AsyncIterator[FakeAgent]:
    """Run a fake QoffeeLink agent on a free loopback port."""
    agent = FakeAgent(certificate)
    await agent.start()
    yield agent
    await agent.stop()


@pytest.fixture(name="session")
async def session_fixture(socket_enabled: None) -> AsyncIterator[aiohttp.ClientSession]:
    """Return a plain aiohttp client session that is closed after the test."""
    async with aiohttp.ClientSession() as client_session:
        yield client_session


@pytest.fixture(name="fast_subscribe")
def fast_subscribe_fixture() -> Iterator[None]:
    """Shorten the delay that collects subscription changes."""
    target = "custom_components.qoffeeautomat.runtime.SUBSCRIBE_DEBOUNCE_SECONDS"
    with patch(target, 0.1):
        yield


@pytest.fixture(name="entry_factory")
async def entry_factory_fixture(
    hass: HomeAssistant,
    fake_agent: FakeAgent,
    enable_custom_integrations: None,
    fast_subscribe: None,
) -> AsyncIterator[EntryFactory]:
    """Return a function that adds a config entry of the fake agent and sets it up.

    The entry stores the agent's current boot id unless the test passes another
    ``last_boot_id``. Entries are unloaded after the test.
    """
    entries: list[MockConfigEntry] = []

    async def create(*, setup: bool = True, **data: Any) -> MockConfigEntry:
        entry = MockConfigEntry(
            domain=DOMAIN,
            unique_id=fake_agent.agent_id,
            title=fake_agent.agent_name,
            data={
                CONF_HOST: fake_agent.host,
                CONF_PORT: fake_agent.port,
                CONF_TOKEN: fake_agent.token,
                CONF_FINGERPRINT: fake_agent.certificate.fingerprint,
                CONF_LAST_BOOT_ID: fake_agent.boot_id,
                **data,
            },
        )
        entry.add_to_hass(hass)
        entries.append(entry)
        if setup:
            await hass.config_entries.async_setup(entry.entry_id)
            await hass.async_block_till_done()
        return entry

    yield create

    for entry in entries:
        if entry.state in (ConfigEntryState.LOADED, ConfigEntryState.SETUP_RETRY):
            await hass.config_entries.async_unload(entry.entry_id)
    await hass.async_block_till_done()


@pytest.fixture(name="hub_entry_factory")
async def hub_entry_factory_fixture(
    hass: HomeAssistant,
    fake_hub: FakeHub,
    enable_custom_integrations: None,
    fast_subscribe: None,
) -> AsyncIterator[EntryFactory]:
    """Return a function that adds a config entry of the fake hub and sets it up.

    Entries are unloaded after the test.
    """
    entries: list[MockConfigEntry] = []

    async def create(*, setup: bool = True, **data: Any) -> MockConfigEntry:
        entry = MockConfigEntry(
            domain=DOMAIN,
            unique_id=fake_hub.hub_id,
            title=fake_hub.hub_name,
            data={
                CONF_HUB: True,
                CONF_HOST: fake_hub.host,
                CONF_PORT: fake_hub.port,
                CONF_TOKEN: fake_hub.token,
                CONF_FINGERPRINT: fake_hub.certificate.fingerprint,
                **data,
            },
        )
        entry.add_to_hass(hass)
        entries.append(entry)
        if setup:
            await hass.config_entries.async_setup(entry.entry_id)
            await hass.async_block_till_done()
        return entry

    yield create

    for entry in entries:
        if entry.state in (ConfigEntryState.LOADED, ConfigEntryState.SETUP_RETRY):
            await hass.config_entries.async_unload(entry.entry_id)
    await hass.async_block_till_done()
