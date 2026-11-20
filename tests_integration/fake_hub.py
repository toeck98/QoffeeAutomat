"""A fake QoffeeAutomat hub for tests (PLAN.md S20, T914).

An aiohttp server with a self-signed certificate that speaks the hub side of the fleet
link with the frames of ``protocol/fixtures/hub``: pairing (``POST /hub/v1/pair``), the
WebSocket handshake (``hello`` with ``hub`` / ``client_hello``) and the requests
``get_agents`` and the agent-scoped ``get_manifest``, ``subscribe`` and ``run``. It
serves two agents, Gaming-PC (the agent of the fake agent of the other tests, so a
direct entry and the hub can list the same PC) and Media-PC. Tests change the agents and
push ``agent_added``, ``agent_removed``, ``agent_status``, ``state``, ``event`` and
``manifest_changed``.
"""

import asyncio
import socket
import ssl
from collections.abc import Callable, Iterable
from dataclasses import dataclass
from typing import Any

from aiohttp import WSMsgType, web

from tests_integration.fake_agent import (
    HOST,
    SUBPROTOCOL,
    Certificate,
    FakeConnection,
    load_fixture,
)

HUB_PAIRING_CODE = "654321"
GAMING_ID = "5b0c6c1e-8e0a-4f6e-9a57-3d2f3a1c9b77"
MEDIA_ID = "7d3f9a10-2b6c-4e8d-9c11-5a0e4b7f8d21"
GAMING_MAC = "A4:B1:C1:00:11:22"


def manifest_of(agent_id: str, name: str, macs: Iterable[str]) -> dict[str, Any]:
    """Return the manifest of the fixtures for another agent."""
    manifest = load_fixture("manifest.json")
    manifest["agent"]["id"] = agent_id
    manifest["agent"]["name"] = name
    manifest["agent"]["macs"] = list(macs)
    return manifest


@dataclass(slots=True)
class HubAgent:
    """An agent that the hub knows."""

    id: str
    name: str
    online: bool = True
    power_state: str = "unknown"
    manifest: dict[str, Any] | None = None

    def entry(self) -> dict[str, Any]:
        """Return the entry of ``get_agents`` and the members of ``agent_added``."""
        entry: dict[str, Any] = {
            "id": self.id,
            "name": self.name,
            "online": self.online,
            "power_state": self.power_state,
        }
        if self.manifest is not None:
            entry["manifest_rev"] = self.manifest["rev"]
            entry["agent"] = dict(self.manifest["agent"])
        return entry


class FakeHub:
    """The fake hub server."""

    def __init__(
        self, certificate: Certificate, *, pairing_code: str = HUB_PAIRING_CODE
    ) -> None:
        """Create the hub; call :meth:`start` to listen."""
        self.certificate = certificate
        pair_response = load_fixture("hub/pair_response.json")
        self.token: str = pair_response["token"]
        self.hub_id: str = pair_response["hub"]["id"]
        self.hub_name: str = pair_response["hub"]["name"]
        self.pairing_code = pairing_code
        # HTTP status the pairing endpoint answers instead of the normal flow.
        self.pair_status: int | None = None
        # A body that the pairing endpoint answers (with status 200) instead of the
        # response of the fixture, to test a response that violates the schema.
        self.pair_response: Any = None
        # Protocol range and features announced in ``hello``.
        self.protocol_range: tuple[int, int] = (1, 1)
        self.features: list[str] = ["fleet"]
        self.host = HOST
        self.port = 0
        self.agents: dict[str, HubAgent] = {}
        self.add_agent(GAMING_ID, "Gaming-PC", macs=[GAMING_MAC])
        self.add_agent(MEDIA_ID, "Media-PC")
        self.pair_requests: list[dict[str, Any]] = []
        self.connections: list[FakeConnection] = []
        self.runs: list[dict[str, Any]] = []
        # Commands that fail: command id -> (error code, message) of the result.
        self.run_errors: dict[str, tuple[str, str]] = {}
        self.manifest_requests: list[str] = []
        self.rejected_connections = 0
        self._runner: web.AppRunner | None = None

    # -- lifecycle -----------------------------------------------------------

    async def start(self, port: int = 0) -> None:
        """Listen on ``port`` (0 picks a free one; a restart passes the old port)."""
        app = web.Application()
        app.router.add_post("/hub/v1/pair", self._pair)
        app.router.add_get("/hub/v1/fleet", self._fleet)
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

    # -- agents --------------------------------------------------------------

    def add_agent(
        self,
        agent_id: str,
        name: str,
        *,
        online: bool = True,
        macs: Iterable[str] = (),
        power_state: str = "unknown",
        known: bool = True,
    ) -> HubAgent:
        """Add an agent to the hub (``known`` false: it never connected)."""
        agent = HubAgent(
            id=agent_id,
            name=name,
            online=online,
            power_state=power_state,
            manifest=manifest_of(agent_id, name, macs) if known else None,
        )
        self.agents[agent_id] = agent
        return agent

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

    async def send_agent_added(self, agent: HubAgent) -> None:
        """Add the agent to the hub and tell the clients."""
        self.agents[agent.id] = agent
        entry = agent.entry()
        del entry["id"]
        await self.send({"t": "agent_added", "a": agent.id, **entry})

    async def send_agent_removed(self, agent_id: str) -> None:
        """Remove the agent from the hub and tell the clients."""
        del self.agents[agent_id]
        await self.send({"t": "agent_removed", "a": agent_id})

    async def send_status(
        self, agent_id: str, *, online: bool, power_state: str = "unknown"
    ) -> None:
        """Change the presence of an agent and tell the clients."""
        agent = self.agents[agent_id]
        agent.online = online
        agent.power_state = power_state
        await self.send(
            {
                "t": "agent_status",
                "a": agent_id,
                "online": online,
                "power_state": power_state,
            }
        )

    async def send_state(self, agent_id: str, values: dict[str, Any]) -> None:
        """Push a ``state`` frame of an agent to every ready connection."""
        await self.send({"t": "state", "a": agent_id, "ts": 1763126400000, "v": values})

    async def send_event(
        self, agent_id: str, event_type: str, data: dict[str, Any] | None = None
    ) -> None:
        """Push an ``event`` frame of an agent to every ready connection."""
        frame: dict[str, Any] = {
            "t": "event",
            "a": agent_id,
            "type": event_type,
            "ts": 1763126402000,
        }
        if data is not None:
            frame["data"] = data
        await self.send(frame)

    async def send_manifest_changed(self, agent_id: str) -> None:
        """Announce the current manifest revision of an agent."""
        manifest = self.agents[agent_id].manifest
        assert manifest is not None
        rev = manifest["rev"]
        await self.send({"t": "manifest_changed", "a": agent_id, "rev": rev})

    def subscribed(self, agent_id: str) -> list[list[str] | str]:
        """The ``metrics`` of every ``subscribe`` request for an agent."""
        return [
            frame["metrics"]
            for connection in self.connections
            for frame in connection.subscriptions
            if frame["a"] == agent_id
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
        hello = load_fixture("hub/fleet_hello.json")
        low, high = self.protocol_range
        hello["protocol"] = {"min": low, "max": high}
        hello["hub"]["id"] = self.hub_id
        hello["hub"]["name"] = self.hub_name
        hello["features"] = list(self.features)
        return hello

    async def _pair(self, request: web.Request) -> web.Response:
        body = await request.json()
        self.pair_requests.append(body)
        if self.pair_status is not None:
            error = {"code": "unavailable", "message": "The hub is not available."}
            return web.json_response({"error": error}, status=self.pair_status)
        if body.get("code") != self.pairing_code:
            error = {"code": "unauthorized", "message": "The code is invalid."}
            return web.json_response({"error": error}, status=401)
        if self.pair_response is not None:
            return web.json_response(self.pair_response)
        response = load_fixture("hub/pair_response.json")
        response["token"] = self.token
        response["hub"] = {"id": self.hub_id, "name": self.hub_name}
        response["fingerprint"] = self.certificate.fingerprint
        return web.json_response(response)

    async def _fleet(self, request: web.Request) -> web.StreamResponse:
        if request.headers.get("Authorization") != f"Bearer {self.token}":
            self.rejected_connections += 1
            error = {"code": "unauthorized", "message": "The token is invalid."}
            return web.json_response({"error": error}, status=401)
        ws = web.WebSocketResponse(protocols=(SUBPROTOCOL,))
        await ws.prepare(request)
        connection = FakeConnection(ws, request.transport)
        self.connections.append(connection)
        await ws.send_json(self._hello())
        async for message in ws:
            if message.type is WSMsgType.TEXT:
                await self._handle(connection, message.json())
        return ws

    async def _handle(self, connection: FakeConnection, frame: dict[str, Any]) -> None:
        ws = connection.ws
        match frame.get("t"):
            case "client_hello":
                connection.client_hello = frame
            case "get_agents":
                agents = [agent.entry() for agent in self.agents.values()]
                await ws.send_json(_ok(frame, {"agents": agents}))
            case "get_manifest":
                self.manifest_requests.append(frame["a"])
                await ws.send_json(self._manifest_result(frame))
            case "subscribe":
                connection.subscriptions.append(frame)
                if frame["a"] not in self.agents:
                    await ws.send_json(_error(frame, "unknown_agent", "No such agent."))
                    return
                await ws.send_json(_ok(frame, {}))
                await ws.send_json(self._snapshot(frame["a"], frame["metrics"]))
            case "run":
                self.runs.append(frame)
                await ws.send_json(self._run_result(frame))
            case _:
                pass

    def _manifest_result(self, frame: dict[str, Any]) -> dict[str, Any]:
        agent = self.agents.get(frame["a"])
        if agent is None:
            return _error(frame, "unknown_agent", "There is no such agent.")
        if agent.manifest is None:
            return _error(frame, "agent_offline", "The agent has not connected yet.")
        return _ok(frame, agent.manifest)

    def _snapshot(self, agent_id: str, metrics: list[str] | str) -> dict[str, Any]:
        state = load_fixture("hub/fleet_state.json")
        state["a"] = agent_id
        state["v"] = {
            metric: value
            for metric, value in state["v"].items()
            if metrics == "*" or metric in metrics
        }
        return state

    def _run_result(self, frame: dict[str, Any]) -> dict[str, Any]:
        agent = self.agents.get(frame["a"])
        command = str(frame.get("command"))
        if agent is None:
            return _error(frame, "unknown_agent", "There is no such agent.")
        if not agent.online:
            return _error(frame, "agent_offline", "The agent is not connected.")
        if (failure := self.run_errors.get(command)) is not None:
            return _error(frame, *failure)
        result = load_fixture("result.run.json")
        result["id"] = frame["id"]
        if frame.get("wait") is False:
            result["data"] = {"run_id": result["data"]["run_id"]}
        return result


def _ok(request: dict[str, Any], data: dict[str, Any]) -> dict[str, Any]:
    return {"t": "result", "id": request["id"], "ok": True, "data": data}


def _error(request: dict[str, Any], code: str, message: str) -> dict[str, Any]:
    return {
        "t": "result",
        "id": request["id"],
        "ok": False,
        "error": {"code": code, "message": message},
    }
