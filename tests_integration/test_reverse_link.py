"""Tests of the reverse link: the endpoint roaming agents dial (D-043, T710).

The token, the address and the enrollment string are tested on their own. The endpoint
(``ReverseLinkView``) is tested on a bare aiohttp application, with a fake agent that
dials it the way the agent does: GET with a bearer token, upgrade, ``hello`` first. The
integration is tested against the web server of Home Assistant: a reverse entry, the
entities that appear after the first connection, the action over the reverse link, the
revocation of a token and the config flow that waits for the agent.
"""

import asyncio
import base64
import hashlib
import json
from collections.abc import AsyncIterator, Callable
from typing import Any

import aiohttp
import pytest
from aiohttp import web
from aiohttp.test_utils import TestClient, make_mocked_request
from homeassistant.config_entries import ConfigEntryState
from homeassistant.core import HomeAssistant
from homeassistant.data_entry_flow import FlowResultType
from homeassistant.helpers import device_registry as dr
from homeassistant.setup import async_setup_component
from pytest_homeassistant_custom_component.common import MockConfigEntry

from custom_components.qoffeeautomat.client import ReverseLinkClient
from custom_components.qoffeeautomat.const import (
    CONF_REVERSE,
    CONF_REVERSE_TOKEN_HASH,
    CONF_REVERSE_URL,
    DOMAIN,
    REVERSE_ENROLLMENT_PREFIX,
    REVERSE_LINK_PATH,
    REVERSE_TOKEN_PREFIX,
)
from custom_components.qoffeeautomat.protocol import SUBPROTOCOL
from custom_components.qoffeeautomat.reverse import (
    ReverseLinks,
    ReverseLinkView,
    bearer_token,
    build_enrollment,
    create_token,
    hash_token,
    parse_enrollment,
    websocket_url,
)
from tests_integration.fake_agent import FakeAgent, load_fixture
from tests_integration.helpers import find_entity_id, state_of

TOKEN = "qalr_AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8"  # noqa: S105
ENDPOINT = "wss://ha.example.org" + REVERSE_LINK_PATH


@pytest.fixture(autouse=True)
def _no_reverse_heartbeat(monkeypatch: pytest.MonkeyPatch) -> None:
    """Disable aiohttp's 30 s WebSocket heartbeat so closed sockets leave no timers."""
    monkeypatch.setattr(
        "custom_components.qoffeeautomat.reverse.HEARTBEAT_SECONDS", None
    )


def bearer(token: str) -> dict[str, str]:
    """Return the header that the agent sends."""
    return {"Authorization": f"Bearer {token}"}


# -- the token ------------------------------------------------------------------------


def test_a_token_has_the_prefix_and_32_random_bytes() -> None:
    token = create_token()

    assert token.startswith("qalr_")
    assert REVERSE_TOKEN_PREFIX == "qalr_"  # noqa: S105
    body = token[len(REVERSE_TOKEN_PREFIX) :]
    assert len(body) == 43  # 32 bytes in unpadded base64url
    assert len(base64.urlsafe_b64decode(body + "=")) == 32
    assert create_token() != token


def test_only_the_sha256_hash_is_kept() -> None:
    assert hash_token(TOKEN) == hashlib.sha256(TOKEN.encode()).hexdigest()
    assert len(hash_token(TOKEN)) == 64
    assert TOKEN not in hash_token(TOKEN)


# -- the address ----------------------------------------------------------------------


@pytest.mark.parametrize(
    ("given", "expected"),
    [
        ("https://ha.example.org", ENDPOINT),
        ("https://ha.example.org/", ENDPOINT),
        ("  https://ha.example.org  ", ENDPOINT),
        ("wss://ha.example.org", ENDPOINT),
        (
            "https://ha.example.org:8123",
            "wss://ha.example.org:8123" + REVERSE_LINK_PATH,
        ),
        ("https://example.org/ha/", "wss://example.org/ha" + REVERSE_LINK_PATH),
        (ENDPOINT, ENDPOINT),
        (
            "http://127.0.0.1:8123",
            "ws://127.0.0.1:8123" + REVERSE_LINK_PATH,
        ),
        ("http://localhost:8123", "ws://localhost:8123" + REVERSE_LINK_PATH),
        ("http://[::1]:8123", "ws://[::1]:8123" + REVERSE_LINK_PATH),
    ],
)
def test_the_address_of_home_assistant_becomes_the_websocket_url(
    given: str, expected: str
) -> None:
    assert websocket_url(given) == expected


@pytest.mark.parametrize(
    "given",
    [
        "",
        "ha.example.org",
        "http://ha.example.org",
        "ws://192.168.1.20:8123",
        "ftp://ha.example.org",
        "https://user:secret@ha.example.org",
        "https://",
    ],
)
def test_an_address_that_would_send_the_token_in_the_clear_is_refused(
    given: str,
) -> None:
    with pytest.raises(ValueError, match=r"."):
        websocket_url(given)


# -- the enrollment string ------------------------------------------------------------


def test_the_enrollment_string_round_trips() -> None:
    fixture = load_fixture("reverse/enrollment.json")

    text = build_enrollment(fixture["url"], fixture["token"], fixture["fp"])

    assert text.startswith(REVERSE_ENROLLMENT_PREFIX)
    assert parse_enrollment(text) == fixture
    assert parse_enrollment(f"  {text}\n") == fixture


def test_the_enrollment_string_does_not_need_a_fingerprint() -> None:
    text = build_enrollment(ENDPOINT, TOKEN)

    assert parse_enrollment(text) == {"url": ENDPOINT, "token": TOKEN}


def test_the_enrollment_string_is_base64url_json() -> None:
    body = build_enrollment(ENDPOINT, TOKEN)[len(REVERSE_ENROLLMENT_PREFIX) :]

    payload = json.loads(base64.urlsafe_b64decode(body + "=" * (-len(body) % 4)))

    assert payload == {"url": ENDPOINT, "token": TOKEN}
    assert "=" not in body


@pytest.mark.parametrize(
    "text",
    [
        "",
        "hello",
        "qalr2.e30",
        "qalr1.!!!",
        "qalr1." + base64.urlsafe_b64encode(b"not json").decode(),
        "qalr1." + base64.urlsafe_b64encode(b'{"url":"wss://x"}').decode(),
        "qalr1." + base64.urlsafe_b64encode(b'{"token":"qalr_x"}').decode(),
        "qalr1." + base64.urlsafe_b64encode(b"[1,2]").decode(),
    ],
)
def test_a_damaged_enrollment_string_is_refused(text: str) -> None:
    with pytest.raises(ValueError, match=r"."):
        parse_enrollment(text)


@pytest.mark.parametrize(
    ("header", "expected"),
    [
        (f"Bearer {TOKEN}", TOKEN),
        (f"bearer {TOKEN}", TOKEN),
        ("Bearer", None),
        ("Bearer  ", None),
        (f"Basic {TOKEN}", None),
        (TOKEN, None),
        ("", None),
    ],
)
def test_the_token_is_read_from_the_bearer_header(
    header: str, expected: str | None
) -> None:
    headers = {"Authorization": header} if header else {}
    request = make_mocked_request("GET", REVERSE_LINK_PATH, headers=headers)

    assert bearer_token(request) == expected


# -- the registry ---------------------------------------------------------------------


async def test_a_token_of_an_entry_is_known_and_finds_the_client() -> None:
    links = ReverseLinks()
    client = ReverseLinkClient("agent")
    links.know("entry", hash_token(TOKEN))

    found = links.lookup(TOKEN)
    assert found.known
    assert found.client is None  # the entry is not loaded yet

    remove = links.attach("entry", client)
    assert links.lookup(TOKEN).client is client

    remove()
    found = links.lookup(TOKEN)
    assert found.known
    assert found.client is None


async def test_an_unknown_token_is_not_known() -> None:
    links = ReverseLinks()
    links.know("entry", hash_token(TOKEN))

    assert not links.lookup(create_token()).known
    assert not links.lookup("").known


async def test_a_rotated_token_is_unknown_and_the_new_one_known() -> None:
    links = ReverseLinks()
    links.know("entry", hash_token(TOKEN))
    new = create_token()

    links.know("entry", hash_token(new))

    assert links.lookup(new).known
    assert not links.lookup(TOKEN).known


async def test_a_forgotten_entry_loses_its_token() -> None:
    links = ReverseLinks()
    client = ReverseLinkClient("agent")
    links.know("entry", hash_token(TOKEN))
    links.attach("entry", client)

    links.forget("entry")

    assert not links.lookup(TOKEN).known
    assert not links.has_entries


async def test_an_old_remover_does_not_detach_a_newer_client() -> None:
    links = ReverseLinks()
    old = ReverseLinkClient("agent")
    new = ReverseLinkClient("agent")
    links.know("entry", hash_token(TOKEN))
    remove_old = links.attach("entry", old)
    links.attach("entry", new)

    remove_old()

    assert links.lookup(TOKEN).client is new


async def test_a_token_that_waits_for_its_first_agent_is_known() -> None:
    links = ReverseLinks()

    future = links.add_pending(hash_token(TOKEN))

    found = links.lookup(TOKEN)
    assert found.known
    assert found.pending is future
    assert found.client is None

    links.remove_pending(hash_token(TOKEN))
    assert future.cancelled()
    assert not links.lookup(TOKEN).known


async def test_an_enrolled_token_counts_as_known_until_its_entry_exists() -> None:
    links = ReverseLinks()
    links.enrolled(hash_token(TOKEN))

    found = links.lookup(TOKEN)

    assert found.known
    assert found.client is None
    assert found.pending is None


# -- the endpoint, on a bare application ----------------------------------------------


@pytest.fixture(name="links")
def links_fixture() -> ReverseLinks:
    """Return the registry that the endpoint under test uses."""
    return ReverseLinks()


@pytest.fixture(name="endpoint")
async def endpoint_fixture(
    socket_enabled: None, aiohttp_client: Any, links: ReverseLinks
) -> TestClient:
    """Serve ``ReverseLinkView`` on a bare aiohttp application."""
    app = web.Application()
    app.router.add_get(REVERSE_LINK_PATH, ReverseLinkView(links).get)
    client: TestClient = await aiohttp_client(app)
    return client


type Dial = Callable[..., Any]


@pytest.fixture(name="dial")
async def dial_fixture(
    endpoint: TestClient, fake_agent: FakeAgent
) -> AsyncIterator[Dial]:
    """Return a function that lets the fake agent dial the endpoint with a token."""
    sockets: list[aiohttp.ClientWebSocketResponse] = []
    tasks: list[asyncio.Task[None]] = []

    async def dial(
        token: str, client: TestClient | None = None
    ) -> aiohttp.ClientWebSocketResponse:
        ws = await (client or endpoint).ws_connect(
            REVERSE_LINK_PATH, headers=bearer(token), protocols=(SUBPROTOCOL,)
        )
        sockets.append(ws)
        tasks.append(asyncio.create_task(fake_agent.serve_socket(ws, None)))
        return ws

    yield dial

    for ws in sockets:
        await ws.close()
    await asyncio.gather(*tasks, return_exceptions=True)


async def test_an_unknown_token_gets_401_and_no_upgrade(
    endpoint: TestClient, links: ReverseLinks
) -> None:
    links.know("entry", hash_token(TOKEN))

    response = await endpoint.get(REVERSE_LINK_PATH, headers=bearer(create_token()))

    assert response.status == 401
    assert response.headers["WWW-Authenticate"] == "Bearer"


@pytest.mark.parametrize("headers", [{}, {"Authorization": "Basic abc"}])
async def test_a_request_without_a_bearer_token_gets_401(
    endpoint: TestClient, links: ReverseLinks, headers: dict[str, str]
) -> None:
    links.know("entry", hash_token(TOKEN))

    response = await endpoint.get(REVERSE_LINK_PATH, headers=headers)

    assert response.status == 401


async def test_a_known_token_of_an_entry_that_is_not_loaded_gets_503(
    endpoint: TestClient, links: ReverseLinks
) -> None:
    """The agent has to retry, not give up: Home Assistant is probably starting."""
    links.know("entry", hash_token(TOKEN))

    response = await endpoint.get(REVERSE_LINK_PATH, headers=bearer(TOKEN))

    assert response.status == 503
    assert response.headers["Retry-After"] == "5"


async def test_a_request_without_the_subprotocol_is_refused(
    endpoint: TestClient, links: ReverseLinks, fake_agent: FakeAgent
) -> None:
    links.know("entry", hash_token(TOKEN))
    links.attach("entry", ReverseLinkClient(fake_agent.agent_id))

    response = await endpoint.get(REVERSE_LINK_PATH, headers=bearer(TOKEN))

    assert response.status == 400


async def test_a_valid_token_gets_an_ordinary_qoffeelink_connection(
    dial: Dial, links: ReverseLinks, fake_agent: FakeAgent
) -> None:
    """Send hello first, client_hello back, then manifest/run work normally."""
    link = ReverseLinkClient(fake_agent.agent_id, client_name="Test HA")
    links.know("entry", hash_token(TOKEN))
    links.attach("entry", link)
    connected: list[bool] = []
    link.add_connection_listener(connected.append)

    await dial(TOKEN)
    await fake_agent.wait_for(lambda: link.connected)
    await fake_agent.wait_ready()

    assert connected == [True]
    assert link.hello is not None
    assert link.hello.agent.id == fake_agent.agent_id
    client_hello = fake_agent.connections[0].client_hello
    assert client_hello is not None
    assert client_hello["t"] == "client_hello"
    assert client_hello["client"]["name"] == "Test HA"

    manifest = await link.get_manifest()
    assert manifest["rev"] == fake_agent.manifest["rev"]
    result = await link.run_command("lock_session", None, wait=True)
    assert result.exit_code == 0
    assert [frame["command"] for frame in fake_agent.runs] == ["lock_session"]

    await link.stop()


async def test_the_newest_connection_of_an_entry_wins(
    dial: Dial, links: ReverseLinks, fake_agent: FakeAgent
) -> None:
    """After a change of network the old connection may be half open: replace it."""
    link = ReverseLinkClient(fake_agent.agent_id)
    links.know("entry", hash_token(TOKEN))
    links.attach("entry", link)

    first = await dial(TOKEN)
    await fake_agent.wait_for(lambda: link.connected)
    second = await dial(TOKEN)

    await fake_agent.wait_for(lambda: first.closed)
    await fake_agent.wait_for(lambda: link.connected and not second.closed)
    assert first.close_code == 1000
    await fake_agent.wait_ready()
    assert (await link.get_manifest())["rev"] == fake_agent.manifest["rev"]

    await link.stop()


async def test_a_token_copied_to_another_pc_is_closed_with_4401(
    dial: Dial, links: ReverseLinks
) -> None:
    """hello.agent.id must match the entry's agent (fixture handshake.wrong_agent)."""
    link = ReverseLinkClient("the-agent-of-another-entry")
    links.know("entry", hash_token(TOKEN))
    links.attach("entry", link)

    ws = await dial(TOKEN)

    async with asyncio.timeout(5):
        while not ws.closed:  # noqa: ASYNC110
            await asyncio.sleep(0.01)
    assert ws.close_code == 4401
    assert not link.connected
    assert link.auth_failed


async def test_a_stopped_entry_accepts_no_connection(
    endpoint: TestClient, links: ReverseLinks, fake_agent: FakeAgent
) -> None:
    link = ReverseLinkClient(fake_agent.agent_id)
    links.know("entry", hash_token(TOKEN))
    links.attach("entry", link)
    await link.stop()

    response = await endpoint.get(REVERSE_LINK_PATH, headers=bearer(TOKEN))

    assert response.status == 503


async def test_the_first_agent_of_a_new_token_is_told_to_the_flow_and_dials_again(
    dial: Dial, endpoint: TestClient, links: ReverseLinks, fake_agent: FakeAgent
) -> None:
    """Enrollment: hello goes to the waiting flow, connection ends, retry waits."""
    future = links.add_pending(hash_token(TOKEN))

    ws = await dial(TOKEN)

    async with asyncio.timeout(5):
        hello = await future
    assert hello.agent.id == fake_agent.agent_id
    async with asyncio.timeout(5):
        while not ws.closed:  # noqa: ASYNC110
            await asyncio.sleep(0.01)
    assert ws.close_code == 1000
    # The entry does not exist yet: the agent is asked to retry, not rejected.
    response = await endpoint.get(REVERSE_LINK_PATH, headers=bearer(TOKEN))
    assert response.status == 503


async def test_an_agent_that_enrolls_without_a_hello_is_closed(
    endpoint: TestClient, links: ReverseLinks
) -> None:
    future = links.add_pending(hash_token(TOKEN))
    ws = await endpoint.ws_connect(
        REVERSE_LINK_PATH, headers=bearer(TOKEN), protocols=(SUBPROTOCOL,)
    )

    await ws.send_str("this is not a frame")

    async with asyncio.timeout(5):
        message = await ws.receive()
    assert message.type is aiohttp.WSMsgType.CLOSE
    assert not future.done()
    await ws.close()


# -- the integration, on the web server of Home Assistant -----------------------------


@pytest.fixture(name="web_server")
async def web_server_fixture(
    hass: HomeAssistant, enable_custom_integrations: None, fast_subscribe: None
) -> AsyncIterator[None]:
    """Set up the web server of Home Assistant; unload the entries afterwards."""
    assert await async_setup_component(hass, "http", {})
    yield
    for entry in hass.config_entries.async_entries(DOMAIN):
        if entry.state is ConfigEntryState.LOADED:
            await hass.config_entries.async_unload(entry.entry_id)
    await hass.async_block_till_done()


async def add_reverse_entry(
    hass: HomeAssistant, agent: FakeAgent, token: str = TOKEN
) -> MockConfigEntry:
    """Add and set up the entry of a PC that dials Home Assistant."""
    entry = MockConfigEntry(
        domain=DOMAIN,
        unique_id=agent.agent_id,
        title=agent.agent_name,
        data={CONF_REVERSE: True, CONF_REVERSE_TOKEN_HASH: hash_token(token)},
    )
    entry.add_to_hass(hass)
    await hass.config_entries.async_setup(entry.entry_id)
    await hass.async_block_till_done()
    return entry


async def test_a_reverse_entry_loads_without_the_agent(
    hass: HomeAssistant, web_server: None, fake_agent: FakeAgent
) -> None:
    """There is nothing to dial: the entry is loaded and waits for the agent."""
    entry = await add_reverse_entry(hass, fake_agent)

    assert entry.state is ConfigEntryState.LOADED
    assert not entry.runtime_data.client.connected
    assert not entry.runtime_data.manifest_manager.loaded


async def test_the_entities_and_the_action_work_over_the_reverse_link(
    hass: HomeAssistant,
    web_server: None,
    fake_agent: FakeAgent,
    hass_client_no_auth: Callable[[], Any],
) -> None:
    """The first connection loads the manifest, adds the entities and runs commands."""
    # Avoid a WoL-button reload on the first manifest (covered by test_wol.py).
    fake_agent.manifest["agent"]["macs"] = []
    entry = await add_reverse_entry(hass, fake_agent)
    client: TestClient = await hass_client_no_auth()
    ws = await client.ws_connect(
        REVERSE_LINK_PATH, headers=bearer(TOKEN), protocols=(SUBPROTOCOL,)
    )
    serving = asyncio.create_task(fake_agent.serve_socket(ws, None))
    try:
        await fake_agent.wait_for(lambda: entry.runtime_data.client.connected)
        await fake_agent.wait_for(lambda: entry.runtime_data.manifest_manager.loaded)
        await fake_agent.wait_for(
            lambda: (
                find_entity_id(
                    hass, "binary_sensor", fake_agent.agent_id, "session.locked"
                )
                is not None
            )
        )
        locked = find_entity_id(
            hass, "binary_sensor", fake_agent.agent_id, "session.locked"
        )
        assert locked is not None
        await fake_agent.wait_for(lambda: state_of(hass, locked) == "off")

        device = dr.async_get(hass).async_get_device_by_identifier(
            (DOMAIN, fake_agent.agent_id), entry.entry_id
        )
        assert device is not None
        response = await hass.services.async_call(
            DOMAIN,
            "run_command",
            {"device_id": device.id, "command": "lock_session"},
            blocking=True,
            return_response=True,
        )
        assert response is not None
        assert response["exit_code"] == 0
        assert [frame["command"] for frame in fake_agent.runs] == ["lock_session"]

        # The agent goes away: the entities become unavailable, the entry stays.
        await ws.close()
        await fake_agent.wait_for(lambda: state_of(hass, locked) == "unavailable")
        assert entry.state is ConfigEntryState.LOADED
    finally:
        await ws.close()
        await asyncio.gather(serving, return_exceptions=True)


async def test_a_removed_entry_makes_its_token_unknown(
    hass: HomeAssistant,
    web_server: None,
    fake_agent: FakeAgent,
    hass_client_no_auth: Callable[[], Any],
) -> None:
    """Revocation: unloaded means retry later (503), removed means rejected (401)."""
    entry = await add_reverse_entry(hass, fake_agent)
    client: TestClient = await hass_client_no_auth()

    await hass.config_entries.async_unload(entry.entry_id)
    await hass.async_block_till_done()
    unloaded = await client.get(REVERSE_LINK_PATH, headers=bearer(TOKEN))
    await hass.config_entries.async_remove(entry.entry_id)
    await hass.async_block_till_done()
    removed = await client.get(REVERSE_LINK_PATH, headers=bearer(TOKEN))

    assert unloaded.status == 503
    assert removed.status == 401


async def test_the_flow_shows_the_enrollment_string_and_creates_the_entry(
    hass: HomeAssistant,
    web_server: None,
    fake_agent: FakeAgent,
    hass_client_no_auth: Callable[[], Any],
) -> None:
    """The user adds a PC that dials in, the agent uses the string, the entry exists."""
    flow = hass.config_entries.flow
    result = await flow.async_init(DOMAIN, context={"source": "user"})
    assert result["type"] is FlowResultType.MENU
    assert result["menu_options"] == ["manual", "reverse", "hub"]
    result = await flow.async_configure(result["flow_id"], {"next_step_id": "reverse"})
    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "reverse"

    # A plain http address would send the token unencrypted.
    again = await flow.async_configure(
        result["flow_id"], {CONF_REVERSE_URL: "http://ha.example.org"}
    )
    assert again["type"] is FlowResultType.FORM
    assert again["errors"] == {CONF_REVERSE_URL: "invalid_reverse_url"}

    waiting = await flow.async_configure(
        result["flow_id"], {CONF_REVERSE_URL: "https://ha.example.org"}
    )
    assert waiting["type"] is FlowResultType.SHOW_PROGRESS
    assert waiting["progress_action"] == "waiting_for_agent"
    placeholders = waiting["description_placeholders"]
    assert placeholders is not None
    enrollment = parse_enrollment(placeholders["enrollment"])
    assert enrollment["url"] == ENDPOINT
    assert enrollment["token"].startswith(REVERSE_TOKEN_PREFIX)

    # The agent joins with the string: it dials, sends hello and is closed.
    client: TestClient = await hass_client_no_auth()
    ws = await client.ws_connect(
        REVERSE_LINK_PATH,
        headers=bearer(enrollment["token"]),
        protocols=(SUBPROTOCOL,),
    )
    serving = asyncio.create_task(fake_agent.serve_socket(ws, None))
    await asyncio.gather(serving, return_exceptions=True)
    await hass.async_block_till_done()

    result = waiting
    for _ in range(3):
        if result["type"] not in (
            FlowResultType.SHOW_PROGRESS,
            FlowResultType.SHOW_PROGRESS_DONE,
        ):
            break
        result = await flow.async_configure(result["flow_id"])
        await hass.async_block_till_done()

    assert result["type"] is FlowResultType.CREATE_ENTRY
    assert result["title"] == fake_agent.agent_name
    assert result["data"] == {
        CONF_REVERSE: True,
        CONF_REVERSE_TOKEN_HASH: hash_token(enrollment["token"]),
    }
    entry = hass.config_entries.async_entries(DOMAIN)[0]
    assert entry.unique_id == fake_agent.agent_id
    # Only the hash is kept, never the token.
    assert enrollment["token"] not in str(entry.data)
