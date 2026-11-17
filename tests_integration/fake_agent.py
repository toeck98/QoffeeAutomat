"""A fake QoffeeLink agent for tests.

An aiohttp server with a self-signed certificate that speaks QoffeeLink v1 with the
frames of ``protocol/fixtures``: pairing (``POST /link/v1/pair``), the WebSocket
handshake (``hello`` / ``client_hello``), ``get_manifest``, ``subscribe``, ``run`` and
the frames an agent pushes (``state``, ``event``, ``manifest_changed``, ``bye``). It can
also drop all connections and be stopped and restarted on the same port.
"""

import asyncio
import datetime
import json
import socket
import ssl
from collections.abc import Callable
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from aiohttp import WSMsgType, web
from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.x509.oid import NameOID

FIXTURES = Path(__file__).resolve().parents[1] / "protocol" / "fixtures"
SUBPROTOCOL = "qoffeelink.v1"

HOST = "127.0.0.1"
PAIRING_CODE = "123456"


def load_fixture(name: str) -> dict[str, Any]:
    """Load a fixture of ``protocol/fixtures`` by file name."""
    data: dict[str, Any] = json.loads((FIXTURES / name).read_text(encoding="utf-8"))
    return data


@dataclass(frozen=True, slots=True)
class Certificate:
    """A self-signed certificate on disk and its SHA-256 fingerprint."""

    cert_file: Path
    key_file: Path
    fingerprint: str


def create_certificate(directory: Path, name: str = "fake-agent") -> Certificate:
    """Create a self-signed ECDSA P-256 certificate like the agent does (S09)."""
    key = ec.generate_private_key(ec.SECP256R1())
    subject = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, name)])
    now = datetime.datetime.now(datetime.UTC)
    certificate = (
        x509.CertificateBuilder()
        .subject_name(subject)
        .issuer_name(subject)
        .public_key(key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(now - datetime.timedelta(days=1))
        .not_valid_after(now + datetime.timedelta(days=30))
        .sign(key, hashes.SHA256())
    )
    cert_file = directory / f"{name}.crt"
    key_file = directory / f"{name}.key"
    cert_file.write_bytes(certificate.public_bytes(serialization.Encoding.PEM))
    key_file.write_bytes(
        key.private_bytes(
            serialization.Encoding.PEM,
            serialization.PrivateFormat.PKCS8,
            serialization.NoEncryption(),
        )
    )
    digest = certificate.fingerprint(hashes.SHA256()).hex().upper()
    fingerprint = ":".join(digest[i : i + 2] for i in range(0, len(digest), 2))
    return Certificate(cert_file, key_file, fingerprint)


@dataclass(slots=True)
class FakeConnection:
    """One WebSocket connection of the fake agent."""

    ws: web.WebSocketResponse
    transport: asyncio.Transport | None
    client_hello: dict[str, Any] | None = None
    subscriptions: list[dict[str, Any]] = field(default_factory=list)

    @property
    def ready(self) -> bool:
        """True when the handshake is complete and the socket is open."""
        return self.client_hello is not None and not self.ws.closed


class FakeAgent:
    """The fake agent server."""

    def __init__(
        self,
        certificate: Certificate,
        *,
        token: str | None = None,
        pairing_code: str = PAIRING_CODE,
    ) -> None:
        """Create the agent; call :meth:`start` to listen."""
        self.certificate = certificate
        pair_response = load_fixture("pair_response.json")
        self.token: str = token or pair_response["token"]
        self.agent_id: str = pair_response["agent"]["id"]
        self.agent_name: str = pair_response["agent"]["name"]
        self.pairing_code = pairing_code
        # HTTP status the pairing endpoint answers instead of the normal flow.
        self.pair_status: int | None = None
        # When true the agent never answers requests (to test timeouts).
        self.silent = False
        # Protocol range announced in ``hello``.
        self.protocol_range: tuple[int, int] = (1, 1)
        self.host = HOST
        self.port = 0
        self.pair_requests: list[dict[str, Any]] = []
        self.connections: list[FakeConnection] = []
        self.runs: list[dict[str, Any]] = []
        # Commands that fail: command id -> (error code, message) of the result.
        self.run_errors: dict[str, tuple[str, str]] = {}
        # ``data`` of a successful ``run`` result instead of the fixture's.
        self.run_result: dict[str, Any] | None = None
        # When set, a ``run`` is not answered: the agent says ``bye`` with this reason
        # and closes the connections, like an agent that an installer stops (T704).
        self.bye_on_run: str | None = None
        self.rejected_connections = 0
        # The manifest answered to ``get_manifest`` and announced by ``hello``; tests
        # change it with :meth:`set_manifest`.
        self.manifest: dict[str, Any] = load_fixture("manifest.json")
        self.manifest_requests = 0
        # Boot id announced in ``hello``; tests change it to simulate a PC start.
        self.boot_id: str = load_fixture("hello.json")["boot_id"]
        # When set, the snapshot after ``subscribe`` is this mapping reduced to the
        # subscribed metrics instead of the ``state.json`` fixture.
        self.snapshot: dict[str, Any] | None = None
        self._runner: web.AppRunner | None = None

    # -- lifecycle -----------------------------------------------------------

    async def start(self, port: int = 0) -> None:
        """Listen on ``port`` (0 picks a free one; a restart passes the old port)."""
        app = web.Application()
        app.router.add_post("/link/v1/pair", self._pair)
        app.router.add_get("/link/v1", self._link)
        runner = web.AppRunner(app, handle_signals=False)
        await runner.setup()
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        sock.bind((self.host, port))
        self.port = sock.getsockname()[1]
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(self.certificate.cert_file, self.certificate.key_file)
        await web.SockSite(runner, sock, ssl_context=context).start()
        self._runner = runner

    async def stop(self) -> None:
        """Close all connections and stop listening."""
        runner, self._runner = self._runner, None
        if runner is not None:
            await runner.cleanup()

    # -- helpers for tests ---------------------------------------------------

    @property
    def ready_connections(self) -> list[FakeConnection]:
        """Connections whose handshake is done and that are still open."""
        return [c for c in self.connections if c.ready]

    async def wait_ready(self, count: int = 1, limit: float = 5.0) -> None:
        """Wait until ``count`` connections completed the handshake."""
        async with asyncio.timeout(limit):
            while len(self.ready_connections) < count:  # noqa: ASYNC110
                await asyncio.sleep(0.01)

    async def wait_for(self, predicate: Callable[[], bool], limit: float = 5.0) -> None:
        """Wait until ``predicate()`` is true."""
        async with asyncio.timeout(limit):
            while not predicate():  # noqa: ASYNC110
                await asyncio.sleep(0.01)

    async def send(self, frame: dict[str, Any]) -> None:
        """Send a frame to every ready connection."""
        for connection in self.ready_connections:
            await connection.ws.send_json(frame)

    async def send_fixture(self, name: str) -> None:
        """Send the frame of a fixture to every ready connection."""
        await self.send(load_fixture(name))

    def set_manifest(self, manifest: dict[str, Any]) -> None:
        """Replace the manifest (the next ``hello`` and ``get_manifest`` use it)."""
        self.manifest = manifest

    async def send_manifest_changed(self) -> None:
        """Announce the current manifest revision to every ready connection."""
        await self.send({"t": "manifest_changed", "rev": self.manifest["rev"]})

    async def send_state(self, values: dict[str, Any]) -> None:
        """Push a ``state`` frame with ``values`` to every ready connection."""
        await self.send({"t": "state", "ts": 1763126400000, "v": values})

    async def send_event(
        self, event_type: str, data: dict[str, Any] | None = None
    ) -> None:
        """Push an ``event`` frame to every ready connection."""
        frame: dict[str, Any] = {"t": "event", "type": event_type, "ts": 1763126402000}
        if data is not None:
            frame["data"] = data
        await self.send(frame)

    async def send_bye(self, reason: str, *, close: bool = True) -> None:
        """Send ``bye`` and, like a real agent, close the connections (1001)."""
        await self.send({"t": "bye", "reason": reason})
        if close:
            await self.close_connections(1001)

    def subscribed_metrics(self) -> list[list[str] | str]:
        """The ``metrics`` of every ``subscribe`` request of every connection."""
        return [
            frame["metrics"]
            for connection in self.connections
            for frame in connection.subscriptions
        ]

    def drop_connections(self) -> None:
        """Abort the TCP connections without a closing handshake."""
        for connection in self.connections:
            if connection.transport is not None:
                connection.transport.abort()

    async def close_connections(self, code: int) -> None:
        """Close every open connection with a WebSocket close code."""
        for connection in self.ready_connections:
            await connection.ws.close(code=code)

    # -- handlers ------------------------------------------------------------

    def _hello(self) -> dict[str, Any]:
        hello = load_fixture("hello.json")
        low, high = self.protocol_range
        hello["protocol"] = {"min": low, "max": high}
        hello["agent"]["id"] = self.agent_id
        hello["agent"]["name"] = self.agent_name
        hello["manifest_rev"] = self.manifest["rev"]
        hello["boot_id"] = self.boot_id
        return hello

    async def _pair(self, request: web.Request) -> web.Response:
        body = await request.json()
        self.pair_requests.append(body)
        if self.pair_status is not None:
            return web.json_response({"code": "unavailable"}, status=self.pair_status)
        if body.get("code") != self.pairing_code:
            return web.json_response({"code": "unauthorized"}, status=401)
        agent = {"id": self.agent_id, "name": self.agent_name}
        return web.json_response({"token": self.token, "agent": agent})

    async def _link(self, request: web.Request) -> web.StreamResponse:
        if request.headers.get("Authorization") != f"Bearer {self.token}":
            self.rejected_connections += 1
            return web.json_response({"code": "unauthorized"}, status=401)
        ws = web.WebSocketResponse(protocols=(SUBPROTOCOL,))
        await ws.prepare(request)
        await self.serve_socket(ws, request.transport)
        return ws

    async def serve_socket(self, ws: Any, transport: asyncio.Transport | None) -> None:
        """Speak the agent's side of QoffeeLink on an open WebSocket until it ends.

        Used for the sockets that Home Assistant accepted, and for the one that the
        agent itself opened when it dials Home Assistant (the reverse link, T710).
        """
        connection = FakeConnection(ws, transport)
        self.connections.append(connection)
        await ws.send_json(self._hello())
        async for message in ws:
            if message.type is WSMsgType.TEXT:
                await self._handle(connection, json.loads(message.data))

    def _snapshot(self, metrics: list[str] | str) -> dict[str, Any]:
        state = load_fixture("state.json")
        if self.snapshot is not None:
            state["v"] = {
                metric: value
                for metric, value in self.snapshot.items()
                if metrics == "*" or metric in metrics
            }
        return state

    async def _handle(self, connection: FakeConnection, frame: dict[str, Any]) -> None:
        ws = connection.ws
        if self.silent and frame.get("t") != "client_hello":
            return
        match frame.get("t"):
            case "client_hello":
                connection.client_hello = frame
            case "get_manifest":
                self.manifest_requests += 1
                await ws.send_json(_ok(frame, self.manifest))
            case "subscribe":
                connection.subscriptions.append(frame)
                await ws.send_json(_ok(frame, {}))
                await ws.send_json(self._snapshot(frame["metrics"]))
            case "run":
                self.runs.append(frame)
                if self.bye_on_run is not None:
                    await self.send_bye(self.bye_on_run)
                else:
                    await ws.send_json(self._run_result(frame))
            case _:
                pass

    def _run_result(self, frame: dict[str, Any]) -> dict[str, Any]:
        """Answer a ``run`` request: an error, or the result with or without output."""
        command = str(frame.get("command"))
        if (failure := self.run_errors.get(command)) is not None:
            code, message = failure
            return {
                "t": "result",
                "id": frame["id"],
                "ok": False,
                "error": {"code": code, "message": message},
            }
        if command == "nope":
            error = load_fixture("result.error.json")
            error["id"] = frame["id"]
            return error
        result = load_fixture("result.run.json")
        result["id"] = frame["id"]
        if self.run_result is not None:
            result["data"] = dict(self.run_result)
        if frame.get("wait") is False:
            # Without ``wait`` the result carries only the id of the run.
            result["data"] = {"run_id": result["data"]["run_id"]}
        return result


def _ok(request: dict[str, Any], data: dict[str, Any]) -> dict[str, Any]:
    return {"t": "result", "id": request["id"], "ok": True, "data": data}
