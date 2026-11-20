"""Helpers of the hub end-to-end test (PLAN.md S14, S20, task T919).

Like ``tests/e2e/e2e_driver.py`` (whose HTTP client and polling helper are reused) it
uses only the standard library: JSON over HTTP(S) with ``urllib`` and the Docker CLI
through ``subprocess``. The test talks to the containers of ``docker-compose.yml``
through the loopback ports that the compose file publishes: the hub, two agents and
Home Assistant.
"""

import json
import os
import subprocess
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from e2e_driver import (
    AGENT_PASSWORD,
    ApiError,
    HomeAssistant,
    JsonClient,
    allow_network,
    wait_for,
)

__all__ = [
    "AGENT_NAMES",
    "COMMAND_ID",
    "FLEET_TAG",
    "HUB_HOST_IN_COMPOSE",
    "HUB_PORT_IN_COMPOSE",
    "HUB_USERNAME",
    "HUB_VARIABLE_VALUE",
    "PROFILE_ID",
    "SENSOR_ID",
    "AgentApi",
    "ApiError",
    "Hub",
    "HubEntry",
    "HubHomeAssistant",
    "allow_network",
    "compose",
    "fleet_profile",
    "library_document",
    "wait_for",
]

COMPOSE_FILE = Path(__file__).with_name("docker-compose.yml")

HUB_PORT = int(os.environ.get("E2E_HUB_PORT", "18490"))
AGENT_PORTS = {
    "agent1": int(os.environ.get("E2E_HUB_AGENT1_PORT", "18491")),
    "agent2": int(os.environ.get("E2E_HUB_AGENT2_PORT", "18492")),
}
HA_PORT = int(os.environ.get("E2E_HUB_HA_PORT", "18223"))

# Names of the two PCs (``E2E_AGENT_NAME`` in docker-compose.yml): the device names in
# Home Assistant.
AGENT_NAMES = {"agent1": "E2E-PC-1", "agent2": "E2E-PC-2"}

# Where the other containers reach the hub: the service name on the compose network.
HUB_URL_IN_COMPOSE = "https://hub:8490"
HUB_HOST_IN_COMPOSE = "hub"
HUB_PORT_IN_COMPOSE = 8490

HUB_USERNAME = "e2e-admin"
HUB_PASSWORD = "e2e-Hub-Admin-Passw0rd-2026"

CSRF_HEADER = {"X-QoffeeAutomat-Csrf": "1"}

# The scenario: one profile with a command and a sensor, assigned to the tag that the
# enrollment token gives both agents. The documents are in ``library/``.
LIBRARY_DIR = Path(__file__).with_name("library")
COMMAND_ID = "hub_echo"
SENSOR_ID = "hub_marker"
PROFILE_ID = "e2e-fleet"
FLEET_TAG = "e2e"
HUB_VARIABLE_VALUE = "Hub-hello"


@dataclass(frozen=True)
class HubEntry:
    """The Home Assistant config entry that "Connect to a hub" created."""

    entry_id: str
    fingerprint: str


def library_document(name: str) -> dict[str, Any]:
    """Load a document of ``library/`` (for example ``hub_echo.command.json``)."""
    document: dict[str, Any] = json.loads(
        (LIBRARY_DIR / name).read_text(encoding="utf-8")
    )
    return document


def fleet_profile(*, local_config: str) -> dict[str, Any]:
    """Return the draft of the scenario's profile with the policy ``local_config``."""
    return {
        "id": PROFILE_ID,
        "name": "E2E fleet",
        "priority": 10,
        "items": [
            {"kind": "command", "id": COMMAND_ID},
            {"kind": "sensor", "id": SENSOR_ID},
        ],
        "variables": {"GREETING": HUB_VARIABLE_VALUE},
        "policy": {"localConfig": local_config, "directLink": "allow"},
    }


def compose(
    *args: str, check: bool = True, capture: bool = False
) -> subprocess.CompletedProcess[str]:
    """Run ``docker compose`` for the hub end-to-end stack."""
    return subprocess.run(
        ["docker", "compose", "-f", str(COMPOSE_FILE), *args],
        check=check,
        text=True,
        capture_output=capture,
    )


def _items(data: Any, key: str) -> list[dict[str, Any]]:
    """Return ``data[key]``, the list of a list response."""
    items: list[dict[str, Any]] = data[key]
    return items


class Hub:
    """The management API of the hub (HTTPS, self-signed certificate)."""

    def __init__(self) -> None:
        """Create the client; call :meth:`set_up` before anything else."""
        self.base_url = f"https://127.0.0.1:{HUB_PORT}"
        self.http = JsonClient(self.base_url, headers=CSRF_HEADER, verify_tls=False)

    def is_healthy(self) -> bool:
        """Return whether the hub answers its health check."""
        status, _ = self.http.request("GET", "/healthz", timeout=5)
        return status == 200

    def set_up(self) -> None:
        """Create the first administrator with the one-time setup token and sign in.

        The token never leaves the container except through ``docker compose exec``,
        which is what an operator does too (``docker compose logs hub`` shows it).
        """
        token = compose(
            "exec", "-T", "hub", "cat", "/data/setup-token.txt", capture=True
        ).stdout.strip()
        assert token, "the hub did not write a setup token"
        self.http.call(
            "POST",
            "/api/v1/auth/setup",
            {"setupToken": token, "username": HUB_USERNAME, "password": HUB_PASSWORD},
        )

    def info(self) -> dict[str, Any]:
        """Return ``GET /api/v1/hub``: id, name, version, TLS mode and fingerprint."""
        info: dict[str, Any] = self.http.call("GET", "/api/v1/hub")
        return info

    def token_client(self, secret: str) -> JsonClient:
        """Return a client without a session that authenticates with an API token."""
        return JsonClient(
            self.base_url,
            headers={"Authorization": f"Bearer {secret}"},
            verify_tls=False,
        )

    def anonymous_client(self) -> JsonClient:
        """Return a client without a session and without a token."""
        return JsonClient(self.base_url, verify_tls=False)

    def client_without_csrf_header(self) -> JsonClient:
        """Return a client with a session that omits the anti-forgery header."""
        client = JsonClient(self.base_url, headers=CSRF_HEADER, verify_tls=False)
        client.call(
            "POST",
            "/api/v1/auth/login",
            {"username": HUB_USERNAME, "password": HUB_PASSWORD},
        )
        client.headers.clear()
        return client

    # -- enrollment ----------------------------------------------------------

    def create_enrollment_token(
        self, *, name: str, tags: list[str], max_uses: int
    ) -> dict[str, Any]:
        """Create an enrollment token; the answer holds ``enrollmentString``."""
        created: dict[str, Any] = self.http.call(
            "POST",
            "/api/v1/enrollment-tokens",
            {
                "name": name,
                "tags": tags,
                "maxUses": max_uses,
                "requiresApproval": False,
                "hubUrl": HUB_URL_IN_COMPOSE,
            },
        )
        return created

    def agents(self) -> list[dict[str, Any]]:
        """Return the enrolled agents."""
        return _items(self.http.call("GET", "/api/v1/agents"), "agents")

    def agent_id(self, name: str) -> str | None:
        """Return the id of the agent with this name, if it is enrolled."""
        for agent in self.agents():
            if agent["name"] == name:
                return str(agent["id"])
        return None

    # -- configuration -------------------------------------------------------

    def save_library_document(
        self, kind: str, doc_id: str, body: dict[str, Any]
    ) -> None:
        """Create or replace a library document (``PUT /api/v1/library/...``)."""
        self.http.call("PUT", f"/api/v1/library/{kind}/{doc_id}", {"body": body})

    def save_profile(self, profile: dict[str, Any]) -> dict[str, Any]:
        """Create a profile, or replace its draft when it exists."""
        status, _ = self.http.request("GET", f"/api/v1/profiles/{profile['id']}")
        if status == 200:
            saved: dict[str, Any] = self.http.call(
                "PUT", f"/api/v1/profiles/{profile['id']}", profile
            )
        else:
            saved = self.http.call("POST", "/api/v1/profiles", profile)
        return saved

    def publish_profile(self, profile_id: str, note: str) -> dict[str, Any]:
        """Publish the draft of a profile as the next revision."""
        published: dict[str, Any] = self.http.call(
            "POST", f"/api/v1/profiles/{profile_id}/publish", {"note": note}
        )
        return published

    def assign(self, profile_id: str, target: str) -> dict[str, Any]:
        """Assign a profile to ``all``, ``tag:<tag>`` or ``agent:<id>``."""
        assigned: dict[str, Any] = self.http.call(
            "POST", "/api/v1/assignments", {"profileId": profile_id, "target": target}
        )
        return assigned

    def rollout(self) -> dict[str, Any]:
        """Return the roll-out overview of all agents."""
        overview: dict[str, Any] = self.http.call("GET", "/api/v1/rollouts")
        return overview

    def config_status(self, agent_id: str) -> dict[str, Any]:
        """Return the roll-out status of one agent."""
        status: dict[str, Any] = self.http.call(
            "GET", f"/api/v1/agents/{agent_id}/config-status"
        )
        return status

    # -- metrics -------------------------------------------------------------

    def latest_metrics(self, agent_id: str) -> list[dict[str, Any]]:
        """Return the newest stored value of every metric of an agent."""
        return _items(
            self.http.call("GET", f"/api/v1/agents/{agent_id}/metrics"), "metrics"
        )

    def query_metrics(
        self, agent_id: str, metric: str, *, start: str, resolution: str = "raw"
    ) -> list[dict[str, Any]]:
        """Return the stored series of one metric (``GET /api/v1/metrics/query``)."""
        result = self.http.call(
            "GET",
            f"/api/v1/metrics/query?agent={agent_id}&metric={metric}"
            f"&from={start}&resolution={resolution}",
        )
        return _items(result, "series")

    def create_api_token(self, name: str, role: str) -> str:
        """Create an API token and return its secret (shown once)."""
        created: dict[str, Any] = self.http.call(
            "POST", "/api/v1/tokens", {"name": name, "role": role}
        )
        return str(created["secret"])

    # -- the management tunnel and fleet actions -----------------------------

    def tunnel(
        self, agent_id: str, method: str, path: str, body: Any = None
    ) -> tuple[int, Any]:
        """Call the API of an agent through the hub; returns ``(status, body)``."""
        return self.http.request(
            method, f"/api/v1/agents/{agent_id}/agent-api/{path}", body, timeout=60
        )

    def create_fleet_pairing(self, name: str) -> dict[str, Any]:
        """Create a one-time code for pairing Home Assistant (scope: every agent)."""
        pairing: dict[str, Any] = self.http.call(
            "POST", "/api/v1/fleet-pairings", {"name": name, "scope": "all"}
        )
        return pairing

    def audit_entries(self) -> list[dict[str, Any]]:
        """Return the newest audit entries, the newest first."""
        return _items(self.http.call("GET", "/api/v1/audit?limit=200"), "entries")


class AgentApi:
    """The management API of an agent (HTTPS listener, remote UI is on in the seed)."""

    def __init__(self, service: str) -> None:
        """Create the client for the compose service ``agent1`` or ``agent2``."""
        self.service = service
        self.name = AGENT_NAMES[service]
        self.http = JsonClient(
            f"https://127.0.0.1:{AGENT_PORTS[service]}",
            headers=CSRF_HEADER,
            verify_tls=False,
        )

    def is_healthy(self) -> bool:
        """Return whether the agent answers its health check."""
        status, _ = self.http.request("GET", "/healthz", timeout=5)
        return status == 200

    def login(self) -> None:
        """Sign in with the password that the container entry point configured."""
        self.http.call("POST", "/api/v1/auth/login", {"password": AGENT_PASSWORD})

    def join(self, enrollment: str) -> dict[str, Any]:
        """Join the hub with an enrollment string (``POST /api/v1/hub/join``)."""
        status: dict[str, Any] = self.http.call(
            "POST", "/api/v1/hub/join", {"enrollment": enrollment}, timeout=60
        )
        return status

    def hub_status(self) -> dict[str, Any]:
        """Return the hub link of the agent (``GET /api/v1/hub``)."""
        status: dict[str, Any] = self.http.call("GET", "/api/v1/hub")
        return status

    def hub_config(self) -> dict[str, Any]:
        """Return what the hub configured on the agent (``GET /api/v1/hub/config``)."""
        config: dict[str, Any] = self.http.call("GET", "/api/v1/hub/config")
        return config

    def command_ids(self) -> set[str]:
        """Return the ids of the commands of the effective configuration."""
        commands = _items(self.http.call("GET", "/api/v1/commands"), "commands")
        return {str(command["id"]) for command in commands}

    def set_command_enabled(self, command_id: str, *, enabled: bool) -> tuple[int, Any]:
        """Enable or disable a local command; returns ``(status, body)``."""
        return self.http.request(
            "PATCH", f"/api/v1/commands/{command_id}", {"enabled": enabled}
        )


class HubHomeAssistant(HomeAssistant):
    """Home Assistant of the hub stack: another port, entities named after the PC."""

    def __init__(self) -> None:
        """Create the client; call :meth:`onboard` before anything else."""
        super().__init__()
        self.http = JsonClient(f"http://127.0.0.1:{HA_PORT}")

    def entity(self, agent_name: str, name: str) -> dict[str, Any] | None:
        """Return the state of the entity ``name`` of the PC ``agent_name``, if any."""
        friendly_name = f"{agent_name} {name}"
        for state in self.states():
            if state["attributes"].get("friendly_name") == friendly_name:
                return state
        return None

    def device_id(self, entity_id: str) -> str:
        """Return the id of the device that owns an entity."""
        return self.render(f"{{{{ device_id('{entity_id}') }}}}").strip()
