"""QoffeeLink protocol client (PLAN.md S07, S09, S10).

This module must not import Home Assistant: it only depends on ``aiohttp`` and
:mod:`.protocol`, so it can become a PyPI library later.

:class:`LinkClient` is everything that does not depend on who opened the WebSocket:
the handshake, requests, subscriptions and the frames the agent pushes.
:class:`QoffeeLinkClient` dials the agent (the normal case, D-005);
:class:`ReverseLinkClient` is given WebSockets that the agent opened itself (the
reverse link of roaming devices, D-043, ADR 0003).

:class:`FleetClient` is the connection to a hub (PLAN.md S20): one WebSocket for all
agents in the scope of a fleet token. :class:`AgentLink` is one agent on it, with the
interface of :class:`LinkClient`, so the code above does not know the route.
"""

import asyncio
import contextlib
import hashlib
import logging
import random
import ssl
from collections.abc import Callable, Mapping, Sequence
from typing import Any, Final

import aiohttp
from aiohttp import web

from .protocol import (
    BYE_REASONS,
    CLOSE_UNAUTHORIZED,
    CLOSE_UNSUPPORTED_VERSION,
    FEATURE_FLEET,
    HUB_FLEET_PATH,
    HUB_PAIR_PATH,
    PROTOCOL_VERSION,
    SUBPROTOCOL,
    AgentAdded,
    AgentEvent,
    AgentInfo,
    AgentManifestChanged,
    AgentRemoved,
    AgentState,
    AgentStatus,
    Bye,
    Event,
    FleetAgent,
    FleetHello,
    FleetMessage,
    Hello,
    HubPairResponse,
    ManifestChanged,
    Message,
    PairResponse,
    ProtocolError,
    ProtocolRange,
    Result,
    RunResultData,
    State,
    build_client_hello,
    build_get_agents,
    build_get_manifest,
    build_pair_request,
    build_run,
    build_subscribe,
    parse_fleet_frame,
    parse_frame,
    parse_get_agents_result_data,
    parse_hub_pair_response,
    parse_pair_response,
    parse_run_result_data,
)

_LOGGER = logging.getLogger(__name__)

PAIR_PATH: Final = "/link/v1/pair"
LINK_PATH: Final = "/link/v1"
MAX_FRAME_BYTES: Final = 1024 * 1024
HEARTBEAT_SECONDS: Final = 30.0
CLIENT_KIND: Final = "homeassistant"

DEFAULT_REQUEST_TIMEOUT: Final = 10.0
DEFAULT_CONNECT_TIMEOUT: Final = 10.0
BACKOFF_MIN: Final = 1.0
BACKOFF_MAX: Final = 60.0
FAST_RETRY_INTERVAL: Final = 1.0

_SHA256_HEX_LENGTH: Final = 64
_CLOSE_NORMAL: Final = 1000
_HTTP_OK: Final = 200
_HTTP_UNAUTHORIZED: Final = 401
_HTTP_FORBIDDEN: Final = 403
_HTTP_TOO_MANY_REQUESTS: Final = 429


class QoffeeLinkError(Exception):
    """Base class of all client errors."""


class CannotConnectError(QoffeeLinkError):
    """The agent is not reachable or the connection failed."""


class CertificateMismatchError(CannotConnectError):
    """The agent's certificate does not have the pinned fingerprint."""


class DirectLinkDeniedError(CannotConnectError):
    """The hub that manages the agent does not allow direct links (D-029, HTTP 403).

    The agent refuses pairing and connections of direct clients while the policy
    ``direct_link: deny`` of its hub is in effect. Home Assistant has to reach such a
    PC through the hub (PLAN.md S20).
    """


class AuthenticationError(QoffeeLinkError):
    """The link token was rejected (HTTP 401 or close code 4401)."""


class UnsupportedProtocolError(QoffeeLinkError):
    """Agent and client share no protocol version.

    ``agent_range`` is the ``(min, max)`` range the agent announced, when known.
    """

    def __init__(
        self, message: str, agent_range: tuple[int, int] | None = None
    ) -> None:
        """Create the error with the protocol range of the agent."""
        super().__init__(message)
        self.agent_range = agent_range


class PairingCodeInvalidError(QoffeeLinkError):
    """The pairing code was refused."""


class PairingCodeExpiredError(QoffeeLinkError):
    """The pairing code expired or was invalidated by too many failed attempts."""


class UnexpectedResponseError(QoffeeLinkError):
    """The agent answered something that does not conform to the protocol."""


class NotConnectedError(QoffeeLinkError):
    """A request needs a connection but there is none (or it was lost)."""


class RequestTimeoutError(QoffeeLinkError):
    """The agent did not answer a request in time."""


class RequestError(QoffeeLinkError):
    """The agent answered a request with an error result."""

    def __init__(self, code: str, message: str) -> None:
        """Remember the error code and message of the agent."""
        super().__init__(f"{code}: {message}")
        self.code = code
        self.message = message


def normalize_fingerprint(fingerprint: str) -> str:
    """Return a SHA-256 fingerprint as upper-case hex pairs separated by colons.

    Accepts any common notation (colons, spaces, either case). Raises
    :class:`ValueError` if the text is not a SHA-256 fingerprint.
    """
    hex_digits = "".join(c for c in fingerprint if c not in ": -").upper()
    if len(hex_digits) != _SHA256_HEX_LENGTH:
        msg = "a SHA-256 fingerprint has 64 hexadecimal digits"
        raise ValueError(msg)
    bytes.fromhex(hex_digits)  # raises ValueError for non-hex characters
    return ":".join(hex_digits[i : i + 2] for i in range(0, len(hex_digits), 2))


def _pin(fingerprint: str) -> aiohttp.Fingerprint:
    """Build the aiohttp pin for a fingerprint."""
    digest = normalize_fingerprint(fingerprint).replace(":", "")
    return aiohttp.Fingerprint(bytes.fromhex(digest))


def _format_digest(digest: bytes) -> str:
    return normalize_fingerprint(digest.hex())


async def async_fetch_fingerprint(
    host: str, port: int, *, connect_timeout: float = DEFAULT_CONNECT_TIMEOUT
) -> str:
    """Read the SHA-256 fingerprint of the certificate that an agent presents.

    The chain is deliberately not validated: the agent uses a self-signed
    certificate and the user compares the returned fingerprint with the one shown
    in the agent UI (trust on first use, PLAN.md S09 item 3).
    """
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE
    writer: asyncio.StreamWriter | None = None
    try:
        async with asyncio.timeout(connect_timeout):
            _, writer = await asyncio.open_connection(host, port, ssl=context)
        ssl_object = writer.get_extra_info("ssl_object")
        der = ssl_object.getpeercert(binary_form=True) if ssl_object else None
        if not der:
            msg = "the agent presented no certificate"
            raise CannotConnectError(msg)
        return _format_digest(hashlib.sha256(der).digest())
    except (TimeoutError, OSError) as err:
        msg = f"cannot reach {host}:{port}: {err}"
        raise CannotConnectError(msg) from err
    finally:
        if writer is not None:
            writer.close()
            with contextlib.suppress(OSError, ssl.SSLError):
                await writer.wait_closed()


async def async_pair(
    session: aiohttp.ClientSession,
    host: str,
    port: int,
    fingerprint: str,
    code: str,
    *,
    client_name: str,
    request_timeout: float = DEFAULT_REQUEST_TIMEOUT,
) -> PairResponse:
    """Exchange a one-time pairing code for a link token (``POST /link/v1/pair``).

    The request is only sent to a server whose certificate has the given
    fingerprint. Raises :class:`PairingCodeInvalidError`,
    :class:`PairingCodeExpiredError`, :class:`CertificateMismatchError`,
    :class:`CannotConnectError` or :class:`UnexpectedResponseError`.
    """
    url = f"https://{_host_for_url(host)}:{port}{PAIR_PATH}"
    body = build_pair_request(code, kind=CLIENT_KIND, name=client_name)
    try:
        async with (
            asyncio.timeout(request_timeout),
            session.post(url, json=body, ssl=_pin(fingerprint)) as response,
        ):
            status = response.status
            payload = await _read_json(response)
    except aiohttp.ServerFingerprintMismatch as err:
        msg = "the certificate of the agent changed"
        raise CertificateMismatchError(msg) from err
    except (aiohttp.ClientError, TimeoutError, OSError) as err:
        msg = f"cannot reach {host}:{port}: {err}"
        raise CannotConnectError(msg) from err

    if status == _HTTP_OK:
        try:
            return parse_pair_response(payload)
        except ProtocolError as err:
            msg = f"invalid pairing response: {err}"
            raise UnexpectedResponseError(msg) from err
    if status == _HTTP_UNAUTHORIZED:
        msg = "the pairing code was refused"
        raise PairingCodeInvalidError(msg)
    if status in (410, _HTTP_TOO_MANY_REQUESTS):
        msg = "the pairing code expired or was invalidated"
        raise PairingCodeExpiredError(msg)
    if status == _HTTP_FORBIDDEN:
        msg = "the hub that manages the agent does not allow direct pairing"
        raise DirectLinkDeniedError(msg)
    msg = f"unexpected HTTP status {status} from the pairing endpoint"
    raise UnexpectedResponseError(msg)


async def async_hub_pair(
    session: aiohttp.ClientSession,
    host: str,
    port: int,
    fingerprint: str,
    code: str,
    *,
    client_name: str,
    request_timeout: float = DEFAULT_REQUEST_TIMEOUT,
) -> HubPairResponse:
    """Exchange a pairing code of a hub for a fleet token (``POST /hub/v1/pair``).

    The request is only sent to a server whose certificate has the given
    fingerprint. Raises :class:`PairingCodeInvalidError` (wrong or expired code),
    :class:`PairingCodeExpiredError` (rate limited), :class:`CertificateMismatchError`,
    :class:`CannotConnectError` or :class:`UnexpectedResponseError`.
    """
    url = f"https://{_host_for_url(host)}:{port}{HUB_PAIR_PATH}"
    body = build_pair_request(code, kind=CLIENT_KIND, name=client_name)
    try:
        async with (
            asyncio.timeout(request_timeout),
            session.post(url, json=body, ssl=_pin(fingerprint)) as response,
        ):
            status = response.status
            payload = await _read_json(response)
    except aiohttp.ServerFingerprintMismatch as err:
        msg = "the certificate of the hub changed"
        raise CertificateMismatchError(msg) from err
    except (aiohttp.ClientError, TimeoutError, OSError) as err:
        msg = f"cannot reach {host}:{port}: {err}"
        raise CannotConnectError(msg) from err

    if status == _HTTP_OK:
        try:
            return parse_hub_pair_response(payload)
        except ProtocolError as err:
            msg = f"invalid pairing response: {err}"
            raise UnexpectedResponseError(msg) from err
    if status == _HTTP_UNAUTHORIZED:
        msg = "the pairing code was refused or has expired"
        raise PairingCodeInvalidError(msg)
    if status == _HTTP_TOO_MANY_REQUESTS:
        msg = "the hub blocked pairing after too many attempts"
        raise PairingCodeExpiredError(msg)
    msg = f"unexpected HTTP status {status} from the hub pairing endpoint"
    raise UnexpectedResponseError(msg)


async def _read_json(response: aiohttp.ClientResponse) -> Any:
    try:
        return await response.json(content_type=None)
    except ValueError:
        return None


def _host_for_url(host: str) -> str:
    return f"[{host}]" if ":" in host and not host.startswith("[") else host


type Listener[T] = Callable[[T], None]


type _Subscription = tuple[Sequence[str] | str, Mapping[str, float] | None]

# The WebSocket of a connection: one that this client opened (``QoffeeLinkClient``) or
# one that the agent opened and Home Assistant accepted (``ReverseLinkClient``). The
# two classes of aiohttp have the same methods for everything the client does.
type _Socket = aiohttp.ClientWebSocketResponse | web.WebSocketResponse


class _Listeners[T]:
    """A set of callbacks; one failing callback does not affect the others."""

    def __init__(self, name: str) -> None:
        self._name = name
        self._items: list[Listener[T]] = []

    def add(self, listener: Listener[T]) -> Callable[[], None]:
        self._items.append(listener)

        def remove() -> None:
            if listener in self._items:
                self._items.remove(listener)

        return remove

    def fire(self, value: T) -> None:
        for listener in tuple(self._items):
            try:
                listener(value)
            except Exception:
                _LOGGER.exception("Error in %s listener", self._name)


class LinkClient:
    """A QoffeeLink client of one agent, whoever opened the WebSocket.

    A subclass decides how connections come about (:class:`QoffeeLinkClient` dials,
    :class:`ReverseLinkClient` is handed accepted sockets) and calls :meth:`_serve`
    for every connection that completed the handshake. :class:`AgentLink` is the same
    interface for an agent that is reached through a hub (the fleet link).
    """

    # The agent id that the requests carry as member ``a`` on the fleet link; ``None``
    # on a link that belongs to one agent.
    _scope: str | None = None

    def __init__(
        self,
        *,
        client_name: str = "Home Assistant",
        client_version: str = "0",
        request_timeout: float = DEFAULT_REQUEST_TIMEOUT,
        features: Sequence[str] = (),
    ) -> None:
        """Create the client; nothing happens until :meth:`start`."""
        self._client_name = client_name
        self._client_version = client_version
        self._request_timeout = request_timeout
        self._features = tuple(features)

        self._ws: _Socket | None = None
        self._ready = False
        self._hello: Hello | None = None
        self._fast_until = 0.0
        self._auth_failed = False
        self._last_error: QoffeeLinkError | None = None
        self._last_bye: Bye | None = None
        self._subscription: _Subscription | None = None
        self._next_id = 0
        self._pending: dict[int, asyncio.Future[Result]] = {}

        self._state_listeners = _Listeners[State]("state")
        self._event_listeners = _Listeners[Event]("event")
        self._manifest_listeners = _Listeners[ManifestChanged]("manifest_changed")
        self._bye_listeners = _Listeners[Bye]("bye")
        self._connection_listeners = _Listeners[bool]("connection")
        self._auth_listeners = _Listeners[None]("auth_failed")
        self._denied_listeners = _Listeners[bool]("direct_link_denied")
        self._direct_link_denied = False

    # -- properties ----------------------------------------------------------

    @property
    def connected(self) -> bool:
        """True while the handshake is done and the connection is open."""
        return self._ready

    @property
    def _can_send(self) -> bool:
        """True when a request can go out right now (``subscribe`` sends at once)."""
        return self._ready

    @property
    def direct_link_denied(self) -> bool:
        """True while the agent refuses direct links because of its hub's policy."""
        return self._direct_link_denied

    @property
    def hello(self) -> Hello | None:
        """The ``hello`` of the current (or last) connection."""
        return self._hello

    @property
    def last_bye(self) -> Bye | None:
        """The last ``bye`` received; cleared on every new connection."""
        return self._last_bye

    @property
    def last_error(self) -> QoffeeLinkError | None:
        """Why the last connection attempt failed; ``None`` after a success."""
        return self._last_error

    @property
    def auth_failed(self) -> bool:
        """True after the agent rejected the token; retries stop until triggered."""
        return self._auth_failed

    # -- listeners -----------------------------------------------------------

    def add_state_listener(self, listener: Listener[State]) -> Callable[[], None]:
        """Call ``listener`` for every ``state`` frame; returns the remover."""
        return self._state_listeners.add(listener)

    def add_event_listener(self, listener: Listener[Event]) -> Callable[[], None]:
        """Call ``listener`` for every ``event`` frame; returns the remover."""
        return self._event_listeners.add(listener)

    def add_manifest_listener(
        self, listener: Listener[ManifestChanged]
    ) -> Callable[[], None]:
        """Call ``listener`` for every ``manifest_changed`` frame."""
        return self._manifest_listeners.add(listener)

    def add_bye_listener(self, listener: Listener[Bye]) -> Callable[[], None]:
        """Call ``listener`` for every ``bye`` frame."""
        return self._bye_listeners.add(listener)

    def add_connection_listener(self, listener: Listener[bool]) -> Callable[[], None]:
        """Call ``listener(True)`` on connect and ``listener(False)`` on disconnect."""
        return self._connection_listeners.add(listener)

    def add_auth_failed_listener(self, listener: Listener[None]) -> Callable[[], None]:
        """Call ``listener`` when the agent rejects the link token."""
        return self._auth_listeners.add(listener)

    def add_direct_link_denied_listener(
        self, listener: Listener[bool]
    ) -> Callable[[], None]:
        """Call ``listener(True)`` when the policy of the hub denies the link.

        ``listener(False)`` follows when a connection succeeds again, which means the
        policy was lifted.
        """
        return self._denied_listeners.add(listener)

    def _set_direct_link_denied(self, denied: bool) -> None:
        if denied != self._direct_link_denied:
            self._direct_link_denied = denied
            self._denied_listeners.fire(denied)

    # -- lifecycle -----------------------------------------------------------

    def start(self) -> None:
        """Start connecting and keep the connection up until :meth:`stop`.

        A client whose connections are opened by the agent has nothing to start.
        """

    async def stop(self) -> None:
        """Close the connection and fail the requests that wait for an answer."""
        ws = self._ws
        if ws is not None:
            with contextlib.suppress(aiohttp.ClientError, OSError):
                await ws.close()
            await asyncio.sleep(0)
        self._fail_pending(NotConnectedError("the client was stopped"))

    def trigger_connect(self) -> None:
        """Try to connect right now.

        Only a client that dials can do that. For a connection that the agent
        opens it does nothing: the agent decides when to dial.
        """

    @property
    def fast_retry_active(self) -> bool:
        """True while a wake-up is in progress: the PC was woken and not seen yet.

        A client that dials retries every second meanwhile; for a link that the
        agent opens the flag only tells that the PC was woken a moment ago, so
        that nothing wakes it twice.
        """
        return asyncio.get_running_loop().time() < self._fast_until

    def start_fast_retry(self, duration: float) -> None:
        """Retry once per interval (one second) for ``duration`` seconds.

        Fast retry ends when the client is connected or ``duration`` has passed,
        whichever comes first (after a Wake-on-LAN press, PLAN.md S10).
        """
        self._fast_until = asyncio.get_running_loop().time() + duration
        self.trigger_connect()

    # -- requests ------------------------------------------------------------

    async def get_manifest(self) -> Mapping[str, Any]:
        """Fetch the manifest (parse it with :func:`.protocol.parse_manifest`)."""
        return await self._request(
            lambda i: build_get_manifest(i, agent_id=self._scope)
        )

    async def subscribe(
        self,
        metrics: Sequence[str] | str,
        intervals: Mapping[str, float] | None = None,
    ) -> None:
        """Replace the subscription; it is sent again after every reconnect."""
        build_subscribe(0, metrics, intervals)  # validates the arguments
        self._subscription = (metrics, intervals)
        if self._can_send:
            await self._request(
                lambda i: build_subscribe(i, metrics, intervals, agent_id=self._scope)
            )

    async def run_command(
        self,
        command: str,
        variables: Mapping[str, str | int | float | bool] | None = None,
        *,
        wait: bool = True,
        user_id: str | None = None,
        request_timeout: float | None = None,
    ) -> RunResultData:
        """Run a command on the PC and return the data of the result."""
        data = await self._request(
            lambda i: build_run(
                i,
                command,
                variables,
                wait=wait,
                user_id=user_id,
                agent_id=self._scope,
            ),
            request_timeout=request_timeout,
        )
        try:
            return parse_run_result_data(data)
        except ProtocolError as err:
            msg = f"invalid run result: {err}"
            raise UnexpectedResponseError(msg) from err

    async def _request(
        self,
        build: Callable[[int], dict[str, Any]],
        *,
        request_timeout: float | None = None,
    ) -> Mapping[str, Any]:
        ws = self._ws
        if ws is None:
            msg = "not connected"
            raise NotConnectedError(msg)
        self._next_id += 1
        request_id = self._next_id
        future: asyncio.Future[Result] = asyncio.get_running_loop().create_future()
        self._pending[request_id] = future
        try:
            await ws.send_json(build(request_id))
            async with asyncio.timeout(
                self._request_timeout if request_timeout is None else request_timeout
            ):
                result = await future
        except TimeoutError as err:
            msg = "the agent did not answer in time"
            raise RequestTimeoutError(msg) from err
        except (aiohttp.ClientError, ConnectionError) as err:
            msg = f"connection lost: {err}"
            raise NotConnectedError(msg) from err
        finally:
            self._pending.pop(request_id, None)
        if not result.ok:
            error = result.error
            if error is None:
                msg = "an error result without error"
                raise UnexpectedResponseError(msg)
            raise RequestError(error.code, error.message)
        return result.data or {}

    def _fail_pending(self, error: QoffeeLinkError) -> None:
        for future in tuple(self._pending.values()):
            if not future.done():
                future.set_exception(error)
        self._pending.clear()

    # -- connection ----------------------------------------------------------

    async def _handshake(
        self, ws: _Socket, *, expected_agent_id: str | None = None
    ) -> Hello:
        """Read ``hello``, check it and answer with ``client_hello``.

        ``expected_agent_id`` is for a connection that the agent opened: a token
        belongs to one agent, so a ``hello`` of another one ends the connection
        with close code 4401 before anything is answered.
        """
        try:
            async with asyncio.timeout(self._request_timeout):
                message = await ws.receive()
        except TimeoutError as err:
            msg = "the agent sent no hello"
            raise CannotConnectError(msg) from err
        if message.type is not aiohttp.WSMsgType.TEXT:
            raise _closed_error(ws.close_code)
        try:
            hello = parse_frame(message.data)
        except ProtocolError as err:
            msg = f"invalid hello: {err}"
            raise UnexpectedResponseError(msg) from err
        if not isinstance(hello, Hello):
            msg = "the first frame was not a hello"
            raise UnexpectedResponseError(msg)

        if expected_agent_id is not None and hello.agent.id != expected_agent_id:
            await ws.close(code=CLOSE_UNAUTHORIZED, message=b"wrong agent")
            msg = "the agent is not the one of this entry"
            raise AuthenticationError(msg)

        chosen = min(hello.protocol.max, PROTOCOL_VERSION)
        if chosen < hello.protocol.min:
            await ws.close(code=aiohttp.WSCloseCode.PROTOCOL_ERROR)
            agent_range = f"{hello.protocol.min}-{hello.protocol.max}"
            msg = (
                f"the agent supports protocol {agent_range}, "
                f"this client supports 1-{PROTOCOL_VERSION}"
            )
            raise UnsupportedProtocolError(
                msg, (hello.protocol.min, hello.protocol.max)
            )
        await ws.send_json(
            build_client_hello(
                chosen,
                kind=CLIENT_KIND,
                name=self._client_name,
                version=self._client_version,
                features=self._features,
            )
        )
        return hello

    def _connection_established(self) -> None:
        """Reset what a successful connection makes obsolete (a subclass extends it)."""
        # Connected: the fast retry did its job. A later drop backs off as usual.
        self._fast_until = 0.0
        self._set_direct_link_denied(False)

    async def _serve(self, ws: _Socket, hello: Hello) -> None:
        """Run one established connection until it ends."""
        self._ws = ws
        self._hello = hello
        self._last_bye = None
        self._last_error = None
        self._auth_failed = False
        self._connection_established()
        reader = asyncio.create_task(self._read_loop(ws), name="qoffeeautomat-read")
        announced = False
        try:
            await self._resubscribe()
            announced = True
            self._ready = True
            self._connection_listeners.fire(True)
            await reader
        except QoffeeLinkError as err:
            self._last_error = err
            _LOGGER.debug("Connection dropped: %s", err)
        finally:
            # A newer connection may have taken the place of this one already; it
            # has told everybody and failed the requests, and must not be disturbed.
            current = self._ws is ws
            if current:
                self._ready = False
                self._ws = None
            reader.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await reader
            with contextlib.suppress(aiohttp.ClientError, OSError):
                await ws.close()
            # Let aiohttp cancel its heartbeat timer before the task ends.
            await asyncio.sleep(0)
            if current:
                self._fail_pending(NotConnectedError("connection lost"))
                if announced:
                    self._connection_listeners.fire(False)
        self._classify_close(ws.close_code)

    def _classify_close(self, code: int | None) -> None:
        if code == CLOSE_UNAUTHORIZED:
            self._on_auth_failed(AuthenticationError("the link token was revoked"))
        elif code == CLOSE_UNSUPPORTED_VERSION:
            self._last_error = UnsupportedProtocolError("unsupported protocol version")

    def _on_auth_failed(self, error: AuthenticationError) -> None:
        self._last_error = error
        if not self._auth_failed:
            self._auth_failed = True
            self._auth_listeners.fire(None)

    async def _resubscribe(self) -> None:
        if self._subscription is None:
            return
        metrics, intervals = self._subscription
        await self._request(
            lambda i: build_subscribe(i, metrics, intervals, agent_id=self._scope)
        )

    async def _read_loop(self, ws: _Socket) -> None:
        try:
            async for message in ws:
                if message.type is aiohttp.WSMsgType.ERROR or self._ws is not ws:
                    # A failed socket, or a newer connection took this one's place.
                    break
                if message.type is not aiohttp.WSMsgType.TEXT:
                    continue
                try:
                    frame = parse_frame(message.data)
                except ProtocolError as err:
                    _LOGGER.debug("Ignoring invalid frame: %s", err)
                    continue
                self._dispatch(frame)
        except (aiohttp.ClientError, OSError) as err:
            _LOGGER.debug("Reading from the agent failed: %s", err)

    def _dispatch(self, frame: Message) -> None:
        match frame:
            case Result():
                future = self._pending.get(frame.id)
                if future is not None and not future.done():
                    future.set_result(frame)
            case State():
                self._state_listeners.fire(frame)
            case Event():
                self._event_listeners.fire(frame)
            case ManifestChanged():
                self._manifest_listeners.fire(frame)
            case Bye():
                self._last_bye = frame
                self._bye_listeners.fire(frame)
            case _:
                pass  # hello after the handshake and unknown types are ignored


class QoffeeLinkClient(LinkClient):
    """A QoffeeLink connection to one agent that Home Assistant dials and keeps up."""

    def __init__(
        self,
        session: aiohttp.ClientSession,
        host: str,
        port: int,
        token: str,
        fingerprint: str,
        *,
        client_name: str = "Home Assistant",
        client_version: str = "0",
        request_timeout: float = DEFAULT_REQUEST_TIMEOUT,
        connect_timeout: float = DEFAULT_CONNECT_TIMEOUT,
        backoff_min: float = BACKOFF_MIN,
        backoff_max: float = BACKOFF_MAX,
        fast_retry_interval: float = FAST_RETRY_INTERVAL,
        features: Sequence[str] = (),
    ) -> None:
        """Create the client; nothing happens until :meth:`start`."""
        super().__init__(
            client_name=client_name,
            client_version=client_version,
            request_timeout=request_timeout,
            features=features,
        )
        self._session = session
        self._host = host
        self._port = port
        self._token = token
        self._pin = _pin(fingerprint)
        self._connect_timeout = connect_timeout
        self._backoff_min = backoff_min
        self._backoff_max = backoff_max
        self._fast_interval = fast_retry_interval
        self._random = random.SystemRandom()

        self._task: asyncio.Task[None] | None = None
        self._wake = asyncio.Event()
        self._closing = False
        self._attempt = 0

    # -- properties ----------------------------------------------------------

    @property
    def host(self) -> str:
        """The host that is connected to."""
        return self._host

    @property
    def port(self) -> int:
        """The link port."""
        return self._port

    # -- lifecycle -----------------------------------------------------------

    async def probe(self) -> Hello:
        """Connect once, read the ``hello`` and disconnect again.

        Raises the typed error of the failure. Used to test the first connection
        before a config entry is set up.
        """
        ws, hello = await self._open(heartbeat=None)
        await ws.close()
        await asyncio.sleep(0)
        return hello

    def start(self) -> None:
        """Start connecting and keep the connection up until :meth:`stop`."""
        if self._task is None:
            self._closing = False
            self._task = asyncio.create_task(
                self._supervise(), name="qoffeeautomat-link"
            )

    def _is_closing(self) -> bool:
        return self._closing

    async def stop(self) -> None:
        """Close the connection and stop reconnecting."""
        self._closing = True
        self._wake.set()
        task, self._task = self._task, None
        if task is not None:
            task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await task
        await super().stop()

    def trigger_connect(self) -> None:
        """Reset the backoff and try to connect right now."""
        if self._ws is not None:
            return
        self._attempt = 0
        self._wake.set()

    def update_address(self, host: str, port: int) -> None:
        """Use another address for the next connection attempt."""
        self._host = host
        self._port = port

    def update_token(self, token: str) -> None:
        """Use a new link token (after reauthentication) and retry right away."""
        self._token = token
        self._auth_failed = False
        self.trigger_connect()

    # -- connection ----------------------------------------------------------

    async def _open(
        self, *, heartbeat: float | None = HEARTBEAT_SECONDS
    ) -> tuple[aiohttp.ClientWebSocketResponse, Hello]:
        """Connect, authenticate and negotiate the protocol version."""
        url = f"wss://{_host_for_url(self._host)}:{self._port}{LINK_PATH}"
        try:
            async with asyncio.timeout(self._connect_timeout):
                ws = await self._session.ws_connect(
                    url,
                    protocols=(SUBPROTOCOL,),
                    headers={"Authorization": f"Bearer {self._token}"},
                    ssl=self._pin,
                    heartbeat=heartbeat,
                    max_msg_size=MAX_FRAME_BYTES,
                )
        except aiohttp.ServerFingerprintMismatch as err:
            msg = "the certificate of the agent changed"
            raise CertificateMismatchError(msg) from err
        except aiohttp.WSServerHandshakeError as err:
            if err.status == _HTTP_UNAUTHORIZED:
                msg = "the link token was rejected"
                raise AuthenticationError(msg) from err
            if err.status == _HTTP_FORBIDDEN:
                msg = "the hub that manages the agent does not allow direct links"
                raise DirectLinkDeniedError(msg) from err
            msg = f"the agent refused the connection (HTTP {err.status})"
            raise CannotConnectError(msg) from err
        except (aiohttp.ClientError, TimeoutError, OSError) as err:
            msg = f"cannot reach {self._host}:{self._port}: {err}"
            raise CannotConnectError(msg) from err

        done = False
        try:
            hello = await self._handshake(ws)
            done = True
        finally:
            if not done:
                await ws.close()
        return ws, hello

    async def _supervise(self) -> None:
        """Connect, serve, and reconnect with backoff until stopped."""
        while not self._closing:
            try:
                ws, hello = await self._open()
            except AuthenticationError as err:
                self._on_auth_failed(err)
                await self._sleep(None)
                continue
            except DirectLinkDeniedError as err:
                self._last_error = err
                _LOGGER.debug("The hub of the agent denies direct links")
                self._set_direct_link_denied(True)
                await self._sleep(self._next_delay())
                continue
            except QoffeeLinkError as err:
                self._last_error = err
                _LOGGER.debug("Connection attempt failed: %s", err)
                await self._sleep(self._next_delay())
                continue
            await self._serve(ws, hello)
            if self._is_closing():
                break
            await self._sleep(None if self._auth_failed else self._next_delay())

    def _connection_established(self) -> None:
        super()._connection_established()
        self._attempt = 0
        self._wake.clear()

    # -- backoff -------------------------------------------------------------

    def _next_delay(self) -> float:
        """Delay before the next attempt: fast retry, else exponential with jitter."""
        if asyncio.get_running_loop().time() < self._fast_until:
            return self._fast_interval
        base = min(self._backoff_max, self._backoff_min * 2**self._attempt)
        self._attempt = min(self._attempt + 1, 32)
        jitter: float = self._random.random()
        return float(base * (0.5 + 0.5 * jitter))

    async def _sleep(self, delay: float | None) -> None:
        """Wait for ``delay`` seconds (forever if ``None``) or until woken."""
        if self._closing:
            return
        with contextlib.suppress(TimeoutError):
            async with asyncio.timeout(delay):
                await self._wake.wait()
        self._wake.clear()


class ReverseLinkClient(LinkClient):
    """The link to an agent that dials Home Assistant (D-043, ADR 0003).

    Nothing here connects: the endpoint of the integration accepts the WebSocket that
    the agent opens and hands it to :meth:`async_serve`, which runs the connection
    until it ends. After the handshake everything is as on a link that Home Assistant
    dialled, so the entities cannot tell the difference. There is at most one
    connection; a newer one replaces the older (after a change of network the old
    TCP connection of a laptop can stay half open for minutes).
    """

    def __init__(
        self,
        agent_id: str,
        *,
        client_name: str = "Home Assistant",
        client_version: str = "0",
        request_timeout: float = DEFAULT_REQUEST_TIMEOUT,
        features: Sequence[str] = (),
    ) -> None:
        """Create the client of the agent with the id ``agent_id``."""
        super().__init__(
            client_name=client_name,
            client_version=client_version,
            request_timeout=request_timeout,
            features=features,
        )
        self._agent_id = agent_id
        self._closing = False
        self._closers: set[asyncio.Task[None]] = set()

    @property
    def agent_id(self) -> str:
        """The id of the agent that may use this link."""
        return self._agent_id

    @property
    def closing(self) -> bool:
        """True after :meth:`stop`: no connection is accepted any more."""
        return self._closing

    async def stop(self) -> None:
        """Close the connection and accept no more."""
        self._closing = True
        await super().stop()
        if self._closers:
            await asyncio.gather(*self._closers, return_exceptions=True)

    async def async_serve(self, ws: web.WebSocketResponse) -> None:
        """Run the connection that the agent opened until it ends.

        ``ws`` must be prepared already (the upgrade is done). Returns when the
        connection has ended; a connection that fails the handshake is closed.
        """
        if self._closing:
            await ws.close(code=aiohttp.WSCloseCode.GOING_AWAY)
            return
        try:
            hello = await self._handshake(ws, expected_agent_id=self._agent_id)
        except AuthenticationError as err:
            self._on_auth_failed(err)
            await self._close_quietly(ws)
            return
        except QoffeeLinkError as err:
            self._last_error = err
            _LOGGER.debug("The handshake of a reverse connection failed: %s", err)
            await self._close_quietly(ws)
            return
        if self._closing:
            await self._close_quietly(ws)  # type: ignore[unreachable]
            return
        # Only a connection that completed the handshake replaces the current one, so
        # a connection that sends garbage cannot push the real agent out.
        self._replace_current()
        await self._serve(ws, hello)

    def _replace_current(self) -> None:
        """End the current connection: a newer one takes its place."""
        ws = self._ws
        if ws is None:
            return
        self._ws = None
        was_ready, self._ready = self._ready, False
        self._fail_pending(NotConnectedError("replaced by a newer connection"))
        if was_ready:
            self._connection_listeners.fire(False)
        task = asyncio.create_task(
            self._close_quietly(ws), name="qoffeeautomat-reverse-replace"
        )
        self._closers.add(task)
        task.add_done_callback(self._closers.discard)

    @staticmethod
    async def _close_quietly(ws: _Socket) -> None:
        with contextlib.suppress(aiohttp.ClientError, OSError, ConnectionError):
            await ws.close(code=_CLOSE_NORMAL)
        # Let aiohttp cancel its heartbeat timer before the handler returns.
        await asyncio.sleep(0)


def _closed_error(code: int | None) -> QoffeeLinkError:
    if code == CLOSE_UNAUTHORIZED:
        return AuthenticationError("the link token was rejected")
    if code == CLOSE_UNSUPPORTED_VERSION:
        return UnsupportedProtocolError("unsupported protocol version")
    return CannotConnectError(f"the agent closed the connection (code {code})")


# ---------------------------------------------------------------------------
# The fleet link: Home Assistant through a hub (PLAN.md S20)
# ---------------------------------------------------------------------------

# Power states after which the next appearance of an agent counts as a start of the PC:
# the hub does not know the boot id of an agent, so the integration derives the start
# from the reason of the last ``bye``. A resume from sleep or hibernation is none.
_BOOT_POWER_STATES: Final = frozenset({"shutdown", "restart"})


class FleetClient:
    """The fleet link to a hub: one connection for all agents in the scope of a token.

    The hub sends ``hello`` with ``hub`` and the feature ``fleet``; agent-scoped frames
    carry the agent id in ``a``. :class:`AgentLink` objects register here and receive
    the frames of their agent, so everything above this class sees the same interface
    as on a direct link (one connection per agent). The client dials the hub, keeps the
    connection up with backoff and jitter and tells its listeners which agents appear
    and disappear.
    """

    def __init__(
        self,
        session: aiohttp.ClientSession,
        host: str,
        port: int,
        token: str,
        fingerprint: str,
        *,
        client_name: str = "Home Assistant",
        client_version: str = "0",
        request_timeout: float = DEFAULT_REQUEST_TIMEOUT,
        connect_timeout: float = DEFAULT_CONNECT_TIMEOUT,
        backoff_min: float = BACKOFF_MIN,
        backoff_max: float = BACKOFF_MAX,
    ) -> None:
        """Create the client; nothing happens until :meth:`start`."""
        self._session = session
        self._host = host
        self._port = port
        self._token = token
        self._pin = _pin(fingerprint)
        self._client_name = client_name
        self._client_version = client_version
        self._request_timeout = request_timeout
        self._connect_timeout = connect_timeout
        self._backoff_min = backoff_min
        self._backoff_max = backoff_max
        self._random = random.SystemRandom()

        self._ws: aiohttp.ClientWebSocketResponse | None = None
        self._ready = False
        self._hello: FleetHello | None = None
        self._auth_failed = False
        self._last_error: QoffeeLinkError | None = None
        self._next_id = 0
        self._pending: dict[int, asyncio.Future[Result]] = {}
        self._links: dict[str, AgentLink] = {}

        self._task: asyncio.Task[None] | None = None
        self._wake = asyncio.Event()
        self._closing = False
        self._attempt = 0

        self._connection_listeners = _Listeners[bool]("fleet connection")
        self._auth_listeners = _Listeners[None]("fleet auth_failed")
        self._added_listeners = _Listeners[AgentAdded]("agent_added")
        self._removed_listeners = _Listeners[AgentRemoved]("agent_removed")

    # -- properties ----------------------------------------------------------

    @property
    def host(self) -> str:
        """The host of the hub."""
        return self._host

    @property
    def port(self) -> int:
        """The port of the hub."""
        return self._port

    @property
    def connected(self) -> bool:
        """True while the handshake is done and the connection is open."""
        return self._ready

    @property
    def hello(self) -> FleetHello | None:
        """The ``hello`` of the hub on the current (or last) connection."""
        return self._hello

    @property
    def last_error(self) -> QoffeeLinkError | None:
        """Why the last connection attempt failed; ``None`` after a success."""
        return self._last_error

    @property
    def auth_failed(self) -> bool:
        """True after the hub rejected the token; retries stop until triggered."""
        return self._auth_failed

    # -- listeners -----------------------------------------------------------

    def add_connection_listener(self, listener: Listener[bool]) -> Callable[[], None]:
        """Call ``listener(True)`` on connect and ``listener(False)`` on disconnect."""
        return self._connection_listeners.add(listener)

    def add_auth_failed_listener(self, listener: Listener[None]) -> Callable[[], None]:
        """Call ``listener`` when the hub rejects the fleet token."""
        return self._auth_listeners.add(listener)

    def add_agent_added_listener(
        self, listener: Listener[AgentAdded]
    ) -> Callable[[], None]:
        """Call ``listener`` when an agent enters the scope of the token."""
        return self._added_listeners.add(listener)

    def add_agent_removed_listener(
        self, listener: Listener[AgentRemoved]
    ) -> Callable[[], None]:
        """Call ``listener`` when an agent leaves the scope or the hub."""
        return self._removed_listeners.add(listener)

    # -- agents --------------------------------------------------------------

    def register_link(self, link: AgentLink) -> None:
        """Route the agent-scoped frames of ``link.agent_id`` to ``link``."""
        self._links[link.agent_id] = link

    def unregister_link(self, link: AgentLink) -> None:
        """Stop routing frames to ``link`` (a newer link of the same agent stays)."""
        if self._links.get(link.agent_id) is link:
            del self._links[link.agent_id]

    async def get_agents(self) -> tuple[FleetAgent, ...]:
        """List the agents in the scope of the token."""
        data = await self.request(build_get_agents)
        try:
            return parse_get_agents_result_data(data)
        except ProtocolError as err:
            msg = f"invalid get_agents result: {err}"
            raise UnexpectedResponseError(msg) from err

    # -- lifecycle -----------------------------------------------------------

    async def probe(self) -> FleetHello:
        """Connect once, read the ``hello`` and disconnect again.

        Raises the typed error of the failure. Used to test a token before a config
        entry is set up or changed.
        """
        ws, hello = await self._open(heartbeat=None)
        await ws.close()
        await asyncio.sleep(0)
        return hello

    def start(self) -> None:
        """Start connecting and keep the connection up until :meth:`stop`."""
        if self._task is None:
            self._closing = False
            self._task = asyncio.create_task(
                self._supervise(), name="qoffeeautomat-fleet"
            )

    async def stop(self) -> None:
        """Close the connection, stop reconnecting and fail waiting requests."""
        self._closing = True
        self._wake.set()
        task, self._task = self._task, None
        if task is not None:
            task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await task
        ws = self._ws
        if ws is not None:
            with contextlib.suppress(aiohttp.ClientError, OSError):
                await ws.close()
            await asyncio.sleep(0)
        self._fail_pending(NotConnectedError("the client was stopped"))

    def trigger_connect(self) -> None:
        """Reset the backoff and try to connect right now."""
        if self._ws is not None:
            return
        self._attempt = 0
        self._wake.set()

    def update_address(self, host: str, port: int) -> None:
        """Use another address for the next connection attempt."""
        self._host = host
        self._port = port

    def update_token(self, token: str) -> None:
        """Use a new fleet token (after reauthentication) and retry right away."""
        self._token = token
        self._auth_failed = False
        self.trigger_connect()

    # -- requests ------------------------------------------------------------

    async def request(
        self,
        build: Callable[[int], dict[str, Any]],
        *,
        request_timeout: float | None = None,
    ) -> Mapping[str, Any]:
        """Send the request that ``build(request_id)`` creates; return its data.

        Raises :class:`NotConnectedError`, :class:`RequestTimeoutError` or
        :class:`RequestError` (also for the codes the hub adds, ``unknown_agent``,
        ``agent_offline`` and ``forbidden``).
        """
        ws = self._ws
        if ws is None:
            msg = "not connected"
            raise NotConnectedError(msg)
        self._next_id += 1
        request_id = self._next_id
        future: asyncio.Future[Result] = asyncio.get_running_loop().create_future()
        self._pending[request_id] = future
        try:
            await ws.send_json(build(request_id))
            async with asyncio.timeout(
                self._request_timeout if request_timeout is None else request_timeout
            ):
                result = await future
        except TimeoutError as err:
            msg = "the hub did not answer in time"
            raise RequestTimeoutError(msg) from err
        except (aiohttp.ClientError, ConnectionError) as err:
            msg = f"connection lost: {err}"
            raise NotConnectedError(msg) from err
        finally:
            self._pending.pop(request_id, None)
        if not result.ok:
            error = result.error
            if error is None:
                msg = "an error result without error"
                raise UnexpectedResponseError(msg)
            raise RequestError(error.code, error.message)
        return result.data or {}

    def _fail_pending(self, error: QoffeeLinkError) -> None:
        for future in tuple(self._pending.values()):
            if not future.done():
                future.set_exception(error)
        self._pending.clear()

    # -- connection ----------------------------------------------------------

    async def _open(
        self, *, heartbeat: float | None = HEARTBEAT_SECONDS
    ) -> tuple[aiohttp.ClientWebSocketResponse, FleetHello]:
        """Connect, authenticate and negotiate the protocol version."""
        url = f"wss://{_host_for_url(self._host)}:{self._port}{HUB_FLEET_PATH}"
        try:
            async with asyncio.timeout(self._connect_timeout):
                ws = await self._session.ws_connect(
                    url,
                    protocols=(SUBPROTOCOL,),
                    headers={"Authorization": f"Bearer {self._token}"},
                    ssl=self._pin,
                    heartbeat=heartbeat,
                    max_msg_size=MAX_FRAME_BYTES,
                )
        except aiohttp.ServerFingerprintMismatch as err:
            msg = "the certificate of the hub changed"
            raise CertificateMismatchError(msg) from err
        except aiohttp.WSServerHandshakeError as err:
            if err.status == _HTTP_UNAUTHORIZED:
                msg = "the fleet token was rejected"
                raise AuthenticationError(msg) from err
            msg = f"the hub refused the connection (HTTP {err.status})"
            raise CannotConnectError(msg) from err
        except (aiohttp.ClientError, TimeoutError, OSError) as err:
            msg = f"cannot reach {self._host}:{self._port}: {err}"
            raise CannotConnectError(msg) from err

        done = False
        try:
            hello = await self._handshake(ws)
            done = True
        finally:
            if not done:
                await ws.close()
        return ws, hello

    async def _handshake(self, ws: aiohttp.ClientWebSocketResponse) -> FleetHello:
        """Read the hello of the hub, check it and answer with ``client_hello``."""
        try:
            async with asyncio.timeout(self._request_timeout):
                message = await ws.receive()
        except TimeoutError as err:
            msg = "the hub sent no hello"
            raise CannotConnectError(msg) from err
        if message.type is not aiohttp.WSMsgType.TEXT:
            raise _closed_error(ws.close_code)
        try:
            hello = parse_fleet_frame(message.data)
        except ProtocolError as err:
            msg = f"invalid hello: {err}"
            raise UnexpectedResponseError(msg) from err
        if not isinstance(hello, FleetHello):
            msg = "the first frame was not the hello of a hub"
            raise UnexpectedResponseError(msg)
        if FEATURE_FLEET not in hello.features:
            msg = "the hub does not offer the fleet link"
            raise UnexpectedResponseError(msg)
        chosen = min(hello.protocol.max, PROTOCOL_VERSION)
        if chosen < hello.protocol.min:
            await ws.close(code=aiohttp.WSCloseCode.PROTOCOL_ERROR)
            hub_range = f"{hello.protocol.min}-{hello.protocol.max}"
            msg = (
                f"the hub supports protocol {hub_range}, "
                f"this client supports 1-{PROTOCOL_VERSION}"
            )
            raise UnsupportedProtocolError(
                msg, (hello.protocol.min, hello.protocol.max)
            )
        await ws.send_json(
            build_client_hello(
                chosen,
                kind=CLIENT_KIND,
                name=self._client_name,
                version=self._client_version,
                features=(FEATURE_FLEET,),
            )
        )
        return hello

    async def _supervise(self) -> None:
        """Connect, serve, and reconnect with backoff until stopped."""
        while not self._closing:
            try:
                ws, hello = await self._open()
            except AuthenticationError as err:
                self._on_auth_failed(err)
                await self._sleep(None)
                continue
            except QoffeeLinkError as err:
                self._last_error = err
                _LOGGER.debug("Connection attempt to the hub failed: %s", err)
                await self._sleep(self._next_delay())
                continue
            await self._serve(ws, hello)
            await self._sleep(None if self._auth_failed else self._next_delay())

    async def _serve(
        self, ws: aiohttp.ClientWebSocketResponse, hello: FleetHello
    ) -> None:
        """Run one established connection until it ends."""
        self._ws = ws
        self._hello = hello
        self._last_error = None
        self._auth_failed = False
        self._attempt = 0
        self._wake.clear()
        reader = asyncio.create_task(
            self._read_loop(ws), name="qoffeeautomat-fleet-read"
        )
        try:
            self._ready = True
            self._connection_listeners.fire(True)
            await reader
        finally:
            self._ready = False
            self._ws = None
            reader.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await reader
            with contextlib.suppress(aiohttp.ClientError, OSError):
                await ws.close()
            # Let aiohttp cancel its heartbeat timer before the task ends.
            await asyncio.sleep(0)
            self._fail_pending(NotConnectedError("connection lost"))
            for link in tuple(self._links.values()):
                link.handle_fleet_down()
            self._connection_listeners.fire(False)
        code = ws.close_code
        if code == CLOSE_UNAUTHORIZED:
            self._on_auth_failed(AuthenticationError("the fleet token was revoked"))
        elif code == CLOSE_UNSUPPORTED_VERSION:
            self._last_error = UnsupportedProtocolError("unsupported protocol version")

    def _on_auth_failed(self, error: AuthenticationError) -> None:
        self._last_error = error
        if not self._auth_failed:
            self._auth_failed = True
            self._auth_listeners.fire(None)

    async def _read_loop(self, ws: aiohttp.ClientWebSocketResponse) -> None:
        try:
            async for message in ws:
                if message.type is aiohttp.WSMsgType.ERROR or self._ws is not ws:
                    break
                if message.type is not aiohttp.WSMsgType.TEXT:
                    continue
                try:
                    frame = parse_fleet_frame(message.data)
                except ProtocolError as err:
                    _LOGGER.debug("Ignoring invalid frame of the hub: %s", err)
                    continue
                try:
                    self._dispatch(frame)
                except Exception:
                    _LOGGER.exception("Error while handling a frame of the hub")
        except (aiohttp.ClientError, OSError) as err:
            _LOGGER.debug("Reading from the hub failed: %s", err)

    def _dispatch(self, frame: FleetMessage) -> None:
        match frame:
            case Result():
                future = self._pending.get(frame.id)
                if future is not None and not future.done():
                    future.set_result(frame)
            case AgentAdded():
                self._added_listeners.fire(frame)
            case AgentRemoved():
                self._removed_listeners.fire(frame)
            case AgentStatus() | AgentManifestChanged() | AgentState() | AgentEvent():
                link = self._links.get(frame.agent_id)
                if link is not None:
                    link.handle_frame(frame)
            case _:
                pass  # a second hello and unknown types are ignored

    # -- backoff -------------------------------------------------------------

    def _next_delay(self) -> float:
        """Delay before the next attempt: exponential with jitter."""
        base = min(self._backoff_max, self._backoff_min * 2**self._attempt)
        self._attempt = min(self._attempt + 1, 32)
        jitter: float = self._random.random()
        return float(base * (0.5 + 0.5 * jitter))

    async def _sleep(self, delay: float | None) -> None:
        """Wait for ``delay`` seconds (forever if ``None``) or until woken."""
        if self._closing:
            return
        with contextlib.suppress(TimeoutError):
            async with asyncio.timeout(delay):
                await self._wake.wait()
        self._wake.clear()


class AgentLink(LinkClient):
    """One agent behind a hub, with the interface of a direct link.

    Requests carry the agent id (``a``) and go over the shared :class:`FleetClient`;
    the frames of the agent arrive through :meth:`handle_frame`. ``connected`` is true
    while the fleet link is up *and* the hub reports the agent online, so everything
    that follows availability behaves as on a direct link. The hub serves manifests
    from its cache, so :meth:`get_manifest` works while the PC is off.

    The hub knows no boot id. The link derives one: the PC counts as started when it
    comes online after the reasons ``shutdown`` or ``restart`` of its last ``bye``.
    """

    def __init__(
        self,
        fleet: FleetClient,
        agent: FleetAgent,
        *,
        client_name: str = "Home Assistant",
        client_version: str = "0",
        request_timeout: float = DEFAULT_REQUEST_TIMEOUT,
    ) -> None:
        """Create the link and register it with ``fleet``."""
        super().__init__(
            client_name=client_name,
            client_version=client_version,
            request_timeout=request_timeout,
        )
        self._fleet = fleet
        self._scope = agent.id
        self._info = agent
        self._manifest_rev = agent.manifest_rev
        self._boots = 0
        self._offline_power = "unknown"
        fleet.register_link(self)

    @property
    def agent_id(self) -> str:
        """The id of the agent."""
        return self._info.id

    @property
    def agent(self) -> FleetAgent:
        """What the hub last said about the agent."""
        return self._info

    @property
    def _can_send(self) -> bool:
        return self._fleet.connected

    async def stop(self) -> None:
        """Detach from the fleet link; the connection stays up for the others."""
        self._fleet.unregister_link(self)
        self._ready = False
        await super().stop()

    async def async_resubscribe(self) -> None:
        """Send the subscription again (a new connection to the hub forgot it)."""
        if self._subscription is None:
            return
        try:
            await self._resubscribe()
        except QoffeeLinkError as err:
            _LOGGER.debug("Could not send the subscription of %s: %s", self._scope, err)

    async def _request(
        self,
        build: Callable[[int], dict[str, Any]],
        *,
        request_timeout: float | None = None,
    ) -> Mapping[str, Any]:
        return await self._fleet.request(
            build,
            request_timeout=(
                self._request_timeout if request_timeout is None else request_timeout
            ),
        )

    # -- what the hub tells --------------------------------------------------

    def apply(self, agent: FleetAgent) -> None:
        """Take an entry of ``get_agents`` or an ``agent_added`` frame."""
        self._info = agent
        if agent.manifest_rev is not None:
            self._manifest_rev = agent.manifest_rev
        self._apply_presence(agent.online, agent.power_state)

    def handle_frame(
        self, frame: AgentStatus | AgentManifestChanged | AgentState | AgentEvent
    ) -> None:
        """Take an agent-scoped frame of the fleet link."""
        match frame:
            case AgentStatus():
                self._apply_presence(frame.online, frame.power_state)
            case AgentManifestChanged():
                self._manifest_rev = frame.rev
                self._manifest_listeners.fire(ManifestChanged(rev=frame.rev))
            case AgentState():
                self._state_listeners.fire(State(ts=frame.ts, values=frame.values))
            case AgentEvent():
                self._event_listeners.fire(
                    Event(type=frame.type, ts=frame.ts, data=frame.data)
                )

    def handle_fleet_down(self) -> None:
        """The connection to the hub is gone: nothing is known about the agent."""
        self._offline_power = "unknown"
        if self._ready:
            self._ready = False
            self._connection_listeners.fire(False)

    def _apply_presence(self, online: bool, power_state: str) -> None:
        if online:
            if self._ready:
                return
            if self._offline_power in _BOOT_POWER_STATES:
                self._boots += 1
            self._offline_power = "unknown"
            self._last_bye = None
            self._hello = self._build_hello()
            self._ready = True
            self._connection_listeners.fire(True)
            return
        self._offline_power = power_state
        if power_state in BYE_REASONS and (
            self._last_bye is None or self._last_bye.reason != power_state
        ):
            # What the agent would have said before it went: the power state follows.
            self._last_bye = Bye(reason=power_state)
            self._bye_listeners.fire(self._last_bye)
        if self._ready:
            self._ready = False
            self._connection_listeners.fire(False)

    def _build_hello(self) -> Hello:
        """Compose the ``hello`` of the agent from what the hub knows."""
        info = self._info.agent or AgentInfo(
            id=self._info.id,
            name=self._info.name,
            version="",
            os="",
            arch="",
            hostname="",
            macs=(),
        )
        fleet_hello = self._fleet.hello
        protocol = (
            fleet_hello.protocol
            if fleet_hello is not None
            else ProtocolRange(min=1, max=PROTOCOL_VERSION)
        )
        return Hello(
            protocol=protocol,
            agent=info,
            manifest_rev=self._manifest_rev or "",
            boot_id=f"hub-{self._boots}",
        )
