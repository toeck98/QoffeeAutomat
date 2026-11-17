"""QoffeeLink protocol client (PLAN.md S07, S09, S10).

This module must not import Home Assistant: it only depends on ``aiohttp`` and
:mod:`.protocol`, so it can become a PyPI library later.
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

from .protocol import (
    CLOSE_UNAUTHORIZED,
    CLOSE_UNSUPPORTED_VERSION,
    PROTOCOL_VERSION,
    SUBPROTOCOL,
    Bye,
    Event,
    Hello,
    ManifestChanged,
    Message,
    PairResponse,
    ProtocolError,
    Result,
    RunResultData,
    State,
    build_client_hello,
    build_get_manifest,
    build_pair_request,
    build_run,
    build_subscribe,
    parse_frame,
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


class QoffeeLinkError(Exception):
    """Base class of all client errors."""


class CannotConnectError(QoffeeLinkError):
    """The agent is not reachable or the connection failed."""


class CertificateMismatchError(CannotConnectError):
    """The agent's certificate does not have the pinned fingerprint."""


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

    if status == 200:
        try:
            return parse_pair_response(payload)
        except ProtocolError as err:
            msg = f"invalid pairing response: {err}"
            raise UnexpectedResponseError(msg) from err
    if status == 401:
        msg = "the pairing code was refused"
        raise PairingCodeInvalidError(msg)
    if status in (410, 429):
        msg = "the pairing code expired or was invalidated"
        raise PairingCodeExpiredError(msg)
    msg = f"unexpected HTTP status {status} from the pairing endpoint"
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


class QoffeeLinkClient:
    """A QoffeeLink connection to one agent that stays up by itself."""

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
        self._fast_interval = fast_retry_interval
        self._features = tuple(features)
        self._random = random.SystemRandom()

        self._ws: aiohttp.ClientWebSocketResponse | None = None
        self._ready = False
        self._hello: Hello | None = None
        self._task: asyncio.Task[None] | None = None
        self._wake = asyncio.Event()
        self._closing = False
        self._attempt = 0
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

    # -- properties ----------------------------------------------------------

    @property
    def host(self) -> str:
        """The host that is connected to."""
        return self._host

    @property
    def port(self) -> int:
        """The link port."""
        return self._port

    @property
    def connected(self) -> bool:
        """True while the handshake is done and the connection is open."""
        return self._ready

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
        if self._ws is not None:
            with contextlib.suppress(aiohttp.ClientError, OSError):
                await self._ws.close()
            await asyncio.sleep(0)
        self._fail_pending(NotConnectedError("the client was stopped"))

    def trigger_connect(self) -> None:
        """Reset the backoff and try to connect right now."""
        if self._ws is not None:
            return
        self._attempt = 0
        self._wake.set()

    def start_fast_retry(self, duration: float) -> None:
        """Retry once per interval (one second) for ``duration`` seconds."""
        self._fast_until = asyncio.get_running_loop().time() + duration
        self.trigger_connect()

    def update_address(self, host: str, port: int) -> None:
        """Use another address for the next connection attempt."""
        self._host = host
        self._port = port

    def update_token(self, token: str) -> None:
        """Use a new link token (after reauthentication) and retry right away."""
        self._token = token
        self._auth_failed = False
        self.trigger_connect()

    # -- requests ------------------------------------------------------------

    async def get_manifest(self) -> Mapping[str, Any]:
        """Fetch the manifest (parse it with :func:`.protocol.parse_manifest`)."""
        return await self._request(build_get_manifest)

    async def subscribe(
        self,
        metrics: Sequence[str] | str,
        intervals: Mapping[str, float] | None = None,
    ) -> None:
        """Replace the subscription; it is sent again after every reconnect."""
        build_subscribe(0, metrics, intervals)  # validates the arguments
        self._subscription = (metrics, intervals)
        if self._ready:
            await self._request(lambda i: build_subscribe(i, metrics, intervals))

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
            lambda i: build_run(i, command, variables, wait=wait, user_id=user_id),
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
            if err.status == 401:
                msg = "the link token was rejected"
                raise AuthenticationError(msg) from err
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

    async def _handshake(self, ws: aiohttp.ClientWebSocketResponse) -> Hello:
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
                _LOGGER.debug("Connection attempt failed: %s", err)
                await self._sleep(self._next_delay())
                continue
            await self._serve(ws, hello)
            if self._is_closing():
                break
            await self._sleep(None if self._auth_failed else self._next_delay())

    async def _serve(self, ws: aiohttp.ClientWebSocketResponse, hello: Hello) -> None:
        """Run one established connection until it ends."""
        self._ws = ws
        self._hello = hello
        self._last_bye = None
        self._last_error = None
        self._auth_failed = False
        self._attempt = 0
        self._wake.clear()
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
        await self._request(lambda i: build_subscribe(i, metrics, intervals))

    async def _read_loop(self, ws: aiohttp.ClientWebSocketResponse) -> None:
        try:
            async for message in ws:
                if message.type is aiohttp.WSMsgType.ERROR:
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


def _closed_error(code: int | None) -> QoffeeLinkError:
    if code == CLOSE_UNAUTHORIZED:
        return AuthenticationError("the link token was rejected")
    if code == CLOSE_UNSUPPORTED_VERSION:
        return UnsupportedProtocolError("unsupported protocol version")
    return CannotConnectError(f"the agent closed the connection (code {code})")
