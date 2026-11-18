"""End-to-end test: real Home Assistant and Linux agent (PLAN.md S12, S14, T508).

The tests share one stack and run in file order: pairing first, restart last.
They see the stack only from outside (REST APIs of both containers and Docker CLI).
"""

import os
from typing import Any

import pytest

from e2e_driver import (
    AGENT_NAME,
    Agent,
    ApiError,
    HomeAssistant,
    PairedAgent,
    compose,
    wait_for,
)

ENTITY_TIMEOUT = 120
RECONNECT_TIMEOUT = 180

# E2E_AGENT_PLATFORM=linux runs the agent with the real Linux platform (T804):
# simulated metrics are absent, "CPU usage" / "Memory used" take their place,
# and a real value need not change within a minute.
REAL_PLATFORM = os.environ.get("E2E_AGENT_PLATFORM", "simulated") == "linux"
VALUE_ENTITY = "CPU usage" if REAL_PLATFORM else "Simulated counter"
PLATFORM_ENTITIES = (
    ("CPU usage", "Memory used")
    if REAL_PLATFORM
    else (
        "Simulated counter",
        "Simulated CPU usage",
        "Simulated mode",
        "Simulated toggle",
    )
)


def _wait_for_entity(ha: HomeAssistant, name: str) -> dict[str, Any]:
    return wait_for(
        lambda: ha.find_entity(name),
        timeout=ENTITY_TIMEOUT,
        what=f"the entity {name!r}",
    )


def _wait_for_no_entity(ha: HomeAssistant, name: str) -> None:
    wait_for(
        lambda: ha.find_entity(name) is None,
        timeout=ENTITY_TIMEOUT,
        what=f"the entity {name!r} to disappear",
    )


def _counter_values(ha: HomeAssistant, *, count: int, timeout: float) -> list[str]:
    """Return ``count`` distinct values as they arrive (1 on the real platform)."""
    seen: list[str] = []
    count = 1 if REAL_PLATFORM else count

    def collect() -> list[str] | None:
        entity = ha.find_entity(VALUE_ENTITY)
        if (
            entity is not None
            and entity["state"] not in ("unavailable", "unknown")
            and entity["state"] not in seen
        ):
            seen.append(entity["state"])
        return seen if len(seen) >= count else None

    return wait_for(
        collect, timeout=timeout, interval=0.5, what=f"{count} different counter values"
    )


def test_config_flow_creates_a_loaded_entry(
    ha: HomeAssistant, paired: PairedAgent
) -> None:
    """Pairing with a code from the agent API creates a config entry that loads."""

    def entry_loaded() -> bool:
        entries = ha.http.call("GET", "/api/config/config_entries/entry")
        return any(
            entry["entry_id"] == paired.entry_id and entry["state"] == "loaded"
            for entry in entries
        )

    assert wait_for(entry_loaded, timeout=ENTITY_TIMEOUT, what="the entry to load")


def test_entities_are_created_from_the_manifest(
    ha: HomeAssistant, paired: PairedAgent
) -> None:
    """Built-in, custom sensor, command and connectivity entities appear."""
    for name in (
        "Connectivity",
        "Power state",
        *PLATFORM_ENTITIES,
        "E2E marker",
        "E2E ping",
    ):
        _wait_for_entity(ha, name)

    connectivity = _wait_for_entity(ha, "Connectivity")
    assert connectivity["state"] == "on"
    marker = wait_for(
        lambda: (
            entity
            if (entity := ha.find_entity("E2E marker")) and entity["state"] == "42"
            else None
        ),
        timeout=ENTITY_TIMEOUT,
        what="the custom sensor to report 42",
    )
    assert marker["entity_id"].startswith("sensor.")
    assert _wait_for_entity(ha, "E2E ping")["entity_id"].startswith("button.")


def test_values_update(ha: HomeAssistant, paired: PairedAgent) -> None:
    """The simulated counter counts up, so its state keeps changing."""
    values = _counter_values(ha, count=3, timeout=60)

    assert all(value.replace(".", "", 1).isdigit() for value in values)


def test_run_command_with_variables(ha: HomeAssistant, paired: PairedAgent) -> None:
    """The action passes variables to the PowerShell command and returns its output."""
    entity = _wait_for_entity(ha, VALUE_ENTITY)
    device_id = ha.render(f"{{{{ device_id('{entity['entity_id']}') }}}}").strip()
    assert device_id

    response = ha.http.call(
        "POST",
        "/api/services/qoffeeautomat/run_command?return_response",
        {
            "device_id": device_id,
            "command": "e2e_echo",
            "variables": {"Name": "Qoffee", "Count": 3},
            "timeout": 60,
        },
        timeout=90,
    )

    result = response["service_response"]
    assert result["exit_code"] == 0, result
    assert "Hello Qoffee x3" in result["stdout"]
    assert "duration_ms" in result

    status, body = ha.http.request(
        "POST",
        "/api/services/qoffeeautomat/run_command?return_response",
        {"device_id": device_id, "command": "does_not_exist"},
    )
    assert status == 400, body


def test_toggling_a_command_adds_and_removes_its_entity(
    ha: HomeAssistant, agent: Agent, paired: PairedAgent
) -> None:
    """Disabling a command on the agent removes its entity; enabling brings it back."""
    _wait_for_entity(ha, "E2E ping")
    try:
        agent.set_command_enabled("e2e_ping", enabled=False)
        _wait_for_no_entity(ha, "E2E ping")
    finally:
        agent.set_command_enabled("e2e_ping", enabled=True)
    _wait_for_entity(ha, "E2E ping")


def test_agent_restart_reconnects(ha: HomeAssistant, paired: PairedAgent) -> None:
    """After the agent container restarts, Home Assistant connects again by itself."""
    assert _counter_values(ha, count=2, timeout=60)

    compose("restart", "agent")

    # Client retries up to 60 s backoff; agent keeps its pairing in /data.
    def connected() -> bool:
        entity = ha.find_entity("Connectivity")
        return entity is not None and entity["state"] == "on"

    wait_for(
        connected,
        timeout=RECONNECT_TIMEOUT,
        interval=2,
        what=f"{AGENT_NAME} to be connected again",
    )

    # Fresh values arrive again, which also shows that the subscription was sent again.
    assert _counter_values(ha, count=2, timeout=RECONNECT_TIMEOUT)
    assert _wait_for_entity(ha, "E2E marker")["state"] == "42"


def test_rejected_api_calls_raise_api_errors(agent: Agent) -> None:
    """Sanity check of the harness: the agent API needs the session of the login."""
    anonymous = Agent()
    with pytest.raises(ApiError) as error:
        anonymous.http.call("GET", "/api/v1/commands")
    assert error.value.status == 401
