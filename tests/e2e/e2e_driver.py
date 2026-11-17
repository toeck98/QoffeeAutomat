"""Helpers of the end-to-end test (PLAN.md S14, task T508).

Only the standard library is used: JSON over HTTP(S) with ``urllib`` and the Docker CLI
through ``subprocess``. The test talks to the two containers of ``docker-compose.yml``
through the loopback ports that the compose file publishes.
"""

import http.cookiejar
import json
import os
import ssl
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path
from typing import Any

COMPOSE_FILE = Path(__file__).with_name("docker-compose.yml")

HA_PORT = int(os.environ.get("E2E_HA_PORT", "18123"))
AGENT_PORT = int(os.environ.get("E2E_AGENT_PORT", "18481"))
AGENT_PASSWORD = os.environ.get("E2E_AGENT_PASSWORD", "e2e-Qoffee-Passw0rd-2026")

# Agent name (``tests/e2e/agent/seed/settings.json``): becomes the device name,
# and so the first part of the friendly name of every entity.
AGENT_NAME = "E2E-PC"
# Where Home Assistant reaches the agent: the service name on the compose network.
AGENT_HOST_IN_COMPOSE = "agent"
AGENT_PORT_IN_COMPOSE = 8481

HA_USERNAME = "e2e"
HA_PASSWORD = "e2e-Home-Assistant-Passw0rd-2026"
HA_CLIENT_ID = f"http://127.0.0.1:{HA_PORT}/"


@dataclass(frozen=True)
class PairedAgent:
    """The result of the config flow."""

    entry_id: str
    fingerprint: str


class ApiError(Exception):
    """An HTTP request got an unexpected status."""

    def __init__(self, method: str, url: str, status: int, body: Any) -> None:
        """Remember what failed."""
        super().__init__(f"{method} {url} -> HTTP {status}: {body!r}")
        self.status = status
        self.body = body


def allow_network() -> None:
    """Re-enable sockets if a pytest plugin in the dev environment blocked them."""
    try:
        import pytest_socket
    except ImportError:
        return
    pytest_socket.enable_socket()


class JsonClient:
    """A small JSON-over-HTTP client with cookies and optional unverified TLS."""

    def __init__(
        self,
        base_url: str,
        *,
        headers: dict[str, str] | None = None,
        verify_tls: bool = True,
    ) -> None:
        """Create a client for ``base_url``; ``headers`` are sent with every request."""
        self.base_url = base_url
        self.headers = dict(headers or {})
        context = ssl.create_default_context()
        if not verify_tls:
            # The agent's cert is self-signed; the test pins nothing on this path.
            context.check_hostname = False
            context.verify_mode = ssl.CERT_NONE
        self._opener = urllib.request.build_opener(
            urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()),
            urllib.request.HTTPSHandler(context=context),
        )

    def request(
        self,
        method: str,
        path: str,
        body: Any = None,
        *,
        form: dict[str, str] | None = None,
        timeout: float = 30.0,
    ) -> tuple[int, Any]:
        """Send a request and return ``(status, parsed body)``; never raises."""
        url = f"{self.base_url}{path}"
        headers = dict(self.headers)
        data: bytes | None = None
        if form is not None:
            data = urllib.parse.urlencode(form).encode()
            headers["Content-Type"] = "application/x-www-form-urlencoded"
        elif body is not None:
            data = json.dumps(body).encode()
            headers["Content-Type"] = "application/json"
        request = urllib.request.Request(url, data=data, headers=headers, method=method)
        try:
            with self._opener.open(request, timeout=timeout) as response:
                return response.status, _parse(response.read(), response.headers)
        except urllib.error.HTTPError as err:
            return err.code, _parse(err.read(), err.headers)

    def call(
        self,
        method: str,
        path: str,
        body: Any = None,
        *,
        form: dict[str, str] | None = None,
        timeout: float = 30.0,
    ) -> Any:
        """Like :meth:`request`, but raise :class:`ApiError` on non-2xx status."""
        status, data = self.request(method, path, body, form=form, timeout=timeout)
        if not 200 <= status < 300:
            raise ApiError(method, f"{self.base_url}{path}", status, data)
        return data


def _parse(raw: bytes, headers: Any) -> Any:
    text = raw.decode("utf-8", errors="replace")
    if "json" in str(headers.get("Content-Type", "")) and text:
        return json.loads(text)
    return text


def wait_for[T](
    check: Callable[[], T | None],
    *,
    timeout: float,
    what: str,
    interval: float = 1.0,
) -> T:
    """Poll ``check`` until truthy; fail with ``what`` after ``timeout``.

    Connection errors and HTTP errors count as "not yet".
    """
    deadline = time.monotonic() + timeout
    last_error: Exception | None = None
    while True:
        try:
            result = check()
        except (OSError, ApiError) as err:
            last_error = err
        else:
            if result:
                return result
        if time.monotonic() >= deadline:
            detail = f" (last error: {last_error})" if last_error else ""
            msg = f"timed out after {timeout:.0f} s waiting for {what}{detail}"
            raise TimeoutError(msg)
        time.sleep(interval)


def compose(*args: str, check: bool = True) -> subprocess.CompletedProcess[str]:
    """Run ``docker compose`` for the end-to-end stack."""
    return subprocess.run(
        ["docker", "compose", "-f", str(COMPOSE_FILE), *args],
        check=check,
        text=True,
        capture_output=False,
    )


class Agent:
    """The management API of the agent (HTTPS listener, remote UI is on in the seed)."""

    def __init__(self) -> None:
        """Create the client; call :meth:`login` before anything else."""
        self.http = JsonClient(
            f"https://127.0.0.1:{AGENT_PORT}",
            headers={"X-QoffeeAutomat-Csrf": "1"},
            verify_tls=False,
        )

    def login(self) -> None:
        """Sign in with the password that the container entry point configured."""
        self.http.call("POST", "/api/v1/auth/login", {"password": AGENT_PASSWORD})

    def is_healthy(self) -> bool:
        """Return whether the agent answers its health check."""
        status, _ = self.http.request("GET", "/healthz", timeout=5)
        return status == 200

    def start_pairing(self) -> dict[str, Any]:
        """Create a pairing code; returns ``code``, ``expiresAt``, ``fingerprint``."""
        data: dict[str, Any] = self.http.call("POST", "/api/v1/link/pairing")
        return data

    def set_command_enabled(self, command_id: str, *, enabled: bool) -> None:
        """Enable or disable a command (``PATCH /commands/{id}``)."""
        self.http.call("PATCH", f"/api/v1/commands/{command_id}", {"enabled": enabled})


class HomeAssistant:
    """The REST API of the Home Assistant container."""

    def __init__(self) -> None:
        """Create the client; call :meth:`onboard` before anything else."""
        self.http = JsonClient(f"http://127.0.0.1:{HA_PORT}")

    def onboard(self) -> None:
        """Wait for Home Assistant, create the first user and obtain an access token."""
        wait_for(
            lambda: self.http.request("GET", "/api/onboarding")[0] == 200,
            timeout=300,
            interval=2,
            what="the Home Assistant onboarding API",
        )
        created = self.http.call(
            "POST",
            "/api/onboarding/users",
            {
                "client_id": HA_CLIENT_ID,
                "name": "E2E",
                "username": HA_USERNAME,
                "password": HA_PASSWORD,
                "language": "en",
            },
        )
        token = self.http.call(
            "POST",
            "/auth/token",
            form={
                "grant_type": "authorization_code",
                "code": created["auth_code"],
                "client_id": HA_CLIENT_ID,
            },
        )
        self.http.headers["Authorization"] = f"Bearer {token['access_token']}"

    def states(self) -> list[dict[str, Any]]:
        """Return the states of all entities."""
        states: list[dict[str, Any]] = self.http.call("GET", "/api/states")
        return states

    def find_entity(self, name: str) -> dict[str, Any] | None:
        """Return the state of the entity of the agent with this entity name, if any."""
        friendly_name = f"{AGENT_NAME} {name}"
        for state in self.states():
            if state["attributes"].get("friendly_name") == friendly_name:
                return state
        return None

    def render(self, template: str) -> str:
        """Render a template and return the text."""
        return str(self.http.call("POST", "/api/template", {"template": template}))
