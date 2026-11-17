"""Tests of the QoffeeLink client against the fake agent and the real agent (T501)."""

import ast
import asyncio
import os
import re
import shutil
from collections.abc import AsyncIterator
from pathlib import Path
from typing import Any

import aiohttp
import pytest

from custom_components.qoffeeautomat.client import (
    AuthenticationError,
    CannotConnectError,
    CertificateMismatchError,
    NotConnectedError,
    PairingCodeExpiredError,
    PairingCodeInvalidError,
    QoffeeLinkClient,
    RequestError,
    RequestTimeoutError,
    UnexpectedResponseError,
    UnsupportedProtocolError,
    async_fetch_fingerprint,
    async_pair,
    normalize_fingerprint,
)
from custom_components.qoffeeautomat.protocol import (
    Bye,
    Event,
    ManifestChanged,
    ProtocolError,
    State,
    parse_manifest,
)
from tests_integration.fake_agent import PAIRING_CODE, FakeAgent

ROOT = Path(__file__).resolve().parents[1]
WRONG_FINGERPRINT = ":".join(["00"] * 32)
FAST = {"backoff_min": 0.05, "backoff_max": 0.2, "connect_timeout": 3.0}


def make_client(
    session: aiohttp.ClientSession, agent: FakeAgent, **options: Any
) -> QoffeeLinkClient:
    """Create a client for the fake agent with test-friendly timings."""
    settings: dict[str, Any] = {**FAST, **options}
    token = settings.pop("token", agent.token)
    fingerprint = settings.pop("fingerprint", agent.certificate.fingerprint)
    return QoffeeLinkClient(
        session, agent.host, agent.port, token, fingerprint, **settings
    )


@pytest.fixture(name="client")
async def client_fixture(
    session: aiohttp.ClientSession, fake_agent: FakeAgent
) -> AsyncIterator[QoffeeLinkClient]:
    """A started client of the fake agent, stopped after the test."""
    client = make_client(session, fake_agent)
    client.start()
    await fake_agent.wait_ready()
    await fake_agent.wait_for(lambda: client.connected)
    yield client
    await client.stop()


def test_client_module_has_no_home_assistant_imports() -> None:
    source = (ROOT / "custom_components" / "qoffeeautomat" / "client.py").read_text(
        encoding="utf-8"
    )
    modules: set[str] = set()
    for node in ast.walk(ast.parse(source)):
        if isinstance(node, ast.Import):
            modules.update(alias.name for alias in node.names)
        elif isinstance(node, ast.ImportFrom) and node.module:
            modules.add(node.module)
    assert not {name for name in modules if name.split(".")[0] == "homeassistant"}


# -- fingerprints ---------------------------------------------------------------


def test_normalize_fingerprint_accepts_common_notations() -> None:
    expected = ":".join(["AB"] * 32)
    assert normalize_fingerprint("ab" * 32) == expected
    assert normalize_fingerprint(expected) == expected
    assert normalize_fingerprint(" ".join(["ab"] * 32)) == expected


@pytest.mark.parametrize("value", ["", "AB:CD", "zz" * 32, "ab" * 33])
def test_normalize_fingerprint_rejects_garbage(value: str) -> None:
    with pytest.raises(ValueError, match=r"fingerprint|hexadecimal"):
        normalize_fingerprint(value)


async def test_fetch_fingerprint_reads_the_certificate(fake_agent: FakeAgent) -> None:
    fingerprint = await async_fetch_fingerprint(fake_agent.host, fake_agent.port)
    assert fingerprint == fake_agent.certificate.fingerprint


async def test_fetch_fingerprint_unreachable(fake_agent: FakeAgent) -> None:
    await fake_agent.stop()
    with pytest.raises(CannotConnectError):
        await async_fetch_fingerprint(
            fake_agent.host, fake_agent.port, connect_timeout=2
        )


# -- pairing --------------------------------------------------------------------


async def test_pair_returns_token_and_agent(
    session: aiohttp.ClientSession, fake_agent: FakeAgent
) -> None:
    response = await async_pair(
        session,
        fake_agent.host,
        fake_agent.port,
        fake_agent.certificate.fingerprint,
        PAIRING_CODE,
        client_name="Home",
    )
    assert response.token == fake_agent.token
    assert response.agent_id == fake_agent.agent_id
    assert response.agent_name == fake_agent.agent_name
    assert fake_agent.pair_requests == [
        {"code": PAIRING_CODE, "client": {"kind": "homeassistant", "name": "Home"}}
    ]


@pytest.mark.parametrize(
    ("code", "status", "error"),
    [
        ("000000", None, PairingCodeInvalidError),
        (PAIRING_CODE, 410, PairingCodeExpiredError),
        (PAIRING_CODE, 429, PairingCodeExpiredError),
        (PAIRING_CODE, 500, UnexpectedResponseError),
    ],
)
async def test_pair_failures(
    session: aiohttp.ClientSession,
    fake_agent: FakeAgent,
    code: str,
    status: int | None,
    error: type[Exception],
) -> None:
    fake_agent.pair_status = status
    with pytest.raises(error):
        await async_pair(
            session,
            fake_agent.host,
            fake_agent.port,
            fake_agent.certificate.fingerprint,
            code,
            client_name="Home",
        )


async def test_pair_refuses_a_different_certificate(
    session: aiohttp.ClientSession, fake_agent: FakeAgent
) -> None:
    with pytest.raises(CertificateMismatchError):
        await async_pair(
            session,
            fake_agent.host,
            fake_agent.port,
            WRONG_FINGERPRINT,
            PAIRING_CODE,
            client_name="Home",
        )
    assert fake_agent.pair_requests == []


async def test_pair_unreachable(
    session: aiohttp.ClientSession, fake_agent: FakeAgent
) -> None:
    await fake_agent.stop()
    with pytest.raises(CannotConnectError):
        await async_pair(
            session,
            fake_agent.host,
            fake_agent.port,
            fake_agent.certificate.fingerprint,
            PAIRING_CODE,
            client_name="Home",
            request_timeout=2,
        )


# -- connecting -----------------------------------------------------------------


async def test_probe_negotiates_the_protocol(
    session: aiohttp.ClientSession, fake_agent: FakeAgent
) -> None:
    hello = await make_client(session, fake_agent, client_name="Test").probe()
    assert hello.agent.id == fake_agent.agent_id
    await fake_agent.wait_for(
        lambda: fake_agent.connections[0].client_hello is not None
    )
    client_hello = fake_agent.connections[0].client_hello
    assert client_hello is not None
    assert client_hello["protocol"] == 1
    assert client_hello["client"]["kind"] == "homeassistant"
    assert client_hello["client"]["name"] == "Test"


async def test_a_certificate_with_another_fingerprint_is_refused(
    session: aiohttp.ClientSession, fake_agent: FakeAgent
) -> None:
    client = make_client(session, fake_agent, fingerprint=WRONG_FINGERPRINT)
    with pytest.raises(CertificateMismatchError):
        await client.probe()

    client.start()
    try:
        await fake_agent.wait_for(lambda: client.last_error is not None)
        assert isinstance(client.last_error, CertificateMismatchError)
        await asyncio.sleep(0.3)
        assert not client.connected
        assert fake_agent.connections == []
    finally:
        await client.stop()


async def test_unreachable_agent(
    session: aiohttp.ClientSession, fake_agent: FakeAgent
) -> None:
    await fake_agent.stop()
    with pytest.raises(CannotConnectError):
        await make_client(session, fake_agent).probe()


async def test_unsupported_protocol(
    session: aiohttp.ClientSession, fake_agent: FakeAgent
) -> None:
    fake_agent.protocol_range = (2, 3)
    with pytest.raises(UnsupportedProtocolError):
        await make_client(session, fake_agent).probe()


async def test_invalid_token_stops_retrying_until_a_new_token(
    session: aiohttp.ClientSession, fake_agent: FakeAgent
) -> None:
    client = make_client(session, fake_agent, token="x" * 43)
    with pytest.raises(AuthenticationError):
        await client.probe()

    failures: list[None] = []
    client.add_auth_failed_listener(failures.append)
    client.start()
    try:
        await fake_agent.wait_for(lambda: client.auth_failed)
        await asyncio.sleep(0.4)
        assert failures == [None]
        # One probe and one attempt of the supervisor; no retry loop follows.
        assert fake_agent.rejected_connections == 2

        client.update_token(fake_agent.token)
        await fake_agent.wait_ready()
        assert not client.auth_failed
    finally:
        await client.stop()


async def test_revoked_token_while_connected(
    fake_agent: FakeAgent, client: QoffeeLinkClient
) -> None:
    failures: list[None] = []
    client.add_auth_failed_listener(failures.append)
    await fake_agent.close_connections(4401)
    await fake_agent.wait_for(lambda: client.auth_failed)
    assert failures == [None]
    assert not client.connected


# -- reconnecting ---------------------------------------------------------------


async def test_reconnects_and_resubscribes_after_a_drop(
    fake_agent: FakeAgent, client: QoffeeLinkClient
) -> None:
    changes: list[bool] = []
    client.add_connection_listener(changes.append)
    await client.subscribe(["cpu.usage"], {"cpu.usage": 5})
    first = fake_agent.connections[0]
    assert first.subscriptions[0]["metrics"] == ["cpu.usage"]

    fake_agent.drop_connections()
    await fake_agent.wait_for(lambda: len(fake_agent.ready_connections) == 0)
    await fake_agent.wait_ready()
    await fake_agent.wait_for(lambda: client.connected)

    second = fake_agent.ready_connections[0]
    assert second is not first
    await fake_agent.wait_for(lambda: len(second.subscriptions) == 1)
    assert second.subscriptions[0]["metrics"] == ["cpu.usage"]
    assert second.subscriptions[0]["intervals"] == {"cpu.usage": 5}
    await fake_agent.wait_for(lambda: changes == [False, True])


async def test_immediate_connect_while_in_backoff(
    session: aiohttp.ClientSession, fake_agent: FakeAgent
) -> None:
    await fake_agent.stop()
    client = make_client(session, fake_agent, backoff_min=60, backoff_max=60)
    client.start()
    try:
        await fake_agent.wait_for(lambda: client.last_error is not None)
        await asyncio.sleep(0.2)
        assert not client.connected

        await fake_agent.start(fake_agent.port)
        client.trigger_connect()
        await fake_agent.wait_ready(limit=2)
    finally:
        await client.stop()


async def test_fast_retry_connects_without_waiting_for_the_backoff(
    session: aiohttp.ClientSession, fake_agent: FakeAgent
) -> None:
    await fake_agent.stop()
    client = make_client(
        session, fake_agent, backoff_min=60, backoff_max=60, fast_retry_interval=0.05
    )
    client.start()
    try:
        await fake_agent.wait_for(lambda: client.last_error is not None)
        client.start_fast_retry(30)
        await fake_agent.start(fake_agent.port)
        await fake_agent.wait_ready(limit=2)
    finally:
        await client.stop()


async def test_trigger_connect_while_connected_does_nothing(
    fake_agent: FakeAgent, client: QoffeeLinkClient
) -> None:
    client.trigger_connect()
    await asyncio.sleep(0.2)
    assert len(fake_agent.connections) == 1


async def test_update_address_is_used_for_the_next_attempt(
    session: aiohttp.ClientSession, fake_agent: FakeAgent
) -> None:
    client = make_client(session, fake_agent)
    client.update_address(fake_agent.host, fake_agent.port)
    assert (client.host, client.port) == (fake_agent.host, fake_agent.port)
    assert (await client.probe()).agent.id == fake_agent.agent_id


# -- frames and requests --------------------------------------------------------


async def test_frames_reach_the_listeners(
    fake_agent: FakeAgent, client: QoffeeLinkClient
) -> None:
    states: list[State] = []
    events: list[Event] = []
    manifests: list[ManifestChanged] = []
    byes: list[Bye] = []
    removers = [
        client.add_state_listener(states.append),
        client.add_event_listener(events.append),
        client.add_manifest_listener(manifests.append),
        client.add_bye_listener(byes.append),
        client.add_bye_listener(lambda _: 1 / 0),  # a failing listener is harmless
    ]
    for name in ("state.json", "event.json", "manifest_changed.json", "bye.json"):
        await fake_agent.send_fixture(name)
    await fake_agent.send({"t": "from_the_future", "x": 1})
    await fake_agent.send({"t": "state"})  # invalid frames are ignored
    await fake_agent.wait_for(lambda: len(byes) == 1)

    assert states[0].values["cpu.usage"] == 12
    assert events[0].type == "session_locked"
    assert manifests[0].rev == "a1b2c3d4e5f6"
    assert client.last_bye == byes[0]
    assert client.hello is not None

    for remove in removers:
        remove()
    remove()  # removing twice is harmless
    await fake_agent.send_fixture("state.json")
    await asyncio.sleep(0.1)
    assert len(states) == 1


async def test_subscribe_delivers_a_snapshot(
    fake_agent: FakeAgent, client: QoffeeLinkClient
) -> None:
    states: list[State] = []
    client.add_state_listener(states.append)
    await client.subscribe("*")
    await fake_agent.wait_for(lambda: len(states) == 1)
    with pytest.raises(ProtocolError, match="metrics"):
        await client.subscribe("cpu.usage")


async def test_get_manifest(client: QoffeeLinkClient) -> None:
    manifest = parse_manifest(await client.get_manifest())
    assert manifest.rev == "143ffa0cd221"


async def test_run_command(fake_agent: FakeAgent, client: QoffeeLinkClient) -> None:
    result = await client.run_command(
        "set_display_profile", {"Profile": "TV-Mode"}, user_id="u1"
    )
    assert result.exit_code == 0
    assert result.stdout == "Applied TV-Mode\n"
    assert fake_agent.runs[0]["vars"] == {"Profile": "TV-Mode"}
    assert fake_agent.runs[0]["context"] == {"user_id": "u1"}


async def test_run_command_error(client: QoffeeLinkClient) -> None:
    with pytest.raises(RequestError) as error:
        await client.run_command("nope")
    assert error.value.code == "unknown_command"


async def test_request_timeout(
    session: aiohttp.ClientSession, fake_agent: FakeAgent
) -> None:
    client = make_client(session, fake_agent, request_timeout=0.2)
    client.start()
    try:
        await fake_agent.wait_ready()
        await fake_agent.wait_for(lambda: client.connected)
        fake_agent.silent = True
        with pytest.raises(RequestTimeoutError):
            await client.get_manifest()
        with pytest.raises(RequestTimeoutError):
            await client.run_command("x", request_timeout=0.1)
    finally:
        await client.stop()


async def test_request_without_connection(
    session: aiohttp.ClientSession, fake_agent: FakeAgent
) -> None:
    client = make_client(session, fake_agent)
    with pytest.raises(NotConnectedError):
        await client.get_manifest()


async def test_stop_fails_pending_requests_and_disconnects(
    fake_agent: FakeAgent, client: QoffeeLinkClient
) -> None:
    changes: list[bool] = []
    client.add_connection_listener(changes.append)
    fake_agent.silent = True
    pending = asyncio.create_task(client.get_manifest())
    await asyncio.sleep(0.1)
    await client.stop()
    with pytest.raises(NotConnectedError):
        await pending
    assert not client.connected
    assert changes == [False]
    await client.stop()  # stopping twice is harmless


# -- the real agent -------------------------------------------------------------

AGENT_DIR_VARIABLE = "QOFFEE_E2E_AGENT_DIR"
AGENT_ASSEMBLY = "QoffeeAutomat.Agent.dll"


def _agent_directory() -> Path | None:
    for candidate in (os.environ.get(AGENT_DIR_VARIABLE), ROOT / "artifacts" / "agent"):
        if candidate and (Path(candidate) / AGENT_ASSEMBLY).is_file():
            return Path(candidate)
    return None


@pytest.mark.skipif(
    _agent_directory() is None or shutil.which("dotnet") is None,
    reason=f"published agent not found (set {AGENT_DIR_VARIABLE}) or dotnet missing",
)
async def test_interoperates_with_the_real_agent(
    socket_enabled: None, tmp_path: Path
) -> None:
    agent_directory = _agent_directory()
    assert agent_directory is not None
    data_dir = tmp_path / "data"
    process = await asyncio.create_subprocess_exec(
        "dotnet",
        str(agent_directory / AGENT_ASSEMBLY),
        "run",
        "--platform",
        "simulated",
        "--data-dir",
        str(data_dir),
        "--ui-port",
        "0",
        "--link-port",
        "0",
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.STDOUT,
        cwd=agent_directory,
    )
    log: list[str] = []
    try:
        ui_port, link_port = await _read_ports(process, log)
        drain = asyncio.create_task(_drain(process, log))
        try:
            await _exercise_real_agent(data_dir, ui_port, link_port)
        finally:
            drain.cancel()
            await asyncio.gather(drain, return_exceptions=True)
    except Exception:
        print("\n".join(log))  # noqa: T201 - shown by pytest when the test fails
        raise
    finally:
        if process.returncode is None:
            process.kill()
        await process.wait()


async def _read_ports(
    process: asyncio.subprocess.Process, log: list[str]
) -> tuple[int, int]:
    """Read the ports from the line ``Listening on http://..., https://...``."""
    assert process.stdout is not None
    async with asyncio.timeout(60):
        while True:
            raw = await process.stdout.readline()
            if not raw:
                msg = "the agent exited before it listened"
                raise RuntimeError(msg)
            line = raw.decode(errors="replace")
            log.append(line.rstrip())
            ui = re.search(r"http://127\.0\.0\.1:(\d+)", line)
            link = re.search(r"https://\S+?:(\d+)", line)
            if ui and link:
                return int(ui.group(1)), int(link.group(1))


async def _drain(process: asyncio.subprocess.Process, log: list[str]) -> None:
    assert process.stdout is not None
    while raw := await process.stdout.readline():
        log.append(raw.decode(errors="replace").rstrip())


async def _exercise_real_agent(data_dir: Path, ui_port: int, link_port: int) -> None:
    setup_token_file = data_dir / "secrets" / "setup-token.txt"
    async with asyncio.timeout(20):
        while not (  # noqa: ASYNC110
            setup_token_file.exists() and setup_token_file.stat().st_size
        ):
            await asyncio.sleep(0.2)
    setup_token = setup_token_file.read_text(encoding="utf-8").strip()

    ui = f"http://127.0.0.1:{ui_port}/api/v1"
    csrf = {"X-QoffeeAutomat-Csrf": "1"}
    jar = aiohttp.CookieJar(unsafe=True)
    async with aiohttp.ClientSession(cookie_jar=jar) as http:
        async with http.post(
            f"{ui}/auth/setup",
            json={"setupToken": setup_token, "password": "correct-horse-battery"},
            headers=csrf,
        ) as response:
            assert response.status == 200
        async with http.post(f"{ui}/link/pairing", headers=csrf) as response:
            assert response.status == 200
            pairing = await response.json()

    fingerprint = await async_fetch_fingerprint("127.0.0.1", link_port)
    assert fingerprint == normalize_fingerprint(pairing["fingerprint"])

    async with aiohttp.ClientSession() as session:
        paired = await async_pair(
            session,
            "127.0.0.1",
            link_port,
            fingerprint,
            pairing["code"],
            client_name="pytest",
        )
        client = QoffeeLinkClient(
            session, "127.0.0.1", link_port, paired.token, fingerprint
        )
        states: list[State] = []
        client.add_state_listener(states.append)
        client.start()
        try:
            async with asyncio.timeout(15):
                while not client.connected:  # noqa: ASYNC110
                    await asyncio.sleep(0.1)
            assert client.hello is not None
            assert client.hello.agent.id == paired.agent_id

            manifest = parse_manifest(await client.get_manifest())
            assert manifest.agent.id == paired.agent_id
            assert manifest.rev == client.hello.manifest_rev

            await client.subscribe("*")
            async with asyncio.timeout(15):
                while not states:  # noqa: ASYNC110
                    await asyncio.sleep(0.1)

            with pytest.raises(RequestError) as error:
                await client.run_command("no_such_command")
            assert error.value.code == "unknown_command"
        finally:
            await client.stop()
