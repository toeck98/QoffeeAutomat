"""Fixtures of the end-to-end test (PLAN.md S14, task T508).

``pytest tests/e2e`` builds and starts the compose stack, runs tests, and stops it.
Environment variables:

* ``E2E_SKIP_COMPOSE=1``  use an already-running stack (nothing started or stopped).
* ``E2E_KEEP_STACK=1``    keep the stack running after tests (CI collects logs first).
"""

import os
import shutil
from collections.abc import Iterator

import pytest

from e2e_driver import (
    AGENT_HOST_IN_COMPOSE,
    AGENT_NAME,
    AGENT_PORT_IN_COMPOSE,
    Agent,
    HomeAssistant,
    PairedAgent,
    allow_network,
    compose,
    wait_for,
)


@pytest.fixture(autouse=True)
def _network_allowed() -> None:
    """Let the test talk to the containers even if dev plugins block sockets."""
    allow_network()


@pytest.fixture(scope="session")
def stack() -> Iterator[None]:
    """Start the compose stack and stop it afterwards."""
    if shutil.which("docker") is None:
        pytest.skip("docker is not installed")
    if os.environ.get("E2E_SKIP_COMPOSE"):
        yield
        return
    compose("up", "--build", "--detach", "--wait", "--wait-timeout", "900")
    try:
        yield
    finally:
        if not os.environ.get("E2E_KEEP_STACK"):
            compose("down", "--volumes", "--remove-orphans", check=False)


@pytest.fixture(scope="session")
def agent(stack: None) -> Agent:
    """The management API of the agent, signed in with the preconfigured password."""
    allow_network()
    client = Agent()
    wait_for(client.is_healthy, timeout=120, what="the agent health check")
    client.login()
    return client


@pytest.fixture(scope="session")
def ha(stack: None) -> HomeAssistant:
    """Home Assistant with an owner account and an access token."""
    allow_network()
    home_assistant = HomeAssistant()
    home_assistant.onboard()
    return home_assistant


@pytest.fixture(scope="session")
def paired(ha: HomeAssistant, agent: Agent) -> PairedAgent:
    """Pair Home Assistant with the agent through the config flow (PLAN.md S10)."""
    allow_network()
    pairing = agent.start_pairing()
    base = "/api/config/config_entries/flow"

    flow = ha.http.call(
        "POST", base, {"handler": "qoffeeautomat", "show_advanced_options": False}
    )
    assert flow["type"] == "form"
    assert flow["step_id"] == "user"

    flow = ha.http.call(
        "POST",
        f"{base}/{flow['flow_id']}",
        {"host": AGENT_HOST_IN_COMPOSE, "port": AGENT_PORT_IN_COMPOSE},
    )
    assert flow["step_id"] == "confirm", flow
    # The config flow shows the fingerprint of the certificate; it is the agent's.
    assert pairing["fingerprint"] in str(flow["description_placeholders"])

    flow = ha.http.call("POST", f"{base}/{flow['flow_id']}", {})
    assert flow["step_id"] == "pair", flow

    result = ha.http.call(
        "POST", f"{base}/{flow['flow_id']}", {"code": pairing["code"]}
    )
    assert result["type"] == "create_entry", result
    assert result["title"] == AGENT_NAME
    return PairedAgent(
        entry_id=result["result"]["entry_id"], fingerprint=pairing["fingerprint"]
    )
