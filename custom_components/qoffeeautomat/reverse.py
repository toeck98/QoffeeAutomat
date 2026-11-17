"""The reverse link: the endpoint that roaming agents dial (D-043, ADR 0003, T710).

A laptop behind another network's NAT cannot be dialled by Home Assistant, so the
agent dials Home Assistant instead: it opens a WebSocket to
``/api/qoffeeautomat/reverse/v1`` with a *reverse token* as bearer credential. After
the upgrade the connection is ordinary QoffeeLink v1 with the agent sending ``hello``
first, so everything else (entities, actions, events) works unchanged.

* Home Assistant creates the token (:func:`create_token`) and keeps only its SHA-256
  hash; the user hands the agent an *enrollment string* (:func:`build_enrollment`).
* The view has no Home Assistant authentication (the agent has no account). It checks
  the token itself and answers HTTP 401 without upgrading when it is unknown; the
  ``HTTPUnauthorized`` is counted by the IP ban of Home Assistant like a failed login.
* A token that belongs to a config entry hands the socket to the ``ReverseLinkClient``
  of the entry. A token that is waiting for its first agent (the config flow shows the
  enrollment string) reads ``hello``, tells the flow which agent it is and closes.
"""

import asyncio
import base64
import binascii
import contextlib
import hashlib
import hmac
import ipaddress
import json
import logging
import secrets
from collections.abc import Callable, Iterable
from dataclasses import dataclass
from typing import Any, Final
from urllib.parse import urlsplit, urlunsplit

import aiohttp
from aiohttp import web
from homeassistant.core import HomeAssistant
from homeassistant.helpers.http import HomeAssistantView
from homeassistant.util.hass_dict import HassKey

from .client import (
    HEARTBEAT_SECONDS,
    MAX_FRAME_BYTES,
    ReverseLinkClient,
    normalize_fingerprint,
)
from .const import (
    DOMAIN,
    REVERSE_ENROLLMENT_PREFIX,
    REVERSE_HELLO_TIMEOUT_SECONDS,
    REVERSE_LINK_PATH,
    REVERSE_TOKEN_PREFIX,
)
from .protocol import SUBPROTOCOL, Hello, ProtocolError, parse_frame

_LOGGER = logging.getLogger(__name__)

_TOKEN_BYTES: Final = 32
_LOOPBACK_HOSTS: Final = frozenset({"localhost"})
_CLOSE_NORMAL: Final = 1000
# How long a freshly enrolled token counts as known before its entry exists.
GRACE_SECONDS: Final = 60.0


def create_token() -> str:
    """Return a new reverse token: ``qalr_`` and 32 random bytes in base64url."""
    encoded = base64.urlsafe_b64encode(secrets.token_bytes(_TOKEN_BYTES))
    return REVERSE_TOKEN_PREFIX + encoded.rstrip(b"=").decode("ascii")


def hash_token(token: str) -> str:
    """Return the SHA-256 of ``token`` in lower-case hex; this is all that is kept."""
    return hashlib.sha256(token.encode("utf-8")).hexdigest()


def _is_loopback(host: str) -> bool:
    if host.lower() in _LOOPBACK_HOSTS:
        return True
    try:
        return ipaddress.ip_address(host.strip("[]")).is_loopback
    except ValueError:
        return False


def websocket_url(base_url: str) -> str:
    """Return the WebSocket URL of the endpoint for the address of Home Assistant.

    ``https://ha.example.org`` becomes ``wss://ha.example.org/api/...``. A plain
    ``http://`` address is refused, because the token would travel unencrypted and the
    agent refuses ``ws://`` anyway; only a loopback host is allowed to stay plain
    (development). Raises :class:`ValueError` with the reason.
    """
    parsed = urlsplit(base_url.strip())
    host = parsed.hostname or ""
    if not host or parsed.scheme not in ("http", "https", "ws", "wss"):
        msg = "the address must start with https:// and name a host"
        raise ValueError(msg)
    if parsed.username or parsed.password:
        msg = "the address must not contain a user name or password"
        raise ValueError(msg)
    secure = parsed.scheme in ("https", "wss")
    if not secure and not _is_loopback(host):
        msg = "the address must use https: the token would be sent unencrypted"
        raise ValueError(msg)
    path = parsed.path.rstrip("/")
    if not path.endswith(REVERSE_LINK_PATH):
        path += REVERSE_LINK_PATH
    return urlunsplit(("wss" if secure else "ws", parsed.netloc, path, "", ""))


def build_enrollment(url: str, token: str, fingerprint: str | None = None) -> str:
    """Return the enrollment string ``qalr1.<base64url JSON {url, token, fp}>``.

    The string contains the token: it is shown once and handled like a password.
    """
    payload: dict[str, str] = {"url": url, "token": token}
    if fingerprint:
        payload["fp"] = normalize_fingerprint(fingerprint)
    raw = json.dumps(payload, separators=(",", ":")).encode("utf-8")
    encoded = base64.urlsafe_b64encode(raw).rstrip(b"=").decode("ascii")
    return REVERSE_ENROLLMENT_PREFIX + encoded


def parse_enrollment(text: str) -> dict[str, str]:
    """Return ``url``, ``token`` and optionally ``fp`` of an enrollment string.

    The agent reads the string itself; this is for tests and tools. Raises
    :class:`ValueError` if it is not valid.
    """
    trimmed = text.strip()
    if not trimmed.startswith(REVERSE_ENROLLMENT_PREFIX):
        msg = f"an enrollment string starts with {REVERSE_ENROLLMENT_PREFIX}"
        raise ValueError(msg)
    body = trimmed[len(REVERSE_ENROLLMENT_PREFIX) :]
    try:
        raw = base64.urlsafe_b64decode(body + "=" * (-len(body) % 4))
        payload = json.loads(raw)
    except (binascii.Error, ValueError) as err:
        msg = "the enrollment string is damaged"
        raise ValueError(msg) from err
    if (
        not isinstance(payload, dict)
        or not isinstance(payload.get("url"), str)
        or not isinstance(payload.get("token"), str)
    ):
        msg = "the enrollment string needs a url and a token"
        raise ValueError(msg)
    return {key: value for key, value in payload.items() if isinstance(value, str)}


def bearer_token(request: web.Request) -> str | None:
    """Return the token of an ``Authorization: Bearer`` header, if there is one."""
    header = request.headers.get("Authorization", "")
    scheme, _, token = header.partition(" ")
    token = token.strip()
    if scheme.lower() != "bearer" or not token:
        return None
    return token


@dataclass(frozen=True, slots=True)
class Lookup:
    """What the registry knows about a presented token."""

    # The client of the loaded entry that owns the token.
    client: ReverseLinkClient | None = None
    # The enrollment (config flow) that waits for the first agent with this token.
    pending: asyncio.Future[Hello] | None = None
    # True if the token is valid at all. Its entry may just not be loaded right now
    # (Home Assistant is starting or reloading it), which is no reason to reject it.
    known: bool = False


def _match(digest: str, candidates: Iterable[str]) -> str | None:
    """Return the candidate equal to ``digest`` using constant-time compare."""
    found: str | None = None
    for candidate in candidates:
        if hmac.compare_digest(candidate, digest):
            found = candidate
    return found


class ReverseLinks:
    """Agents that may dial in: reverse entries and enrollments in progress."""

    def __init__(self) -> None:
        """Create an empty registry."""
        # Token hash of every reverse entry, loaded or not, by entry id.
        self._hashes: dict[str, str] = {}
        self._clients: dict[str, ReverseLinkClient] = {}
        self._pending: dict[str, asyncio.Future[Hello]] = {}
        # Hashes of tokens enrolled a moment ago; their entry is being created.
        self._grace: dict[str, float] = {}
        self.view_registered = False

    @property
    def has_entries(self) -> bool:
        """True if at least one reverse entry exists."""
        return bool(self._hashes)

    def know(self, entry_id: str, token_hash: str) -> None:
        """Remember the token of a reverse entry, replacing an older one of the entry.

        A token that an entry no longer has (it was rotated) is unknown again, so the
        agent that still uses it is rejected for good.
        """
        self._hashes[entry_id] = token_hash

    def forget(self, entry_id: str) -> None:
        """Forget the token of a removed entry."""
        self._hashes.pop(entry_id, None)
        self._clients.pop(entry_id, None)

    def attach(self, entry_id: str, client: ReverseLinkClient) -> Callable[[], None]:
        """Let the agent of a loaded entry use ``client``; returns the remover."""
        self._clients[entry_id] = client

        def remove() -> None:
            if self._clients.get(entry_id) is client:
                del self._clients[entry_id]

        return remove

    def add_pending(self, token_hash: str) -> asyncio.Future[Hello]:
        """Wait for the first agent that uses a new token (the config flow).

        The future gets the ``hello`` of that agent.
        """
        future: asyncio.Future[Hello] = asyncio.get_running_loop().create_future()
        self._pending[token_hash] = future
        return future

    def remove_pending(self, token_hash: str) -> None:
        """Stop waiting for the token."""
        future = self._pending.pop(token_hash, None)
        if future is not None and not future.done():
            future.cancel()

    def enrolled(self, token_hash: str) -> None:
        """Note that an agent used a new token: the entry for it is about to exist.

        The agent dials again within a second. Until the entry is set up its token
        counts as known, so the agent is asked to retry instead of being rejected.
        """
        self._grace[token_hash] = asyncio.get_running_loop().time() + GRACE_SECONDS

    def lookup(self, token: str) -> Lookup:
        """Return what is known about ``token`` (all comparisons in constant time)."""
        digest = hash_token(token)
        now = asyncio.get_running_loop().time()
        self._grace = {key: end for key, end in self._grace.items() if end > now}
        by_hash = {value: key for key, value in self._hashes.items()}
        if (stored := _match(digest, by_hash)) is not None:
            return Lookup(client=self._clients.get(by_hash[stored]), known=True)
        if (stored := _match(digest, self._pending)) is not None:
            future = self._pending[stored]
            if not future.done():
                return Lookup(pending=future, known=True)
        return Lookup(known=_match(digest, self._grace) is not None)


REVERSE_LINKS: HassKey[ReverseLinks] = HassKey(f"{DOMAIN}_reverse_links")


def async_get_reverse_links(hass: HomeAssistant) -> ReverseLinks:
    """Return the registry of the reverse links of this Home Assistant."""
    links = hass.data.get(REVERSE_LINKS)
    if links is None:
        links = hass.data[REVERSE_LINKS] = ReverseLinks()
    return links


def async_ensure_view(hass: HomeAssistant) -> bool:
    """Register the endpoint once; returns False if Home Assistant has no web server.

    Nothing is exposed until the first reverse entry (or enrollment) needs it.
    """
    links = async_get_reverse_links(hass)
    if links.view_registered:
        return True
    http: Any = getattr(hass, "http", None)
    if http is None:
        return False
    http.register_view(ReverseLinkView(links))
    links.view_registered = True
    return True


class ReverseLinkView(HomeAssistantView):
    """``GET /api/qoffeeautomat/reverse/v1``: the WebSocket that agents dial."""

    url = REVERSE_LINK_PATH
    name = "api:qoffeeautomat:reverse"
    # The agent has no Home Assistant account: the view checks the reverse token.
    requires_auth = False
    cors_allowed = False

    def __init__(self, links: ReverseLinks) -> None:
        """Serve the agents that ``links`` knows."""
        self._links = links

    async def get(self, request: web.Request) -> web.StreamResponse:
        """Authenticate the agent, upgrade the request and serve the connection."""
        token = bearer_token(request)
        found = self._links.lookup(token) if token else Lookup()
        if not found.known:
            # Raised, not returned: the IP ban of Home Assistant counts it.
            raise web.HTTPUnauthorized(headers={"WWW-Authenticate": "Bearer"})
        client, pending = found.client, found.pending
        if (client is None or client.closing) and pending is None:
            # A valid token whose entry is not loaded right now (Home Assistant is
            # starting or reloading it): the agent must retry, not give up.
            raise web.HTTPServiceUnavailable(headers={"Retry-After": "5"})
        offered = {
            item.strip()
            for item in request.headers.get("Sec-WebSocket-Protocol", "").split(",")
        }
        if SUBPROTOCOL not in offered:
            message = f"the sub-protocol {SUBPROTOCOL} is required"
            raise web.HTTPBadRequest(text=message)

        ws = web.WebSocketResponse(
            protocols=(SUBPROTOCOL,),
            heartbeat=HEARTBEAT_SECONDS,
            max_msg_size=MAX_FRAME_BYTES,
            compress=True,
        )
        await ws.prepare(request)
        if client is not None:
            await client.async_serve(ws)
        elif pending is not None and token is not None:
            await _async_enroll(self._links, hash_token(token), ws, pending)
        return ws


async def _async_enroll(
    links: ReverseLinks,
    token_hash: str,
    ws: web.WebSocketResponse,
    pending: asyncio.Future[Hello],
) -> None:
    """Read ``hello`` of the first agent that uses a new token and close.

    The config flow creates the entry from the ``hello``; the agent dials again (its
    delay after a closed connection is about a second) and is served by the new entry.
    """
    try:
        async with asyncio.timeout(REVERSE_HELLO_TIMEOUT_SECONDS):
            message = await ws.receive()
        if message.type is not aiohttp.WSMsgType.TEXT:
            return
        hello = parse_frame(message.data)
    except (TimeoutError, ProtocolError) as err:
        _LOGGER.debug("An enrolling agent sent no usable hello: %s", err)
        with contextlib.suppress(aiohttp.ClientError, OSError, ConnectionError):
            await ws.close(code=aiohttp.WSCloseCode.PROTOCOL_ERROR)
        await asyncio.sleep(0)
        return
    if isinstance(hello, Hello) and not pending.done():
        links.enrolled(token_hash)
        pending.set_result(hello)
    with contextlib.suppress(aiohttp.ClientError, OSError, ConnectionError):
        await ws.close(code=_CLOSE_NORMAL, message=b"enrolled")
    await asyncio.sleep(0)
